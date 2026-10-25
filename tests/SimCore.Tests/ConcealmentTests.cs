using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Concealment from the air under trees (content/mvp/concealment.json, Sim/Systems/ConcealmentSystem.cs; docs/design.md,
// "Concealment from the air"): every cell of the camera table for infantry and vehicles, the firing reveal and its timing, the
// Hide stance, thermal, ground radar, the low helicopter's bonus, ground sight unchanged, losing a unit under the trees, the
// leaf-marker API and the same hash on two runs. Player 1 is the Sentinel (the air units), player 2 the Resistance (the
// targets). An air unit is pinned over a cell before each tick (Look), so the level distance to the target is exact; the plain
// camera is the air superiority fighter's (the Sentinel's camera-only air unit; the Orlan is the Resistance's).
public class ConcealmentTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int TX = 32, TY = 32; // the target's cell, inside the forest

    // Wooded is a Sentinel-lobby world on flat terrain with a forest of the density over cells [20, 44) both ways, with fog of
    // war unless fog is false.
    private static World Wooded(int density, bool fog = true, int size = 64)
    {
        var w = Arena.NewSentinel(size, edit: fog ? s => s with { Fog = true } : null);
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                flags[y * size + x] = (byte)(x is >= 20 and < 44 && y is >= 20 and < 44 ? density : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], flags, Rules));
        return w;
    }

    // Look pins air unit eye over the centre of cell (x, y) and steps once, the eye looking around this tick.
    private static void Look(World w, Unit eye, int x, int y)
    {
        eye.Pos = new Cell(x, y).Center();
        eye.LookedAt = uint.MaxValue;
        w.Step();
    }

    // Move makes the vision of the next tick find t moving (as the movement system leaves Vel after a step).
    private static void Move(Unit t) => t.Vel = new Vec(Fixed.FromRaw(20), Fixed.Zero);

    private static Unit Air(World w, string archetype)
    {
        var u = w.Spawn(1, archetype, 2, 2);
        u.Stance = Stance.HoldFire;
        return u;
    }

    private static Unit Target(World w, bool vehicle, int x = TX, int y = TY)
    {
        var t = w.Spawn(2, vehicle ? "technical" : "Rifleman", x, y);
        t.Stance = Stance.HoldFire; // it never fires unless a test makes it (a shot reveals it)
        return t;
    }

    // Sees: whether air unit archetype, pinned cells away east of a target of that kind (moving or not) under forest of density,
    // spots it on its first look.
    private static bool Sees(string archetype, int density, bool vehicle, bool moving, int cells, Action<World, Unit>? setUp = null)
    {
        var w = Wooded(density);
        var eye = Air(w, archetype);
        setUp?.Invoke(w, eye);
        var t = Target(w, vehicle);
        if (moving)
        {
            Move(t);
        }
        Look(w, eye, TX + cells, TY);
        Assert.True(w.InAir(eye));
        Assert.True(w.FogMatchesRebuild());
        return World.IsSpotted(t, 1);
    }

    [Theory]
    [InlineData(0, false, 30, true)]  // in the open: anywhere in its vision
    [InlineData(1, false, 30, true)]  // scrub: seen normally
    [InlineData(1, true, 30, true)]
    [InlineData(2, false, 3, true)]   // grove, still: only within 3 cells
    [InlineData(2, false, 4, false)]
    [InlineData(2, true, 8, true)]    // grove, moving: within 8
    [InlineData(2, true, 9, false)]
    [InlineData(3, false, 0, false)]  // dense, still: hidden from cameras, even straight above
    [InlineData(3, false, 1, false)]
    [InlineData(3, true, 4, true)]    // dense, moving: within 4
    [InlineData(3, true, 5, false)]
    public void ACameraSeesInfantryUnderTheTreesAsTheTableSays(int density, bool moving, int cells, bool seen) =>
        Assert.Equal(seen, Sees("fighter_jet", density, false, moving, cells));

    // Vehicles hide less well: twice the infantry's reaches, and still in dense forest within 4 cells.
    [Theory]
    [InlineData(1, false, 30, true)]
    [InlineData(2, false, 6, true)]
    [InlineData(2, false, 7, false)]
    [InlineData(2, true, 16, true)]
    [InlineData(2, true, 17, false)]
    [InlineData(3, false, 4, true)]
    [InlineData(3, false, 5, false)]
    [InlineData(3, true, 8, true)]
    [InlineData(3, true, 9, false)]
    public void ACameraSeesVehiclesUnderTheTreesAsTheTableSays(int density, bool moving, int cells, bool seen) =>
        Assert.Equal(seen, Sees("fighter_jet", density, true, moving, cells));

    // The firing column: a unit that fired in the last tick is seen as in the open, at 20 cells, in a grove and in dense forest.
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void AShotRevealsTheShooterToTheAir(int density, bool vehicle)
    {
        Assert.False(Sees("fighter_jet", density, vehicle, false, 20));
        var w = Wooded(density);
        var eye = Air(w, "fighter_jet");
        var t = Target(w, vehicle);
        w.Run(5); // (a shot at tick 0 would read as none: LastShot 0 is "never fired")
        t.LastShot = w.Tick - 1; // it fired in the tick just run
        Look(w, eye, TX + 20, TY);
        Assert.True(World.IsSpotted(t, 1));
    }

    // The reveal lasts the next 100 vision passes after the shot (5 s), then the canopy hides the unit again (its last spot
    // holds until the eye's next look, as any spot does: at most VisionTicks - 1 ticks).
    [Fact]
    public void TheRevealLastsFiveSeconds()
    {
        var w = Wooded(3);
        var eye = Air(w, "fighter_jet");
        var t = Target(w, false);
        w.Run(5);
        var shot = w.Tick - 1;
        t.LastShot = shot;
        Assert.Equal(new AirCover(false, Fixed.Zero, shot + 101), w.AirCoverOf(t));
        for (var k = 1; k <= 100; k++)
        {
            Look(w, eye, TX + 20, TY);
            Assert.True(World.IsSpotted(t, 1), $"pass {k} after the shot");
        }
        Assert.Equal(shot + 101, w.Tick);
        Assert.True(w.AirCoverOf(t).Hidden);
        for (var k = 0; k < (int)World.VisionTicks; k++)
        {
            Look(w, eye, TX + 20, TY);
        }
        Assert.False(World.IsSpotted(t, 1));
    }

    // Hide holds fire to stay hidden: a soldier at the edge of dense forest with an enemy in reach in the open fires on Guard
    // (and the drone 10 cells away sees it), holds on Hide (and stays hidden), and on Hide still fires when ordered to.
    [Theory]
    [InlineData(Stance.Guard, false, true)]
    [InlineData(Stance.HoldFire, false, false)]
    [InlineData(Stance.Hide, false, false)]
    [InlineData(Stance.Hide, true, true)]
    public void HideHoldsFireToStayHidden(Stance stance, bool ordered, bool fires)
    {
        var w = Wooded(3);
        var eye = Air(w, "fighter_jet");
        var soldier = w.Spawn(2, "Rifleman", 20, TY); // the forest's first column
        var dummy = w.Spawn(1, "Rifleman", 17, TY);   // 3 cells out in the open, in the rifle's reach
        dummy.Stance = Stance.HoldFire;
        dummy.VisionBonus = Fixed.FromRaw(-dummy.Def.Vision.Raw); // it sees only its own cell: no ground eye on the soldier
        dummy.HP = 100_000;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [soldier.Id], Stance = stance });
        Look(w, eye, 30, TY);
        Assert.True(World.IsSpotted(dummy, 2));
        if (ordered)
        {
            w.Order(2, CommandKind.Attack, [soldier], dummy.Id); // an order needs the target spotted: after the first look
        }
        for (var i = 0; i < 60; i++)
        {
            Look(w, eye, 30, TY);
        }
        Assert.Equal(stance, soldier.Stance);
        Assert.Equal(fires, soldier.LastShot != 0);
        Assert.Equal(fires, World.IsSpotted(soldier, 1));
        Assert.Equal(!fires, w.AirCoverOf(soldier).Hidden);
    }

    [Fact]
    public void HideIsAContentStance()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"pursue\": false,", "\"pursue\": false, \"stance\": \"hide\",") } : s)
            .ToList();
        Assert.Equal((int)Stance.Hide, ContentLoader.Load(sources).Archetype("engineer")!.Stance);
    }

    // Thermal (the Overseer): a vehicle whose engine ran in the last 30 s, under any canopy, as in the open; parked longer, only
    // its camera's reach.
    [Fact]
    public void ThermalSeesAVehicleWhoseEngineRanInTheLast30Seconds()
    {
        var w = Wooded(3);
        var eye = Air(w, "overseer_drone");
        Assert.Equal(AirSensors.Camera | AirSensors.Thermal, w.SensorsOf(eye));
        var t = Target(w, true);
        Look(w, eye, TX + 20, TY);
        Assert.False(World.IsSpotted(t, 1)); // parked since it came: cold
        Move(t);
        var moved = w.Tick;
        Look(w, eye, TX + 20, TY);
        Assert.Equal(moved, t.MovedAt);
        Assert.True(World.IsSpotted(t, 1));
        while (w.Tick <= moved + 600)
        {
            Look(w, eye, TX + 20, TY);
            Assert.True(World.IsSpotted(t, 1), $"tick {w.Tick - 1}");
        }
        for (var k = 0; k < (int)World.VisionTicks; k++)
        {
            Look(w, eye, TX + 20, TY);
        }
        Assert.False(World.IsSpotted(t, 1)); // 30 s parked: cold again
        Look(w, eye, TX + 4, TY);
        Assert.True(World.IsSpotted(t, 1)); // its camera still sees it within 4 cells
    }

    // Thermal and infantry: moving in scrub or a grove it is seen as in the open; moving in dense forest only the camera's 4 cells;
    // still, never more than the camera's reach (a grove's 3 cells, nothing in dense forest).
    [Theory]
    [InlineData(2, true, 20, true)]
    [InlineData(1, true, 20, true)]
    [InlineData(3, true, 20, false)]
    [InlineData(3, true, 4, true)]
    [InlineData(2, false, 20, false)]
    [InlineData(2, false, 3, true)]
    [InlineData(3, false, 0, false)]
    public void ThermalSeesMovingInfantryInGrovesNeverStillInDense(int density, bool moving, int cells, bool seen) =>
        Assert.Equal(seen, Sees("overseer_drone", density, false, moving, cells));

    // Who has thermal (the owner's default): the Overseer, the Watcher and the gunship; the Orlan, the jets and the helicopters a
    // camera; the stealth multirole's heavy (ground) mode a camera and ground radar.
    [Fact]
    public void TheSensorsAreTheOwnersDefault()
    {
        var c = TestContent.Mvp;
        foreach (var id in new[] { "overseer_drone", "watcher_drone", "gunship" })
        {
            Assert.Equal(AirSensors.Camera | AirSensors.Thermal, c.Archetype(id)!.Sensors);
        }
        foreach (var id in new[] { "scout_drone", "multirole_jet", "fighter_jet", "attack_jet", "attack_helicopter", "light_helicopter", "stealth_multirole" })
        {
            Assert.Equal(AirSensors.Camera, c.Archetype(id)!.Sensors);
        }
        var f35 = c.Archetype("stealth_multirole")!;
        Assert.Equal(AirSensors.None, f35.Loadouts[0].Sensors);
        Assert.Equal(AirSensors.Camera | AirSensors.GroundRadar, f35.Loadouts[1].Sensors);
        var cr = c.Concealment!;
        Assert.Equal(new long[] { ConcealmentRules.Seen, ConcealmentRules.Seen, 3 * Fixed.OneRaw, 0 }, cr.InfantryStill);
        Assert.Equal(new long[] { ConcealmentRules.Seen, ConcealmentRules.Seen, 6 * Fixed.OneRaw, 4 * Fixed.OneRaw }, cr.VehicleStill);
        Assert.Equal(100, cr.FireRevealTicks);
        Assert.Equal(600, cr.EngineTicks);
        Assert.False(cr.Covers(1));
        Assert.True(cr.Covers(2) && cr.Covers(3));
    }

    // Ground radar (the stealth multirole in its heavy mode): moving vehicles under any canopy, as in the open; never a parked
    // vehicle (even with its engine warm) or infantry. In stealth mode it has a camera only.
    [Theory]
    [InlineData(1, true, true, 3, true)]
    [InlineData(1, true, true, 2, true)]
    [InlineData(0, true, true, 3, false)]
    [InlineData(1, true, false, 3, false)]
    [InlineData(1, false, true, 2, false)]
    public void GroundRadarSeesMovingVehiclesInAnyForest(int loadout, bool vehicle, bool moving, int density, bool seen)
    {
        var w = Wooded(density);
        var eye = Air(w, "stealth_multirole");
        eye.Loadout = loadout;
        var t = Target(w, vehicle);
        w.Run(1); // (a move seen at tick 0 would read as none: MovedAt 0 is "never")
        if (!moving && vehicle)
        {
            // It drives a tick (seen), then parks with its engine warm: lost by the eye's next look.
            Move(t);
            Look(w, eye, TX + 20, TY);
            Assert.Equal(loadout == 1, World.IsSpotted(t, 1));
            for (var k = 1; k < (int)World.VisionTicks; k++)
            {
                Look(w, eye, TX + 20, TY);
            }
            Assert.NotEqual(0u, t.MovedAt);
        }
        if (moving)
        {
            Move(t);
        }
        Look(w, eye, TX + 20, TY);
        Assert.True(w.InAir(eye));
        Assert.Equal(seen, World.IsSpotted(t, 1));
    }

    // A low helicopter sees 2 cells further under the trees: still infantry in a grove at 5 cells (the fighter: 3); flying higher
    // than 40 m it has no bonus; dense forest still hides a still soldier from it.
    [Theory]
    [InlineData("light_helicopter", 2, 5, 0, true)]
    [InlineData("light_helicopter", 2, 6, 0, false)]
    [InlineData("fighter_jet", 2, 5, 0, false)]
    [InlineData("light_helicopter", 2, 5, 60, false)]
    [InlineData("light_helicopter", 2, 3, 60, true)]
    [InlineData("light_helicopter", 3, 0, 0, false)]
    public void ALowHelicopterSeesABitFurtherUnderTheTrees(string archetype, int density, int cells, int heightM, bool seen) =>
        Assert.Equal(seen, Sees(archetype, density, false, false, cells,
            heightM == 0 ? null : (_, eye) => eye.Altitude = Fixed.FromRaw(heightM * 512L)));

    // Ground units see into the forest as before: a soldier 3 cells away sees an enemy 1 cell into dense forest (the drone over
    // it does not), not one 4 cells in; the grids still match a rebuild.
    [Fact]
    public void GroundSightIntoForestIsUnchanged()
    {
        var w = Wooded(3);
        var eye = Air(w, "fighter_jet");
        w.Spawn(1, "Rifleman", 18, TY);
        var edge = Target(w, false, 21, TY);
        var deep = Target(w, false, 24, TY);
        Look(w, eye, 22, TY);
        Assert.True(World.IsSpotted(edge, 1));
        Assert.False(World.IsSpotted(deep, 1));
        Assert.True(w.FogOf(1)!.Visible(24, TY)); // the canopy over it is seen from the air
        Assert.True(w.FogMatchesRebuild());
    }

    // An air-spotted unit that slips under the trees is lost the normal way: out of SpottedBy and SeenBy within the eye's next
    // look, so the client draws its last-seen marker where it was.
    [Fact]
    public void AUnitThatSlipsUnderTheTreesIsLost()
    {
        var w = Wooded(3);
        var eye = Air(w, "fighter_jet");
        var t = Target(w, false, 10, TY); // in the open
        Look(w, eye, 20, TY);
        Assert.True(World.IsSpotted(t, 1) && World.IsVisibleTo(t, 1));
        t.Pos = new Cell(25, TY).Center(); // under dense canopy, standing still
        for (var k = 0; k < (int)World.VisionTicks; k++)
        {
            Look(w, eye, 20, TY);
        }
        Assert.False(World.IsSpotted(t, 1));
        Assert.False(World.IsVisibleTo(t, 1));
        Assert.True(w.AirCoverOf(t).Hidden);
    }

    // The leaf marker's API: hidden under grove or dense canopy with a camera reach, not in the open or scrub, not while revealed,
    // nothing without fog; structures are never concealed.
    [Fact]
    public void AirCoverTellsTheClientWhatEnemyCamerasSee()
    {
        var w = Wooded(2);
        var still = Target(w, false);
        var moving = Target(w, true, TX, TY + 2);
        var open = Target(w, false, 5, 5);
        Move(moving);
        Assert.Equal(new AirCover(true, Fixed.FromInt(3), 0), w.AirCoverOf(still));
        Assert.Equal(new AirCover(true, Fixed.FromInt(16), 0), w.AirCoverOf(moving));
        Assert.Equal(default, w.AirCoverOf(open));
        var dense = Wooded(3);
        Assert.Equal(new AirCover(true, Fixed.Zero, 0), dense.AirCoverOf(Target(dense, false)));
        var scrub = Wooded(1);
        Assert.Equal(default, scrub.AirCoverOf(Target(scrub, false)));
        var noFog = Wooded(3, fog: false);
        Assert.Equal(default, noFog.AirCoverOf(Target(noFog, false)));
    }

    // Without fog of war nothing changes: the drone spots a still soldier in dense forest by distance, as before.
    [Fact]
    public void WithoutFogTheAirSeesUnderTheTreesAsBefore()
    {
        var w = Wooded(3, fog: false);
        var eye = Air(w, "fighter_jet");
        var t = Target(w, false);
        Look(w, eye, TX + 20, TY);
        Assert.True(World.IsSpotted(t, 1));
        Assert.Equal(0u, t.MovedAt);
    }

    // The same match twice gives the same hash every tick: drones, a helicopter and the stealth multirole over stripes of forest,
    // infantry and vehicles walking, standing and firing under them.
    [Fact]
    public void TheSameMatchHashesTheSameTwice()
    {
        var a = Match();
        var b = Match();
        var hidden = 0;
        for (var i = 0; i < 400; i++)
        {
            a.Step();
            b.Step();
            Assert.Equal(a.Hash(), b.Hash());
            hidden += a.Units.Count(u => a.AirCoverOf(u).Hidden);
        }
        Assert.True(hidden > 0);
        Assert.Contains(a.Units, u => u.MovedAt != 0);
        Assert.True(a.FogMatchesRebuild());
    }

    private static World Match()
    {
        const int size = 96;
        var w = Arena.NewSentinel(size, 3, s => s with { Fog = true });
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                flags[y * size + x] = (byte)(x / 8 % 4);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, new int[(size + 1) * (size + 1)], flags, Rules));
        var walkers = new List<Unit>();
        for (var i = 0; i < 24; i++)
        {
            var t = w.Spawn(2, i % 6 == 5 ? "Gunner" : "Rifleman", 30 + i % 6 * 6, 30 + i / 6 * 8);
            if (i % 3 == 0)
            {
                walkers.Add(t);
            }
        }
        for (var i = 0; i < 4; i++)
        {
            var (v, _) = w.SpawnCrewed(2, "technical", 34 + i * 10, 64);
            walkers.Add(v);
        }
        w.Order(2, CommandKind.Move, walkers, cell: new Cell(80, 50));
        for (var i = 0; i < 6; i++)
        {
            w.Spawn(1, "Rifleman", 20, 30 + i * 6); // they meet the walkers at the forest stripes and fire
        }
        foreach (var id in new[] { "overseer_drone", "watcher_drone", "fighter_jet", "light_helicopter", "stealth_multirole" })
        {
            var u = w.Spawn(1, id, 50, 50);
            if (id == "stealth_multirole")
            {
                u.Loadout = 1;
            }
        }
        return w;
    }

    // Bad concealment content is refused.
    [Theory]
    [InlineData("archetypes/sentinel.json", "\"sensors\": [\"camera\", \"thermal\"], // under the trees: warm engines", "\"sensors\": [\"camera\", \"sonar\"], //", "unknown sensor")]
    [InlineData("archetypes/sentinel.json", "\"sensors\": [\"camera\", \"thermal\"], // under the trees: warm engines", "\"sensors\": [\"camera\", \"camera\"], //", "listed twice")]
    [InlineData("archetypes/infantry.json", "\"pursue\": false,", "\"pursue\": false, \"sensors\": [\"camera\"],", "sensors are for flying archetypes")]
    [InlineData("concealment.json", "\"still\": [-1, -1, 30, 0]", "\"still\": [-1, 30, 0]", "want 4 values")]
    [InlineData("concealment.json", "\"fireReveal\": 100,", "", "fireReveal is required")]
    [InlineData("concealment.json", "\"still\": [-1, -1, 30, 0]", "\"still\": [-1, -2, 30, 0]", "concealment")]
    public void BadConcealmentContentIsRefused(string file, string from, string to, string want)
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == file ? s with { Json = s.Json.Replace(from, to, StringComparison.Ordinal) } : s)
            .ToList();
        Assert.NotEqual(TestContent.MvpSources().Single(s => s.Name == file).Json, sources.Single(s => s.Name == file).Json);
        Assert.Contains(want, Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }

    [Fact]
    public void TwoConcealmentBlocksAreRefused()
    {
        var sources = TestContent.MvpSources();
        sources.Add(sources.Single(s => s.Name == "concealment.json") with { Name = "extra/concealment.json" });
        Assert.Contains("concealment: already defined", Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }
}

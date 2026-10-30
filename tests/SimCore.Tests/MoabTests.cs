using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The massive bomb plane (an MC-130 type with one MOAB type bomb; the game repo's moab spec, owner 2026-10-28 to 30;
// docs/design.md, "Massive bomb plane"): a slow, long-range, unstealthy XL plane that flies a straight run to a ground point and
// releases one bomb over it; the bomb falls straight down (no parachute) for about 8 s while every enemy player is warned; its
// blast falls in three rings (a core that destroys everything, a heavy ring, a light ring fading out), collapses every tunnel
// within 40 cells with everyone inside, flattens the forest within 40 cells and starts fires. Shot down before the release,
// nothing falls.
public class MoabTests(ITestOutputHelper output)
{
    private const string Plane = "mc130";
    private const long Cell1 = Fixed.OneRaw;
    private const int M = 512; // raw per metre
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0, int index = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target, Index = index });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static long Dist(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static WeaponDef Bomb(Unit u) => u.Def.Weapons.Single(x => x.Effect.Blast is not null);

    // A flying plane of player 1 at (x, y), at its height already.
    private static Unit Fly(World w, int x, int y, int facing = Facing.East, byte player = 1)
    {
        var b = w.Spawn(player, Plane, x, y, facing);
        w.Step();
        return b;
    }

    private static TerrainMap Forest(int n, int density)
    {
        var flags = new byte[n * n];
        Array.Fill(flags, (byte)density);
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, Rules);
    }

    private sealed record Drop(BombRelease R, uint ReleaseTick, BombImpact I, uint ImpactTick);

    // Drops orders u onto `point` and flies until its bomb goes off (or max ticks): the release and the impact.
    private static Drop? DropOn(World w, Unit u, Cell point, int max = 6000, Action<World>? each = null)
    {
        Issue(w, CommandKind.AttackGround, u, point);
        BombRelease? rel = null;
        uint relTick = 0;
        for (var t = 0; t < max; t++)
        {
            w.Step();
            each?.Invoke(w);
            if (w.BombReleases.FirstOrDefault(r => r.Aircraft == u.Id) is { Bomb: not 0 } r)
            {
                (rel, relTick) = (r, w.Tick);
            }
            if (rel is { } got && w.BombImpacts.FirstOrDefault(i => i.Bomb == got.Bomb) is { Bomb: not 0 } imp)
            {
                return new Drop(got, relTick, imp, w.Tick);
            }
        }
        return null;
    }

    // ---- Content ----

    [Fact]
    public void TheMassiveBombPlaneIsData()
    {
        var c = TestContent.Mvp;
        var a = c.Archetypes.Single(x => x.Id == Plane);
        var w = Sentinel(64);
        var u = w.Spawn(1, Plane, 10, 10);
        Assert.Equal(AircraftClass.XL, a.Aircraft!.SizeClass);
        Assert.Equal(-1, a.Exclusive); // no limit on how many (owner)
        Assert.Equal(300 * M, a.Flight!.HeightRaw);
        Assert.Equal(110, (u.Def.Speed.Raw * World.TicksPerSecond * 10 + Cell1 / 2) / Cell1); // 11 cells/s: slower than every bomber
        Assert.Equal(48000, u.Def.Archetype.Lifetime); // 40 minutes of fuel (the gunship's 45 stays the longest)
        Assert.Equal(InterceptClass.Airlifter, a.Flight.Class);
        Assert.Equal(30, a.Countermeasures.Single().Salvos);
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        var bomb = Bomb(u);
        var e = bomb.Effect;
        Assert.Equal((3000, 1, true, "moab"), (e.Damage, e.Ammo, e.PointsOnly, e.Warhead.Id));
        Assert.Equal(1, u.StockMax(bomb.Stock));
        var rb = e.Blast!;
        Assert.Equal([150 * Cell1 / 10, 400 * Cell1 / 10, 750 * Cell1 / 10], rb.Rings.Select(r => r.RadiusRaw));
        Assert.Equal([true, false, false], rb.Rings.Select(r => r.Destroys));
        Assert.Equal((40 * Cell1, 40 * Cell1, 1), (rb.TunnelRadiusRaw, rb.FlattenRaw, rb.FlattenTo));
        Assert.Equal(75 * Cell1, e.Splash.Raw);
        var dl = e.Delivery!;
        Assert.True(dl.Straight && dl.Warn);
        Assert.Equal(1000, dl.Retard);
        Assert.Equal(300 * M, dl.ReleaseHeightRaw);
        // Every sensor sees it at full range: no signature block.
        Assert.Equal(Signature.Normal, a.Signature);
    }

    [Fact]
    public void TheHeavyHangarBuildsThemWithoutALimit()
    {
        var w = Sentinel(400);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        for (var i = 0; i < 2; i++)
        {
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [af.Hangar], Unit = Plane });
        }
        var built = new List<Unit>();
        for (var t = 0; t < 8000 && built.Count < 2; t++)
        {
            w.Step();
            built = [.. w.Units.Where(x => x.Def.Archetype.Id == Plane && x.Alive)];
        }
        output.WriteLine($"built {built.Count} by tick {w.Tick} ({w.Tick / World.TicksPerSecond} s)");
        Assert.Equal(2, built.Count); // no limit (owner): not in the heavy bombers' exclusive group
        Assert.All(built, u => Assert.Equal(1, u.Charges[Bomb(u).Stock]));
    }

    [Fact]
    public void RingPercentsFallByRing()
    {
        var rb = TestContent.Mvp.Component("moab_bomb")!.Effects.OfType<WeaponEffect>().Single().Blast!;
        Assert.Equal(100, rb.PercentAt(0));
        Assert.Equal(100, rb.PercentAt(15 * Cell1));
        Assert.Equal(40, rb.PercentAt(15 * Cell1 + 1));
        Assert.Equal(40, rb.PercentAt(40 * Cell1));
        Assert.Equal(10, rb.PercentAt(40 * Cell1 + 1));
        Assert.Equal(5, rb.PercentAt(57 * Cell1 + Cell1 / 2)); // halfway across the light ring
        Assert.Equal(0, rb.PercentAt(75 * Cell1));
        Assert.Equal(-1, rb.RingAt(75 * Cell1 + 1));
    }

    // ---- The drop ----

    [Fact]
    public void ItReleasesOverThePointAndTheBombFallsStraightDownInAbout8Seconds()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200);
        var point = new Cell(250, 205);
        var d = DropOn(w, b, point) ?? throw new Xunit.Sdk.XunitException("no drop");
        var fall = (d.ImpactTick - d.ReleaseTick) / (double)World.TicksPerSecond;
        var aside = Cells(Dist(d.R.At, d.I.At));
        var off = Cells(Dist(d.I.At, point.Center()));
        output.WriteLine($"released at {F(d.R.Altitude.Raw / (double)M)} m, fell {F(fall)} s, landed {F(aside)} cells from under the release, " +
            $"{F(off)} cells from the point; plane speed {F(Cells(World.Len3(d.R.VelX, d.R.VelY, 0)))} cells/s");
        Assert.InRange(d.R.Altitude.Raw, 280 * M, 320 * M);
        Assert.InRange(fall, 7.4, 8.4);
        Assert.True(aside < 0.1, "straight down: no forward throw");
        Assert.True(off < 1.0, "released over the point");
        Assert.Equal(0, b.Charges[Bomb(b).Stock]);
        Assert.Equal(7, d.I.Tier);
    }

    [Fact]
    public void EveryEnemyIsWarnedFromTheReleaseToTheImpact()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200);
        var point = new Cell(250, 200);
        var warned = new List<(uint Tick, IncomingStrike S)>();
        var events = new List<IncomingEvent>();
        var d = DropOn(w, b, point, each: x =>
        {
            warned.AddRange(x.IncomingStrikes.Select(s => (x.Tick, s)));
            events.AddRange(x.IncomingEvents);
        }) ?? throw new Xunit.Sdk.XunitException("no drop");
        Assert.NotEmpty(warned);
        Assert.Equal(d.ReleaseTick, warned[0].Tick); // from the release (never before)
        Assert.Equal(d.ImpactTick - 1, warned[^1].Tick); // every tick to the impact
        Assert.Equal((int)(d.ImpactTick - d.ReleaseTick), warned.Count);
        Assert.All(warned, x =>
        {
            Assert.Equal(d.R.Bomb, x.S.Projectile);
            Assert.Equal(1 << 2, (int)x.S.Warned); // player 2, not the bomber's own side
            Assert.Equal(75 * Cell1, x.S.Radius.Raw);
            Assert.True(Dist(x.S.At, d.I.At) < Cell1 / 4, "its point is where it lands");
        });
        Assert.Equal([IncomingEventKind.Warned, IncomingEventKind.Struck], events.Select(e => e.Kind));
        output.WriteLine($"warned {warned.Count} ticks ({F(warned.Count / 20.0)} s); predicted impact tick {warned[0].S.ImpactTick}, actual {d.ImpactTick}");
        Assert.InRange((long)d.ImpactTick - warned[0].S.ImpactTick, -2, 2);
    }

    [Fact]
    public void ShotDownBeforeTheReleaseNothingFalls()
    {
        var w = Sentinel(400);
        var target = w.Spawn(2, "Rifleman", 250, 200);
        var b = Fly(w, 40, 200);
        Issue(w, CommandKind.AttackGround, b, new Cell(250, 200));
        var released = 0;
        var impacts = 0;
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            if (b.Alive && Dist(b.Pos, new Cell(250, 200).Center()) < 90 * Cell1)
            {
                b.HP = 0; // brought down on its run in (as a missile's last hit would)
            }
            released += w.BombReleases.Count(r => r.Aircraft == b.Id);
            impacts += w.BombImpacts.Count(i => i.Aircraft == b.Id);
            Assert.Empty(w.IncomingStrikes);
        }
        output.WriteLine($"plane alive {b.Alive}, killed by {b.KilledBy}; releases {released}, impacts {impacts}");
        Assert.False(b.Alive);
        Assert.Equal(0, released);
        Assert.Equal(0, impacts);
        Assert.True(target.Alive && target.HP == target.Def.MaxHp, "nothing fell on the point");
    }

    // ---- The blast ----

    [Fact]
    public void DamageFallsByRingAndTargetClass()
    {
        var w = Sentinel(400);
        var point = new Cell(250, 200);
        // The Resistance's units (player 2) and, for the classes it has none of, the Sentinel's own (player 1: the blast hurts friends too).
        Unit At(string id, int dx, int dy = 0, byte player = 2) => w.Spawn(player, id, point.X + dx, point.Y + dy);
        // The core (to 15 cells): everything dies, whatever its armour, with whoever is inside.
        var coreTank = At("main_battle_tank", 6, 4, 1);
        coreTank.HP = 100_000;
        var coreBlock = At("block_panel_5", -10, -3);
        var coreGarrison = Enumerable.Range(0, 3).Select(i => At("Rifleman", -10 + i, -1)).ToList();
        foreach (var s in coreGarrison)
        {
            s.Garrison = coreBlock.Id;
        }
        var coreSoldier = At("Rifleman", 0, 8);
        // The heavy ring (15 to 40 cells: 1,200): soldiers, cars and trucks die, buildings wrecked, tanks badly hurt.
        var heavySoldier = At("Rifleman", 0, -25);
        var heavyCar = At("technical", 25, 6);
        var heavyTruck = At("humvee", -25, 10, 1);
        var heavyTank = At("main_battle_tank", 0, 30, 1);
        var villa = At("villa_walled", -30, -12);
        var villaGarrison = Enumerable.Range(0, 2).Select(i => At("Rifleman", -30 + i, -10)).ToList();
        foreach (var s in villaGarrison)
        {
            s.Garrison = villa.Id;
        }
        var garage = At("vehicle_garage", 30, -18);
        // The light ring (40 to 75 cells: 300 fading to 0): soldiers in the open hurt or killed, buildings damaged.
        var lightNear = At("Rifleman", 45, 0);
        var lightFar = At("Rifleman", -72, 0);
        var lightBlock = At("block_brick_4", 0, 60);
        var lightTank = At("main_battle_tank", 0, -55, 1);
        // Beyond 75 cells: nothing.
        var beyond = At("Rifleman", 0, 80);
        w.Step();
        var all = new List<Unit> { coreTank, coreBlock, coreSoldier, heavySoldier, heavyCar, heavyTruck, heavyTank, villa, garage, lightNear,
            lightFar, lightBlock, lightTank, beyond };
        foreach (var u in all)
        {
            u.Idle();
        }
        var b = Fly(w, 40, 200);
        var d = DropOn(w, b, point) ?? throw new Xunit.Sdk.XunitException("no drop");
        w.Step();
        string Hp(Unit u) => $"{u.Def.Archetype.Id} {Math.Max(0, u.HP)}/{u.Def.MaxHp} at {F(Cells(Dist(u.Pos, d.I.At)))}";
        foreach (var u in all)
        {
            output.WriteLine(Hp(u));
        }
        Assert.False(coreTank.Alive, "a tank with 100,000 HP in the core");
        Assert.False(coreBlock.Alive);
        Assert.All(coreGarrison, s => Assert.False(s.Alive));
        Assert.False(coreSoldier.Alive);
        Assert.False(heavySoldier.Alive);
        Assert.False(heavyCar.Alive);
        Assert.False(heavyTruck.Alive);
        Assert.True(heavyTank.Alive, "a tank lives through the heavy ring");
        Assert.True(heavyTank.HP < heavyTank.Def.MaxHp * 6 / 10, "badly hurt");
        Assert.False(villa.Alive, "a house in the heavy ring falls");
        Assert.All(villaGarrison, s => Assert.False(s.Alive, "its garrison dies when it falls"));
        Assert.True(garage.HP < garage.Def.MaxHp / 10, "a garage wrecked");
        Assert.False(lightNear.Alive, "a soldier in the open near the heavy ring");
        Assert.True(lightFar.Alive && lightFar.HP < lightFar.Def.MaxHp, "near the edge: hurt");
        Assert.True(lightBlock.Alive && lightBlock.HP < lightBlock.Def.MaxHp, "a building damaged");
        Assert.True(lightTank.Alive && lightTank.HP > lightTank.Def.MaxHp * 8 / 10, "a tank in the light ring: scratched");
        Assert.Equal(beyond.Def.MaxHp, beyond.HP);
    }

    [Fact]
    public void TunnelsCollapseWithin40CellsAndNotBeyond()
    {
        var w = Sentinel(400);
        var point = new Cell(250, 200);
        // One network: entrances 0, 20, 36, 47 and 60 cells off the point (each within the tunnels' link range of the next).
        var offsets = new[] { 0, 20, 36, 47, 60 };
        var entrances = offsets.Select(dx => w.Spawn(2, "tunnel_large", point.X + dx, point.Y)).ToList();
        w.Step();
        for (var i = 1; i < entrances.Count; i++)
        {
            Assert.True(World.TunnelLinked(entrances[i - 1], entrances[i]), $"entrances {i - 1} and {i} linked");
        }
        var below = entrances.Select(e =>
        {
            var us = Enumerable.Range(0, 2).Select(k => w.Spawn(2, "Rifleman", 20 + k, 300)).ToList();
            foreach (var u in us)
            {
                u.Idle();
                u.Inside = World.Underground;
                u.Tunnel = e.Id;
                u.Pos = e.Pos;
            }
            return us;
        }).ToList();
        // Two on the passage between the entrances at 36 and 47 cells: one at a place 39 cells from the point (inside 40), one at 41.5.
        Unit Walker(double cells)
        {
            var u = w.Spawn(2, "Rifleman", 20, 310);
            double da = Cells(Dist(entrances[2].Pos, point.Center())), db = Cells(Dist(entrances[3].Pos, point.Center()));
            const int leg = 10_000;
            u.Idle();
            u.Inside = World.Underground;
            u.Tunnel = entrances[2].Id;
            u.TunnelNext = entrances[3].Id;
            u.TunnelLeg = leg;
            u.TunnelLeft = leg - (int)(leg * (cells - da) / (db - da));
            u.Pos = entrances[2].Pos;
            return u;
        }
        var walker = Walker(39);
        var farWalker = Walker(41.5);
        w.Step();
        var b = Fly(w, 40, 200);
        var collapses = new List<TunnelCollapse>();
        var kills = new List<TunnelKill>();
        var d = DropOn(w, b, point, each: x =>
        {
            collapses.AddRange(x.TunnelCollapses);
            kills.AddRange(x.TunnelKills);
        }) ?? throw new Xunit.Sdk.XunitException("no drop");
        w.Run(2);
        output.WriteLine($"impact {F(Cells(Dist(d.I.At, point.Center())))} cells off; collapsed {collapses.Count}, killed underground {kills.Count}");
        for (var i = 0; i < entrances.Count; i++)
        {
            var e = entrances[i];
            output.WriteLine($"entrance {offsets[i]} cells: rubble {e.Rubble}, HP {e.HP}; below alive {below[i].Count(u => u.Alive)}");
        }
        for (var i = 0; i < 3; i++)
        {
            Assert.True(entrances[i].Rubble, $"the entrance {offsets[i]} cells off collapses");
            Assert.All(below[i], u => Assert.False(u.Alive, "everyone inside dies"));
        }
        Assert.False(walker.Alive, "on the passage 39 cells off");
        Assert.True(farWalker.Alive && farWalker.InTunnel, "on the passage 41.5 cells off");
        for (var i = 3; i < 5; i++)
        {
            Assert.False(entrances[i].Rubble, $"the entrance {offsets[i]} cells off stands");
            Assert.True(entrances[i].Alive);
            Assert.All(below[i], u => Assert.True(u.Alive && u.InTunnel, "underground beyond 40 cells: untouched"));
        }
        Assert.Equal(entrances.Take(3).Select(e => e.Id), collapses.Select(c => c.Entrance).Order());
        Assert.Equal(7, kills.Count);
    }

    [Fact]
    public void TheForestWithin40CellsIsFlattenedAndTheCoreBurns()
    {
        var w = Sentinel(400);
        w.AttachTerrain(Forest(400, 3));
        var g = w.Ground!;
        var point = new Cell(250, 200);
        var trunksBefore = CountTrunks(g, point, 38);
        var b = Fly(w, 40, 200);
        var d = DropOn(w, b, point) ?? throw new Xunit.Sdk.XunitException("no drop");
        var at = Cell.Of(d.I.At);
        var f = Assert.Single(w.Flattens);
        int flat = 0, standing = 0, burningCore = 0, burningOut = 0, untouchedFar = 0;
        for (var y = at.Y - 50; y <= at.Y + 50; y++)
        {
            for (var x = at.X - 50; x <= at.X + 50; x++)
            {
                var c = new Cell(x, y);
                var r = Cells(Dist(c.Center(), d.I.At));
                if (r <= 39)
                {
                    flat += g.Flattened(c) && g.Forest(c) <= 1 ? 1 : 0;
                    standing += g.HasTrunk(c) ? 1 : 0;
                    burningCore += r <= 15 && g.Burning(c) ? 1 : 0;
                }
                else if (r >= 41)
                {
                    untouchedFar += !g.Flattened(c) ? 1 : 0;
                    burningOut += g.Burning(c) ? 1 : 0;
                    Assert.False(g.Flattened(c), $"{c} is {F(r)} cells off");
                }
            }
        }
        output.WriteLine($"flattened {f.Cells} cells, felled {f.Felled} trunks (of {trunksBefore} within 38 cells); burning in the core {burningCore}, " +
            $"outside 40 cells {burningOut}; fires {w.Fires.Count}");
        Assert.Equal(0, standing);
        Assert.True(f.Felled >= trunksBefore);
        Assert.InRange(f.Cells, 4900, 5100); // pi x 40^2: about 5,027 cells
        Assert.True(flat >= 4700);
        Assert.True(burningCore > 50, "the core burns");
        Assert.True(w.Fires.Count <= w.FireRulesInPlay!.MaxBurning, "under the fire rules' cap");
        Assert.True(untouchedFar > 0);
    }

    private static int CountTrunks(TerrainMap g, Cell at, int r)
    {
        var n = 0;
        for (var y = at.Y - r; y <= at.Y + r; y++)
        {
            for (var x = at.X - r; x <= at.X + r; x++)
            {
                var c = new Cell(x, y);
                n += g.HasTrunk(c) && Cells(Dist(c.Center(), at.Center())) <= r ? 1 : 0;
            }
        }
        return n;
    }

    [Fact]
    public void AfterTheDropItFliesHome()
    {
        var w = Sentinel(400);
        var b = Fly(w, 40, 200);
        var d = DropOn(w, b, new Cell(250, 200)) ?? throw new Xunit.Sdk.XunitException("no drop");
        w.Run(40);
        output.WriteLine($"order after the drop: {b.Order.Kind}, home {b.HomeReason}");
        Assert.True(b.Alive);
        Assert.Equal(0, b.Charges[Bomb(b).Stock]);
        Assert.NotEqual(OrderKind.AttackGround, b.Order.Kind);
        Assert.True(d.R.Aircraft == b.Id);
    }

    // ---- Determinism ----

    private static List<ulong> Script(bool reversed)
    {
        var w = Sentinel(400, 7);
        w.AttachTerrain(Forest(400, 2));
        var a = w.Spawn(1, Plane, 40, 200, Facing.East);
        var c = w.Spawn(1, Plane, 360, 60, Facing.West);
        var e1 = w.Spawn(2, "tunnel_large", 250, 200);
        var e2 = w.Spawn(2, "tunnel_large", 270, 200);
        var soldiers = Enumerable.Range(0, 6).Select(i => w.Spawn(2, "Rifleman", 240 + 4 * i, 215)).ToList();
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackGround, Units = [a.Id], Cell = new Cell(252, 202) },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackGround, Units = [c.Id], Cell = new Cell(150, 120) },
        };
        foreach (var cmd in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(cmd);
        }
        var hashes = new List<ulong>();
        var drops = 0;
        for (var t = 0; t < 1800; t++)
        {
            w.Step();
            drops += w.BombImpacts.Count;
            hashes.Add(w.Hash());
            if (t % 300 == 299)
            {
                Assert.Equal(w.HashReference(), w.Hash());
            }
        }
        Assert.Equal(2, drops);
        Assert.True(e1.Rubble && e2.Rubble);
        Assert.All(soldiers, s => Assert.False(s.Alive));
        Assert.Equal(2, w.Flattens.Count);
        Assert.NotEmpty(w.Fires);
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
    }
}

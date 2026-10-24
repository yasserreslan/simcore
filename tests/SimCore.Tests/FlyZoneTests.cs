using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// E12: the height planner (FlightHeight.cs: a look-ahead clearance profile over terrain and structure
// tops, a bounded vertical acceleration, no bang-bang) and the fly zone (decisions D-FZ1 to D-FZ4: the
// map grown by flight.flyZoneMargin on every side, for every aircraft; the map's own clamp never used
// for one).
public class FlyZoneTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private const int Metre = 512;

    private static long Cells(long raw) => raw / Fixed.OneRaw;

    private static World Sentinel(int size, ulong seed = 1, bool fog = false) =>
        Arena.NewSentinel(size, seed, fog ? s => s with { Fog = true } : null);

    // A row of n town buildings along y = row from x0, pitch cells apart, each on a pad raised `mound`
    // metres over the flat ground round it (a two-cell blend): the narrow bumps a single far sample of
    // the ground passes before the aircraft does.
    private static List<Unit> Row(World w, int x0, int row, int n, int pitch, int mound)
    {
        var size = w.Grid.Width;
        var g = new TerrainMap(size, size, new int[(size + 1) * (size + 1)], new byte[size * size], Rules);
        w.AttachTerrain(g);
        string[] kinds = ["block_brick_4", "block_panel_5", "shop_row"];
        var row1 = new List<Unit>();
        for (var i = 0; i < n; i++)
        {
            var kind = kinds[i % kinds.Length];
            var a = w.Content.Archetypes.First(x => x.Id == kind);
            var c = new Cell(x0 + i * pitch, row - a.FootprintH / 2);
            g.SetPad(c, a.FootprintW, a.FootprintH, Fixed.FromRaw((long)mound * Metre), 2);
            row1.Add(w.Spawn(2, kind, c.X, c.Y));
        }
        return row1;
    }

    // Flight records what a test checks of an aircraft's vertical motion.
    private sealed class Flight
    {
        public long MaxAccel, Reversals, MinRoofClear = long.MaxValue, MaxVz;
        private int _sign;
        private long _vz;
        private bool _has;

        public void Add(World w, Unit u, List<Unit> roofs)
        {
            var vz = u.VelZ.Raw;
            if (_has)
            {
                MaxAccel = Math.Max(MaxAccel, Math.Abs(vz - _vz));
            }
            (_vz, _has) = (vz, true);
            MaxVz = Math.Max(MaxVz, Math.Abs(vz));
            var s = Math.Abs(vz) >= 16 ? Math.Sign(vz) : 0; // a reversal you would see: 0.6 m/s either way (settling's last raw units are not)
            if (s != 0)
            {
                if (_sign != 0 && s != _sign)
                {
                    Reversals++;
                }
                _sign = s;
            }
            var cell = Cell.Of(u.Pos);
            foreach (var b in roofs)
            {
                var o = World.FootprintOrigin(b);
                if (cell.X >= o.X && cell.Y >= o.Y && cell.X < o.X + World.FootW(b) && cell.Y < o.Y + World.FootH(b))
                {
                    var top = w.Ground!.HeightAt(b.Pos).Raw + b.Def.Archetype.HeightRaw;
                    MinRoofClear = Math.Min(MinRoofClear, u.Altitude.Raw - top);
                }
            }
        }
    }

    // ---- The height planner ----

    [Theory]
    [InlineData("Multirole 500 lb")]
    [InlineData("fighter_jet")]
    [InlineData("watcher_drone")]
    [InlineData("overseer_drone")]
    public void CrossingARowOfStructuresOnMoundsItClimbsOnceEarlyAndNeverBobs(string kind)
    {
        // Six buildings on 6 m mounds, 10 cells apart, across its path. Before E12 the height command
        // chased one sample of ground 2 s ahead at its full climb or dive rate: crossing this row the
        // multirole reversed its vertical speed 16 times (the fighter 20, the Watcher 7), its vertical
        // speed jumping by up to 996 raw a tick squared (about 80 g), and began to climb only past the
        // first mound. Now: one reversal (up, then down after the row), at most half its g (drones: a
        // second to full climb), climbing from about 10 cells before the first mound.
        var w = Sentinel(240);
        var row = Row(w, 100, 120, 6, 10, 6);
        var u = w.Spawn(1, kind, 30, 120, Facing.East);
        u.Lifetime = 100_000;
        // A patrol's first leg: straight at its first waypoint, across the row.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(225, 120) });
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(20, 120), Queue = true });
        w.Run(30);
        var f = new Flight();
        var climbStarted = -1;
        for (var t = 0; t < 1000 && u.Pos.X.Raw < 210 * Fixed.OneRaw; t++)
        {
            w.Step();
            f.Add(w, u, row);
            if (climbStarted < 0 && u.VelZ.Raw > 0)
            {
                climbStarted = (int)Cells(u.Pos.X.Raw);
            }
        }
        Assert.True(u.Pos.X.Raw >= 160 * Fixed.OneRaw, $"it crossed the row: at {Cells(u.Pos.X.Raw)}");
        var fl = u.Def.Archetype.Flight!;
        var acc = fl.Wing is { } g ? g.TurnAccel / 800 : fl.ClimbPerTick / World.TicksPerSecond;
        Assert.True(f.MaxAccel <= Math.Max(1, acc), $"vertical acceleration {f.MaxAccel} raw a tick squared (bound {acc})");
        Assert.True(f.Reversals <= 1, $"climbs over the row and comes down once: {f.Reversals} reversals of its vertical speed");
        Assert.InRange(climbStarted, 20, 99); // it starts climbing before the first mound (x 100)
        Assert.True(f.MinRoofClear >= fl.HeightRaw - 20 * Metre, $"over the roofs: {f.MinRoofClear / Metre} m");
        Assert.Equal(0, w.AirFloorClamps);
    }

    [Fact]
    public void StrafingBesideARowOfBuildingsItKeepsClearOfTheRoofs()
    {
        // Before E12 the strafing dive held only its 10 m floor over the ground: 5.4 m below a 16.5 m roof
        // (structures were not seen at all). Now the profile at its floor covers the roofs on its path.
        var w = Sentinel(240);
        var row = Row(w, 110, 120, 4, 9, 0);
        var target = w.SpawnCrewed(2, "technical", 140, 126).Vehicle;
        var jets = new List<Unit> { w.Spawn(1, "Multirole 500 lb", 50, 60, Facing.East), w.Spawn(1, "Multirole 500 lb", 50, 180, Facing.East) };
        foreach (var j in jets)
        {
            j.Lifetime = 100_000;
            foreach (var wd in j.Def.Weapons.Where(x => x.Effect.Delivery is not null && x.Stock >= 0))
            {
                j.MutableCharges[wd.Stock] = 0; // no bombs: gun passes (strafing dives to its floor)
            }
        }
        w.Run(5);
        w.Order(1, CommandKind.Attack, jets, target: target.Id);
        var fs = jets.Select(_ => new Flight()).ToList();
        long low = long.MaxValue;
        for (var t = 0; t < 3000; t++)
        {
            w.Step();
            foreach (var j in jets)
            {
                if (j.Strike is not null)
                {
                    low = Math.Min(low, j.Altitude.Raw - w.Ground!.HeightAt(j.Pos).Raw);
                }
            }
            for (var i = 0; i < jets.Count; i++)
            {
                fs[i].Add(w, jets[i], row);
            }
        }
        Assert.True(low <= 30 * Metre, $"they made their passes low: {low / Metre} m");
        Assert.Contains(fs, f => f.MinRoofClear < 40 * Metre); // and low over the roofs
        foreach (var f in fs)
        {
            Assert.True(f.MinRoofClear >= 5 * Metre, $"at least 5 m over any roof it crossed: {f.MinRoofClear / (double)Metre:0.0} m");
            Assert.True(f.MaxAccel <= 72, $"vertical acceleration bounded by its g: {f.MaxAccel}");
        }
        Assert.Equal(0, w.AirFloorClamps);
    }

    [Fact]
    public void TheClearanceGridFollowsStructuresComingAndGoing()
    {
        var w = Sentinel(96);
        var g = new TerrainMap(96, 96, new int[97 * 97], new byte[96 * 96], Rules);
        w.AttachTerrain(g);
        var d = w.Spawn(1, "watcher_drone", 20, 20);
        w.Step();
        var at = new Vec(Fixed.FromInt(50), Fixed.FromInt(50));
        Assert.Equal(0, w.ClearanceAt(at));
        var b = w.Spawn(2, "block_brick_4", 48, 48);
        w.Step();
        Assert.Equal(b.Def.Archetype.HeightRaw, w.ClearanceAt(at));
        b.HP = 0;
        w.Step();
        w.Step();
        Assert.Equal(0, w.ClearanceAt(at));
        g.SetPad(new Cell(48, 48), 4, 4, Fixed.FromInt(3), 0);
        w.Step();
        Assert.Equal(Fixed.FromInt(3).Raw, w.ClearanceAt(at));
        Assert.True(d.Alive);
    }

    // ---- The quick turn onto a fresh order (D-TURN1) ----

    [Theory]
    [InlineData("Multirole 500 lb", 39)]
    [InlineData("fighter_jet", 39)]
    public void AFreshOrderBehindItTurnsTheJetAroundFasterThanBefore(string jet, int before)
    {
        // Cruising east, ordered 120 cells west: before E12 the heading came within 10 degrees of west in
        // 39 ticks (1.95 s) for both jets; the quick turn (max g, idle and speed brakes down to its corner
        // speed, where it turns tightest) does it in 32 to 33 (1.6 s), never below its corner speed.
        var w = Sentinel(400);
        var u = w.Spawn(1, jet, 200, 200, Facing.East);
        u.Lifetime = 100_000;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(330, 200));
        w.Run(100);
        var g = u.Def.Archetype.Flight!.Wing!;
        w.Order(1, CommandKind.Move, [u], cell: new Cell((int)Cells(u.Pos.X.Raw) - 120, 200));
        var t = RunUntilWest(w, u, 400, out var slowest);
        Assert.True(t > 0 && t <= before - 5, $"{jet}: 180 degrees in {t} ticks (before E12: {before})");
        Assert.True(slowest >= g.Corner, $"never below its corner speed: {slowest}");
        w.Run(10);
        Assert.Equal(0, u.Wing!.QuickTurn); // over once it points at its aim, or after 2 s
        w.Run(200);
        Assert.InRange(u.Wing.Speed, g.Cruise * 95 / 100, g.Cruise * 105 / 100); // back to cruise after
    }

    [Fact]
    public void OnlyAPlayersOrderStartsAQuickTurnNotAGuardsOwnPick()
    {
        var w = Sentinel(200);
        var u = w.Spawn(1, "Multirole 500 lb", 100, 100, Facing.East);
        u.Lifetime = 100_000;
        w.Run(5);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Guard, Cell = new Cell(80, 80), Cell2 = new Cell(120, 120) });
        w.Step();
        Assert.True(u.Wing!.QuickTurn > 0, "the Guard order is the player's");
        w.Run(60);
        Assert.Equal(0, u.Wing.QuickTurn);
        var truck = w.SpawnCrewed(2, "technical", 110, 110).Vehicle;
        Assert.True(RunUntil(w, () => u.Order.Kind == OrderKind.Attack && u.Order.Target == truck.Id, 400) > 0, "Guard picks the truck");
        Assert.Equal(0, u.Wing.QuickTurn); // its own pick: no quick turn
        w.Order(1, CommandKind.Move, [u], cell: new Cell(30, 30));
        w.Step();
        Assert.True(u.Wing.QuickTurn > 0, "a Move is the player's");
    }

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    // RunUntilWest steps until u heads within 10 degrees of west; the ticks it took (-1: never) and its lowest speed.
    private static int RunUntilWest(World w, Unit u, int max, out long slowest)
    {
        slowest = long.MaxValue;
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            slowest = Math.Min(slowest, u.Wing!.Speed);
            if (u.Vel.X.Raw < 0 && Math.Abs(u.Vel.Y.Raw) * 1000 <= -u.Vel.X.Raw * 176)
            {
                return t;
            }
        }
        return -1;
    }

    // ---- The fly zone ----

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    [InlineData(1024)] // E13 (D-SPD1): faster transit on a big map, a wider fly zone
    public void RandomOrdersUseTheBandButNeverLeaveTheFlyZone(int size)
    {
        // Drones (the Watcher, the Overseer, the scout drone) and both jets under 10,000 ticks of
        // random orders (dashes too), with fog of war (off the map an air viewer sees its own disc,
        // clipped). They turn over the band past the map's edge, never past the fly zone, and the
        // last-resort clamp never fires: nothing ever clamps an aircraft to the map.
        string[] kinds = ["watcher_drone", "overseer_drone", "scout_drone", "Multirole 500 lb", "fighter_jet"];
        for (ulong seed = 1; seed <= (size > 512 ? 1UL : 3UL); seed++)
        {
            var w = Sentinel(size, seed, fog: true);
            var rng = new SimCore.Rng.Rand(seed * 7919);
            var air = new List<Unit>();
            foreach (var k in kinds)
            {
                for (var i = 0; i < 2; i++)
                {
                    var u = w.Spawn(k == "scout_drone" ? (byte)2 : (byte)1, k, rng.Intn(size), rng.Intn(size), rng.Intn(Facing.Count));
                    u.Lifetime = 100_000;
                    air.Add(u);
                }
            }
            long beyond = 0;
            for (var t = 0; t < (size > 512 ? 5000 : 10_000); t++)
            {
                if (t % 150 == 0)
                {
                    foreach (var u in air)
                    {
                        var c = new Cell(rng.Intn(size), rng.Intn(size));
                        var c2 = new Cell(rng.Intn(size), rng.Intn(size));
                        var cmd = new Command { Tick = w.Tick, Player = u.Owner, Units = [u.Id], Cell = c, Cell2 = c2 };
                        switch (rng.Intn(7))
                        {
                            case 0:
                                w.Order(u.Owner, CommandKind.Move, [u], cell: c);
                                break;
                            case 1:
                                w.Issue(cmd with { Kind = CommandKind.AirTask, Mission = AirTaskKind.Patrol, Cell2 = null, Queue = rng.Intn(2) == 0 });
                                break;
                            case 2:
                                w.Issue(cmd with { Kind = CommandKind.AirTask, Mission = AirTaskKind.Guard });
                                break;
                            case 3:
                                w.Issue(cmd with { Kind = CommandKind.AirTask, Mission = AirTaskKind.Search, On = true });
                                break;
                            case 4:
                                w.Issue(cmd with { Kind = CommandKind.AirTask, Mission = AirTaskKind.Roam });
                                break;
                            case 5:
                                if (u.Wing is { } st)
                                {
                                    st.Boost = 200; // a dash
                                }
                                break;
                            default:
                                w.Order(u.Owner, CommandKind.Stop, [u]);
                                break;
                        }
                    }
                }
                w.Step();
                foreach (var u in air)
                {
                    var m = w.FlyZoneMarginFor(u);
                    long x = u.Pos.X.Raw, y = u.Pos.Y.Raw, edge = (long)size * Fixed.OneRaw;
                    Assert.True(x >= -m && y >= -m && x < edge + m && y < edge + m,
                        $"seed {seed} tick {t}: {u.Def.Archetype.Id} at ({Cells(x)}, {Cells(y)}) past its fly zone ({Cells(m)} cells)");
                    if (x < 0 || y < 0 || x >= edge || y >= edge)
                    {
                        beyond++;
                    }
                }
            }
            Assert.True(beyond > 0, $"seed {seed}: some turns swing out over the band");
            Assert.Equal(0, w.AirEdgeClamps);
        }
    }

    [Fact]
    public void AnOrbitAtTheMapsEdgeSwingsOutOverTheBandAndComesBack()
    {
        // An orbit ordered at the edge keeps its centre on the map (D-FZ3) and flies half of it over
        // the band; it never sticks and never leaves the fly zone.
        var w = Sentinel(160);
        var d = w.Spawn(1, "watcher_drone", 0, 80, Facing.West);
        long minX = long.MaxValue, maxX = long.MinValue;
        for (var t = 0; t < 1200; t++)
        {
            w.Step();
            (minX, maxX) = (Math.Min(minX, d.Pos.X.Raw), Math.Max(maxX, d.Pos.X.Raw));
        }
        var r = d.Def.Archetype.Flight!.OrbitRadius.Raw;
        Assert.InRange(w.OrbitCentreOf(d).X.Raw, 0, Fixed.OneRaw); // on the map, at the edge
        Assert.True(minX < -r / 2, $"over the band: {Cells(minX)}");
        Assert.True(maxX > r / 2, $"and back over the map: {Cells(maxX)}");
        Assert.True(minX >= -World.FlyZoneMarginOf(d));
        Assert.Equal(0, w.AirEdgeClamps);
    }

    [Fact]
    public void OverTheBandAnAircraftSeesOnlyWhatItsDiscReaches()
    {
        // D-FZ4: off the map its sight is its own disc clipped to the map, not a disc at the edge.
        var w = Sentinel(96, fog: true);
        var d = w.Spawn(2, "scout_drone", 50, 50); // the Resistance's
        w.Step();
        var sight = d.Def.Vision.Raw;
        d.Pos = new Vec(Fixed.FromRaw(-sight / 2), Fixed.FromInt(50));
        d.Vel = new Vec(Fixed.FromRaw(-d.Def.Speed.Raw), Fixed.Zero);
        w.Run(2);
        Assert.True(d.Pos.X.Raw < 0, "still over the band");
        var reach = (int)Cells(sight / 2) - 1;
        var fog = w.FogOf(2)!;
        Assert.True(fog.Visible(Math.Max(0, reach - 4), 50), "the edge of the map under its disc");
        Assert.False(fog.Visible(reach + 6, 50), "nothing past its disc");
    }

    [Theory]
    [InlineData("archetypes/sentinel.json", "\"flyZoneMargin\": 500,", "\"flyZoneMargin\": 400,", "multirole_jet")]
    [InlineData("archetypes/sentinel.json", "\"relaunch\": true },", "\"relaunch\": true, \"flyZoneMargin\": 300 },", "watcher_drone")]
    public void TheLoaderRefusesAFlyZoneTooNarrowForTheTurn(string file, string from, string to, string who)
    {
        var sources = TestContent.MvpSources().Select(s => s.Name == file ? s with { Json = ReplaceFirst(s.Json, from, to) } : s).ToList();
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains("flyZoneMargin", e.Message, StringComparison.Ordinal);
        Assert.Contains(who, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlyZoneIs48CellsByDefaultAndWiderForTheJets()
    {
        long Zone(string id) => Cells(TestContent.Mvp.Archetypes.First(a => a.Id == id).Flight!.FlyZoneMarginRaw);
        Assert.Equal((48L, 48L, 48L, 50L, 55L), (Zone("watcher_drone"), Zone("overseer_drone"), Zone("scout_drone"), Zone("multirole_jet"), Zone("fighter_jet")));
        var w = Sentinel(64);
        // The widest: the stealth multirole's in its stealth mode (twice its 24-cell turn at its 26 cells/s cruise plus a second: about
        // 74 cells); before it, the strategic bomber's 65 (H4: twice its 23.5-cell turn at cruise plus a second).
        var f35 = w.Spawn(1, "stealth_multirole", 30, 30);
        Assert.Equal(w.FlightOf(f35).FlyZoneMarginRaw, w.FlyZoneMargin);
        Assert.InRange(w.FlyZoneMargin, 74 * Fixed.OneRaw, 75 * Fixed.OneRaw);
        Assert.Equal(65 * Fixed.OneRaw, TestContent.Mvp.Archetypes.First(a => a.Id == "strategic_bomber").Flight!.FlyZoneMarginRaw);
    }

    private static string ReplaceFirst(string s, string from, string to)
    {
        var i = s.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0, $"content has {from}");
        return string.Concat(s.AsSpan(0, i), to, s.AsSpan(i + from.Length));
    }
}

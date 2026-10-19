using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The airfield's runway craters and wear, milestone M3a (the game repo's docs/airfield-design.md,
// 1.1): blasts on a finished runway strip crater it by their weapon's runway field (small or large
// on impact) or wear its 2 x 2 cell patches until one craters; craters split the runway into open
// stretches; builders on a Repair order fill craters (nearest first) and take worn patches back to 0.
public class AirfieldCraterTests
{
    // At facing 0 a facility at (30, 40) puts the airfield's origin at (41, 16): the runway strip on
    // map y 16-26, x 41-101 (frame x 0-60), connectors at frame x 10, 30 and 50.
    private static readonly Cell FacilityCell = new(30, 40);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static (World W, Airfield Af) Field(ulong seed = 1, Cell? facility = null)
    {
        var w = Arena.NewSentinel(160, seed);
        var af = w.SpawnAirfield(Def(w, "airfield_facility"), facility ?? FacilityCell, 0, [(0, 0)]) ?? throw new InvalidOperationException("no airfield");
        return (w, af);
    }

    // At is the map point of the airfield frame's (x, y), in tenths of a cell.
    private static Vec At(Airfield af, int x10, int y10) => World.AirfieldPoint(af, x10, y10);

    private static void Blast(World w, Vec at, RunwayEffect? effect)
    {
        w.RunwayBlast(at, effect);
        w.Step();
    }

    private static RunwayEffect Small => new(CraterKind.Small, 0);

    private static RunwayEffect Wear(int n) => new(CraterKind.None, n);

    private static Fixed Cells(int tenths) => Fixed.Frac(tenths, 10);

    // ---- Each weapon's runway effect ----

    [Fact]
    public void EachWeaponDoesWhatItsRunwayFieldSays()
    {
        var checkedWeapons = 0;
        foreach (var comp in TestContent.Mvp.Components)
        {
            foreach (var e in comp.Effects)
            {
                var effect = e switch
                {
                    WeaponEffect x => x.Runway,
                    StrikeEffect x => x.Runway,
                    _ => null,
                };
                if (e is not (WeaponEffect or StrikeEffect))
                {
                    continue;
                }
                var (w, af) = Field();
                Blast(w, At(af, 300, 50), effect);
                checkedWeapons++;
                switch (effect)
                {
                    case null:
                        Assert.Empty(af.Craters); // small arms, the mortar, AA missiles, traps
                        Assert.Empty(af.Wear);
                        break;
                    case { Crater: CraterKind.Small }:
                        var s = Assert.Single(af.Craters);
                        Assert.Equal((CraterKind.Small, Cells(15), 300 * 100), (s.Kind, s.Radius, s.WorkTotal));
                        Assert.Empty(af.Wear);
                        break;
                    case { Crater: CraterKind.Large }:
                        var l = Assert.Single(af.Craters);
                        Assert.Equal((CraterKind.Large, Cells(30), 600 * 100), (l.Kind, l.Radius, l.WorkTotal));
                        break;
                    default:
                        Assert.Empty(af.Craters);
                        Assert.Equal(effect.Wear, (int)af.Wear.Max());
                        break;
                }
            }
        }
        Assert.True(checkedWeapons > 20);
    }

    [Fact]
    public void TheRealWeaponsKeepTheirTiers()
    {
        RunwayEffect? Of(string id) => TestContent.Mvp.Component(id)!.Effects.OfType<WeaponEffect>().Single().Runway;
        Assert.Equal(Small, Of("twin_missiles"));
        Assert.Equal(new RunwayEffect(CraterKind.Large, 0), Of("ballistic_missile"));
        Assert.Equal(Wear(4), Of("heavy_rockets"));
        Assert.Null(Of("mortar_tube"));
    }

    [Fact]
    public void OnlyTheRunwayStripOfAFinishedSegmentCraters()
    {
        var (w, af) = Field();
        Blast(w, At(af, 300, 130), Small);   // the grass strip
        Blast(w, At(af, 300, 200), Small);   // the parallel taxiway
        Blast(w, At(af, -50, 50), Small);    // beyond the west end
        Blast(w, At(af, 650, 50), Small);    // beyond the east end
        Blast(w, At(af, 100, 300), Small);   // a row
        Assert.Empty(af.Craters);
        Blast(w, At(af, 0, 0), Small);       // the strip's corner
        Blast(w, At(af, 599, 99), Small);    // the far corner
        Assert.Equal(2, af.Craters.Count);
        Assert.Equal(At(af, 0, 0), af.Craters[0].At);

        // A segment still being laid is not a runway yet.
        var w2 = Arena.NewSentinel(160);
        var worker = w2.Spawn(1, "worker", 28, 38);
        w2.Issue(new Command { Tick = w2.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = FacilityCell, Unit = "airfield_facility" });
        w2.Run(2);
        var af2 = Assert.Single(w2.Airfields);
        w2.Unit(af2.Facility)!.Construction = ConstructionState.Complete;
        w2.Issue(new Command { Tick = w2.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = World.AirfieldRowCell(af2, 0, 0), Unit = "airfield_row" });
        w2.Run(2);
        Assert.True(af2.HasRunway);
        Blast(w2, At(af2, 300, 50), Small);
        Assert.Empty(af2.Craters);
    }

    [Fact]
    public void TurnedAirfieldsCraterInTheirOwnFrame()
    {
        var w = Arena.NewSentinel(160);
        var af = w.SpawnAirfield(Def(w, "airfield_facility"), new Cell(105 - 34, 35 - 11), Facing.East, [(0, 0)]) ?? throw new InvalidOperationException();
        var at = At(af, 300, 50);
        Blast(w, at, Small);
        var c = Assert.Single(af.Craters);
        Assert.Equal((Cells(300), Cells(50), at), (c.X, c.Y, c.At));
        Blast(w, At(af, 300, 120), Small); // grass, turned
        Assert.Single(af.Craters);
    }

    // ---- Stretches ----

    [Fact]
    public void CratersSplitTheRunwayIntoOpenStretches()
    {
        var (w, af) = Field();
        Assert.Equal([new RunwayStretch(Cells(0), Cells(600))], af.OpenStretches());
        var watcher = TestContent.Mvp.Archetype("watcher_drone")!.Aircraft!;
        Assert.NotNull(af.LongestOpenStretch(watcher.MinLandRun));

        // A small crater at x 30 closes 3 cells plus 2 either side: x 26.5 to 33.5.
        Blast(w, At(af, 300, 50), Small);
        Assert.Equal([new RunwayStretch(Cells(0), Cells(265)), new RunwayStretch(Cells(335), Cells(600))], af.OpenStretches());
        Assert.Null(af.LongestOpenStretch(watcher.MinLandRun)); // 36 cells: closed for the Watcher
        Assert.Equal(new RunwayStretch(Cells(0), Cells(265)), af.LongestOpenStretch(watcher.MinTakeoffRun)); // ties: west first

        // A crater at x 5 leaves 0 to 1.5 (no connector) and 8.5 to 26.5.
        Blast(w, At(af, 50, 50), Small);
        Assert.Equal(3, af.OpenStretches().Count);
        Assert.Equal(new RunwayStretch(Cells(335), Cells(600)), af.LongestOpenStretch(Cells(10)));
        Assert.Null(af.LongestOpenStretch(Cells(300)));
    }

    [Fact]
    public void PastTheCapANewCraterEnlargesTheNearest()
    {
        var (w, af) = Field();
        for (var i = 0; i < 16; i++)
        {
            Blast(w, At(af, 20 + 36 * i, 50), Small);
        }
        Assert.Equal(16, af.Craters.Count);
        Assert.All(af.Craters, c => Assert.Equal(CraterKind.Small, c.Kind));
        Blast(w, At(af, 60, 50), Small); // nearest: the second (x 5.6)
        Assert.Equal(16, af.Craters.Count);
        var big = af.Craters.Single(c => c.Kind == CraterKind.Large);
        Assert.Equal((2u, Cells(30)), (big.Id, big.Radius));
        Assert.Equal(16u, af.NextCrater);
    }

    // ---- Wear ----

    [Fact]
    public void WearAddsUpAndCratersAtTwentyThenStartsOver()
    {
        var (w, af) = Field();
        Assert.Equal(5, af.WearAcross);
        var at = At(af, 301, 51); // patch (along 15, across 2)
        var i = 15 * 5 + 2;
        for (var hit = 1; hit <= 4; hit++)
        {
            Blast(w, at, Wear(4)); // separate volleys
            Assert.Equal(4 * hit, af.WearAt(i));
            Assert.Equal(hit, af.WearAt(i - 1)); // a quarter to each of the 4 neighbours
            Assert.Equal(hit, af.WearAt(i + 1));
            Assert.Equal(hit, af.WearAt(i - 5));
            Assert.Equal(hit, af.WearAt(i + 5));
            Assert.Equal(0, af.WearAt(i + 6)); // not the diagonals
        }
        Assert.Empty(af.Craters);
        Blast(w, at, Wear(4));
        var c = Assert.Single(af.Craters);
        Assert.Equal(CraterKind.Small, c.Kind);
        Assert.Equal(World.WearPatchCentre(af, i), c.At);
        Assert.Empty(af.Wear); // it and its neighbours start over: nothing worn is left

        // Medium and light rockets add nothing to the neighbours.
        Blast(w, At(af, 101, 51), Wear(3));
        Assert.Equal(3, af.Wear.Sum(v => v));
    }

    [Fact]
    public void TheWearArrayGrowsWithTheRunway()
    {
        var (w, af) = Field(facility: new Cell(50, 40)); // room for a west segment and its corridor
        Blast(w, At(af, 301, 51), Wear(2));
        Assert.Equal(3 * 10 * 5, af.Wear.Count);
        var worker = w.Spawn(1, "worker", 50, 20);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = World.AirfieldSegmentCell(af, -1), Unit = "airfield_runway" });
        w.Run(2);
        Assert.Equal(-1, af.West);
        Assert.Equal(4 * 10 * 5, af.Wear.Count);
        Assert.Equal(-1, af.WearWest);
        Assert.Equal(2, af.WearAt((15 + 10) * 5 + 2)); // the same patch, 10 patches further from the new west end
    }

    // ---- The engine's own scatter: one salvo closes the runway, stray rockets don't ----

    // Salvo fires `rockets` rockets of the rocket truck (30-tube) at the middle of the runway and
    // returns the airfield after they all landed.
    private static Airfield Salvo(ulong seed, int rockets)
    {
        var (w, af) = Field(seed);
        var (truck, _) = w.SpawnCrewed(2, "heavy_rocket_launcher", 71, 70);
        truck.MutableCharges[0] = rockets;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(71, 21)); // frame x 30.5, the centre line
        w.Run(50 + 30 * 10 + 200);
        Assert.Equal(0, truck.Charges[0]);
        return af;
    }

    [Fact]
    public void OneFullSalvoClosesTheRunway()
    {
        var watcher = TestContent.Mvp.Archetype("watcher_drone")!.Aircraft!;
        var oneOrTwo = 0;
        for (var seed = 1UL; seed <= 50; seed++)
        {
            var af = Salvo(seed, 30);
            var n = af.Craters.Count;
            if (n is 1 or 2)
            {
                oneOrTwo++;
            }
            if (n > 0)
            {
                Assert.Null(af.LongestOpenStretch(watcher.MinLandRun)); // no 36 cells left to land on
            }
        }
        Assert.True(oneOrTwo >= 45, $"1 or 2 craters in {oneOrTwo} of 50 salvos");
    }

    [Fact]
    public void AFewStrayRocketsDontCraterButTheirWearShows()
    {
        for (var seed = 1UL; seed <= 50; seed++)
        {
            var af = Salvo(seed, 5);
            Assert.Empty(af.Craters);
            Assert.NotEmpty(af.Wear); // hashed state the client draws as chips and scorch
        }
    }

    // ---- Repair ----

    [Fact]
    public void BuildersFillCratersNearestFirstThenWornPatches()
    {
        var (w, af) = Field();
        Blast(w, At(af, 100, 50), Small);  // near the worker
        Blast(w, At(af, 500, 50), Small);  // far
        Blast(w, At(af, 301, 51), Wear(4)); // a worn patch: 4, its neighbours 1
        var near = af.Craters[0];
        var far = af.Craters[1];
        var worker = w.Spawn(1, "worker", 51, 30); // on the grass by frame x 10
        var seg = af.Segments[1];
        w.Order(1, CommandKind.Repair, [worker], seg);
        w.Step();
        Assert.Equal(OrderKind.Repair, worker.Order.Kind);

        var t = RunUntil(w, () => near.Repaired);
        Assert.InRange(t, 300, 300 + 120); // 15 s of work, after a short walk
        Assert.False(far.Repaired);
        Assert.Equal(2, af.OpenStretches().Count);
        RunUntil(w, () => far.Repaired);
        Assert.Equal([new RunwayStretch(Cells(0), Cells(600))], af.OpenStretches());
        Assert.Equal(2, af.Craters.Count); // repaired patches stay for the client to draw

        // Then the worn patches: 2 wear a second (8 wear in all: 4 s of work, plus walking).
        var t2 = RunUntil(w, () => af.Wear.Count == 0);
        Assert.InRange(t2, 80, 80 + 400);
        w.Run(2);
        Assert.Equal(OrderKind.Idle, worker.Order.Kind); // nothing left
    }

    [Fact]
    public void HelpersOnOneCraterAddHalfThenNothing()
    {
        int Fill(int workers)
        {
            var (w, af) = Field();
            Blast(w, At(af, 300, 50), Small);
            var team = Enumerable.Range(0, workers).Select(i => w.Spawn(1, "worker", 70 + i % 2, 21 + i / 2)).ToList();
            w.Order(1, CommandKind.Repair, team, af.Segments[0]);
            return RunUntil(w, () => af.Craters[0].Repaired);
        }
        var one = Fill(1);
        Assert.InRange(one, 300, 305);
        Assert.InRange(Fill(2), 200, 205); // 150%
        Assert.InRange(Fill(3), 200, 205); // the third adds nothing
    }

    private static int RunUntil(World w, Func<bool> done, int max = 4000)
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

    // ---- Determinism ----

    [Fact]
    public void CratersAndWearAreDeterministicAndHashed()
    {
        ulong Run(out ulong before)
        {
            var (w, af) = Field(7);
            var (truck, _) = w.SpawnCrewed(2, "heavy_rocket_launcher", 71, 70);
            w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(71, 21));
            w.Run(250);
            before = w.Hash();
            w.Run(300);
            var worker = w.Spawn(1, "worker", 60, 30);
            w.Order(1, CommandKind.Repair, [worker], af.Segments[1]);
            w.Run(400);
            return w.Hash();
        }
        Assert.Equal(Run(out var a), Run(out var b));
        Assert.Equal(a, b);

        // Crater state is in the hash.
        var (w1, af1) = Field();
        var (w2, _) = Field();
        Blast(w1, At(af1, 300, 50), Small);
        w2.Step();
        Assert.NotEqual(w1.Hash(), w2.Hash());
    }
}

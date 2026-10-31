using SimCore.Bots;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Resistance barracks (docs/design.md, "Resistance barracks"): a worker builds it in 4 stages; it trains every Resistance
// foot unit and the worker, never a vehicle, one at a time out through its gate (the middle of the south side) to a rally point
// 10 m out; below 35% HP it stops and resumes when above again; 6 soldiers fire out of it with +1 cell of range (a 7th and a
// worker refused), and when it collapses they all get out with at most 40% of their HP.
public class BarracksTests
{
    private const string Id = "resistance_barracks";
    private const int Gate = 20; // the gate's doorTicks

    private static readonly ContentSet C = TestContent.Mvp;

    // A finished barracks of player 1 at (20, 10): x 20-35, y 10-21; its gate at x 28, the wall at y 22.
    private static (World W, Unit B) Yard(ulong seed = 1)
    {
        var w = Arena.New(64, seed);
        var b = w.Spawn(1, Id, 20, 10);
        return (w, b);
    }

    private static void Produce(World w, Unit b, string unit) =>
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [b.Id], Unit = unit });

    private static List<Unit> Made(World w, string arch) => [.. w.Units.Where(u => u.Def.Archetype.Id == arch && u.Alive)];

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    [Fact]
    public void ItsNumbersAndWhatItTrains()
    {
        var a = C.Archetype(Id)!;
        var w = Arena.New(8);
        var def = w.Player(1)!.Setup.Find(C, Id)!;
        Assert.Equal((16, 12, 2000, 800, 1000), (a.FootprintW, a.FootprintH, def.MaxHp, a.Cost, a.BuildTime)); // 32 x 24 m, 50 s
        Assert.Equal(Fixed.FromInt(8), def.Vision);
        Assert.Equal("building", C.ArmorClasses[def.Armor]);
        Assert.Equal([15, 40, 70, 95], a.Construction!.Stages);
        var pr = a.Production!;
        Assert.Equal((1, 10, 1, 35, Gate, ""), (pr.Bays, pr.QueueMax, pr.Doors, pr.MinHpPercent, pr.DoorTicks, pr.Crew));
        Assert.Equal([(80, 135)], pr.Exits); // the gate, the middle of the south side, 1.5 cells out
        Assert.Equal((80, 170), pr.Rally); // 10 m out of the wall
        var g = a.Garrison!;
        Assert.Equal((6, Fixed.FromInt(1), 40, 100, true), (g.Capacity, g.RangeBonus, g.CollapseHp, g.CollapseSurvival, g.Owned));
        Assert.False(World.CanTurn(a)); // a factory: laid out with its gate south

        // On its own line in the Resistance, not the Sentinel.
        var res = C.Factions.Single(f => f.Id == "resistance");
        Assert.True(res.Archetypes[a.Index]);
        Assert.False(C.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);

        // Every Resistance foot unit and the worker, nothing else of the faction.
        string[] foot = ["assault", "engineer", "anti_armor", "kornet", "manpads", "sniper", "mortar", "worker"];
        foreach (var x in C.Archetypes.Where(x => res.Archetypes[x.Index]))
        {
            Assert.True(pr.Builds(x.Tags) == foot.Contains(x.Id), x.Id);
        }
        Assert.All(foot, id => Assert.True(C.Archetype(id)!.BuildTime > 0, id)); // a training time each
        Assert.False(pr.Builds(C.Archetype("pilot")!.Tags)); // the Sentinel's pilot is never trained
        Assert.Null(w.Player(1)!.Setup.Find(C, "Sapper")!.Builder); // only the worker builds (the engineer builds no structures)
    }

    [Fact]
    public void AWorkerBuildsItThroughFourStages()
    {
        var w = Arena.New(64);
        var worker = w.Spawn(1, "worker", 19, 14);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(20, 10), Unit = Id });
        w.Step();
        var site = Assert.Single(w.All(Id));
        Assert.False(site.IsBuilt);
        Assert.InRange(site.HP, site.Def.MaxHp / 10, site.Def.MaxHp / 10 + 5); // a fresh site: 10%
        var seen = new List<int> { World.ConstructionStage(site) };
        var t = 0;
        for (; t < 1200 && !site.IsBuilt; t++)
        {
            var b = w.Unit(site.Id)!;
            if (World.ConstructionStage(b) != seen[^1])
            {
                seen.Add(World.ConstructionStage(b));
            }
            Produce(w, site, "Rifleman"); // a site trains nobody
            w.Step();
        }
        Assert.True(site.IsBuilt);
        Assert.InRange(t + 1, 1000, 1005); // 50 s with one worker beside it (the first tick counted)
        Assert.Equal([0, 1, 2, 3, 4], seen);
        Assert.Equal(site.Def.MaxHp, site.HP);
        Assert.Equal(1, site.Owner);
        Assert.Empty(Made(w, "assault"));
    }

    [Fact]
    public void ItTrainsARiflemanAWorkerAndAMortarAndRefusesAVehicle()
    {
        var (w, b) = Yard();
        foreach (var u in new[] { "Rifleman", "technical", "worker", "mortar", "cargo_truck" })
        {
            Produce(w, b, u);
        }
        w.Step();
        Assert.Equal("assault", w.Match.Defs[b.Bays[0].Def].Archetype.Id); // one at a time
        Assert.Equal(["worker", "mortar"], b.ProductionQueue.Select(d => w.Match.Defs[d].Archetype.Id)); // no vehicle queued
        w.Run(100 + Gate - 2);
        Assert.Empty(Made(w, "assault"));
        w.Run(1);
        var rifleman = Assert.Single(Made(w, "assault"));
        Assert.Equal(1, rifleman.Owner);
        Assert.Equal("Rifleman", rifleman.Def.Name);
        Assert.Equal(0u, rifleman.Inside); // nobody's crew: he walks
        w.Run(800);
        Assert.Single(Made(w, "worker"));
        Assert.Single(Made(w, "mortar"));
        Assert.Empty(Made(w, "technical"));
        Assert.Empty(Made(w, "cargo_truck"));
        Assert.Empty(b.ProductionQueue);
        foreach (var u in new[] { rifleman, Made(w, "worker")[0], Made(w, "mortar")[0] })
        {
            Assert.True(Within(u.Pos, new Cell(28, 27).Center(), 3), $"{u.Def.Archetype.Id} at {u.Pos}"); // gathered at the rally
        }
    }

    [Fact]
    public void ATrainedSoldierLeavesByTheGateAndWalksToTheRally()
    {
        var (w, b) = Yard();
        Produce(w, b, "Rifleman");
        w.Run(101);
        Assert.Equal(DoorState.Opening, b.Doors[0].State); // the gate slides open for him
        w.Run(Gate);
        var s = Assert.Single(Made(w, "assault"));
        Assert.True(Within(s.Pos, World.BayPoint(b, 0), 1)); // in the yard, behind the gate
        Assert.Equal(new Vec(Fixed.FromInt(28), Fixed.FromInt(19) + Fixed.FromRaw(Fixed.OneRaw / 2)), World.BayPoint(b, 0));
        Assert.Equal(b.Id, s.OutOf);
        Assert.Equal(new Cell(28, 27), s.Order.Cell);
        var exit = World.ExitPoint(b, 0);
        Assert.Equal(new Vec(Fixed.FromInt(28), Fixed.FromInt(23) + Fixed.FromRaw(Fixed.OneRaw / 2)), exit);
        var wall = 22L * Fixed.OneRaw;
        var passed = false;
        for (var t = 0; t < 200 && s.OutOf != 0; t++)
        {
            w.Step();
            if (s.Pos.Y.Raw < wall)
            {
                Assert.True(Math.Abs(s.Pos.X.Raw - exit.X.Raw) < Fixed.OneRaw, $"tick {t}: {s.Pos} off the gate's lane");
            }
            passed |= s.Pos.Y.Raw >= wall;
        }
        Assert.True(passed);
        Assert.Equal(0u, s.OutOf); // out: the barracks' footprint blocks him again
        w.Run(150);
        Assert.Equal(DoorState.Closed, b.Doors[0].State); // the gate shut behind him
        Assert.True(Within(s.Pos, new Cell(28, 27).Center(), 1));
    }

    [Fact]
    public void BelowThirtyFivePercentItStopsAndResumesAboveIt()
    {
        var (w, b) = Yard();
        Produce(w, b, "Rifleman");
        w.Step();
        b.HP = b.Def.MaxHp * 34 / 100;
        w.Run(400);
        Assert.Empty(Made(w, "assault"));
        b.HP = b.Def.MaxHp * 36 / 100; // repaired just above
        w.Run(100 + Gate + 5);
        Assert.Single(Made(w, "assault"));
    }

    [Fact]
    public void SixSoldiersFireOutWithOneMoreCellAndASeventhIsRefused()
    {
        var (w, b) = Yard();
        var men = Enumerable.Range(0, 7).Select(i => w.Spawn(1, "Rifleman", 24 + i, 26)).ToList();
        var worker = w.Spawn(1, "worker", 31, 27);
        w.Order(1, CommandKind.Enter, men, b.Id);
        w.Order(1, CommandKind.Enter, [worker], b.Id);
        w.Run(200);
        Assert.Equal(6, men.Count(u => u.Garrison == b.Id));
        var seventh = Assert.Single(men, u => u.Garrison == 0);
        Assert.Equal(0u, worker.Garrison); // soldiers only
        Assert.Equal(6, b.Occupants);
        Assert.Equal(1, b.Owner);
        Assert.All(men.Where(u => u.Garrison != 0), u => Assert.Equal(Fixed.FromInt(1), u.RangeBonus));
        w.Order(1, CommandKind.Move, [seventh, worker], cell: new Cell(5, 60)); // out of the way: only the garrison can fire

        // The rifle reaches 5 cells; from the windows 6, counted from the wall (y 22).
        w.Run(150);
        var far = w.Spawn(2, "Rifleman", 28, 28); // 6.5 cells out
        far.HP = 100_000;
        far.Stance = Stance.HoldFire;
        w.Run(100);
        Assert.Equal(100_000, far.HP);
        far.HP = 0;
        var near = w.Spawn(2, "Rifleman", 28, 27); // 5.5 cells out: only the bonus reaches him
        near.HP = 100_000;
        near.Stance = Stance.HoldFire;
        w.Run(100);
        Assert.True(near.HP < 100_000, "the garrison did not fire out");
    }

    // Its footprint blocks like any factory's (a box, a garrisonable one too): soldiers behind it walk round to its wicket doors.
    [Fact]
    public void SoldiersBehindItWalkRoundToItsDoors()
    {
        var (w, b) = Yard();
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 26 + i, 6)).ToList();
        w.Order(1, CommandKind.Enter, men, b.Id);
        var crossed = false;
        for (var t = 0; t < 600 && men.Any(u => u.Garrison == 0); t++)
        {
            w.Step();
            crossed |= men.Any(u => u.Garrison == 0 && u.Pos.X.Raw > 20L * Fixed.OneRaw + Fixed.OneRaw / 2 && u.Pos.X.Raw < 36L * Fixed.OneRaw - Fixed.OneRaw / 2 &&
                u.Pos.Y.Raw > 10L * Fixed.OneRaw + Fixed.OneRaw / 2 && u.Pos.Y.Raw < 22L * Fixed.OneRaw - Fixed.OneRaw / 2);
        }
        Assert.All(men, u => Assert.Equal(b.Id, u.Garrison));
        Assert.False(crossed, "walked through its footprint");
    }

    [Fact]
    public void WhenItCollapsesEveryoneGetsOutWithAtMostFortyPercent()
    {
        var (w, b) = Yard();
        var men = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 25 + i, 26)).ToList();
        men[5].HP = 10; // already badly hurt: keeps its 10
        w.Order(1, CommandKind.Enter, men, b.Id);
        w.Run(200);
        Assert.All(men, u => Assert.Equal(b.Id, u.Garrison));
        b.HP = 0;
        w.Run(2);
        Assert.DoesNotContain(w.All(Id), u => u.Alive);
        Assert.All(men, u => Assert.True(u.Alive && u.Garrison == 0));
        var max = men[0].Def.MaxHp;
        Assert.All(men.Take(5), u => Assert.Equal(max * 40 / 100, u.HP));
        Assert.Equal(10, men[5].HP);
    }

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var (w, b) = Yard();
            w.Spawn(1, "Rifleman", 28, 24); // in the gate's lane: steps aside
            foreach (var u in new[] { "Rifleman", "worker", "Tank hunter", "mortar", "kornet" })
            {
                Produce(w, b, u);
            }
            var hashes = new ulong[900];
            for (var i = 0; i < hashes.Length; i++)
            {
                w.Step();
                hashes[i] = w.Hash();
            }
            Assert.Equal(6, w.Units.Count(u => u.Def.Archetype.Tags.Matches(C.Archetype("worker")!.Tags) && u.Owner == 1 && u.Alive));
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }

    // A skirmish AI with a barracks at home (and the enemy's) plays on: it neither builds nor trains (no AI use yet).
    [Fact]
    public void TheSkirmishAiPlaysOnBesideABarracks()
    {
        var w = Arena.New(96);
        w.Spawn(2, Id, 4, 30);
        w.Spawn(2, "supply_depot", 24, 30);
        for (var i = 0; i < 8; i++)
        {
            w.Spawn(2, "Rifleman", 10 + i, 45);
        }
        w.Spawn(1, Id, 70, 70);
        w.Spawn(1, "Rifleman", 80, 90).Stance = Stance.HoldFire;
        var ai = new SkirmishAi(2);
        var cmds = new List<Command>();
        for (var t = 0; t < 600; t++)
        {
            foreach (var c in ai.Think(w))
            {
                cmds.Add(c);
                w.Issue(c);
            }
            w.Step();
        }
        Assert.NotEmpty(cmds);
        Assert.DoesNotContain(cmds, c => c.Kind is CommandKind.Produce or CommandKind.Build);
        Assert.All(w.All(Id), u => Assert.True(u.Alive));
    }
}

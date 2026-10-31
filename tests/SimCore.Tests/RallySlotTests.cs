using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Rally slots (docs/design.md, "Rally slots"; World.RallySlot): a unit sent to a factory's rally point (produced, serviced,
// airlifted in) takes its own free slot there instead of the flag's one cell, so units trained one after another fall in line
// instead of pressing into the ones that came first. The slots: lines of unitAi.rallyColumns (5) across the way from the
// factory to the flag, the flag's slot in the middle of the first; each next line farther from the factory; a body's width
// plus rallyGap apart in whole cells. Slots taken (a body standing there, or a unit walking to it), walls, water, cells it
// cannot reach and the factory's exit lanes are skipped.
public class RallySlotTests
{
    private const string Barracks = "resistance_barracks";
    private const string Garage = "vehicle_garage";

    // What a run of training to one rally point gave: every unit made (by ID), the slot it was sent to, whether any was ever
    // flagged stuck, the tick the last one stopped moving, and the positions 2 s before the end.
    private sealed record Run(World W, Unit Factory, List<Unit> Units, Dictionary<uint, Cell> Slots, int StuckTicks,
        Dictionary<uint, Vec> Before);

    // Train trains n of unit at factory f (keeping its queue fed), runs `ticks` ticks, and records the run.
    private static Run Train(World w, Unit f, string unit, int n, int ticks)
    {
        var issued = 0;
        var slots = new Dictionary<uint, Cell>();
        var stuck = 0;
        var before = new Dictionary<uint, Vec>();
        var there = Mine(w, f).Select(u => u.Id).ToList(); // already on the map: not sent
        for (var t = 0; t < ticks; t++)
        {
            if (issued < n && f.ProductionQueue.Count < 2)
            {
                w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [f.Id], Unit = unit });
                issued++;
            }
            w.Step();
            foreach (var u in Mine(w, f))
            {
                if (!slots.ContainsKey(u.Id) && !there.Contains(u.Id))
                {
                    Assert.Equal(OrderKind.Move, u.Order.Kind);
                    slots[u.Id] = u.Order.Cell;
                }
                stuck += u.Intent.Stuck ? 1 : 0;
            }
            if (t == ticks - 41)
            {
                foreach (var u in Mine(w, f))
                {
                    before[u.Id] = u.Pos;
                }
            }
        }
        return new Run(w, f, [.. Mine(w, f).Where(u => !there.Contains(u.Id))], slots, stuck, before);
    }

    private static List<Unit> Mine(World w, Unit f) =>
        [.. w.Units.Where(u => u.Owner == 1 && u != f && !u.Def.IsStructure && u.Inside == 0 && u.Alive).OrderBy(u => u.Id)];

    // Settled checks what every rally test wants: n units, all idle and at rest for the last 2 s, none ever pressing until
    // flagged stuck, each sent to its own slot, standing apart (no two bodies overlapping by more than a sliver), and all
    // within `within` cells of the flag.
    private static void Settled(Run r, int n, int within)
    {
        var flag = r.W.RallyCell(r.Factory);
        Assert.Equal(n, r.Units.Count);
        Assert.All(r.Units, u => Assert.Equal(OrderKind.Idle, u.Order.Kind));
        Assert.All(r.Units, u => Assert.Equal(r.Before[u.Id], u.Pos)); // nobody moved in the last 2 s: nobody pushing
        Assert.Equal(0, r.StuckTicks);
        Assert.Equal(n, r.Slots.Values.Distinct().Count());
        foreach (var a in r.Units)
        {
            Assert.True(Math.Max(Math.Abs(Cell.Of(a.Pos).X - flag.X), Math.Abs(Cell.Of(a.Pos).Y - flag.Y)) <= within, $"{a.Id} at {a.Pos}, flag {flag}");
            foreach (var b in r.Units.Where(b => b.Id > a.Id))
            {
                var touch = a.Def.Archetype.Radius.Raw + b.Def.Archetype.Radius.Raw;
                Assert.True((a.Pos - b.Pos).Len().Raw >= touch * 9 / 10, $"{a.Id} and {b.Id} overlap: {a.Pos} {b.Pos}");
            }
        }
    }

    [Fact]
    public void TwelveSoldiersFromTheBarracksFallInLineAtTheFlag()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, Barracks, 20, 10); // its gate at x 28 on the wall at y 22; the flag 5 cells out at (28, 27)
        var r = Train(w, b, "Rifleman", 12, 1800);
        Settled(r, 12, 3);
        Assert.Equal(new Cell(28, 27), w.RallyCell(b));
        // The first on the flag; then a block of 3 lines of 5 across the way out (west-east) round it: the line farthest from
        // the barracks first, each from the middle, then one each side in turn; then the flag's line (its (29, 27) taken: the
        // first man stands a little east of the flag's centre, nudged by those walking past him), then the one nearest the
        // gate.
        Assert.Equal(
            [new(28, 27), new(28, 28), new(27, 28), new(29, 28), new(26, 28), new(30, 28), new(27, 27), new(26, 27), new(30, 27),
             new(28, 26), new(27, 26), new(29, 26)],
            r.Units.Select(u => r.Slots[u.Id]).ToList<Cell>());
    }

    [Fact]
    public void EightTechnicalsFromTheGarageTakeSlotsTwoCellsApart()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, Garage, 20, 10); // its flag at (28, 26)
        var r = Train(w, g, "technical", 8, 1600);
        Settled(r, 8, 6);
        var slots = r.Slots.Values.ToList();
        foreach (var a in slots)
        {
            Assert.All(slots.Where(b => b != a), b => Assert.True(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) >= 2, $"{a} {b}"));
        }
        // The first on the flag, then 5 in the line past it, then 2 more in the flag's own line.
        var flag = w.RallyCell(g);
        Assert.Equal(flag, r.Slots[r.Units[0].Id]);
        Assert.Equal(5, slots.Count(c => c.Y == flag.Y + 2));
        Assert.Equal(3, slots.Count(c => c.Y == flag.Y));
        Assert.All(slots, c => Assert.InRange(c.X, flag.X - 4, flag.X + 4));
    }

    [Fact]
    public void AFlagAgainstAWallAndWaterGivesSlotsOnlyOnOpenGround()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, Barracks, 20, 10);
        var flag = new Cell(28, 30);
        // A rock wall right past the flag (the next lines would be in it) and a pond west of it.
        for (var x = 18; x <= 40; x++)
        {
            w.Grid.Set(new Cell(x, 31), Terrain.Blocked);
        }
        for (var y = 26; y <= 30; y++)
        {
            for (var x = 22; x <= 26; x++)
            {
                w.Grid.Set(new Cell(x, y), Terrain.Water);
            }
        }
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [b.Id], Cell = flag });
        var r = Train(w, b, "Rifleman", 12, 1800);
        Settled(r, 12, 5);
        Assert.All(r.Slots.Values, c => Assert.Equal(Terrain.Clear, w.Grid.At(c)));
        Assert.All(r.Units, u => Assert.Equal(Terrain.Clear, w.Grid.At(Cell.Of(u.Pos))));
    }

    [Fact]
    public void AFlagOnABuildingsFrontKeepsThemOffIt()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, Barracks, 20, 10);
        var depot = w.Spawn(1, "supply_depot", 26, 26); // the rally on it: its front's middle, a cell out
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [b.Id], Target = depot.Id });
        var r = Train(w, b, "Rifleman", 10, 1600);
        Settled(r, 10, 5);
        var o = World.FootprintOrigin(depot);
        Assert.All(r.Units, u => Assert.False(
            Cell.Of(u.Pos).X >= o.X && Cell.Of(u.Pos).X < o.X + depot.Def.Archetype.FootprintW &&
            Cell.Of(u.Pos).Y >= o.Y && Cell.Of(u.Pos).Y < o.Y + depot.Def.Archetype.FootprintH, $"{u.Id} at {u.Pos} on the depot"));
    }

    [Fact]
    public void AFlagOnACellNobodyCanReachSendsThemBesideIt()
    {
        var w = Arena.New(64);
        var g = w.Spawn(1, Garage, 20, 10);
        var flag = new Cell(30, 34);
        // A ring of rock round the flag: the cell inside is open ground no unit can reach.
        for (var d = -1; d <= 1; d++)
        {
            w.Grid.Set(new Cell(flag.X + d, flag.Y - 1), Terrain.Blocked);
            w.Grid.Set(new Cell(flag.X + d, flag.Y + 1), Terrain.Blocked);
            w.Grid.Set(new Cell(flag.X - 1, flag.Y + d), Terrain.Blocked);
            w.Grid.Set(new Cell(flag.X + 1, flag.Y + d), Terrain.Blocked);
        }
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetRally, Units = [g.Id], Cell = flag });
        var r = Train(w, g, "dirt_bike", 4, 1200);
        Settled(r, 4, 6);
        Assert.DoesNotContain(flag, r.Slots.Values);
    }

    [Fact]
    public void ASlotTakenByAStandingSoldierIsSkipped()
    {
        var w = Arena.New(64);
        var b = w.Spawn(1, Barracks, 20, 10);
        var guard = w.Spawn(1, "Rifleman", 28, 28); // standing on the first slot, a cell past the flag
        var r = Train(w, b, "Rifleman", 2, 600);
        Assert.Equal([new(27, 28), new(29, 28)], r.Units.Select(u => r.Slots[u.Id]).ToList<Cell>());
        Assert.Equal(new Cell(28, 28), Cell.Of(guard.Pos)); // nobody pushed him off it
    }

    [Fact]
    public void TheSameRunGivesTheSameWorldEveryTimeAndInEitherMovementOrder()
    {
        static World Build(bool reverse)
        {
            var w = Arena.New(64, seed: 5);
            w.ReverseMoveOrder = reverse;
            w.Spawn(1, Barracks, 20, 10);
            w.Spawn(1, Garage, 40, 10);
            return w;
        }
        var a = Build(false);
        var b = Build(true);
        foreach (var w in new[] { a, b })
        {
            foreach (var f in w.Units.Where(u => u.Def.Archetype.Production is not null).ToList())
            {
                for (var i = 0; i < 6; i++)
                {
                    w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [f.Id], Unit = f.Def.Archetype.Id == Barracks ? "Rifleman" : "technical" });
                }
            }
        }
        for (var t = 0; t < 1200; t++)
        {
            a.Step();
            b.Step();
            Assert.Equal(a.Hash(), b.Hash());
        }
        Assert.Equal(12, a.Units.Count(u => u.Owner == 1 && !u.Def.IsStructure && u.Inside == 0));
    }
}

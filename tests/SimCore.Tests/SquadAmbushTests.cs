using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Infantry squads, phase S3 (docs/design.md, "Squads: Ambush"): the Ambush order and its zone-length refusals, the side picked,
// each role's position round the zone, garrisons, the mines laid from the soldiers' own stock (anti-tank on the road from the far
// end back, anti-personnel off the far side), hold fire until it springs, each of the triggers, no chasing, springing again, a
// column driving into a set trap, determinism and replays.
public class SquadAmbushTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // The zone: west to east along the road, 24 cells. The right side (1) is south, the left (-1) north.
    private static readonly Cell Start = new(16, 30);
    private static readonly Cell End = new(40, 30);

    private static void Ambush(World w, byte player, IEnumerable<Unit> units, Cell a, Cell? b) =>
        w.Issue(new Command
        {
            Tick = w.Tick, Player = player, Kind = CommandKind.SetSquadMode, Units = [.. units.Select(u => u.Id)], Index = (int)SquadMode.Ambush,
            Cell = a, Cell2 = b,
        });

    private static void Fire(World w, Unit member) =>
        w.Issue(new Command { Tick = w.Tick, Player = member.Owner, Kind = CommandKind.SpringAmbush, Units = [member.Id] });

    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        w.Issue(new Command { Tick = w.Tick, Player = list[0].Owner, Kind = CommandKind.FormSquad, Units = [.. list.Select(u => u.Id)] });
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    // RunUntil steps until the squad's state is `state` (at most limit ticks).
    private static void RunUntil(World w, Squad s, SquadState state, int limit = 1500)
    {
        for (var t = 0; t < limit && s.State != state; t++)
        {
            w.Run(1);
        }
        Assert.Equal(state, s.State);
    }

    private static void Placed(World w, Squad s, int limit = 200)
    {
        for (var t = 0; t < limit && !s.HoldPlaced; t++)
        {
            w.Run(1);
        }
        Assert.True(s.HoldPlaced, "it picked its positions");
    }

    // RoadMap: 64 x 64 of flat terrain with a paved road two cells wide along rows 30 and 31; woods: dense forest north of it (-1:
    // rows 21 to 27, columns 14 to 42), south of it (1: rows 34 to 40) or none (0).
    private static World RoadMap(int woods, bool sentinel = false, bool fog = false)
    {
        const int size = 64;
        Func<MatchSetup, MatchSetup>? edit = fog ? s => s with { Fog = true } : null;
        var w = sentinel ? Arena.NewSentinel(size, edit: edit) : Arena.New(size, edit: edit);
        var corners = new int[(size + 1) * (size + 1)];
        var flags = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var wood = x is >= 14 and <= 42 && ((woods < 0 && y is >= 21 and <= 27) || (woods > 0 && y is >= 34 and <= 40));
                flags[y * size + x] = y is 30 or 31 ? TerrainMap.WithRoad(0, RoadKind.Paved) : (byte)(wood ? 3 : 0);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, flags, Rules));
        return w;
    }

    // Lateral is how far a cell lies off the zone's line (row 30's centre line... the zone runs along the centres of row 30), in cells.
    private static double Lateral(Cell c) => Math.Abs(c.Y - Start.Y);

    private static Cell PickOf(World w, Unit u) => w.SquadHoldPick(u.Id)!.Value.Cell;

    private static string Dump(World w, Squad s) =>
        string.Join("; ", s.HoldPicks.Select(p => $"{w.SquadRoleOf(w.Unit(p.Unit)!)} {p.Unit} {p.Cell}{(p.Building != 0 ? " in " + p.Building : "")}"));

    // The squad scores both sides of the zone and takes the one with the woods (cover and concealment); everyone stays on it.
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void TheSideWithCoverIsTaken(int woods)
    {
        var w = RoadMap(woods);
        var men = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 26, 46), w.Spawn(1, "Rifleman", 27, 46), w.Spawn(1, "Rifleman", 28, 46), w.Spawn(1, "Tank hunter", 29, 46),
            w.Spawn(1, "Marksman", 30, 46),
        };
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        w.Run(1);
        Assert.Equal(SquadMode.Ambush, s.Mode);
        Assert.Contains(w.SquadEvents, e => e.Kind == SquadEventKind.Setting && e.Squad == s.Id);
        Placed(w, s);
        Assert.Equal(woods, s.AmbushSide);
        Assert.All(s.HoldPicks, p => Assert.True(woods < 0 ? p.Cell.Y < 30 : p.Cell.Y > 31, $"all on one side: {Dump(w, s)}"));
        Assert.Equal(Start, s.AmbushStart);
        Assert.Equal(End, s.AmbushEnd);
    }

    private static List<Unit> FullSquad(World w, int x, int y) =>
    [
        w.Spawn(1, "Rifleman", x, y), w.Spawn(1, "Rifleman", x + 1, y), w.Spawn(1, "Rifleman", x + 2, y), w.Spawn(1, "Tank hunter", x + 3, y),
        w.Spawn(1, "Tank hunter", x + 4, y), w.Spawn(1, "kornet", x, y + 1), w.Spawn(1, "Marksman", x + 1, y + 1), w.Spawn(1, "Sapper", x + 2, y + 1),
        w.Spawn(1, "mortar", x + 3, y + 1), w.Spawn(1, "manpads", x + 4, y + 1),
    ];

    // Each role takes its place: the line 3 to 8 cells from the zone, the RPGs at its two ends, the Kornet toward the far end
    // looking down the road, the sniper farther back toward the approach, the mortar 8 or more cells back and set up.
    [Fact]
    public void EachRoleTakesItsPlace()
    {
        var w = RoadMap(-1);
        var men = FullSquad(w, 26, 14);
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        Placed(w, s);
        var dump = Dump(w, s);
        Assert.Equal(-1, s.AmbushSide);
        var line = new[] { men[0], men[1], men[2], men[7] };
        Assert.All(line, u => Assert.InRange(Lateral(PickOf(w, u)), 3, 8));
        var rpgs = new[] { PickOf(w, men[3]), PickOf(w, men[4]) };
        Assert.True(rpgs.Count(c => c.X >= End.X - 6) == 1 && rpgs.Count(c => c.X <= Start.X + 6) == 1, $"RPGs at the two ends: {dump}");
        Assert.True(PickOf(w, men[5]).X > (Start.X + End.X) / 2, $"the Kornet toward the far end: {dump}");
        var lineLat = line.Average(u => Lateral(PickOf(w, u)));
        Assert.True(Lateral(PickOf(w, men[6])) >= lineLat + 2.5, $"the sniper farther back: {dump}");
        Assert.True(PickOf(w, men[6]).X < (Start.X + End.X) / 2, $"the sniper toward the approach: {dump}");
        Assert.True(Lateral(PickOf(w, men[8])) >= 8, $"the mortar 8 cells or more back (its minimum range): {dump}");
        RunUntil(w, s, SquadState.Ready);
        Assert.Equal(DeployState.Deployed, men[8].Deploy);
        Assert.All(men, u => Assert.Equal(Stance.HoldFire, u.Stance));
        foreach (var u in men)
        {
            Assert.True(World.Within(u.Pos, PickOf(w, u).Center(), Fixed.One), $"{u.Id} at {u.Pos}, his position {PickOf(w, u)}");
        }
        Assert.Equal(Stance.Guard, s.Stance);
    }

    // The Sentinel's medic stands behind the line, out of sight of the zone (beyond the woods).
    [Fact]
    public void TheMedicStandsOutOfSightOfTheZone()
    {
        var w = RoadMap(-1, sentinel: true);
        var men = new List<Unit>
        {
            w.Spawn(1, "sentinel_medic", 26, 14), w.Spawn(1, "Rifleman", 27, 14), w.Spawn(1, "Rifleman", 28, 14), w.Spawn(1, "Rifleman", 29, 14),
        };
        var s = Form(w, men);
        Ambush(w, 1, [men[1]], Start, End);
        Placed(w, s);
        var dump = Dump(w, s);
        var medic = PickOf(w, men[0]);
        var lineLat = men.Skip(1).Average(u => Lateral(PickOf(w, u)));
        Assert.True(Lateral(medic) >= lineLat + 2, $"behind the line: {dump}");
        // Out of sight: two rows or more of the dense woods stand between him and the zone.
        Assert.True(medic.Y <= 25 && w.Ground!.Forest(medic) == 3, $"deep in the woods: {dump}");
    }

    // A garrisonable building on the side in reach of the zone is a position: riflemen, RPGs and the sniper go in; the Igla and the
    // mortar stay out.
    [Fact]
    public void GarrisonsAreUsed()
    {
        var w = RoadMap(0);
        var house = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(28, 21), Facing.South))!; // 5 x 5: rows 21 to 25
        var men = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 26, 14), w.Spawn(1, "Rifleman", 27, 14), w.Spawn(1, "Rifleman", 28, 14), w.Spawn(1, "Tank hunter", 29, 14),
            w.Spawn(1, "Marksman", 30, 14), w.Spawn(1, "manpads", 26, 15), w.Spawn(1, "mortar", 27, 15),
        };
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        Placed(w, s);
        var dump = Dump(w, s);
        Assert.Equal(-1, s.AmbushSide); // the house's side
        var inHouse = s.HoldPicks.Where(p => p.Building == house.Id).Select(p => w.Unit(p.Unit)!).ToList();
        Assert.True(inHouse.Count >= 1, $"someone picked the house: {dump}");
        Assert.All(inHouse, u => Assert.Contains(w.SquadRoleOf(u), new[] { SquadRole.Line, SquadRole.Flank, SquadRole.Recon }));
        Assert.Equal(0u, w.SquadHoldPick(men[5].Id)!.Value.Building);
        Assert.Equal(0u, w.SquadHoldPick(men[6].Id)!.Value.Building);
        RunUntil(w, s, SquadState.Ready);
        Assert.All(inHouse, u => Assert.True(w.SquadHoldGarrisoned(u.Id), $"{u.Id} inside"));
    }

    // The line lays mines from its own stock: anti-tank mines on the road from the far end back, anti-personnel mines off the far
    // side, the engineer his one booby trap; a rifleman with nothing left lays nothing and just takes his spot.
    [Fact]
    public void MinesAreLaidFromTheirOwnStock()
    {
        var w = RoadMap(-1);
        var men = new List<Unit>
        {
            w.Spawn(1, "Rifleman", 26, 14), w.Spawn(1, "Rifleman", 27, 14), w.Spawn(1, "Rifleman", 28, 14), w.Spawn(1, "Sapper", 29, 14),
            w.Spawn(1, "Tank hunter", 30, 14),
        };
        var empty = men[2];
        for (var i = 0; i < empty.MutableCharges.Length; i++)
        {
            empty.MutableCharges[i] = 0;
        }
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        Placed(w, s);
        Assert.Equal(-1, s.AmbushSide);
        var tasks = s.AmbushTasks;
        Assert.Equal(4, tasks.Count(t => t.Kind == AmbushMineKind.AntiTank));
        Assert.Equal(8, tasks.Count(t => t.Kind == AmbushMineKind.AntiPersonnel));
        Assert.Equal(1, tasks.Count(t => t.Kind == AmbushMineKind.Trap));
        Assert.DoesNotContain(tasks, t => t.Unit == empty.Id || t.Unit == men[4].Id); // the RPG lays none (only the line)
        Assert.All(tasks.Where(t => t.Kind == AmbushMineKind.AntiTank), t => Assert.True(w.Ground!.Road(t.Cell), $"AT on the road: {t.Cell}"));
        Assert.All(tasks.Where(t => t.Kind != AmbushMineKind.AntiTank), t => Assert.True(t.Cell.Y > 31, $"AP off the far side: {t.Cell}"));
        // The far end first: each layer's anti-tank mines run from the far end back, the first dealt at the far end.
        var at = tasks.Where(t => t.Kind == AmbushMineKind.AntiTank).ToList();
        Assert.True(at.Max(t => t.Cell.X) >= End.X - 1, $"one at the far end: {string.Join(", ", at)}");
        foreach (var g in at.GroupBy(t => t.Unit))
        {
            var xs = g.Select(t => t.Cell.X).ToList();
            Assert.Equal(xs.OrderByDescending(x => x), xs);
        }
        var events = 0;
        for (var t = 0; t < 1500 && s.State != SquadState.Ready; t++)
        {
            w.Run(1);
            events += w.SquadEvents.Count(e => e.Kind == SquadEventKind.MineLaid && e.Squad == s.Id);
        }
        Assert.Equal(SquadState.Ready, s.State);
        Assert.Equal(13, events);
        var laid = w.SquadAmbushMines(s.Id);
        Assert.Equal(13, laid.Count);
        Assert.Equal(13, s.AmbushMines.Count);
        Assert.Equal(4, laid.Count(m => m.Kind == AmbushMineKind.AntiTank));
        Assert.All(laid.Where(m => m.Kind == AmbushMineKind.AntiTank), m => Assert.True(w.Ground!.Road(m.Cell), $"AT mine on the road: {m.Cell}"));
        Assert.All(tasks, t => Assert.NotEqual(AmbushTaskState.Pending, t.State));
        foreach (var u in new[] { men[0], men[1] })
        {
            Assert.All(u.Def.Abilities, ab => Assert.Equal(0, u.Charges[ab.Stock])); // his whole stock laid
        }
        var trapKit = men[3].Def.Abilities.Single(ab => ab.Effect is PlacerEffect p && p.Archetype == w.Content.Squad!.Ambush!.Trap);
        Assert.Equal(2, men[3].Charges[trapKit.Stock]); // one booby trap of three
        Assert.True(World.Within(empty.Pos, PickOf(w, empty).Center(), Fixed.One), "the one with none left took his spot");
    }

    // A squad of two snipers on a short zone: they stand back near its start, out of the enemy's sight (7 or 8 cells) but within
    // theirs (14).
    private static (World W, Squad S, List<Unit> Men) Snipers(bool riflemen = false)
    {
        var w = RoadMap(0);
        var men = riflemen
            ? new List<Unit> { w.Spawn(1, "Rifleman", 22, 44), w.Spawn(1, "Rifleman", 23, 44), w.Spawn(1, "Rifleman", 24, 44) }
            : new List<Unit> { w.Spawn(1, "Marksman", 22, 44), w.Spawn(1, "Marksman", 23, 44) };
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], new Cell(20, 30), new Cell(30, 30));
        w.Run(1);
        RunUntil(w, s, SquadState.Ready);
        return (w, s, men);
    }

    // Each trigger springs a set ambush, and the trigger is reported; everyone takes the squad's stance.
    [Theory]
    [InlineData(AmbushTrigger.FarEnd)]
    [InlineData(AmbushTrigger.Crowd)]
    [InlineData(AmbushTrigger.Mine)]
    [InlineData(AmbushTrigger.Spotted)]
    [InlineData(AmbushTrigger.Hurt)]
    [InlineData(AmbushTrigger.Order)]
    public void EachTriggerSpringsIt(AmbushTrigger trigger)
    {
        var (w, s, men) = Snipers();
        var dump = Dump(w, s);
        w.Run(40);
        Assert.Equal(SquadState.Ready, s.State); // nothing has happened
        switch (trigger)
        {
            case AmbushTrigger.FarEnd:
                w.SpawnCrewed(2, "technical", 29, 30, Facing.East);
                break;
            case AmbushTrigger.Crowd:
                w.Spawn(2, "Rifleman", 27, 30);
                w.Spawn(2, "Rifleman", 28, 30);
                w.Spawn(2, "Rifleman", 29, 31);
                break;
            case AmbushTrigger.Mine:
                var mine = w.Spawn(1, "ap_mine", 25, 30);
                w.Order(1, CommandKind.Detonate, [mine]);
                break;
            case AmbushTrigger.Spotted:
                var at = PickOf(w, men[0]);
                w.Spawn(2, "Rifleman", at.X, at.Y + 4);
                break;
            case AmbushTrigger.Hurt:
                w.Hurt(men[1], w.Content.Warhead("explosive")!, 5, 100);
                break;
            case AmbushTrigger.Order:
                Fire(w, men[1]);
                break;
        }
        for (var t = 0; t < 30 && s.State != SquadState.Sprung; t++)
        {
            w.Run(1);
        }
        Assert.True(s.State == SquadState.Sprung, $"sprung by {trigger}: {s.State}, {s.AmbushTrigger}; {dump}");
        Assert.Equal(trigger, s.AmbushTrigger);
        Assert.All(men, u => Assert.Equal(Stance.Guard, u.Stance));
    }

    // Ready, everyone holds fire at an enemy in his reach that has not seen him; on Fire they shoot.
    [Fact]
    public void ItHoldsFireUntilSprung()
    {
        var (w, s, men) = Snipers();
        var at = PickOf(w, men[0]);
        var enemy = w.Spawn(2, "Rifleman", at.X + 9, at.Y + 3); // 9.5 cells: in the sniper's reach and sight, out of his
        enemy.Stance = Stance.HoldFire;
        for (var t = 0; t < 100; t++)
        {
            w.Run(1);
            Assert.All(men, u => Assert.Equal(0u, u.AutoTarget));
        }
        Assert.Equal(SquadState.Ready, s.State);
        Assert.Equal(enemy.Def.MaxHp, enemy.HP);
        Fire(w, men[0]);
        w.Run(120);
        Assert.Equal(SquadState.Sprung, s.State);
        Assert.True(!enemy.Alive || enemy.HP < enemy.Def.MaxHp, "they shot him");
    }

    // Sprung, nobody chases: riflemen hurt from beyond their reach stay at their positions (in Guard they would go after him).
    [Fact]
    public void NobodyChases()
    {
        var (w, s, men) = Snipers(riflemen: true);
        var picks = men.ToDictionary(u => u.Id, u => PickOf(w, u));
        var at = picks[men[0].Id];
        w.Spawn(2, "Marksman", at.X, at.Y + 10); // he sees them and shoots from 10 cells: their rifles reach 5
        for (var t = 0; t < 300; t++)
        {
            w.Run(1);
            foreach (var u in men.Where(u => u.Alive))
            {
                Assert.Equal(AiState.None, u.Ai);
                Assert.True(World.Within(u.Pos, picks[u.Id].Center(), Fixed.FromRaw(Fixed.OneRaw * 3 / 2)), $"{u.Id} at {u.Pos}, his spot {picks[u.Id]}");
            }
        }
        Assert.Equal(SquadState.Sprung, s.State);
        Assert.Contains(s.AmbushTrigger, new[] { AmbushTrigger.Spotted, AmbushTrigger.Hurt });
    }

    // After a fight it is Ready again (hold fire) and springs again; the mines are not laid again.
    [Fact]
    public void ItSpringsAgain()
    {
        var (w, s, men) = Snipers(riflemen: true);
        var mines = s.AmbushMines.Count;
        Assert.Equal(18, mines); // three riflemen, 6 each
        Fire(w, men[0]);
        w.Run(2);
        Assert.Equal(SquadState.Sprung, s.State);
        var rearm = w.Content.Squad!.Ambush!.RearmTicks;
        w.Run(rearm + 5);
        Assert.Equal(SquadState.Ready, s.State);
        Assert.All(men, u => Assert.Equal(Stance.HoldFire, u.Stance));
        Assert.Equal(SquadMode.Ambush, s.Mode);
        Fire(w, men[1]);
        w.Run(2);
        Assert.Equal(SquadState.Sprung, s.State);
        Assert.Equal(mines, s.AmbushMines.Count);
        Assert.All(s.AmbushTasks, t => Assert.Equal(AmbushTaskState.Done, t.State));
    }

    // A column of three technicals drives down the road into a set trap: it springs, the mines and the squad stop it, nobody chases.
    [Fact]
    public void AColumnDrivesIntoASetTrap()
    {
        var w = RoadMap(-1);
        var men = FullSquad(w, 26, 14);
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        w.Run(1);
        RunUntil(w, s, SquadState.Ready);
        var picks = men.ToDictionary(u => u.Id, u => PickOf(w, u));
        var column = new List<Unit>();
        for (var i = 0; i < 3; i++)
        {
            var (v, _) = w.SpawnCrewed(2, "technical", 2 + 3 * i, 30, Facing.East);
            column.Add(v);
        }
        w.Order(2, CommandKind.Move, column, cell: new Cell(62, 30));
        var sprung = false;
        for (var t = 0; t < 900; t++)
        {
            w.Run(1);
            sprung |= w.SquadEvents.Any(e => e.Kind == SquadEventKind.Sprung && e.Squad == s.Id);
            foreach (var u in men.Where(u => u.Alive && u.Inside == 0 && u.Garrison == 0))
            {
                Assert.True(World.Within(u.Pos, picks[u.Id].Center(), Fixed.FromInt(2)), $"{u.Id} at {u.Pos}, his spot {picks[u.Id]} (tick {w.Tick})");
            }
        }
        Assert.True(sprung, "it sprang");
        Assert.NotEqual(AmbushTrigger.None, s.AmbushTrigger);
        Assert.True(column.Count(v => !v.Alive) >= 2, $"the column stopped: {column.Count(v => !v.Alive)} of 3 destroyed, trigger {s.AmbushTrigger}");
    }

    // A zone shorter than 6 cells or longer than 30 (or none) is refused, with the reason for the client; 6 and 30 are taken.
    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(0, false)]
    public void ZoneLengthsOutsideTheLimitsAreRefused(int length, bool taken)
    {
        var w = Arena.New(64);
        var men = new List<Unit> { w.Spawn(1, "Rifleman", 20, 40), w.Spawn(1, "Rifleman", 21, 40) };
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], new Cell(10, 30), length == 0 ? null : new Cell(10 + length, 30));
        w.Run(1);
        if (taken)
        {
            Assert.Equal(SquadMode.Ambush, s.Mode);
            Assert.DoesNotContain(w.OrderRefusals, r => r.Reason == OrderRefusalReason.ZoneLength);
            return;
        }
        Assert.Equal(SquadMode.March, s.Mode);
        var refusal = Assert.Single(w.OrderRefusals, r => r.Reason == OrderRefusalReason.ZoneLength);
        Assert.Equal(s.Leader, refusal.Aircraft);
        Assert.Equal(new Cell(10, 30), refusal.Cell);
    }

    // A move ends the ambush (the squad marches; its hold fire lifted, its garrisons and mortar out); a mode does too.
    [Fact]
    public void AMoveOrAModeEndsIt()
    {
        var (w, s, men) = Snipers(riflemen: true);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [men[0].Id], Cell = new Cell(30, 50) });
        w.Run(1);
        Assert.Equal(SquadMode.March, s.Mode);
        Assert.Equal(SquadState.Moving, s.State);
        Assert.All(men, u => Assert.Equal(Stance.Guard, u.Stance));
        Assert.Equal(18, w.SquadAmbushMines(s.Id).Count); // the mines stay
        Ambush(w, 1, [men[0]], new Cell(20, 30), new Cell(30, 30));
        w.Run(1);
        Assert.Equal(SquadState.Setting, s.State);
        Assert.Empty(s.AmbushMines);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetSquadMode, Units = [men[0].Id], Index = (int)SquadMode.Assault });
        w.Run(1);
        Assert.Equal(SquadMode.Assault, s.Mode);
        Assert.All(men, u => Assert.Equal(Stance.Guard, u.Stance));
    }

    // A stance given while the ambush holds fire is the squad's: its members take it when it springs.
    [Fact]
    public void AStanceWaitsForTheSpring()
    {
        var (w, s, men) = Snipers();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [men[0].Id], Stance = Stance.HoldGround });
        w.Run(2);
        Assert.Equal(Stance.HoldGround, s.Stance);
        Assert.All(men, u => Assert.Equal(Stance.HoldFire, u.Stance));
        Fire(w, men[0]);
        w.Run(2);
        Assert.All(men, u => Assert.Equal(Stance.HoldGround, u.Stance));
    }

    // The same ambush gives the same positions, mines and hashes on two runs; the hash agrees with the reference hash.
    [Fact]
    public void Deterministic()
    {
        static (ulong, List<HoldPick>, List<AmbushMineTask>) Play()
        {
            var w = RoadMap(-1);
            var men = FullSquad(w, 26, 14);
            var s = Form(w, men);
            Ambush(w, 1, [men[0]], Start, End);
            for (var i = 0; i < 500; i++)
            {
                w.Run(1);
                Assert.Equal(w.Hash(), w.HashReference());
            }
            return (w.Hash(), s.HoldPicks.ToList(), s.AmbushTasks.ToList());
        }
        var (h1, p1, t1) = Play();
        var (h2, p2, t2) = Play();
        Assert.Equal(h1, h2);
        Assert.Equal(p1, p2);
        Assert.Equal(t1, t2);
        Assert.NotEmpty(p1);
        Assert.NotEmpty(t1);
    }

    // Replays store the Ambush order (its zone) and Fire, and play them back to the same hashes.
    [Fact]
    public void ReplayRoundTrip()
    {
        var setup = TestContent.Battle(11);
        var preview = Replay.NewWorld(TestContent.Mvp, setup);
        var blue = preview.Units.Where(u => u.Owner == 1 && preview.CanJoinSquad(u)).Select(u => u.Id).ToList();
        var start = Cell.Of(preview.Unit(blue[0])!.Pos);
        var a = new Cell(start.X + 2, start.Y + 6);
        var commands = new List<Command>
        {
            new() { Tick = 5, Player = 1, Kind = CommandKind.FormSquad, Units = blue },
            new() { Tick = 10, Player = 1, Kind = CommandKind.SetSquadMode, Units = [blue[0]], Index = 3, Cell = a, Cell2 = new Cell(a.X + 12, a.Y) },
            new() { Tick = 300, Player = 1, Kind = CommandKind.SpringAmbush, Units = [blue[1]] },
            new() { Tick = 400, Player = 1, Kind = CommandKind.Move, Units = [blue[1]], Cell = new Cell(40, 40) },
        };
        var live = Replay.NewWorld(TestContent.Mvp, setup);
        var issued = commands.Select(c => live.Issue(c)).ToList();
        var hashes = new List<ulong>();
        var sprung = 0;
        while (live.Tick < 450)
        {
            live.Step();
            hashes.Add(live.Hash());
            sprung += live.SquadEvents.Count(e => e.Kind == SquadEventKind.Sprung);
        }
        Assert.True(sprung >= 1, $"sprung {sprung} times");
        var rec = new Replay { Setup = setup, Ticks = 450, Commands = issued };
        using var buf = new MemoryStream();
        rec.Encode(buf);
        buf.Position = 0;
        var loaded = Replay.Decode(buf);
        Assert.Equal(issued.Select(c => (c.Kind, c.Cell2)), loaded.Commands.Select(c => (c.Kind, c.Cell2)));
        var (replayed, _) = loaded.Play(TestContent.Mvp);
        Assert.Equal(hashes, replayed);
    }

    // Content: the ambush part is optional (without it Ambush is ignored), needs hold, and its numbers are hashed.
    [Fact]
    public void TheAmbushBlockIsContent()
    {
        static List<ContentSource> Edit(Func<string, string> f) =>
            [.. TestContent.MvpSources().Select(x => x.Name == "squad.json" ? x with { Json = f(x.Json) } : x)];
        var cut = TestContent.MvpSources().Single(x => x.Name == "squad.json").Json;
        var at = cut.IndexOf("    // Ambush (phase S3)", StringComparison.Ordinal);
        var noAmbush = ContentLoader.Load(Edit(j => j[..at].TrimEnd().TrimEnd(',') + "\n  }\n}\n"));
        Assert.Null(noAmbush.Squad!.Ambush);
        Assert.NotNull(noAmbush.Squad.Hold);
        Assert.NotEqual(noAmbush.Hash, TestContent.Mvp.Hash);
        var tuned = ContentLoader.Load(Edit(j => j.Replace("\"rearmTicks\": 200", "\"rearmTicks\": 220", StringComparison.Ordinal)));
        Assert.NotEqual(tuned.Hash, TestContent.Mvp.Hash);
        var badEnd = Edit(j => j.Replace("\"end\": \"both\"", "\"end\": \"middle\"", StringComparison.Ordinal));
        Assert.Contains("end", Assert.Throws<ContentException>(() => ContentLoader.Load(badEnd)).Message);
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "battle.json")).For(noAmbush, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(64, 64), MatchCompiler.Compile(noAmbush, setup));
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 28 + i, 40)).ToList();
        var s = Form(w, men);
        Ambush(w, 1, [men[0]], Start, End);
        w.Run(5);
        Assert.Equal(SquadMode.March, s.Mode);
    }
}

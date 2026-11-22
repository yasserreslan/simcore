using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-3 part 2, the squad's manoeuvres (docs/design.md, "Infantry combat: the squad's manoeuvres"; battle rules 11.5, 11.7,
// 7.2's rise, 11.1's no chase in a line).
public class SquadManoeuvreTests(ITestOutputHelper output)
{
    private static World New(int size = 48, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static void Stance(World w, Stance st, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.SetStance, Units = [.. units.Select(u => u.Id)], Stance = st });

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static Unit[] Men(World w, byte player, int x, int y0, int n, int dy = 1, string name = "Rifleman") =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, name, x, y0 + i * dy))];

    private static bool Able(Unit u) => u.Alive && u.Life is not { Wound: WoundState.Down };

    [Fact]
    public void TheBlockLoadsTheManoeuvresNumbers()
    {
        var r = New().Content.InfantryCombat!;
        Assert.Equal((120, 2, 55, 80, 40, 400, 30, 120, 667, 200, 60),
            (r[CombatValue.SqFlankFipTicks], r[CombatValue.SqFlankHitsBelow], r[CombatValue.SqFlankCoverAvg], r[CombatValue.SqFlankDistancePercent],
             r[CombatValue.SqFlankMaxExposed], r[CombatValue.SqFlankRetryTicks], r[CombatValue.SqAssaultRange], r[CombatValue.SqAssaultR],
             r[CombatValue.SqAssaultShare], r[CombatValue.SqAssaultMaxTicks], r[CombatValue.GaitRiseMove]));
    }

    // 11.5: six rifles idle in the open, an enemy behind a wall 5 cells off whom they can't hurt: after 6 s of fighting in place
    // two flankers (never the leader) go round and end outside the wall's cover toward them (from where he stood when they set
    // out: he may shift a cell or two; 3 aborts the flank). (Against more rifles the open ground
    // on the way is over flankMaxExposed: no safe flank, the rules' own answer.)
    [Fact]
    public void AStalledSquadFlanksAnEnemyInHardCover()
    {
        var w = New();
        foreach (var y in new[] { 16, 17, 18 })
        {
            w.Grid.Set(new Cell(20, y), Terrain.Blocked);
        }
        var blue = Men(w, 1, 16, 12, 6, 2);
        var red = new[] { w.Spawn(2, "Rifleman", 21, 17) };
        Stance(w, Sim.Stance.HoldFire, red); // he keeps his head down: no flanker is hit on the way (which aborts the flank)
        red[0].HP = 20000; // he lasts
        var s = Form(w, blue);
        Form(w, red);
        uint[] flankers = [];
        var redAt = default(Cell);
        var r = w.Content.InfantryCombat!;
        var outside = 0;
        for (var i = 0; i < 900 && w.ManoeuvreStats.Flanks == 0; i++)
        {
            (red[0].Fight!.Posture, red[0].Fight!.PostureTo, red[0].Fight!.PostureLeft) = (Posture.Kneel, Posture.Kneel, 0); // he keeps down behind it
            w.Step();
            if (s.Mind is { FlankStage: 2 } m && flankers.Length == 0)
            {
                flankers = [.. m.Line.Where(p => p.Bounding).Select(p => p.Unit)];
                redAt = Cell.Of(red[0].Pos);
            }
        }
        output.WriteLine($"plans {w.ManoeuvreStats.Plans} rejected {w.ManoeuvreStats.Rejected} there {w.ManoeuvreStats.Flanks} aborted {w.ManoeuvreStats.Aborts} at {w.Tick}, flankers {string.Join(",", flankers)}");
        Assert.True(w.ManoeuvreStats.Flanks >= 1, "no flank reached its cell");
        Assert.Equal(2, flankers.Length);
        Assert.DoesNotContain(s.Leader, flankers);
        foreach (var id in flankers)
        {
            var f = w.Unit(id)!;
            var e = red.Where(Able).OrderBy(x => (x.Pos - f.Pos).LenSq()).FirstOrDefault();
            Cell fc = Cell.Of(f.Pos), ec = redAt; // the cover map's reading, cell to cell, from where he was when they set out
            if (e is null || w.CoverToward(ec, World.Dir32(fc.X - ec.X, fc.Y - ec.Y), r) == CoverKind.None)
            {
                outside++;
            }
        }
        Assert.Equal(flankers.Length, outside);
    }

    // INF-E1b-6 (row 1312, the coordinator's pursuit call; scene 4's seed 56): seven Shaken rifles, closed (3 to 3.5 cells) on two
    // enemies behind a wall who are Broken and pinned on Hold ground: they rush them; 10.3's Steady condition is for an enemy that
    // can still fight. Steady or Shaken, a squad that is not Broken itself pursues a Broken or Routing group. A Broken squad doesn't.
    [Theory]
    [InlineData(4000, true)]
    [InlineData(1500, false)]
    public void AShakenSquadRushesABrokenPinnedGroupBehindAWall(int morale100, bool rushes)
    {
        var w = New();
        for (var y = 15; y <= 21; y++)
        {
            w.Grid.Set(new Cell(19, y), Terrain.Blocked);
        }
        var blue = Men(w, 1, 17, 15, 7); // seven: R 120+ against two behind 55% cover (seed 56's four had R 134 to 180 against two hit and unseen)
        var red = new[] { w.Spawn(2, "Rifleman", 20, 17), w.Spawn(2, "Rifleman", 20, 19) };
        Stance(w, Sim.Stance.HoldGround, red);
        foreach (var e in red)
        {
            e.HP = 20000; // they last long enough to be rushed
        }
        var s = Form(w, blue);
        var rs = Form(w, red);
        var assaulted = false;
        MoraleState spirit = MoraleState.Steady, theirs = MoraleState.Steady;
        for (var i = 0; i < 300 && !assaulted; i++)
        {
            foreach (var e in red)
            {
                if (e.Fight is { } ef)
                {
                    ef.Suppression100 = 9500; // pinned behind the wall
                }
            }
            if (s.Mind is { } bm && rs.Mind is { } rm)
            {
                (bm.Morale100, rm.Morale100) = (morale100, 500); // blue Shaken (or Broken), red Broken and routing
            }
            w.Step();
            assaulted = s.Mind is { Choice: SquadChoice.Assault };
            (spirit, theirs) = (s.Mind?.Spirit ?? spirit, rs.Mind?.Spirit ?? theirs);
        }
        output.WriteLine($"assaulted {assaulted} at {w.Tick} spirit {spirit} theirs {theirs} ratio {s.Mind!.Ratio} choice {s.Mind.Choice} assaults {w.ManoeuvreStats.Assaults}");
        Assert.Equal(MoraleState.Broken, theirs);
        Assert.Equal(morale100 >= 2000 ? MoraleState.Shaken : MoraleState.Broken, spirit);
        Assert.Equal(rushes, assaulted);
    }

    // 11.7: six rifles 3 cells from two pinned enemies: the assault, four runners (two thirds) who run at them, the rest
    // suppressing; the runners reach point blank (the enemy here can't die: the assault then fails on its 200 ticks).
    [Fact]
    public void ACloseSquadAssaultsAPinnedEnemy()
    {
        var w = New();
        var blue = Men(w, 1, 16, 13, 6, 2);
        var red = new[] { w.Spawn(2, "Rifleman", 20, 17), w.Spawn(2, "Rifleman", 20, 19) };
        Stance(w, Sim.Stance.HoldGround, red);
        foreach (var e in red)
        {
            e.HP = 20000; // they last long enough to be rushed
        }
        var s = Form(w, blue);
        Form(w, red);
        int runners = 0, ran = 0, suppressing = 0;
        var closest = long.MaxValue;
        for (var i = 0; i < 400 && red.Any(Able); i++)
        {
            foreach (var e in red)
            {
                if (e.Fight is { } ef)
                {
                    ef.Suppression100 = 9500; // pinned: they hug the ground
                }
            }
            w.Step();
            if (s.Mind is { Choice: SquadChoice.Assault } m)
            {
                runners = Math.Max(runners, m.Line.Count(p => p.Bounding));
                ran += blue.Count(u => u.Fight is { Gait: Gait.Run });
                suppressing += blue.Count(u => u.Fight is { Suppressing: true });
                foreach (var p in m.Line.Where(p => p.Bounding))
                {
                    if (w.Unit(p.Unit) is { } ru)
                    {
                        closest = Math.Min(closest, red.Min(e => (e.Pos - ru.Pos).Len().Raw));
                    }
                }
            }
        }
        output.WriteLine($"assaults {w.ManoeuvreStats.Assaults} failed {w.ManoeuvreStats.AssaultFails} runners {runners} ran {ran} suppressing {suppressing} closest {closest * 10 / Fixed.OneRaw} at {w.Tick}");
        Assert.True(w.ManoeuvreStats.Assaults >= 1, "no assault");
        Assert.Equal(4, runners);
        Assert.True(ran > 0, "nobody ran");
        Assert.True(closest * 2 <= 3 * Fixed.OneRaw, $"the runners got no closer than {closest * 10 / Fixed.OneRaw} tenths"); // point blank: 1.5 cells
    }

    // 11.1, part 2: a squad's line places its men: nobody with a place in it chases on his own (scene 4's 6 072 chasing stalls).
    [Fact]
    public void NobodyInALineChases()
    {
        var w = New(64);
        var blue = Men(w, 1, 10, 14, 6, 2).Append(w.Spawn(1, "Gunner", 9, 19)).ToArray();
        var red = Men(w, 2, 30, 16, 4, 2);
        Stance(w, Sim.Stance.HoldGround, red);
        var s = Form(w, blue);
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [blue[0].Id], Cell = new Cell(40, 19) });
        var chasing = 0;
        var lined = 0;
        for (var i = 0; i < 700 && red.Any(Able); i++)
        {
            w.Step();
            if (s.Mind is { HasLine: true } m)
            {
                lined++;
                chasing += m.Line.Count(p => w.Unit(p.Unit) is { Ai: not AiState.None });
            }
        }
        Assert.True(lined > 0, "no line");
        Assert.Equal(0, chasing);
    }

    // 7.2, part 2: a kneeling man ordered to move steps off while he stands up (the kneel-to-stand stalls of scene 4).
    [Fact]
    public void AKneelingManStepsOffAsHeRises()
    {
        var w = New();
        var u = w.Spawn(1, "Rifleman", 10, 10);
        w.Step();
        (u.Fight!.Posture, u.Fight.PostureTo) = (Posture.Kneel, Posture.Kneel);
        var at = u.Pos;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [u.Id], Cell = new Cell(20, 10) });
        var movedRising = false;
        for (var i = 0; i < 20; i++)
        {
            w.Step();
            movedRising |= u.Fight.PostureLeft > 0 && u.Pos != at;
        }
        Assert.True(movedRising, "he stood still while he rose");
    }
}

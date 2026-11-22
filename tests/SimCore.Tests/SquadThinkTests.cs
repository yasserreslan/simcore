using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-E1b-3, the squad think (docs/design.md, "Infantry combat: the squad think"; battle rules 10, 11, 15.4 to 15.7, 16.2, 19).
public class SquadThinkTests
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

    [Fact]
    public void TheBlockLoadsTheSquadThinksNumbers()
    {
        var r = New().Content.InfantryCombat!;
        Assert.Equal((20, 10, 22, 40, 10, 70, 20, 30, 90, 150, 200, 30),
            (r[CombatValue.SqUrgentThinks], r[CombatValue.SqRifle], r[CombatValue.SqLmg], r[CombatValue.SqChoiceHoldTicks], r[CombatValue.SqChoiceHysteresis],
             r[CombatValue.SqAdvanceToPercent], r[CombatValue.SqContactSpacing], r[CombatValue.SqBoundMin], r[CombatValue.SqBoundMaxTicks], r[CombatValue.SqMarchR],
             r[CombatValue.SqSuppressHitBelow], r[CombatValue.SqLmgDeployTicks]));
    }

    // 10.2: six rifles against two at the edge of their range: R well over 100 for the six, under it for the two.
    [Fact]
    public void TheForceRatioFavoursTheStrongerSquad()
    {
        var w = New();
        var blue = Men(w, 1, 10, 10, 6);
        var red = Men(w, 2, 16, 12, 2);
        Stance(w, Sim.Stance.HoldGround, [.. blue, .. red]);
        var bs = Form(w, blue);
        var rs = Form(w, red);
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        Assert.True(bs.Mind!.Ratio > 150, $"blue R {bs.Mind.Ratio}");
        Assert.True(rs.Mind!.Ratio is > 0 and < 70, $"red R {rs.Mind.Ratio}");
        Assert.NotEqual(SquadChoice.None, bs.Mind.Choice);
    }

    // 11.1: an idle squad standing in a file north-south sees an enemy to the north-east: it forms a line across the axis, its men
    // dealt to the slots in their order along it (no crossings), each slot 2 cells from the next, and walks there.
    [Fact]
    public void AnAlertSquadFormsALineAcrossTheThreatAxisWithoutCrossings()
    {
        var w = New();
        var blue = Men(w, 1, 20, 16, 6);
        var s = Form(w, blue);
        var red = w.Spawn(2, "Rifleman", 30, 18);
        Stance(w, Sim.Stance.HoldFire, red);
        for (var i = 0; i < 40 && s.Mind is not { HasLine: true }; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        Assert.True(m.HasLine);
        var (ax, ay) = World.Directions[m.LineAxis];
        long Lat(Cell c) => (long)c.X * -ay + (long)c.Y * ax;
        var bySlot = m.Line.OrderBy(p => Lat(p.Slot)).Select(p => p.Unit).ToList();
        var byPlace = m.Line.OrderBy(p => Lat(Cell.Of(w.Unit(p.Unit)!.Pos))).ThenBy(p => p.Unit).Select(p => p.Unit).ToList();
        Assert.Equal(byPlace, bySlot); // dealt in place order: nobody's way crosses another's
        var lats = m.Line.Select(p => Lat(p.Slot)).OrderBy(x => x).ToList();
        Assert.True(lats[^1] - lats[0] >= 8 * 900, $"span {(lats[^1] - lats[0]) / 1024.0}"); // about 10 cells wide for 6 men
        for (var i = 0; i < 100; i++)
        {
            w.Step();
        }
        foreach (var p in m.Line)
        {
            var u = w.Unit(p.Unit)!;
            Assert.True(u.Fight!.HasSpot || (u.Pos - p.Goal.Center()).Len().Raw <= Fixed.OneRaw, $"#{u.Id} not at his goal");
            Assert.True(w.InBox(u, Cell.Of(u.Pos), w.Content.InfantryCombat!), $"#{u.Id} outside his box");
        }
    }

    // 19.3: in a still stance the squad fights in place: no line, nobody moves on his own.
    [Fact]
    public void AStillStanceSquadFightsInPlace()
    {
        var w = New();
        var blue = Men(w, 1, 20, 16, 6);
        Stance(w, Sim.Stance.HoldGround, blue);
        var s = Form(w, blue);
        for (var i = 0; i < 200; i++)
        {
            w.Step(); // the squad takes its shape first
        }
        var start = blue.Select(u => Cell.Of(u.Pos)).ToList();
        var red = w.Spawn(2, "Rifleman", 30, 18);
        Stance(w, Sim.Stance.HoldFire, red);
        for (var i = 0; i < 100; i++)
        {
            w.Step();
            Assert.False(s.Mind is { HasLine: true });
        }
        Assert.Equal(start, blue.Select(u => Cell.Of(u.Pos)).ToList());
    }

    // 19.4: any order of the player ends the squad's automatic behaviour at the next tick.
    [Fact]
    public void APlayersOrderEndsTheSquadsOwnBehaviour()
    {
        var w = New();
        var blue = Men(w, 1, 20, 16, 6);
        var s = Form(w, blue);
        var red = w.Spawn(2, "Rifleman", 30, 18);
        Stance(w, Sim.Stance.HoldFire, red);
        for (var i = 0; i < 40 && s.Mind is not { HasLine: true }; i++)
        {
            w.Step();
        }
        Assert.True(s.Mind!.HasLine);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [blue[0].Id], Cell = new Cell(10, 30) });
        w.Step();
        Assert.False(s.Mind.HasLine);
        Assert.Equal(SquadChoice.None, s.Mind.Choice);
        var d0 = (blue[0].Pos - new Cell(10, 30).Center()).Len().Raw;
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            Assert.False(s.Mind.HasLine); // under a Move the squad goes on (INF-MOVE carries it)
        }
        var d1 = (blue[0].Pos - new Cell(10, 30).Center()).Len().Raw;
        Assert.True(d1 < d0 / 2, $"the squad went its way: {d0 / 1024.0} to {d1 / 1024.0}, order {s.Order}, state {s.State}");
    }

    // NearLine: p within clear (raw) of the segment a to b.
    private static bool NearLine(Vec p, Vec a, Vec b, long clear)
    {
        long dx = b.X.Raw - a.X.Raw, dy = b.Y.Raw - a.Y.Raw, px = p.X.Raw - a.X.Raw, py = p.Y.Raw - a.Y.Raw;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp((px * dx + py * dy) * 1024 / len, 0, 1024);
        long qx = px - dx * t / 1024, qy = py - dy * t / 1024;
        return qx * qx + qy * qy <= clear * clear;
    }

    private static bool Within(Vec p, Cell c, int cells) => (p - c.Center()).Len().Raw <= cells * Fixed.OneRaw;

    // Scene 4 (11.3): blue (6 rifles and an LMG) attack-moves onto 4 red; blue bounds with at most one element moving at any tick.
    [Fact]
    public void BoundingNeverMovesBothElements()
    {
        var w = New(64);
        var blue = Men(w, 1, 10, 14, 6, 2).Append(w.Spawn(1, "Gunner", 9, 19)).ToArray();
        var red = Men(w, 2, 30, 16, 4, 2);
        Stance(w, Sim.Stance.HoldGround, red);
        var s = Form(w, blue);
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AttackMove, Units = [blue[0].Id], Cell = new Cell(40, 19) });
        int bounds = 0, both = 0;
        for (var i = 0; i < 700 && red.Any(u => u.Alive); i++)
        {
            w.Step();
            if (s.Mind is not { HasLine: true } m)
            {
                continue;
            }
            var moving = m.Choice != SquadChoice.Advance || m.Marching ? 1 : m.Line.Where(p => p.Bounding && p.Element != 0).Select(p => p.Element).Distinct().Count(); // the march is the whole line (11.4)
            both += moving > 1 ? 1 : 0;
            bounds += m.Moving != 0 ? 1 : 0;
        }
        Assert.Equal(0, both);
        Assert.True(bounds > 0, "no bound");
    }

    // 11.6: a soldier ordered to suppress fires at the place: misses by construction (no man there), landing within a cell of it,
    // and the enemy beside it gets the near-miss suppression.
    [Fact]
    public void SuppressiveFireLandsRoundThePlaceAndSuppresses()
    {
        var w = New();
        var blue = w.Spawn(1, "Rifleman", 10, 10);
        var red = w.Spawn(2, "Rifleman", 14, 11);
        Stance(w, Sim.Stance.HoldFire, red);
        Stance(w, Sim.Stance.HoldGround, blue);
        var shots = 0;
        for (var i = 0; i < 100; i++)
        {
            (blue.Fight!.Suppressing, blue.Fight.SuppressAt) = (true, new Cell(14, 10));
            w.Step();
            foreach (var e in w.ShotEvents.Where(e => e.Shooter == blue.Id))
            {
                Assert.Equal(0u, e.Target);
                Assert.True((e.At - new Cell(14, 10).Center()).Len().Raw <= 3 * Fixed.OneRaw / 2);
                shots++;
            }
        }
        Assert.True(shots >= 6, $"{shots} shots");
        Assert.True(red.Fight!.Suppression > 0);
        Assert.Equal(red.Def.MaxHp, red.HP);
    }

    // 15.7: a soldier doesn't fire through a squadmate standing in his line of fire.
    [Fact]
    public void NoShotThroughASquadmate()
    {
        var w = New();
        var a = w.Spawn(1, "Rifleman", 10, 10);
        var b = w.Spawn(1, "Rifleman", 12, 10);
        var red = w.Spawn(2, "Rifleman", 30, 30);
        Stance(w, Sim.Stance.HoldGround, a, b);
        Stance(w, Sim.Stance.HoldFire, red);
        Form(w, a, b);
        for (var i = 0; i < 100; i++)
        {
            w.Step(); // the pair takes its shape; red then stands in line beyond B
        }
        red.Pos = new Vec(Fixed.FromRaw(b.Pos.X.Raw + (b.Pos.X.Raw - a.Pos.X.Raw) * 3 / 2), Fixed.FromRaw(b.Pos.Y.Raw + (b.Pos.Y.Raw - a.Pos.Y.Raw) * 3 / 2));
        int fromA = 0, fromB = 0;
        var clear = w.Content.InfantryCombat![CombatValue.PosFireLineClear] * Fixed.OneRaw / 10;
        int inLine = 0;
        for (var i = 0; i < 100 && red.Alive; i++)
        {
            var blocked = NearLine(b.Pos, a.Pos, red.Pos, clear);
            w.Step();
            inLine += blocked ? 1 : 0;
            fromA += blocked ? w.ShotEvents.Count(e => e.Shooter == a.Id) : 0;
            fromB += w.ShotEvents.Count(e => e.Shooter == b.Id);
        }
        Assert.True(inLine > 50, $"{inLine} ticks in line");
        Assert.Equal(0, fromA);
        Assert.True(fromB > 0);
    }

    // INF-E1b-6 (row 1315, 15.7's friendly line of fire): a squadmate between him and his target. Free to move (Guard), he side-steps a
    // cell (a strafe) and fires at it; on Hold ground he can't step, so he picks the other enemy, off the line. On a334956 he held
    // his fire at the man in line (NoShotThroughASquadmate's rule, with no step and no other target).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASquadmateInTheLineHeSideStepsElsePicksAnotherTarget(bool free)
    {
        var w = New();
        var a = w.Spawn(1, "Rifleman", 10, 10);
        var b = w.Spawn(1, "Rifleman", 12, 10);
        var red = w.Spawn(2, "Rifleman", 30, 30);
        var other = w.Spawn(2, "Rifleman", 31, 31);
        Stance(w, Sim.Stance.HoldGround, a, b);
        Stance(w, Sim.Stance.HoldFire, red, other);
        (red.HP, other.HP) = (20000, 20000);
        Form(w, a, b);
        for (var i = 0; i < 100; i++)
        {
            w.Step(); // the pair takes its shape; red then stands in line beyond B, the other off the line and a little farther
        }
        if (free)
        {
            a.Stance = Sim.Stance.Guard; // he alone may move on his own now (a squad's stance order would set both)
        }
        var (ax, ay, bx, by) = (a.Pos.X.Raw, a.Pos.Y.Raw, b.Pos.X.Raw, b.Pos.Y.Raw);
        red.Pos = new Vec(Fixed.FromRaw(bx + (bx - ax) * 3 / 2), Fixed.FromRaw(by + (by - ay) * 3 / 2));
        other.Pos = free ? new Vec(Fixed.FromRaw(40 * Fixed.OneRaw), Fixed.FromRaw(40 * Fixed.OneRaw)) // far off: only the man in line
            : new Vec(Fixed.FromRaw(bx + (bx - ax) * 3 / 2 - (by - ay) * 2), Fixed.FromRaw(by + (by - ay) * 3 / 2 + (bx - ax) * 2));
        var start = Cell.Of(a.Pos);
        int atRed = 0, atOther = 0;
        Cell? end = null;
        for (var i = 0; i < 120; i++)
        {
            w.Step();
            atRed += w.ShotEvents.Count(e => e.Shooter == a.Id && e.Target == red.Id);
            atOther += w.ShotEvents.Count(e => e.Shooter == a.Id && e.Target == other.Id);
            end ??= atRed + atOther > 0 ? Cell.Of(a.Pos) : null; // where he fired from first
        }
        Assert.True(free ? atRed > 0 : atOther > 0, $"a fired at red {atRed}, at the other {atOther}");
        Assert.NotNull(end);
        var dx = Math.Max(Math.Abs(end!.Value.X - start.X), Math.Abs(end.Value.Y - start.Y));
        if (free)
        {
            Assert.Equal(1, dx); // one step aside
        }
        else
        {
            Assert.Equal(0, dx);
        }
    }

    // INF-E1b-6 (row 1314, 15.6): in contact, his magazine empty, behind a rock he ducks to reload, in the open he crouches (brief
    // 6.5). And the LMG's reload is announced: while he reloads he is left out of 15.2's nearest-shooter test, so the rifleman next
    // to him gets his target in his sector. On a334956 the man knelt through his reload and the LMG kept his sector.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HeDucksOrCrouchesToReloadAndTheLmgsReloadIsAnnounced(bool rock)
    {
        var w = New();
        if (rock)
        {
            w.Grid.Set(new Cell(11, 10), Terrain.Blocked);
        }
        var a = w.Spawn(1, "Rifleman", 10, 10);
        var red = w.Spawn(2, "Rifleman", 14, 10);
        Stance(w, Sim.Stance.HoldGround, a);
        Stance(w, Sim.Stance.HoldFire, red);
        red.HP = 20000;
        for (var i = 0; i < 80; i++)
        {
            w.Step();
        }
        a.Arms!.MutableMag[a.Arms.InHand] = 0;
        var ducked = false;
        for (var i = 0; i < 60 && !ducked; i++)
        {
            w.Step();
            ducked = a.Arms.Reloading >= 0 && a.Fight is { PostureTo: Posture.Crouch } f && f.Ducked == rock;
        }
        Assert.True(ducked, $"reloading {a.Arms.Reloading} posture {a.Fight!.PostureTo} ducked {a.Fight.Ducked}");

        var w2 = New();
        var rifle = w2.Spawn(1, "Rifleman", 10, 8);
        var lmg = w2.Spawn(1, "Gunner", 10, 12);
        var e = w2.Spawn(2, "Rifleman", 16, 12);
        Stance(w2, Sim.Stance.HoldGround, rifle, lmg);
        Stance(w2, Sim.Stance.HoldFire, e);
        e.HP = 20000;
        var s = Form(w2, rifle, lmg);
        for (var i = 0; i < 80; i++)
        {
            w2.Step();
        }
        var r = w2.Content.InfantryCombat!;
        (rifle.Pos, lmg.Pos, e.Pos) = (new Cell(10, 8).Center(), new Cell(10, 12).Center(), new Cell(16, 14).Center()); // e across from the LMG
        Assert.True(s.Mind!.Axis >= 0);
        Assert.Equal(r[CombatValue.TargetSectorNext], w2.SectorOf(rifle, s, e, r));
        lmg.Arms!.Reloading = 0;
        Assert.Equal(r[CombatValue.TargetSectorIn], w2.SectorOf(rifle, s, e, r));
    }

    // 15.6: with every magazine at a quarter and nobody to shoot, at most a third of the squad reloads at once.
    [Fact]
    public void ReloadsAreStaggeredAcrossTheSquad()
    {
        var w = New();
        var men = Men(w, 1, 10, 10, 6);
        var s = Form(w, men);
        var size = 0;
        foreach (var u in men)
        {
            var wi = u.Arms!.InHand;
            size = u.Def.Weapons[wi].Effect.Magazine;
            u.Arms.MutableMag[wi] = Math.Max(1, size / 4);
        }
        Assert.True(size > 0);
        int most = 0, done = 0;
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            most = Math.Max(most, men.Count(u => u.Arms!.Reloading >= 0));
            done = men.Count(u => u.Arms!.Mag[u.Arms.InHand] == size);
        }
        Assert.Equal(2, most);
        Assert.Equal(6, done);
        _ = s;
    }

    // 15.4: an enemy LMG gunner deployed within his range of the squad is its focus target.
    [Fact]
    public void ADeployedEnemyLmgIsNamedTheFocus()
    {
        var w = New();
        var blue = Men(w, 1, 10, 10, 4);
        var s = Form(w, blue);
        var gunner = w.Spawn(2, "Gunner", 15, 11);
        Stance(w, Sim.Stance.HoldFire, gunner);
        for (var i = 0; i < 120 && s.Mind is not { Focus: not 0 }; i++)
        {
            w.Step();
        }
        Assert.True(gunner.Fight!.Deployed || gunner.Fight.DeployLeft > 0);
        Assert.Equal(gunner.Id, s.Mind!.Focus);
    }

    // 16.2: an LMG gunner who stops in contact for 2 s sets up (1.5 s without fire), prone in the open; walking ends it.
    [Fact]
    public void TheLmgDeploysOnAStopAndPacksUpToMove()
    {
        var w = New();
        var g = w.Spawn(1, "Gunner", 10, 10);
        var red = w.Spawn(2, "Rifleman", 15, 10);
        Stance(w, Sim.Stance.HoldFire, red);
        Stance(w, Sim.Stance.HoldGround, g);
        uint started = 0, deployed = 0;
        for (var i = 0; i < 200 && deployed == 0; i++)
        {
            w.Step();
            started = started == 0 && g.Fight!.DeployLeft > 0 ? w.Tick : started;
            deployed = g.Fight!.Deployed ? w.Tick : 0;
            Assert.False(g.Fight.DeployLeft > 0 && w.ShotEvents.Any(e => e.Shooter == g.Id)); // no fire while setting up
        }
        Assert.True(started != 0 && deployed - started is >= 29 and <= 31, $"{started} {deployed}");
        for (var i = 0; i < 30; i++)
        {
            w.Step();
        }
        Assert.Equal(Posture.Prone, g.Fight!.Posture);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [g.Id], Cell = new Cell(10, 20) });
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        Assert.False(g.Fight.Deployed);
    }

    // Scene 29 (the squad's part): two equal squads facing each other at 5 cells for 30 s change their choice at most twice.
    [Fact]
    public void AnEvenFightDoesNotDither()
    {
        var w = New();
        var blue = Men(w, 1, 10, 10, 6, 2);
        var red = Men(w, 2, 15, 10, 6, 2);
        var bs = Form(w, blue);
        var rs = Form(w, red);
        int changes = 0;
        SquadChoice lb = SquadChoice.None, lr = SquadChoice.None;
        for (var i = 0; i < 600; i++)
        {
            w.Step();
            var cb = bs.Mind?.Choice ?? SquadChoice.None;
            var cr = rs.Mind?.Choice ?? SquadChoice.None;
            changes += (lb != SquadChoice.None && cb != lb && cb != SquadChoice.None ? 1 : 0) + (lr != SquadChoice.None && cr != lr && cr != SquadChoice.None ? 1 : 0);
            (lb, lr) = (cb, cr);
        }
        Assert.True(changes <= 4, $"{changes} changes"); // at most twice each
    }
}

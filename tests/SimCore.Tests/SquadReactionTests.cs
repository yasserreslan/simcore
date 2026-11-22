using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-4 part 3: reactions to indirect fire and air (battle rules 14.4, scene 14), a loose soldier's one-man morale (brief 7.3),
// the cornered routing man (12.4), the per-squad call switch (13.2, 19.4).
public class SquadReactionTests(ITestOutputHelper output)
{
    private static World New(int size = 48, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static Vec At(int x, int y) => new Cell(x, y).Center();

    // Scene 14 (14.4): shells on an idle squad in contact: the squad marks the first impact, its men leave the zone (a cell of less
    // danger), never backward while the enemy is in reach, and keep 2 cells apart while it lasts.
    [Fact]
    public void AnIdleSquadLeavesTheImpactZoneForwardOrAcrossAndSpreads()
    {
        var w = New();
        var blue = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 14, 18 + i * 2, Facing.East)).ToArray();
        var red = Enumerable.Range(0, 3).Select(i => w.Spawn(2, "Rifleman", 19, 19 + i * 2, Facing.West)).ToArray();
        var bs = Form(w, blue);
        Form(w, red);
        foreach (var u in red)
        {
            u.HP = u.Def.MaxHp * 40 / 100; // (red weak: blue stays and fights in place)
        }
        var m = bs.Mind!;
        for (var i = 0; i < 300 && !m.InContact; i++)
        {
            w.Step();
        }
        Assert.True(m.InContact, "in contact");
        for (var i = 0; i < 30; i++)
        {
            w.Step();
        }
        var x0 = blue.ToDictionary(u => u.Id, u => u.Pos.X.Raw);
        var shell = blue[1].Pos;
        for (var k = 0; k < 3; k++)
        {
            w.NoteBlast(k == 0 ? shell : shell + new Vec(Fixed.FromInt(k - 1), Fixed.Zero), Fixed.FromInt(2)); // 3 shells (scene 14)
            w.Step();
        }
        Assert.Equal(Cell.Of(shell), m.ImpactAt);
        Assert.True(m.SpreadUntil > w.Tick, "the squad spreads");
        int back = 0;
        for (var i = 0; i < 80; i++)
        {
            w.Step();
            foreach (var u in blue)
            {
                back += u.Alive && u.Fight is { InContact: true, OutOfReach: false } && u.Pos.X.Raw < x0[u.Id] - Fixed.OneRaw / 2 ? 1 : 0;
            }
        }
        var inner = blue.Count(u => u.Alive && (u.Pos - shell).Len().Raw < 3 * Fixed.OneRaw);
        output.WriteLine($"impact {m.ImpactAt}, in the inner zone after 4 s: {inner}, steps back in reach: {back}, positions {string.Join(" ", blue.Select(u => Cell.Of(u.Pos)))}");
        Assert.True(inner == 0, $"{inner} still in the inner zone"); // (part 4: he crouch-runs out, 14.4; strafing crouched he took 5 s)
        Assert.Equal(0, back);
    }

    // 14.4: an air attack run seen within 10 cells: a man in the open goes prone (if he may) for 10 s.
    [Fact]
    public void AnAirAttackRunPutsMenInTheOpenProne()
    {
        var w = New();
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 14, 18 + i * 2, Facing.East)).ToArray();
        Form(w, men);
        w.Spawn(2, "Rifleman", 21, 20, Facing.West);
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        w.NoteAirBlast(At(20, 20), Fixed.FromInt(2));
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        output.WriteLine(string.Join(" ", men.Select(u => $"{u.Fight!.PostureTo} until {u.Fight.AirProneUntil}")));
        Assert.All(men, u => Assert.True(u.Fight!.AirProneUntil > 0, "he saw the run"));
        Assert.Contains(men, u => u.Fight!.PostureTo == Posture.Prone);
    }

    // Brief 7.3: a soldier not in a squad has a one-man squad's morale: his own wound -5, an enemy he kills +5 (no higher than the
    // events take it), pinned for over 8 s up to -2/s; under 20 he is Broken (cowed with no order).
    [Fact]
    public void ALooseSoldierHasHisOwnMorale()
    {
        var w = New();
        var u = w.Spawn(1, "Rifleman", 14, 20, Facing.East);
        for (var i = 0; i < 40; i++)
        {
            w.Step();
        }
        var f = u.Fight!;
        Assert.Equal(8000, f.LooseMorale100);
        u.HP = u.Def.MaxHp * 30 / 100;
        w.Step();
        Assert.InRange(f.LooseMorale100, 7400, 7600);
        for (var i = 0; i < 640; i++)
        {
            (f.Suppression100, f.ShotNear1, f.ShotNear2) = (8000, w.Tick, w.Tick); // pinned for 32 s, shots near him
            w.Step();
        }
        output.WriteLine($"morale {f.LooseMorale100} {f.Spirit} cowed {f.Cowed}");
        Assert.InRange(f.LooseMorale100, 2500, 3500); // 75 - 2/s for 24 s
        Assert.Equal(MoraleState.Shaken, f.Spirit);
    }

    // 12.4: a routing man fired on from within 3 cells who can't run (pinned hard) drops prone and stops firing; a loose one with an
    // enemy within 2 cells is cornered too (part 4).
    [Fact]
    public void ACorneredRoutingManGoesProne()
    {
        var w = New();
        var u = w.Spawn(1, "Rifleman", 14, 20, Facing.East);
        var e = w.Spawn(2, "Rifleman", 16, 20, Facing.West);
        for (var i = 0; i < 40; i++)
        {
            w.Step();
        }
        var f = u.Fight!;
        f.LooseMorale100 = 500; // routing, the enemy 2 cells off
        for (var i = 0; i < 40; i++)
        {
            f.Suppression100 = 9500;
            w.Step();
        }
        output.WriteLine($"routs {f.Routs} cornered {f.Cornered} posture {f.PostureTo} enemy at {Cell.Of(e.Pos)}");
        Assert.True(f.Routs && f.Cornered);
        Assert.Equal(Posture.Prone, f.PostureTo);
    }

    // 12.4, part 4 (the coordinator's answer): a loose routing man with no enemy within 2 cells is not cornered: he runs to the
    // nearest building he may garrison (or his side's nearest structure) and goes in, where he rallies; on the part-3 base he lay
    // prone where he stood (always cornered).
    [Fact]
    public void ALooseRoutingManRunsToABuildingAndGoesIn()
    {
        var w = New();
        var house = w.Spawn(1, "resistance_barracks", 6, 8); // its door at (11, 20)
        var u = w.Spawn(1, "Rifleman", 20, 20, Facing.East);
        var e = w.Spawn(2, "Rifleman", 25, 20, Facing.West);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [e.Id], Stance = Stance.HoldFire });
        for (var i = 0; i < 40; i++)
        {
            w.Step();
        }
        var f = u.Fight!;
        var cornered = 0;
        for (var i = 0; i < 600 && u.Garrison == 0; i++)
        {
            f.LooseMorale100 = Math.Min(f.LooseMorale100, 500);
            w.Step();
            cornered += f.Cornered ? 1 : 0;
        }
        output.WriteLine($"{w.Tick} at {Cell.Of(u.Pos)} garrison {u.Garrison} cornered ticks {cornered} morale {f.LooseMorale100}");
        Assert.Equal(house.Id, u.Garrison);
        Assert.Equal(0, cornered);
    }

    // 13.2, 19.4: a squad whose switch is off never answers a call.
    [Fact]
    public void TheSwitchOffSquadDoesNotAnswer()
    {
        var w = New();
        var a = Form(w, [.. Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 10, 18 + i, Facing.East))]);
        var b = Form(w, [.. Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 10, 36 + i, Facing.East))]);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetAutoSupport, Units = [b.Members[0]], On = false });
        var red = Enumerable.Range(0, 6).Select(i => w.Spawn(2, "Rifleman", 18, 17 + i, Facing.West)).ToArray();
        Form(w, red);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackMove, Units = [red[0].Id], Cell = new Cell(6, 19) });
        var called = false;
        for (var i = 0; i < 300; i++)
        {
            w.Step();
            called |= a.Mind!.Calling;
            Assert.Equal(0u, b.Mind!.HelpOf);
        }
        Assert.True(called, "A called");
        Assert.False(b.AutoSupport);
    }
}

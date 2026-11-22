using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-4 part 1: morale (brief 7.3; battle rules 12.4, 10.2, 10.3, 11.8, 4.6, 6.2).
public class SquadMoraleTests(ITestOutputHelper output)
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

    private static Unit[] Men(World w, byte player, int x, int y0, int n, int dy = 1) =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, "Rifleman", x, y0 + i * dy))];

    // Steps until the squad's next think has run (its rates and state).
    private static void Think(World w, Squad s)
    {
        var at = s.Mind!.MoraleTick;
        for (var i = 0; i < 12 && s.Mind.MoraleTick == at; i++)
        {
            w.Step();
        }
    }

    [Fact]
    public void TheBlockLoadsTheMoraleNumbers()
    {
        var r = New().Content.InfantryCombat!;
        Assert.Equal((80, 15, 8, 5, 20, 200, 50, 20, 40, 35, 10, 20),
            (r[CombatValue.MoStart], r[CombatValue.MoKilled], r[CombatValue.MoDown], r[CombatValue.MoWounded], r[CombatValue.MoLeader],
             r[CombatValue.MoLeaderPauseTicks], r[CombatValue.MoSteadyAt], r[CombatValue.MoBrokenBelow], r[CombatValue.MoRally],
             r[CombatValue.MoRallyLed], r[CombatValue.MoRoutBelow], r[CombatValue.MoRoutEnd]));
        Assert.Equal((90, 70, 120, 140, 2, 4, 80, 90, 60), (r[CombatValue.MoShakenAim], r[CombatValue.MoBrokenAim], r[CombatValue.MoShakenSuppress],
            r[CombatValue.MoBrokenSuppress], r[CombatValue.MoShakenReact], r[CombatValue.MoBrokenReact], r[CombatValue.MoShakenH],
            r[CombatValue.MoShakenForce], r[CombatValue.MoBrokenForce]));
    }

    // Brief 7.3: a member wounded -5, down -8, killed -15; the leader killed -15 and -20 more, with 11.8's 10 s pause. A squad at rest
    // starts at 80 and its rates don't take it above (the rates restore, events go above).
    [Fact]
    public void LossesCostMoraleAndTheLeaderMore()
    {
        var w = New();
        var men = Men(w, 1, 20, 20, 5);
        var s = Form(w, men);
        for (var i = 0; i < 200; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        Assert.Equal((8000, MoraleState.Steady), (m.Morale100, m.Spirit));
        var leader = s.Leader;
        var others = men.Where(u => u.Id != leader).ToArray();
        int Hit(Unit u, int hp)
        {
            var before = m.Morale100;
            u.HP = hp;
            w.Step();
            return m.Morale100 - before;
        }
        var max = others[0].Def.MaxHp;
        var wound = Hit(others[0], max * 30 / 100);
        var down = Hit(others[1], max * 10 / 100);
        var killed = Hit(others[2], 0);
        var lead = Hit(men.First(u => u.Id == leader), 0);
        output.WriteLine($"wounded {wound} down {down} killed {killed} leader {lead} -> {m.Morale100} {m.Spirit} pause {m.LeaderPauseUntil} tick {w.Tick}");
        Assert.InRange(wound, -500, -300); // (a think between: +3/s quiet, +1/s the leader)
        Assert.InRange(down, -800, -600);
        Assert.InRange(killed, -1500, -1300);
        Assert.InRange(lead, -3500, -3300);
        Assert.Equal(MoraleState.Broken, m.Spirit);
        Assert.True(m.LeaderPauseUntil > w.Tick + 150, "the leader's pause");
    }

    // Part 4 (the coordinator's answer, rule 8.4's arithmetic): losses set a ceiling of 80 - 15 x the men lost until the squad rallies;
    // a kill's +5 lifts it only up to there, so an even trade no longer cancels itself: three for three leaves 35 (Shaken), and the
    // side that lost four of six (80 - 4 x 15 = 20) breaks. On the base the kills' +5 made it 50 and 40.
    [Fact]
    public void AnEvenTradeBreaksTheSideThatLostFourOfSix()
    {
        var w = New(64);
        var a = Men(w, 1, 20, 20, 6);
        var b = Men(w, 2, 27, 20, 6); // in sight, in a fight (not calm: the ceiling holds), but holding fire: the kills are the test's
        var sa = Form(w, a);
        var sb = Form(w, b);
        foreach (var (p, men) in new[] { ((byte)1, a), ((byte)2, b) })
        {
            w.Issue(new Command { Tick = w.Tick, Player = p, Kind = CommandKind.SetStance, Stance = Stance.HoldFire, Units = [.. men.Select(u => u.Id)] });
        }
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        void Kill(Unit t, Unit by)
        {
            var hp = t.HP;
            t.HP = 0;
            w.NoteHurt(t, by, hp);
            for (var i = 0; i < 4; i++)
            {
                w.Step();
            }
        }
        var (ma, mb) = (sa.Mind!, sb.Mind!);
        var morale = new List<(int, int)>();
        for (var k = 0; k < 4; k++)
        {
            Kill(b[5 - k], a[0]); // a's leader-free rank shoots (a[0] and b[0] live: no leader penalty)
            Kill(a[5 - k], b[0]);
            morale.Add((ma.Morale100, mb.Morale100));
        }
        output.WriteLine(string.Join(" ", morale.Select(p => $"{p.Item1}/{p.Item2}")) + $" {ma.Spirit}/{mb.Spirit}");
        Assert.InRange(morale[2].Item1, 3000, 3500); // three for three: 35 at best, Shaken
        Assert.Equal(MoraleState.Broken, ma.Spirit); // four of six lost: Broken although it killed four
        Assert.Equal(MoraleState.Broken, mb.Spirit);
    }

    // Brief 7.3, 12.4: Broken under 20, back to Shaken only at 40 (rally), Steady at 50; routing under 10 until 20, still Broken.
    [Fact]
    public void TheStatesFollowTheThresholdsWithTheRally()
    {
        var w = New();
        var s = Form(w, Men(w, 1, 20, 20, 4));
        for (var i = 0; i < 20; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        (MoraleState, bool) At(int v)
        {
            m.Morale100 = v;
            Think(w, s);
            return (m.Spirit, m.Routing);
        }
        Assert.Equal((MoraleState.Broken, false), At(1700));
        Assert.Equal((MoraleState.Broken, false), At(3700)); // not yet rallied
        Assert.Equal((MoraleState.Shaken, false), At(4100));
        Assert.Equal((MoraleState.Steady, false), At(5000));
        Assert.Equal((MoraleState.Shaken, false), At(4600));
        Assert.Equal((MoraleState.Broken, true), At(700));
        Assert.Equal((MoraleState.Broken, true), At(1700)); // the rout lasts to 20
        Assert.Equal((MoraleState.Broken, false), At(2100)); // still Broken until 40
        Assert.All(s.Members, id => Assert.Equal(MoraleState.Broken, w.Unit(id)!.Fight!.Spirit));
    }

    // Brief 7.3: a Shaken soldier aims 10% worse, a Broken one 30%.
    [Fact]
    public void ShakenAndBrokenMenAimWorse()
    {
        var w = New();
        var a = w.Spawn(1, "Rifleman", 20, 20);
        var e = w.Spawn(2, "Rifleman", 23, 20);
        w.Step();
        var weapon = a.Def.Weapons[0];
        var steady = w.HitChance(a, e, weapon);
        a.Fight!.Spirit = MoraleState.Shaken;
        var shaken = w.HitChance(a, e, weapon);
        a.Fight.Spirit = MoraleState.Broken;
        var broken = w.HitChance(a, e, weapon);
        output.WriteLine($"steady {steady} shaken {shaken} broken {broken}");
        Assert.InRange(shaken * 100 / steady, 89, 90);
        Assert.InRange(broken * 100 / steady, 69, 70);
    }

    // 12.4, 12.2: a broken squad with no order withdraws to safety and only returns fire (cowed); any order of the player lifts that
    // (decision 6), the state stays.
    [Fact]
    public void ABrokenIdleSquadGoesToSafetyAndAnOrderLiftsIt()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 4, 2);
        var red = Men(w, 2, 25, 18, 6, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        var withdrew = false;
        for (var i = 0; i < 200 && !withdrew; i++)
        {
            m.Morale100 = 1500;
            w.Step();
            withdrew = m.Choice == SquadChoice.Withdraw;
        }
        output.WriteLine($"{w.Tick} choice {m.Choice} spirit {m.Spirit} to {m.WithdrawTo} in contact {m.InContact}");
        Assert.True(withdrew, "a broken idle squad withdraws");
        Assert.All(blue.Where(u => u.Alive), u => Assert.True(u.Fight!.Cowed));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [blue[0].Id], Cell = new Cell(30, 20) });
        w.Step();
        Assert.Equal(MoraleState.Broken, m.Spirit);
        Assert.All(blue.Where(u => u.Alive), u => Assert.False(u.Fight!.Cowed));
        Think(w, s);
        Assert.All(blue.Where(u => u.Alive), u => Assert.False(u.Fight!.Cowed)); // under the order
    }

    // Part 2 (12.2, the coordinator's answer): a broken idle squad garrisons its safety building (the shelter), not its door.
    [Fact]
    public void ABrokenSquadGoesIntoItsSafetyBuilding()
    {
        var w = New();
        var house = w.Spawn(1, "resistance_barracks", 6, 8); // its door at (11, 20): behind the line
        var blue = Men(w, 1, 20, 20, 4, 2);
        var red = Men(w, 2, 25, 18, 6, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [.. blue.Select(u => u.Id)], Stance = Stance.Guard });
        var s = Form(w, blue);
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        for (var i = 0; i < 600 && blue.Any(u => u.Alive && u.Garrison == 0); i++)
        {
            m.Morale100 = Math.Min(m.Morale100, 1500);
            var was = m.BrokenSafe;
            w.Step();
            if (m.BrokenSafe && !was)
            {
                output.WriteLine($"safe t{w.Tick} building {m.SafeBuilding} orders {string.Join(" ", blue.Select(u => u.Order.Kind))} to {m.WithdrawTo}");
            }
            if (i % 10 == 0 && i < 100)
            {
                output.WriteLine($"t{w.Tick} building {m.SafeBuilding} {m.Choice} {m.Spirit} orders {string.Join(" ", blue.Select(u => u.Order.Kind))} to {m.WithdrawTo}");
            }
        }
        output.WriteLine($"pass {w.Grid.Passable(World.DoorCell(house, 0))} {World.DoorCell(house, 1)} {w.Grid.Passable(World.DoorCell(house, 1))} can {w.CanGarrison(blue[0], house)} door {World.DoorCell(house, 0)} doors {World.DoorCount(house)} at {string.Join(" ", blue.Select(u => $"{u.Pos.X.Raw / 1024},{u.Pos.Y.Raw / 1024} {u.Order.Kind} {u.Fight?.Gait}"))}");
        output.WriteLine($"axis {m.Axis} line {m.HasLine} group {m.GroupCount} contact {m.InContact} stance {blue[0].Stance} mode {s.Mode} order {s.Order}");
        output.WriteLine($"{w.Tick} choice {m.Choice} spirit {m.Spirit} to {m.WithdrawTo} safe {m.BrokenSafe} inside {blue.Count(u => u.Garrison == house.Id)}");
        Assert.All(blue.Where(u => u.Alive), u => Assert.Equal(house.Id, u.Garrison));
    }

    // 12.4, 7.3's exception 2: routing with no order, the men run to safety backs turned, without firing.
    [Fact]
    public void ARoutingSquadRunsWithoutFiring()
    {
        var w = New();
        var blue = Men(w, 1, 20, 20, 4, 2);
        var red = Men(w, 2, 25, 18, 6, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        for (var i = 0; i < 60; i++)
        {
            w.Step();
        }
        var m = s.Mind!;
        int running = 0, shots = 0;
        for (var i = 0; i < 160; i++)
        {
            m.Morale100 = 500;
            w.Step();
            running += blue.Count(u => u.Alive && u.Fight is { Routs: true, Gait: Gait.Run or Gait.Sprint });
            shots += w.ShotEvents.Count(e => blue.Any(u => u.Id == e.Shooter));
        }
        output.WriteLine($"{w.Tick} choice {m.Choice} routing {m.Routing} running {running} shots {shots}");
        Assert.True(m.Routing);
        Assert.True(running > 0, "routing men run");
        Assert.Equal(0, shots);
    }
}

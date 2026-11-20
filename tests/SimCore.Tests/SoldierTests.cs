using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// INF-E2a (docs/design.md, "Wounds, rescue, bodies, ranks and Retreat"): wounds and the down state, rescue, the medic's treatment,
// bodies and scavenging, ranks, and the Retreat order, from the content's soldiers block.
public class SoldierTests
{
    private static Squad Form(World w, IEnumerable<Unit> units)
    {
        var list = units.ToList();
        w.Issue(new Command { Tick = w.Tick, Player = list[0].Owner, Kind = CommandKind.FormSquad, Units = [.. list.Select(u => u.Id)] });
        w.Run(1);
        return w.SquadOf(list[0].Id) ?? throw new InvalidOperationException("no squad formed");
    }

    private static int Rifle(Unit u) => u.Charges[u.Def.Weapons[0].Stock];

    private static List<SoldierEvent> RunCollect(World w, int ticks, Func<bool>? until = null)
    {
        var all = new List<SoldierEvent>();
        for (var i = 0; i < ticks && !(until?.Invoke() ?? false); i++)
        {
            w.Step();
            all.AddRange(w.SoldierEvents);
        }
        return all;
    }

    [Fact]
    public void TheBlockAppliesToInfantryOnly()
    {
        var w = Arena.New(32);
        var r = w.Content.Soldiers!;
        Assert.Equal((35, 15, 900, 1800, 300), (r[SoldierValue.WoundedPercent], r[SoldierValue.DownPercent], r[SoldierValue.BleedTicks],
            r[SoldierValue.BodyTicks], r[SoldierValue.BodyMax]));
        Assert.Equal(SoldierRules.Names.Length, Enum.GetValues<SoldierValue>().Length);
        Assert.NotNull(w.Spawn(1, "Rifleman", 5, 5).Life);
        Assert.True(w.Units.Where(u => u.Def.Tags.HasAll(r.Need)).All(u => u.Life is not null));
        Assert.True(w.Units.Where(u => !u.Def.Tags.HasAll(r.Need)).All(u => u.Life is null));
    }

    [Fact]
    public void WoundedSlowsAndAimsWorseDownCantActAndBleedsOutIntoABody()
    {
        var w = Arena.New(32);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        var load = Rifle(u);
        u.HP = u.Def.MaxHp * 35 / 100; // at 35%: wounded
        w.Step();
        Assert.Equal(WoundState.Wounded, u.Life!.Wound);
        Assert.Contains(w.SoldierEvents, e => e.Kind == SoldierEventKind.Wounded && e.Unit == u.Id);
        Assert.Equal((60, 75), (w.WalkPercent(u), w.AimPercent(u)));
        Assert.Equal((150, 125), (w.PostureChangePercent(u) * 3, w.SuppressionGainPercent(u))); // the INF-E1b hooks: 50% slower, +25%
        u.HP = 5; // under 15%: down
        w.Step();
        Assert.Equal(WoundState.Down, u.Life.Wound);
        Assert.False(u.CanAct);
        var downAt = w.Tick;
        w.Order(1, CommandKind.Move, [u], cell: new Cell(20, 10));
        w.Step();
        Assert.Equal(OrderKind.Idle, u.Order.Kind); // no orders
        var events = RunCollect(w, 1000, () => !u.Alive);
        Assert.False(u.Alive);
        Assert.InRange((int)(w.Tick - downAt), 898, 901); // 45 s
        Assert.Contains(events, e => e.Kind == SoldierEventKind.BledOut && e.Unit == u.Id);
        var body = w.BodyOf(u.Id)!;
        Assert.Equal(load, body.Ammo[(int)AmmoGroup.Rifle]);
        Assert.Equal(0, body.Grenades);
        Assert.Contains(events, e => e.Kind == SoldierEventKind.BodyLies && e.Unit == u.Id);
    }

    [Fact]
    public void DeathFromAHitIsStillDeathAndTheDownedManCrawlsToASquadmate()
    {
        var w = Arena.New(32);
        var a = w.Spawn(1, "Rifleman", 10, 10);
        var b = w.Spawn(1, "Rifleman", 13, 10);
        Form(w, [a, b]);
        b.HP = 0; // to 0 or less: dead at once, as before
        w.Step();
        Assert.False(b.Alive);
        Assert.NotNull(w.BodyOf(b.Id));

        var c = w.Spawn(1, "Rifleman", 13, 10);
        var d = w.Spawn(1, "Rifleman", 10, 10);
        Form(w, [c, d]);
        d.HP = 3;
        w.Step();
        var before = (c.Pos - d.Pos).Len();
        w.Run(100); // 5 s at 0.3 cells a second
        var after = (c.Pos - d.Pos).Len();
        Assert.True(after < before - Fixed.One, $"crawled from {before} to {after}");
        Assert.True(after > before - Fixed.Frac(17, 10));
    }

    [Fact]
    public void ASquadmateCarriesTheDownedManToHardCoverAndStopsTheBleeding()
    {
        var w = Arena.New(32);
        for (var x = 6; x <= 14; x++)
        {
            w.Grid.Set(new Cell(x, 13), Terrain.Blocked); // a wall south of him; the threat is north
        }
        var d = w.Spawn(1, "Rifleman", 10, 10);
        var m = w.Spawn(1, "Rifleman", 12, 10);
        Form(w, [d, m]);
        d.LastHitFrom = new Cell(10, 0).Center();
        d.LastHitTick = 1;
        d.HP = 4;
        var events = RunCollect(w, 900, () => d.Life!.Carrier == 0 && d.Life.BleedPausedUntil > 0);
        Assert.Contains(events, e => e.Kind == SoldierEventKind.CarryStart && e.Unit == m.Id && e.Other == d.Id);
        Assert.Contains(events, e => e.Kind == SoldierEventKind.CarryStop && e.Unit == m.Id && e.Value == 1);
        Assert.True(d.Pos.Y > new Cell(0, 13).Center().Y, $"put down behind the wall: {d.Pos}");
        Assert.Equal(2, m.Life!.Xp);
        var left = d.Life!.BleedLeft;
        w.Run(100);
        Assert.Equal(left, d.Life.BleedLeft); // pressure on the wound: no bleeding for 15 s
    }

    [Fact]
    public void TheMedicTreatsTheDownedFirstBackToWounded()
    {
        var w = Arena.NewSentinel(32);
        var medic = w.Spawn(1, "sentinel_medic", 10, 10);
        var hurt = w.Spawn(1, "Rifleman", 13, 10);
        var down = w.Spawn(1, "Rifleman", 7, 10);
        hurt.HP = hurt.Def.MaxHp * 30 / 100;
        down.HP = 4;
        w.Step();
        Assert.Equal(WoundState.Down, down.Life!.Wound);
        var events = RunCollect(w, 600, () => down.Life!.Wound != WoundState.Down);
        Assert.Equal(down.Id, events.First(e => e.Kind == SoldierEventKind.TreatStart).Other);
        var treated = events.Single(e => e.Kind == SoldierEventKind.Treated);
        Assert.Equal((down.Id, medic.Id), (treated.Unit, treated.Other));
        Assert.Equal(down.Def.MaxHp * 35 / 100, treated.Value); // back to wounded at 35% (then his aid bag heals him as before)
        Assert.NotEqual(WoundState.Down, down.Life!.Wound);
        Assert.Equal(2, medic.Life!.Xp);
        w.Run(800); // then the wounded: his aid bag heals them both above 35%
        Assert.Equal(WoundState.Healthy, hurt.Life!.Wound);
    }

    [Fact]
    public void TheAuraNeverHealsADownedMan()
    {
        var w = Arena.NewSentinel(32);
        var medic = w.Spawn(1, "sentinel_medic", 10, 10);
        var down = w.Spawn(1, "Rifleman", 11, 10);
        w.Order(1, CommandKind.Move, [medic], cell: new Cell(10, 10)); // an order of the player's: no treatment meanwhile
        down.HP = 4;
        w.Run(30);
        Assert.Equal(4, down.HP);
    }

    [Fact]
    public void BodiesLast90SecondsAtMost300TheOldestFirst()
    {
        var w = Arena.New(64);
        var men = new List<Unit>();
        for (var i = 0; i < 301; i++)
        {
            men.Add(w.Spawn(1, "Rifleman", 2 + i % 60, 2 + i / 60 * 2));
        }
        foreach (var u in men)
        {
            u.HP = 0;
        }
        w.Step();
        Assert.Equal(300, w.Bodies.Count);
        Assert.Null(w.BodyOf(men[0].Id));
        Assert.Equal(men[1].Id, w.Bodies[0].Id);
        w.Run(1799);
        Assert.Equal(300, w.Bodies.Count);
        w.Step();
        Assert.Empty(w.Bodies);
    }

    [Fact]
    public void ScavengingByOrderAndOnHisOwnWhenLowOnAmmo()
    {
        var w = Arena.New(32);
        var dead = w.Spawn(2, "Rifleman", 14, 10); // either side's dead
        dead.HP = 0;
        w.Step();
        var body = w.BodyOf(dead.Id)!;
        var full = body.Ammo[(int)AmmoGroup.Rifle];
        var s = w.Spawn(1, "Rifleman", 8, 10);
        var load = Rifle(s);
        s.MutableCharges[s.Def.Weapons[0].Stock] = 100; // half: not low, he doesn't go on his own
        w.Run(80);
        Assert.Equal(100, Rifle(s));
        w.Order(1, CommandKind.Scavenge, [s], target: dead.Id);
        var events = RunCollect(w, 400, () => Rifle(s) != 100);
        Assert.Equal(load, Rifle(s));
        Assert.Equal(full - (load - 100), body.Ammo[(int)AmmoGroup.Rifle]);
        var took = events.Single(e => e.Kind == SoldierEventKind.Scavenged);
        Assert.Equal((s.Id, dead.Id, load - 100), (took.Unit, took.Other, took.Value));
        Assert.Single(events, e => e.Kind == SoldierEventKind.ScavengeStart);

        var low = w.Spawn(1, "Rifleman", 12, 12); // within 4 cells of the body, under 25%
        low.MutableCharges[low.Def.Weapons[0].Stock] = 20;
        var t0 = w.Tick;
        RunCollect(w, 400, () => Rifle(low) != 20);
        Assert.Equal(20 + (full - (load - 100)), Rifle(low)); // he took what was left (it fits)
        Assert.True(w.Tick - t0 >= 60);
    }

    [Fact]
    public void KillsGivePointsRanksGiveAimAndTheHighestRankLeads()
    {
        var w = Arena.New(32);
        var shooter = w.Spawn(1, "Rifleman", 10, 10, Facing.East);
        var victim = w.Spawn(2, "Rifleman", 13, 10, Facing.West);
        Array.Fill(victim.MutableCharges, 0);
        Array.Fill(victim.Arms!.MutableMag, 0);
        shooter.Life!.Xp = 7;
        victim.HP = 10;
        var events = RunCollect(w, 400, () => !victim.Alive);
        Assert.False(victim.Alive);
        w.Step();
        Assert.Equal(9, shooter.Life.Xp);
        Assert.Equal(SoldierRank.Regular, shooter.Life.Rank);
        Assert.Contains(w.SoldierEvents.Concat(events), e => e.Kind == SoldierEventKind.RankUp && e.Unit == shooter.Id && e.Value == 1);
        Assert.Equal(105, w.AimPercent(shooter));

        var men = new[] { w.Spawn(1, "Rifleman", 20, 20), w.Spawn(1, "Rifleman", 22, 20), w.Spawn(1, "Rifleman", 24, 20) };
        var s = Form(w, men);
        Assert.Equal(men[0].Id, s.Leader);
        men[2].Life!.Xp = 20;
        w.Step();
        Assert.Equal(SoldierRank.Veteran, men[2].Life!.Rank);
        Assert.Equal(men[2].Id, s.Leader);
        Assert.Equal(90, w.ReloadPercent(men[2]));
    }

    [Fact]
    public void SurvivingABattleGivesPoints()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 10, 10, Facing.East);
        var b = w.Spawn(2, "Rifleman", 13, 10, Facing.West);
        Array.Fill(b.MutableCharges, 0);
        Array.Fill(b.Arms!.MutableMag, 0);
        for (var i = 0; i < 20; i++)
        {
            b.HP = b.Def.MaxHp;
            w.Step();
        }
        Assert.True(a.Life!.InBattle);
        b.HP = b.Def.MaxHp * 30 / 100; // wounded during it
        w.Step();
        b.Pos = new Cell(60, 60).Center(); // the fight is over
        w.Run(700);
        Assert.False(a.Life.InBattle);
        Assert.Equal(3, a.Life.Xp);
        Assert.Equal(3 + 2, b.Life!.Xp); // he was wounded during it
    }

    [Fact]
    public void RetreatFallsBackInPairsAwayFromTheThreatThenHolds()
    {
        static (World W, Squad S, Unit[] Men, List<ulong> Hashes, List<SoldierEvent> Events, int MaxMoving) Run(Cell? to)
        {
            var w = Arena.New(64);
            var men = new[] { w.Spawn(1, "Rifleman", 30, 20), w.Spawn(1, "Rifleman", 32, 20), w.Spawn(1, "Rifleman", 34, 20), w.Spawn(1, "Rifleman", 36, 20) };
            var enemy = w.Spawn(2, "Rifleman", 33, 12, Facing.South);
            Array.Fill(enemy.MutableCharges, 0);
            Array.Fill(enemy.Arms!.MutableMag, 0);
            var s = Form(w, men);
            w.Run(5);
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Retreat, Units = [men[1].Id], Cell2 = to });
            var hashes = new List<ulong>();
            var events = new List<SoldierEvent>();
            var maxMoving = 0;
            for (var i = 0; i < 2400 && !events.Any(e => e.Kind == SoldierEventKind.RetreatEnd); i++)
            {
                w.Step();
                enemy.HP = enemy.Def.MaxHp;
                events.AddRange(w.SoldierEvents);
                hashes.Add(w.Hash());
                Assert.Equal(w.HashReference(), hashes[^1]);
                if (s.Retreating)
                {
                    maxMoving = Math.Max(maxMoving, men.Count(m => m.Order.Kind == OrderKind.Move && // bounding, not finishing his last step
                        s.RetreatPlaces.Single(p => p.Unit == m.Id).Cell is var c && (c.Center() - m.Pos).Len() > Fixed.Frac(15, 10)));
                }
            }
            return (w, s, men, hashes, events, maxMoving);
        }
        var (w, s, men, hashes, events, maxMoving) = Run(null);
        var start = events.Single(e => e.Kind == SoldierEventKind.RetreatStart);
        Assert.InRange(start.At.Y.Raw, new Cell(0, 26).Center().Y.Raw - Fixed.OneRaw, new Cell(0, 40).Center().Y.Raw + Fixed.OneRaw); // 6 to 20 cells back
        Assert.Equal(1, events.Single(e => e.Kind == SoldierEventKind.RetreatEnd).Value);
        Assert.InRange(maxMoving, 1, 2); // one pair at a time
        Assert.Equal(SquadMode.Hold, s.Mode);
        Assert.All(men, m => Assert.True(m.Pos.Y > new Cell(0, 24).Center().Y, $"{m.Id} at {m.Pos}"));
        Assert.Equal(hashes, Run(null).Hashes); // the same run twice: the same hashes

        var dragged = Run(new Cell(50, 40));
        Assert.Equal(new Cell(50, 40).Center(), dragged.Events.Single(e => e.Kind == SoldierEventKind.RetreatStart).At);
        Assert.Equal(1, dragged.Events.Single(e => e.Kind == SoldierEventKind.RetreatEnd).Value);
    }

    [Fact]
    public void AnotherOrderEndsTheRetreat()
    {
        var w = Arena.New(64);
        var men = new[] { w.Spawn(1, "Rifleman", 30, 20), w.Spawn(1, "Rifleman", 32, 20) };
        var s = Form(w, men);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Retreat, Units = [men[0].Id] });
        w.Run(10);
        Assert.True(s.Retreating);
        w.Order(1, CommandKind.Move, [men[0]], cell: new Cell(30, 10));
        var events = RunCollect(w, 2);
        Assert.False(s.Retreating);
        Assert.Equal(0, events.Single(e => e.Kind == SoldierEventKind.RetreatEnd).Value);
    }
}

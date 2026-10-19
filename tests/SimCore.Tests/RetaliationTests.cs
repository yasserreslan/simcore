using SimCore.FixedPoint;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// Retaliation, the leash and the walk back (the unit AI, Zero Hour's player rules).
public class RetaliationTests
{
    private static void SetStance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    private static long Cells(Vec a, Vec b) => (a - b).Len().Raw / Fixed.OneRaw;

    // A marksman 12 cells off hits a rifleman (vision 7: it can't see him). He and an idle friend
    // within 8 cells go after the shot; one 12 cells away stays put. Once the marksman withdraws
    // past the leash (vision x 2.2 = 15.4 cells from their guard points) they walk back and idle.
    [Fact]
    public void OutrangedVictimChasesAndReturns()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 30, 40);
        var b = w.Spawn(1, "Rifleman", 34, 40);
        var c = w.Spawn(1, "Rifleman", 42, 40);
        a.HP = b.HP = 10_000; // they live through the test
        var sniper = w.Spawn(2, "Marksman", 30, 28);
        sniper.HP = 10_000;
        var (homeA, homeB, homeC) = (a.Pos, b.Pos, c.Pos);

        w.Run(5);
        Assert.Equal(sniper.Id, a.LastHitBy);
        Assert.Equal(AiState.Engage, a.Ai);
        Assert.Equal(AiState.Engage, b.Ai);
        Assert.Equal(AiState.None, c.Ai); // 12 cells from the victim: too far to help
        Assert.Equal(homeA, a.GuardPoint);
        Assert.Equal(homeB, b.GuardPoint);

        w.Run(150);
        Assert.True(Cells(a.Pos, homeA) >= 3, $"a moved {Cells(a.Pos, homeA)}");
        Assert.True(Cells(b.Pos, homeB) >= 3, $"b moved {Cells(b.Pos, homeB)}");
        Assert.Equal(homeC, c.Pos);

        // The marksman runs north, past the leash.
        SetStance(w, sniper, Stance.HoldFire);
        w.Order(2, CommandKind.Move, [sniper], cell: new Cell(30, 2));
        var returned = false;
        for (var i = 0; i < 900 && !(a.Ai == AiState.None && b.Ai == AiState.None); i++)
        {
            w.Run(1);
            returned |= a.Ai == AiState.Return;
        }
        Assert.True(returned);
        Assert.Equal(AiState.None, a.Ai);
        Assert.Equal(AiState.None, b.Ai);
        Assert.True(Cells(a.Pos, homeA) <= 1, $"a is {Cells(a.Pos, homeA)} cells from home");
        Assert.True(Cells(b.Pos, homeB) <= 1, $"b is {Cells(b.Pos, homeB)} cells from home");
        Assert.Equal(OrderKind.Idle, a.Order.Kind);
    }

    // The attacker stays inside the leash but out of reach (behind a wall of rock): the chase gives
    // up after the chase time (300 ticks) and the rifleman walks back.
    [Fact]
    public void TimerGivesUp()
    {
        var w = Arena.New(64);
        for (var x = 0; x < 64; x++)
        {
            w.Grid.Set(new Cell(x, 33), Terrain.Blocked);
            w.Grid.Set(new Cell(x, 34), Terrain.Blocked);
        }
        var a = w.Spawn(1, "Rifleman", 30, 40);
        a.HP = 10_000;
        var sniper = w.Spawn(2, "Marksman", 30, 29);
        var home = a.Pos;
        var t0 = 0u;
        for (var i = 0; i < 40 && a.Ai != AiState.Engage; i++)
        {
            w.Run(1);
            t0 = w.Tick;
        }
        Assert.Equal(AiState.Engage, a.Ai);
        sniper.Stance = Stance.HoldFire; // no new hits: one chase
        w.Run(280);
        Assert.Equal(AiState.Engage, a.Ai);
        w.Run(40);
        Assert.NotEqual(AiState.Engage, a.Ai); // gave up at 300 ticks
        Assert.True(w.Tick - t0 >= 300);
        w.Run(600);
        Assert.Equal(AiState.None, a.Ai);
        Assert.True(Cells(a.Pos, home) <= 1);
        Assert.Equal(sniper.Def.MaxHp, sniper.HP);
    }

    public enum Case
    {
        HoldGround,
        ReturnFire,
        HoldFire,
        MoveOrder,
        Engineer,
        RocketTruck,
        Driver,
        TooFar,
    }

    // Victims that never move on their own: stances that don't pursue, a unit with an order, units
    // whose archetype doesn't pursue (pistol, launchers), a driver inside his vehicle, and a shooter
    // beyond 20 cells.
    [Theory]
    [InlineData(Case.HoldGround)]
    [InlineData(Case.ReturnFire)]
    [InlineData(Case.HoldFire)]
    [InlineData(Case.MoveOrder)]
    [InlineData(Case.Engineer)]
    [InlineData(Case.RocketTruck)]
    [InlineData(Case.Driver)]
    [InlineData(Case.TooFar)]
    public void NotTriggered(Case c)
    {
        var w = Arena.New(96);
        Unit victim;
        switch (c)
        {
            case Case.Engineer:
                victim = w.Spawn(1, "Sapper", 40, 60);
                break;
            case Case.RocketTruck:
                victim = w.SpawnCrewed(1, "light_rocket_truck", 40, 60).Vehicle;
                break;
            case Case.Driver:
                victim = w.SpawnCrewed(1, "technical", 40, 60).Driver;
                break;
            case Case.TooFar:
                victim = w.SpawnCrewed(1, "technical", 40, 60).Vehicle;
                break;
            default:
                victim = w.Spawn(1, "Rifleman", 40, 60);
                break;
        }
        victim.HP = 10_000;
        var home = victim.Pos;
        if (c == Case.HoldGround)
        {
            victim.Stance = Stance.HoldGround;
        }
        if (c == Case.ReturnFire)
        {
            victim.Stance = Stance.ReturnFire;
        }
        if (c == Case.HoldFire)
        {
            victim.Stance = Stance.HoldFire;
        }
        var shooter = c == Case.TooFar ? w.Spawn(2, "kornet", 40, 35) : w.Spawn(2, "Marksman", 40, 50);
        shooter.HP = 10_000;
        if (c == Case.TooFar)
        {
            var spotter = w.Spawn(2, "worker", 40, 58); // a spotter for the Kornet, 25 cells off
            spotter.HP = 100_000;
        }
        if (c == Case.MoveOrder)
        {
            w.Order(1, CommandKind.Move, [victim], cell: new Cell(90, 60)); // across the marksman's front
        }
        var hit = false;
        for (var i = 0; i < 200; i++)
        {
            w.Run(1);
            hit |= victim.LastHitBy == shooter.Id || (c == Case.Driver && w.Unit(victim.Inside)?.LastHitBy == shooter.Id);
            Assert.Equal(AiState.None, victim.Ai);
        }
        Assert.True(hit, "the shooter hit");
        if (c != Case.MoveOrder)
        {
            Assert.Equal(home, victim.Pos);
        }
    }

    // A friend with an order of its own doesn't join; one idle does.
    [Fact]
    public void FriendsWithOrdersStayOnThem()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 30, 40);
        var busy = w.Spawn(1, "Rifleman", 33, 40);
        var idle = w.Spawn(1, "Rifleman", 27, 40);
        a.HP = busy.HP = idle.HP = 10_000;
        w.Order(1, CommandKind.Move, [busy], cell: new Cell(33, 60));
        w.Spawn(2, "Marksman", 30, 28).HP = 10_000;
        w.Run(5);
        Assert.Equal(AiState.Engage, a.Ai);
        Assert.Equal(AiState.Engage, idle.Ai);
        Assert.Equal(AiState.None, busy.Ai);
        Assert.Equal(OrderKind.Move, busy.Order.Kind);
    }

    // A rocket salvo hitting a cluster: each victim calls for help at most once per cooldown
    // (20 ticks), however many rockets land on it.
    [Fact]
    public void SalvoCooldown()
    {
        var w = Arena.New(96);
        var squad = new List<Unit>();
        for (var i = 0; i < 9; i++)
        {
            var u = w.Spawn(1, "Rifleman", 40 + i % 3, 60 + i / 3);
            u.HP = 100_000;
            squad.Add(u);
        }
        var (truck, _) = w.SpawnCrewed(2, "medium_rocket_launcher", 41, 50); // 6-rocket salvos, 12 cells
        truck.HP = 100_000;
        var spotter = w.Spawn(2, "worker", 44, 57);
        spotter.HP = 100_000;
        var hits = 0;
        var ticks = 0;
        for (; ticks < 400; ticks++)
        {
            w.Run(1);
            hits += squad.Count(u => u.LastHitTick + 1 == w.Tick && u.LastHitBy == truck.Id);
        }
        Assert.True(hits > 0);
        var cap = squad.Count * (ticks / 20 + 1);
        Assert.True(w.RetaliateSearches <= cap, $"{w.RetaliateSearches} searches for {hits} hits (cap {cap})");
    }

    // Fog of war: a marksman in the fog hits a rifleman. The rifleman and his friend can't see him;
    // they head for where the shot came from, spot him on the way and fight him.
    [Fact]
    public void UnspottedShooter()
    {
        var w = Arena.New(64, edit: s => s with { Fog = true });
        var a = w.Spawn(1, "Rifleman", 30, 40);
        var b = w.Spawn(1, "Rifleman", 33, 40);
        a.HP = b.HP = 10_000;
        var sniper = w.Spawn(2, "Marksman", 30, 29);
        w.Run(5);
        Assert.False(World.IsVisibleTo(sniper, 1));
        Assert.Equal(AiState.Engage, a.Ai);
        Assert.Equal(sniper.Pos, a.SeekPoint);
        var seen = false;
        for (var i = 0; i < 400 && sniper.Alive; i++)
        {
            w.Run(1);
            seen |= World.IsVisibleTo(sniper, 1);
        }
        Assert.True(seen);
        Assert.False(sniper.Alive);
    }

    // Walking back, an enemy that comes within the inner guard circle (vision x 1.8 = 12.6 cells)
    // is engaged.
    [Fact]
    public void ReturnScanEngagesInTheInnerCircle()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 30, 40);
        a.HP = 10_000;
        var sniper = w.Spawn(2, "Marksman", 30, 28);
        sniper.HP = 10_000;
        w.Run(120);
        Assert.Equal(AiState.Engage, a.Ai);
        sniper.Stance = Stance.HoldFire;
        w.Order(2, CommandKind.Move, [sniper], cell: new Cell(30, 2));
        for (var i = 0; i < 400 && a.Ai != AiState.Return; i++)
        {
            w.Run(1);
        }
        Assert.Equal(AiState.Return, a.Ai);
        var at = Cell.Of(a.Pos);
        var intruder = w.Spawn(2, "Rifleman", at.X + 3, at.Y + 1); // spotted (7 cells of vision) and inside the circle
        Assert.True(World.Within(a.GuardPoint, intruder.Pos, Fixed.FromRaw(a.Def.Vision.Raw * 180 / 100)));
        intruder.Stance = Stance.HoldFire;
        for (var i = 0; i < 40 && a.AiTarget != intruder.Id; i++)
        {
            w.Run(1);
        }
        Assert.Equal(AiState.Engage, a.Ai);
        Assert.Equal(intruder.Id, a.AiTarget);
    }

    // Stop ends a chase where the unit stands.
    [Fact]
    public void StopEndsTheChase()
    {
        var w = Arena.New(64);
        var a = w.Spawn(1, "Rifleman", 30, 40);
        a.HP = 10_000;
        w.Spawn(2, "Marksman", 30, 28).HP = 10_000;
        w.Run(30);
        Assert.Equal(AiState.Engage, a.Ai);
        w.Order(1, CommandKind.Stop, [a]);
        w.Run(1);
        Assert.Equal(AiState.None, a.Ai);
        Assert.False(a.Moving);
    }

    // A garrisoned soldier never leaves the building to retaliate.
    [Fact]
    public void GarrisonedSoldiersStayInside()
    {
        var w = Arena.New(64);
        var def = w.Player(1)!.Setup.Find(w.Content, "block_brick_4")!;
        var b = w.Unit(w.Spawn(def, new Cell(24, 36), Facing.South))!;
        var s = w.Spawn(1, "Rifleman", 30, 46);
        w.Order(1, CommandKind.Enter, [s], b.Id);
        w.Run(200);
        Assert.Equal(b.Id, s.Garrison);
        var rpg = w.Spawn(2, "Tank hunter", 31, 26); // rockets: they hurt the occupants
        rpg.HP = 10_000;
        b.HP = 100_000;
        for (var i = 0; i < 300; i++)
        {
            w.Run(1);
            Assert.Equal(AiState.None, s.Ai);
        }
        Assert.Equal(b.Id, s.Garrison);
    }

    // The spatial index and a scan of every unit give the same fight, tick for tick.
    [Fact]
    public void SameWorldWithAndWithoutTheSpatialIndex()
    {
        static World Build(bool full)
        {
            var w = Arena.New(64, seed: 7);
            w.FullScan = full;
            for (var i = 0; i < 6; i++)
            {
                w.Spawn(1, i % 2 == 0 ? "Rifleman" : "Gunner", 24 + i * 3, 44 + i % 3);
            }
            w.Spawn(2, "Marksman", 30, 34);
            w.Spawn(2, "Marksman", 36, 35);
            w.Spawn(2, "Rifleman", 44, 34);
            w.SpawnCrewed(2, "technical", 20, 32);
            return w;
        }
        var a = Build(false);
        var b = Build(true);
        for (var i = 0; i < 600; i++)
        {
            a.Step();
            b.Step();
            Assert.Equal(a.Hash(), b.Hash());
        }
        Assert.True(a.Units.Any(u => u.Ai != AiState.None) || a.RetaliateSearches > 0);
    }
}

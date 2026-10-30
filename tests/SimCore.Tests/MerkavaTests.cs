using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Sentinel's Merkava type tank (merkava_tank, "Merkava"; docs/design.md, "Merkava type tank"; the owner's spec of
// 2026-10-30): the heaviest tank (3,000 HP, directional armour 35/75/120/150%, radius 13, speed 26), its own 120 mm gun
// (merkava_120mm: 50 rounds, 22 cells, HE 350 with a 4-cell splash or the dart 1,100), the Abrams type's machine guns, smoke and
// Trophy (now 4 charges on both tanks, missiles only), and 5 passengers: soldiers but not the heavy weapon soldiers (crew
// passengerExcludeTags), in and out at the rear door (crew door "rear", World.DoorPoint), never firing from inside, and
// surviving its destruction by the killing hit's damage before armour (crew passengerSurvive: under 1,500 all, under 2,500
// each 50%, else none; World.BailOuts).
public class MerkavaTests(ITestOutputHelper output)
{
    private const string Tank = "merkava_tank";
    private const string Abrams = "main_battle_tank";

    // A Sentinel (player 1) against Resistance (player 2) world.
    private static World Sentinel(int n = 96, ulong seed = 1) => Arena.NewSentinel(n, seed);

    // A world where both players field the Sentinel (tank against tank).
    private static World Duel(int n = 96, ulong seed = 1) =>
        Arena.NewSentinel(n, seed, m => m with { Players = [m.Players[0], m.Players[0] with { Id = 2, Army = [] }] });

    private static void HoldFire(World w, params Unit[] units)
    {
        foreach (var u in units)
        {
            w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.HoldFire });
        }
    }

    // Behind: how far point p lies behind unit v's hull along its facing (positive: behind it), in raw units.
    private static long Behind(Vec p, Vec at, int facing)
    {
        var d = Facing.Dir(facing);
        var off = at - p;
        return (off.X.Raw * d.X.Raw + off.Y.Raw * d.Y.Raw) / Fixed.OneRaw;
    }

    // Loaded: a crewed Merkava of player 1 at (x, y) with 5 riflemen in the back.
    private static (Unit Tank, Unit Driver, List<Unit> Riders) Loaded(World w, int x, int y, int facing = Facing.East)
    {
        var (t, d) = w.SpawnCrewed(1, Tank, x, y, facing);
        var riders = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", x, y)).ToList();
        foreach (var r in riders)
        {
            World.Ride(r, t);
        }
        return (t, d, riders);
    }

    // ---- Content ----

    [Fact]
    public void ItIsASentinelTankFromTheMilitaryGarageOnly()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Tank)!;
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[a.Index]);
        Assert.True(c.Archetype("military_garage")!.Production!.Builds(a.Tags));
        Assert.False(c.Archetype("vehicle_garage")!.Production!.Builds(a.Tags)); // excludeTags: tank

        // The military garage builds one in 60 s and it drives out of its bay; the vehicle garage refuses the order.
        var w = Arena.NewSentinel(96);
        var mg = w.Spawn(1, "military_garage", 20, 10);
        var vg = w.Spawn(1, "vehicle_garage", 70, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [mg.Id, vg.Id], Unit = Tank });
        var built = 0u;
        for (var t = 0; t < 1700 && built == 0; t++)
        {
            w.Step();
            if (w.All(Tank).FirstOrDefault() is { } tank)
            {
                built = w.Tick;
                Assert.Equal(1, tank.Owner);
            }
        }
        output.WriteLine($"built at tick {built}");
        Assert.InRange(built, 1200u, 1300u); // 1,200 ticks in a bay, then out of the door
        Assert.Single(w.All(Tank));
        w.Run(400);
        var m = w.All(Tank).Single();
        Assert.False(m.InGarage);
        Assert.NotEqual(0u, m.Driver); // crewed by the garage
    }

    [Fact]
    public void ItsStatsAreTheSpecs()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Tank)!;
        Assert.Equal((3000, 3500, 1200), (a.MaxHp, a.Cost, a.BuildTime));
        Assert.Equal(c.ArmorClass("armored_vehicle"), a.Armor);
        Assert.Equal(Locomotor.Tracked, a.Locomotor);
        Assert.Equal(Fixed.Frac(26, 200), a.Speed); // 2.6 cells/s
        Assert.Equal(2, a.TurnRate);
        Assert.Equal(Fixed.Frac(13, 10), a.Radius); // bigger than the Abrams type's 12
        Assert.True(a.Turret);
        Assert.Equal(3, a.Crusher);
        Assert.True(a.Tags.Matches(c.TagsOf("tank")) && a.Tags.Matches(c.TagsOf("heavy")));
        Assert.Equal(new DirectionalArmor(35, 75, 120, 150, 4, 4), a.DirectionalArmor);
        var abrams = c.Archetype(Abrams)!;
        Assert.True(a.MaxHp > abrams.MaxHp && a.Radius > abrams.Radius && a.Speed < abrams.Speed);

        // The crew: a driver as the Abrams type's, 5 soldiers in the back (not the heavy weapon soldiers), the rear door, the bands.
        var crew = a.Crew!;
        Assert.Equal((c.TagsOf("soldier"), crew.Survival, crew.ExitDamage), (abrams.Crew!.Tags, abrams.Crew.Survival, abrams.Crew.ExitDamage));
        Assert.Equal(c.TagsOf("soldier"), crew.Tags);
        Assert.Equal(5, crew.Passengers);
        Assert.Equal(c.TagsOf("soldier"), crew.PassengerTags);
        Assert.Equal(c.TagsOf("mortar", "sniper", "atgm"), crew.PassengerExcludeTags);
        Assert.True(crew.RearDoor);
        Assert.Equal(0, crew.UnloadInterval); // all out at once
        Assert.Equal([new SurviveBand(1500, 100), new SurviveBand(2500, 50), new SurviveBand(0, 0)], crew.PassengerSurvive);
        Assert.Equal(40, crew.ExitDamage);

        var w = Sentinel();
        var def = w.Player(1)!.Setup.Find(w.Content, Tank)!;
        Assert.Equal(["merkava_120mm", "tank_mgs", "trophy_aps", "smoke_launchers"], def.Components.Select(x => x.Id));
        var (he, ap, mg) = (def.Weapons[0], def.Weapons[1], def.Weapons[2]);
        Assert.Equal(("he_mpat", 350, Fixed.FromInt(22), 120, Fixed.FromInt(4)), (he.Warhead.Id, he.Damage, he.Range, he.Reload, he.Effect.Splash));
        Assert.Equal(("apfsds", 1100, Fixed.FromInt(22), 120), (ap.Warhead.Id, ap.Damage, ap.Range, ap.Reload));
        Assert.Equal(he.Stock, ap.Stock); // one magazine
        Assert.Equal(50, def.StockMax[he.Stock]);
        Assert.True(ap.Effect.SharedAmmo);
        Assert.True(mg.Effect.Coaxial);
        Assert.Equal(4, def.StockMax[def.ApsStock]); // the same Trophy as the Abrams type's
        Assert.Equal(2, def.StockMax[def.SmokeStock]);
        Assert.Equal(3, def.StockMax.Count); // rounds, Trophy, smoke: no mortar
    }

    [Theory]
    [InlineData("\"door\": \"rear\"", "\"door\": \"front\"", "crew.door must be \"rear\"")]
    [InlineData("\"passengerExcludeTags\": [\"mortar\", \"sniper\", \"atgm\"]", "\"passengerExcludeTags\": [\"mortarz\"]", "unknown tag \"mortarz\"")]
    [InlineData("{ \"below\": 2500, \"survival\": 50 }", "{ \"below\": 1400, \"survival\": 50 }", "rising order")]
    [InlineData("{ \"survival\": 0 }", "{ \"below\": 3000, \"survival\": 0 }", "passengerSurvive[2].below is 3000, want 0..0")]
    [InlineData("{ \"below\": 1500, \"survival\": 100 }", "{ \"below\": 1500, \"survival\": 101 }", "survival is 101, want 0..100")]
    [InlineData("{ \"below\": 1500, \"survival\": 100 }", "{ \"below\": 1500, \"survival\": 100, \"chance\": 1 }", "chance")]
    public void BadPassengerContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources().Select(s => s.Name == "archetypes/sentinel.json" ? s with { Json = Once(s.Json, from, to) } : s);
        var e = Assert.ThrowsAny<Exception>(() => SimCore.Content.ContentLoader.Load(sources));
        output.WriteLine(e.Message);
        Assert.Contains(error, e.Message);
    }

    [Fact]
    public void PassengerRulesNeedPassengerSeats()
    {
        var sources = TestContent.MvpSources().Select(s => s.Name == "archetypes/sentinel.json"
            ? s with { Json = Once(s.Json, "\"passengers\": 5, \"passengerTags\": [\"soldier\"], ", "") } : s);
        var e = Assert.ThrowsAny<Exception>(() => SimCore.Content.ContentLoader.Load(sources));
        Assert.Contains("need crew.passengers", e.Message);
    }

    private static string Once(string json, string from, string to)
    {
        var i = json.IndexOf(from, StringComparison.Ordinal);
        Assert.True(i >= 0 && json.IndexOf(from, i + 1, StringComparison.Ordinal) < 0, $"\"{from}\" once");
        return json[..i] + to + json[(i + from.Length)..];
    }

    // ---- The gun and the armour ----

    [Fact]
    public void ItsGunOutDamagesTheAbramsTypes()
    {
        var w = Sentinel();
        var m = w.Player(1)!.Setup.Find(w.Content, Tank)!;
        var a = w.Player(1)!.Setup.Find(w.Content, Abrams)!;
        for (var i = 0; i < 2; i++)
        {
            Assert.True(m.Weapons[i].Damage > a.Weapons[i].Damage, "HE and the dart both hit harder");
            Assert.True(m.Weapons[i].Range > a.Weapons[i].Range, "and reach further");
        }
        Assert.True(m.Weapons[0].Effect.Splash > a.Weapons[0].Effect.Splash);
        Assert.True(m.StockMax[m.Weapons[0].Stock] > a.StockMax[a.Weapons[0].Stock], "more rounds");

        // Head-on against an Abrams type: the Merkava type's dart takes 440 off its front (1,100 x 40%), the Abrams type's own 360.
        int FirstHit(string shooter)
        {
            var d = Duel();
            var (s, _) = d.SpawnCrewed(1, shooter, 20, 40, Facing.East);
            var (t, _) = d.SpawnCrewed(2, Abrams, 38, 40, Facing.West);
            t.HP = 100_000;
            HoldFire(d, t);
            d.Order(1, CommandKind.Attack, [s], t.Id);
            for (var i = 0; i < 300; i++)
            {
                d.Step();
                if (d.ArmorHits.FirstOrDefault(h => h.Unit == t.Id && h.Damage > 100) is { Unit: not 0 } h)
                {
                    Assert.Equal(HitFacing.Front, h.Facing);
                    return h.Damage;
                }
            }
            return 0;
        }
        Assert.Equal((440, 360), (FirstHit(Tank), FirstHit(Abrams)));
    }

    [Theory]
    [InlineData(Facing.West, HitFacing.Front, 315)]
    [InlineData(Facing.North, HitFacing.Side, 675)]
    [InlineData(Facing.East, HitFacing.Rear, 1080)]
    public void ItsArmourTakesAnAbramsDartByWhereItLands(int facing, HitFacing where, int damage)
    {
        // 900 x 35% on the front, 75% on a side, 120% on the rear.
        var w = Duel();
        var (a, _) = w.SpawnCrewed(2, Abrams, 20, 40, Facing.East);
        var (t, _) = w.SpawnCrewed(1, Tank, 38, 40, facing);
        t.HP = 100_000;
        HoldFire(w, t);
        w.Order(2, CommandKind.Attack, [a], t.Id);
        ArmorHit? hit = null;
        for (var i = 0; i < 300 && hit is null; i++)
        {
            w.Step();
            hit = w.ArmorHits.FirstOrDefault(h => h.Unit == t.Id && h.Damage > 100) is { Unit: not 0 } h ? h : null;
        }
        Assert.Equal((where, damage), (hit!.Value.Facing, hit.Value.Damage));
    }

    // ---- Passengers ----

    [Fact]
    public void FiveSoldiersBoardAtTheRearDoorAndASixthIsRefused()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 40, 40, Facing.East);
        var men = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 48, 37 + i)).ToList(); // in front of it
        var door = World.DoorPoint(t);
        Assert.Equal(-(13 + 10) * Fixed.OneRaw / 10, (door - t.Pos).X.Raw); // a cell behind the hull (radius 1.3 cells)
        w.Order(1, CommandKind.Enter, men, t.Id);
        var last = men.ToDictionary(m => m.Id, m => m.Pos);
        var boardedFrom = new List<Vec>();
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            foreach (var m in men)
            {
                if (m.Inside == t.Id && last[m.Id] != m.Pos)
                {
                    boardedFrom.Add(last[m.Id]);
                }
                last[m.Id] = m.Pos;
            }
        }
        Assert.Equal(5, w.PassengersOf(t).Count);
        Assert.Equal(5, boardedFrom.Count);
        foreach (var p in boardedFrom)
        {
            output.WriteLine($"boarded from ({p.X.Raw / (double)Fixed.OneRaw:F2}, {p.Y.Raw / (double)Fixed.OneRaw:F2}); door ({door.X.Raw / (double)Fixed.OneRaw:F2})");
            Assert.True(World.Within(p, door, Fixed.Frac(16, 10)), "at the rear door (within the 1.5-cell enter range)");
            Assert.True(Behind(p, t.Pos, t.Facing) > Fixed.OneRaw, "behind the hull");
        }
        var left = Assert.Single(men, m => m.Inside == 0);
        Assert.False(w.CanEnter(left, t));
        Assert.True(World.PassengerAllowed(t, left)); // refused only for want of a seat
    }

    [Fact]
    public void TheHeavyWeaponSoldiersAndWorkersAreRefusedByTag()
    {
        // The Sentinel fields none of the heavy weapon soldiers today: a content copy whose Sentinel faction adds them, so they
        // can be the tank's own side's.
        var sources = TestContent.MvpSources().Select(x => x.Name == "factions/sentinel.json"
            ? x with { Json = Once(Once(x.Json, "\"assault\", \"anti_armor\",", "\"assault\", \"mortar\", \"sniper\", \"scout_station\", \"scout_drone\", \"kornet\", \"anti_armor\","),
                "\"rifle\", \"smg\",", "\"rifle\", \"sniper_rifle\", \"smg\",") } : x);
        var content = SimCore.Content.ContentLoader.Load(sources);
        var setup = SimCore.Replays.Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
        Preset[] extra =
        [
            new() { Name = "Marksman", Archetype = "sniper", Components = ["sniper_rifle", "pistol"] },
        ];
        setup = setup with
        {
            Players = [.. setup.Players.Select(p => p with { Army = [], Presets = p.Id == 1 ? [.. p.Presets, .. extra] : p.Presets })],
        };
        var w = new World(new Grid(96, 96), MatchCompiler.Compile(content, setup));
        var (t, _) = w.SpawnCrewed(1, Tank, 40, 40, Facing.East);
        var c = w.Content;
        foreach (var (who, allowed) in new[] { ("mortar", false), ("Marksman", false), ("kornet", false), ("worker", false),
                     ("Rifleman", true), ("Tank hunter", true), ("manpads", true), ("sentinel_medic", true), ("pilot", true) })
        {
            var u = w.Spawn(1, who, 30, 30);
            output.WriteLine($"{who}: tags {string.Join(",", c.Tags.Where((_, i) => (u.Def.Tags.Bits & (1UL << i)) != 0))}");
            Assert.Equal(allowed, World.PassengerAllowed(t, u));
            Assert.Equal(allowed, w.CanRide(u, t));
        }
        // An Enter order is not even taken by them.
        var mortar = w.Spawn(1, "mortar", 36, 40);
        var sniper = w.Spawn(1, "Marksman", 36, 41);
        w.Order(1, CommandKind.Enter, [mortar, sniper], t.Id);
        w.Run(100);
        Assert.Empty(w.PassengersOf(t));
        Assert.All(new[] { mortar, sniper }, u => Assert.NotEqual(OrderKind.Enter, u.Order.Kind));
        // The other carriers are unchanged: the Humvee still takes a mortar team (infantry).
        var (h, _) = w.SpawnCrewed(1, "humvee", 60, 60);
        Assert.True(World.PassengerAllowed(h, mortar));
    }

    [Fact]
    public void ASquadOfFiveBoardsAndComesOutAsOne()
    {
        var w = Sentinel();
        var (t, driver) = w.SpawnCrewed(1, Tank, 40, 30, Facing.North);
        var men = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 36 + i, 40)).ToList();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [.. men.Select(m => m.Id)] });
        w.Run(1);
        var s = w.SquadOf(men[0].Id)!;
        Assert.Equal(5, s.Members.Count);
        w.Order(1, CommandKind.Enter, [men[3]], t.Id); // given to one member
        w.Run(400);
        Assert.All(men, m => Assert.Equal(t.Id, m.Inside));
        Assert.Equal(5, w.PassengersOf(t).Count);
        w.Order(1, CommandKind.Exit, [men[1]]); // given to one member
        w.Run(1);
        Assert.All(men, m => Assert.Equal(0u, m.Inside));
        Assert.Equal(t.Id, driver.Inside); // the driver stays at the wheel
        Assert.All(men, m => Assert.True(Behind(m.Pos, t.Pos, t.Facing) > 0, "out at the rear"));
        Assert.Equal(5, w.SquadOf(men[0].Id)!.Members.Count);
    }

    [Theory]
    [InlineData(Facing.East)]
    [InlineData(Facing.North)]
    [InlineData(Facing.South + 4)] // south-west
    public void TheyUnloadAllAtOnceAtTheRear(int facing)
    {
        var w = Sentinel();
        var (t, d, riders) = Loaded(w, 40, 40, facing);
        w.Order(1, CommandKind.Exit, [t]);
        w.Step();
        Assert.Empty(w.PassengersOf(t));
        Assert.Equal(t.Id, d.Inside);
        var door = World.DoorPoint(t);
        foreach (var r in riders)
        {
            Assert.Equal(0u, r.Inside);
            Assert.True(Behind(r.Pos, t.Pos, facing) > Fixed.OneRaw, "behind the hull");
            Assert.True(World.Within(r.Pos, door, Fixed.FromInt(3)), "round the rear door");
        }
        Assert.Equal(riders.Count, riders.Select(r => Cell.Of(r.Pos)).Distinct().Count());
    }

    [Fact]
    public void TheyNeverFireFromInside()
    {
        var w = Sentinel();
        var (t, _, riders) = Loaded(w, 40, 40);
        HoldFire(w, t);
        var enemy = w.Spawn(2, "Rifleman", 44, 40); // 4 cells away, in every rifle's reach
        HoldFire(w, enemy);
        w.Order(1, CommandKind.Attack, riders, enemy.Id); // even ordered to
        w.Run(200);
        Assert.Equal(enemy.Def.MaxHp, enemy.HP);
        Assert.All(riders, r => Assert.Equal(t.Id, r.Inside));
        Assert.All(riders, r => Assert.Equal(0u, r.AutoTarget));
        // Out, they fire.
        w.Order(1, CommandKind.Exit, [t]);
        w.Run(1);
        w.Order(1, CommandKind.Attack, riders, enemy.Id);
        w.Run(100);
        Assert.True(enemy.HP < enemy.Def.MaxHp);
    }

    [Fact]
    public void PassengersAreNeitherSeenNorHit()
    {
        // The HE round's 4-cell splash on the tank does not reach the soldiers inside.
        var w = Duel();
        var (t, _, riders) = Loaded(w, 40, 40);
        t.HP = 100_000;
        HoldFire(w, t);
        var (enemy, _) = w.SpawnCrewed(2, Tank, 60, 40, Facing.West);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [enemy.Id], Cell = new Cell(40, 40) });
        w.Run(300);
        Assert.True(t.HP < 100_000);
        Assert.All(riders, r => Assert.Equal(r.Def.MaxHp, r.HP));
        Assert.All(riders, r => Assert.False(World.IsVisibleTo(r, 2)));
    }

    // ---- Survival ----

    // KillWith: the loaded tank, at 1 HP, takes one hit of `damage` (before armour) through warhead wh at `pct` percent of a
    // splash; the next tick its passengers come out (or not). Returns the bail-out reports.
    private static List<BailOutEvent> KillWith(World w, Unit t, string wh, long damage, long pct = 100)
    {
        t.HP = 1;
        w.Hurt(t, w.Content.Warhead(wh)!, damage, pct);
        Assert.False(t.Alive);
        w.Step();
        return [.. w.BailOuts];
    }

    [Fact]
    public void UnderFifteenHundredAllBailOutAtTheRearWithFortyPercentLost()
    {
        // A Kornet type's missile (126) into its rear, live (Trophy empty so it gets through).
        var w = Sentinel();
        var (t, _, riders) = Loaded(w, 60, 40, Facing.West);
        t.HP = 10;
        t.MutableCharges[t.Def.ApsStock] = 0;
        HoldFire(w, t);
        var k = w.Spawn(2, "kornet", 85, 40, Facing.West); // behind it
        w.Order(2, CommandKind.Attack, [k], t.Id);
        var (pos, facing) = (t.Pos, t.Facing);
        var ev = new List<BailOutEvent>();
        for (var i = 0; i < 200 && ev.Count == 0; i++)
        {
            w.Step();
            ev.AddRange(w.BailOuts);
        }
        Assert.False(t.Alive);
        Assert.Equal(riders.Select(r => r.Id), ev.Select(e => e.Unit)); // ID order
        Assert.All(ev, e => Assert.Equal((t.Id, true, 126, 0, 100), (e.Vehicle, e.Survived, e.KillHit, e.Band, e.Chance)));
        foreach (var r in riders)
        {
            Assert.Equal((0u, r.Def.MaxHp * 60 / 100), (r.Inside, r.HP)); // 40% of its HP lost
            Assert.True(Behind(r.Pos, pos, facing) > Fixed.OneRaw, "out at the rear");
        }
        Assert.Equal(riders.Count, riders.Select(r => Cell.Of(r.Pos)).Distinct().Count());
    }

    [Fact]
    public void FireAndOutrightKillsCountAsUnderFifteenHundred()
    {
        // No hit at all (burning, damage over time, a kill outright): KillHit 0, the first band.
        var w = Sentinel();
        var (t, _, riders) = Loaded(w, 40, 40);
        t.HP = 0;
        w.Step();
        Assert.Equal(5, w.BailOuts.Count);
        Assert.All(w.BailOuts, e => Assert.Equal((true, 0, 0), (e.Survived, e.KillHit, e.Band)));
        Assert.All(riders, r => Assert.True(r.Alive));
    }

    [Theory]
    [InlineData(1499, 100, 0)]
    [InlineData(1500, 100, 1)]
    [InlineData(2499, 100, 1)]
    [InlineData(2500, 100, 2)]
    [InlineData(4000, 50, 1)] // a splash share: the hit before armour is the weapon's damage at its splash percent (2,000)
    [InlineData(2500, 30, 0)] // the Jericho type's flat 30% ring: 750
    public void TheBandIsTheKillingHitBeforeArmour(int damage, int pct, int band)
    {
        var w = Sentinel();
        var (t, _, _) = Loaded(w, 40, 40);
        var ev = KillWith(w, t, "bomb", damage, pct);
        Assert.Equal(5, ev.Count);
        Assert.All(ev, e => Assert.Equal((damage * pct / 100, band), (e.KillHit, e.Band)));
        Assert.Equal(band, t.Def.Crew!.SurviveBandOf(damage * pct / 100));
    }

    [Fact]
    public void FifteenHundredToTwentyFourNinetyNineEachRollsFiftyPercent()
    {
        // The strategic bomber's 2,000 lb type bomb (2,000 before armour): each passenger rolls 50% on the world's generator, in
        // ID order after the driver. Seed 7 pinned; the same seed gives the same outcome.
        List<bool> Run(ulong seed)
        {
            var w = Sentinel(96, seed);
            var (t, _, riders) = Loaded(w, 40, 40);
            var ev = KillWith(w, t, "bomb", 2000);
            Assert.Equal(riders.Select(r => r.Id), ev.Select(e => e.Unit));
            Assert.All(ev, e => Assert.Equal((1, 50), (e.Band, e.Chance)));
            foreach (var (r, e) in riders.Zip(ev))
            {
                Assert.Equal(e.Survived, r.Alive);
                Assert.Equal(e.Survived ? r.Def.MaxHp * 60 / 100 : 0, Math.Max(0, r.HP));
            }
            return [.. ev.Select(e => e.Survived)];
        }
        var seven = Run(7);
        output.WriteLine("seed 7: " + string.Join(", ", seven));
        Assert.Equal(seven, Run(7));
        Assert.Equal([true, true, true, false, true], seven);
        var alive = 0;
        for (var seed = 1UL; seed <= 40; seed++)
        {
            alive += Run(seed).Count(x => x);
        }
        output.WriteLine($"40 seeds: {alive} of 200 bailed out");
        Assert.InRange(alive, 79, 121); // 50% of 200, within 3 standard deviations
    }

    [Fact]
    public void TwentyFiveHundredOrMoreAllDie()
    {
        // The Jericho type's missile at full (2,500 on heavy_ballistic): nobody gets out.
        var w = Sentinel();
        var (t, d, riders) = Loaded(w, 40, 40);
        var ev = KillWith(w, t, "heavy_ballistic", 2500);
        Assert.Equal(5, ev.Count);
        Assert.All(ev, e => Assert.Equal((false, 2500, 2, 0), (e.Survived, e.KillHit, e.Band, e.Chance)));
        Assert.All(riders, r => Assert.False(r.Alive));
        Assert.Equal(0u, d.Inside); // the driver keeps the crew's own 50% roll
    }

    [Fact]
    public void TheOtherCarriersKeepTheCrewsRoll()
    {
        // The Humvee has no bands: every passenger rolls the crew's 50% whatever the hit (KillHit unrecorded, band -1).
        var w = Sentinel();
        var (h, _) = w.SpawnCrewed(1, "humvee", 40, 40);
        for (var i = 0; i < 5; i++)
        {
            World.Ride(w.Spawn(1, "Rifleman", 40, 40), h);
        }
        var ev = KillWith(w, h, "heavy_ballistic", 2500);
        Assert.Equal(5, ev.Count);
        Assert.All(ev, e => Assert.Equal((0, -1, 50), (e.KillHit, e.Band, e.Chance)));
    }

    [Fact]
    public void ItIsDeterministic()
    {
        static ulong Play()
        {
            var w = Sentinel(96, 3);
            var (t, _) = w.SpawnCrewed(1, Tank, 40, 40, Facing.East);
            var men = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "Rifleman", 48, 37 + i)).ToList();
            w.Order(1, CommandKind.Enter, men, t.Id);
            w.Run(300);
            w.Order(1, CommandKind.Exit, [t]);
            w.Run(20);
            w.Order(1, CommandKind.Enter, men, t.Id);
            w.Run(200);
            t.HP = 1;
            w.Hurt(t, w.Content.Warhead("bomb")!, 2000, 100);
            w.Run(5);
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }

    // ---- Trophy: the same on both tanks ----

    [Theory]
    [InlineData(Tank)]
    [InlineData(Abrams)]
    public void BothTanksCarryTheSameTrophyWithFourCharges(string tank)
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, tank, 40, 40);
        Assert.Equal(new ApsStatus(4, 4, 0), w.ApsOf(t));
        Assert.Same(w.Content.Component("trophy_aps"), t.Def.Components.Single(c => c.Effects.OfType<ApsEffect>().Any()));
    }

    // Engagements: steps the world `ticks` times, collecting the APS reports.
    private static List<ApsEvent> Engagements(World w, int ticks, Func<World, bool>? stop = null)
    {
        var events = new List<ApsEvent>();
        for (var i = 0; i < ticks && stop?.Invoke(w) != true; i++)
        {
            w.Step();
            events.AddRange(w.ApsEvents);
        }
        return events;
    }

    [Theory]
    [InlineData(Tank, "Tank hunter", 8)]
    [InlineData(Tank, "kornet", 25)]
    [InlineData(Abrams, "Tank hunter", 8)]
    [InlineData(Abrams, "kornet", 25)]
    public void TrophyStillTakesOnAnRpgAndAKornet(string tank, string shooter, int cells)
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, tank, 60, 40, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var s = w.Spawn(2, shooter, 60 - cells, 40, Facing.East);
        w.Order(2, CommandKind.Attack, [s], t.Id);
        var ev = Engagements(w, 150);
        var e = ev.First(e => e.Kind is ApsEventKind.Intercepted or ApsEventKind.Failed); // engaged: a charge of 4 spent
        Assert.Equal(3, e.ChargesLeft);
    }

    [Theory]
    [InlineData(Tank)]
    [InlineData(Abrams)]
    public void TrophyIgnoresTheKamansMissilesAndDive(string tank)
    {
        var w = Arena.NewSentinel(400);
        var (t, _) = w.SpawnCrewed(1, tank, 300, 150, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var truck = w.SpawnCrewed(2, "kaman_truck", 10, 150, Facing.East).Vehicle;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.Attack, Units = [truck.Id], Target = t.Id });
        var missiles = 0;
        var blast = false;
        var ev = Engagements(w, 4000, x =>
        {
            missiles += x.KamanEvents.Count(e => e.Kind == KamanEventKind.Missile);
            blast |= x.KamanEvents.Any(e => e.Kind == KamanEventKind.Blast);
            return blast && x.Projectiles.Count == 0;
        });
        output.WriteLine($"{tank}: {missiles} missiles, blast {blast}, HP {t.HP}");
        Assert.Equal(2, missiles);
        Assert.True(blast);
        Assert.Empty(ev);
        Assert.Equal(4, w.ApsOf(t)!.Value.Charges);
        Assert.True(t.HP < 100_000);
    }

    [Theory]
    [InlineData(Tank)]
    [InlineData(Abrams)]
    public void TrophyIgnoresOneWayAndFpvDrones(string tank)
    {
        // A one-way attack drone off the drone truck's rail, then an FPV drone flown at it.
        var w = Arena.NewSentinel(120);
        var (t, _) = w.SpawnCrewed(1, tank, 90, 30, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var truck = w.SpawnCrewed(2, "drone_truck", 5, 30, Facing.East).Vehicle;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(90, 30) });
        var hit = false;
        var ev = Engagements(w, 3000, x => hit |= x.Detonations.Any(d => d.Hit == t.Id));
        Assert.True(hit, "the one-way drone hit it");
        Assert.Empty(ev);
        var hp = t.HP;
        Assert.True(hp < 100_000);

        var pilot = w.Spawn(2, "Drone pilot", 70, 30);
        w.Order(2, CommandKind.UseAbility, [pilot], t.Id, ability: "drone_kit");
        Assert.Empty(Engagements(w, 200));
        Assert.True(t.HP < hp, "the FPV drone hit it");
        Assert.Equal(4, w.ApsOf(t)!.Value.Charges);
    }
}

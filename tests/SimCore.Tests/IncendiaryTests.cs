using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Scenarios;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The incendiary bomb (docs/design.md, "Incendiary bomb"; the owner 2026-10-25): one for each medium jet (the multirole, the
// stealth multirole in both modes, the air superiority jet), behind the "incendiary" upgrade (off in every shipped lobby), dropped
// only on a ground point; a direct hit and the inner ring kill soldiers and light vehicles, the rest heavy damage by max-HP shares;
// it sets the forest within 6 cells on fire spreading at 3x and burns the open ground there for 12 s (a patch that hurts infantry
// and walls vehicles off), under the fire rules' cap.
public class IncendiaryTests(ITestOutputHelper output)
{
    private const string Upgrade = "incendiary";
    private const string Multirole = "Multirole 500 lb", Fighter = "fighter_jet", F35 = "stealth_multirole";
    private static readonly string[] Jets = [Multirole, Fighter, F35];
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
    private static readonly FireRules Fire = TestContent.Mvp.Fire!;

    private static Func<MatchSetup, MatchSetup> Upgraded(byte player = 1) =>
        s => s with { Players = [.. s.Players.Select(p => p.Id == player ? p with { Upgrades = [Upgrade] } : p)] };

    private static World NewWorld(int n, bool upgraded, Func<int, int, int>? forest = null)
    {
        var w = Arena.NewSentinel(n, 1, upgraded ? Upgraded() : null);
        if (forest is not null)
        {
            w.AttachTerrain(Map(n, forest));
        }
        return w;
    }

    private static TerrainMap Map(int n, Func<int, int, int> forest)
    {
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                flags[y * n + x] = (byte)forest(x, y);
            }
        }
        return new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, Rules);
    }

    private static WeaponEffect Bomb => TestContent.Mvp.Component("incendiary_bomb")!.Effects.OfType<WeaponEffect>().Single();

    private static WeaponDef Incendiary(Unit u) => u.Def.Weapons.Single(x => x.Effect.Upgrade == Upgrade);

    private static int Carried(Unit u) => u.Charges[Incendiary(u).Stock];

    // Blast is what the bomb's impact does: the splash (its two rings), the trees, the fire.
    private static void Blast(World w, Vec at)
    {
        var b = Bomb;
        w.Explode(at, b.Damage, b.Warhead, b.Splash, 0, b.SplashCenter, b.SplashEdge, b.SplashInner, b.SplashOuterPercent);
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, int index = 0, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Index = index, Target = target });

    // Fly spawns a jet of player 1 in the air and gives it its full load (as a rearm would: the incendiary only with the upgrade).
    private static Unit Fly(World w, string jet, int x, int y, int mode = 0)
    {
        var u = w.Spawn(1, jet, x, y, Facing.East);
        u.Loadout = mode;
        for (var i = 0; i < u.Charges.Count; i++)
        {
            u.MutableCharges[i] = u.StockMax(i);
        }
        w.Step();
        return u;
    }

    // Drops runs until the jet's first n bomb impacts and returns their warheads.
    private static List<BombImpact> Drops(World w, Unit u, int n, int max = 4000)
    {
        var impacts = new List<BombImpact>();
        for (var t = 0; t < max && impacts.Count < n; t++)
        {
            w.Step();
            impacts.AddRange(w.BombImpacts.Where(x => x.Aircraft == u.Id));
        }
        return impacts;
    }

    // ---- The switch ----

    [Fact]
    public void EveryMediumJetHasOneIncendiaryBehindTheUpgrade()
    {
        var c = TestContent.Mvp;
        Assert.Equal([Upgrade], c.Upgrades);
        var b = Bomb;
        Assert.Equal((1, true, Upgrade, "incendiary"), (b.Ammo, b.PointsOnly, b.Upgrade, b.Warhead.Id));
        Assert.Equal(["single"], b.Delivery!.FireModes.Select(m => m.Id));
        var w = NewWorld(64, upgraded: false);
        foreach (var jet in Jets)
        {
            var u = w.Spawn(1, jet, 20, 20);
            Assert.Single(u.Def.Weapons, x => x.Effect.Upgrade is not null); // one, on top of its other bombs
            Assert.Equal(0, u.StockMax(Incendiary(u).Stock)); // without the upgrade it carries none
            Assert.Equal(0, Carried(u));
            Assert.False(u.Carries(Incendiary(u)));
            Assert.Equal(0u, u.Upgrades);
        }
        // Every shipped lobby is without it (the golden battle too).
        foreach (var file in new[] { "battle.json", "sentinel.json" })
        {
            var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", file));
            Assert.All(setup.Players, p => Assert.Null(p.Upgrades));
        }
        Assert.All(w.Players, p => Assert.Equal(0u, p.Upgrades));
    }

    [Fact]
    public void WithoutTheUpgradeTheJetsLoadoutsAreUnchanged()
    {
        var w = NewWorld(64, upgraded: false);
        int Bombs(Unit u) => u.Def.Weapons.Where(x => x.Effect.Delivery is not null && x.Effect.Upgrade is null).Sum(x => u.StockMax(x.Stock));
        Assert.Equal(4, Bombs(w.Spawn(1, Multirole, 10, 10)));
        Assert.Equal(2, Bombs(w.Spawn(1, "Multirole 2000 lb", 10, 12)));
        Assert.Equal(2, Bombs(w.Spawn(1, Fighter, 10, 14)));
        var f35 = w.Spawn(1, F35, 10, 16);
        Assert.Equal(2, Bombs(f35));
        f35.Loadout = 1;
        Assert.Equal(10, Bombs(f35));
        // The F-35's bomb weapons: its heavy bombs (selected) and the incendiary; picking the incendiary is refused without it.
        Assert.Equal(["f35_bombs", "incendiary_bomb"], World.BombWeaponsOf(f35).Select(x => Component(w, x)));
        Issue(w, CommandKind.SetBombWeapon, f35, index: 1);
        w.Step();
        Assert.Equal(0, f35.BombWeapon);
    }

    private static string Component(World w, WeaponDef x) =>
        w.Content.Components.Single(c => c.Effects.Contains(x.Effect)).Id;

    [Fact]
    public void TheUpgradeSwitchesItOnForEveryJetAndBothF35Modes()
    {
        var w = NewWorld(64, upgraded: true);
        Assert.Equal(1u, w.Player(1)!.Upgrades);
        Assert.Equal(0u, w.Player(2)!.Upgrades);
        foreach (var jet in Jets)
        {
            var u = w.Spawn(1, jet, 20, 20);
            Assert.Equal((1, 1), (u.StockMax(Incendiary(u).Stock), Carried(u)));
            Assert.True(u.Carries(Incendiary(u)));
        }
        var f35 = w.Spawn(1, F35, 30, 30);
        foreach (var mode in new[] { 1, 0 })
        {
            f35.Loadout = mode;
            Assert.Equal(1, f35.StockMax(Incendiary(f35).Stock)); // one in both modes, an extra bomb (not a swap)
        }
        Assert.Equal((2, 10), (f35.StockMax(f35.Def.Weapons.Single(x => x.Effect.Delivery is not null && !x.Effect.PointsOnly).Stock),
            f35.StockMax(f35.Def.Weapons.Single(x => x.Effect.Interceptor is not null).Stock)));
    }

    [Fact]
    public void TheDemoLobbyStartsPlayerOneWithIt()
    {
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "incendiary_demo.json")).For(TestContent.Mvp, 1);
        Assert.Equal(Scenario.SandboxName, setup.Scenario);
        Assert.Equal([Upgrade], setup.Players.Single(p => p.Id == 1).Upgrades);
        Assert.Null(setup.Players.Single(p => p.Id == 2).Upgrades);
        var w = new World(new Grid(64, 64), MatchCompiler.Compile(TestContent.Mvp, setup));
        Assert.Equal(1, Carried(w.Spawn(1, Multirole, 20, 20)));
    }

    [Fact]
    public void GrantingTheUpgradeInPlayLoadsItAtTheNextRearm()
    {
        var w = NewWorld(64, upgraded: false);
        var jet = w.Spawn(1, Multirole, 20, 20);
        var later = w.Spawn(1, Fighter, 22, 20);
        Assert.False(w.GrantUpgrade(1, "napalm"));
        Assert.True(w.GrantUpgrade(1, Upgrade)); // the hook the upgrade system will call
        Assert.Equal(1u, w.Player(1)!.Upgrades);
        Assert.Equal(1u, jet.Upgrades);
        Assert.Equal(1, jet.StockMax(Incendiary(jet).Stock));
        Assert.Equal(0, Carried(jet)); // nothing loaded at once: the next rearm fills it
        Assert.Equal(0, Carried(later));
        Assert.Equal(1, w.Spawn(1, F35, 24, 20) is var made ? Carried(made) : -1); // a jet made after it comes with it
        Assert.Equal(0u, w.Spawn(2, "technical", 30, 30).Upgrades);
        Assert.Equal(w.Hash(), w.HashReference());
    }

    [Fact]
    public void TheSandboxGrantsItAndARealMatchIgnoresTheCommand()
    {
        var setup = TestContent.Sentinel(3) with { Scenario = Scenario.SandboxName };
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        var w = new World(new Grid(64, 64), MatchCompiler.Compile(TestContent.Mvp, setup));
        w.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.SandboxUpgrade, Unit = Upgrade });
        w.Step();
        Assert.Equal(1u, w.Player(1)!.Upgrades);
        var real = new World(new Grid(64, 64), MatchCompiler.Compile(TestContent.Mvp, setup with { Scenario = "battle" }));
        real.Issue(new Command { Tick = 0, Player = 1, Kind = CommandKind.SandboxUpgrade, Unit = Upgrade });
        real.Step();
        Assert.Equal(0u, real.Player(1)!.Upgrades);
        // A lobby naming an upgrade the content does not have is refused.
        var bad = TestContent.Sentinel(3) with { Players = [.. setup.Players.Select(p => p with { Upgrades = ["napalm"] })] };
        Assert.Contains("unknown upgrade", Assert.Throws<MatchSetupException>(() => MatchCompiler.Compile(TestContent.Mvp, bad)).Message);
        // The lobby's upgrades are in its digest; a lobby without them digests as before.
        Assert.NotEqual(TestContent.Sentinel(3).Digest(), (TestContent.Sentinel(3) with { Players = [.. TestContent.Sentinel(3).Players.Select(p => p with { Upgrades = [Upgrade] })] }).Digest());
    }

    // ---- Ground points only ----

    [Fact]
    public void ItIsDroppedOnAGroundPointNeverOnAUnit()
    {
        foreach (var jet in Jets)
        {
            var w = NewWorld(400, upgraded: true);
            var u = Fly(w, jet, 60, 200);
            var tech = w.Spawn(2, "technical", 220, 200);
            Assert.NotSame(Incendiary(u), World.AirGroundWeapon(u, tech)); // never at a unit
            // An attack on a unit drops the jet's other bombs (or strafes), never the incendiary.
            Issue(w, CommandKind.Attack, u, Cell.Of(tech.Pos), target: tech.Id);
            var onUnit = Drops(w, u, 1, 3000);
            Assert.All(onUnit, x => Assert.NotEqual("incendiary", x.Warhead));
            Assert.Equal(1, Carried(u));
            output.WriteLine($"{jet}: on a unit {string.Join(", ", onUnit.Select(x => x.Warhead))}");
        }
    }

    [Fact]
    public void AttackGroundDropsItFirstOnTheMultiroleAndTheFighterAndWhenPickedOnTheF35()
    {
        foreach (var jet in new[] { Multirole, Fighter })
        {
            var w = NewWorld(400, upgraded: true);
            var u = Fly(w, jet, 60, 200);
            Issue(w, CommandKind.AttackGround, u, new Cell(220, 200));
            var drops = Drops(w, u, 2);
            Assert.Equal("incendiary", drops[0].Warhead); // its only bomb slot: selected, first on a point while aboard
            Assert.Equal(0, Carried(u));
            Assert.Equal("bomb", drops[1].Warhead); // then its other bombs on the same point
            Assert.Equal(3, World.StrikeTier(drops[0].Damage));
            output.WriteLine($"{jet}: {string.Join(", ", drops.Select(x => x.Warhead))}");
        }
        // The F-35: its heavy bombs unless the picker (SetBombWeapon) takes the incendiary; on a unit its heavy bombs still.
        foreach (var mode in new[] { 0, 1 })
        {
            var w = NewWorld(400, upgraded: true);
            var u = Fly(w, F35, 60, 200, mode);
            Issue(w, CommandKind.AttackGround, u, new Cell(220, 200));
            Assert.Equal("bomb", Drops(w, u, 1)[0].Warhead);
            Issue(w, CommandKind.SetBombWeapon, u, index: 1);
            w.Step();
            Assert.Equal(1, u.BombWeapon);
            var tech = w.Spawn(2, "technical", 300, 120);
            Assert.Equal("f35_bombs", Component(w, World.AirGroundWeapon(u, tech)!));
            Assert.Same(Incendiary(u), World.AirGroundWeapon(u, null));
            Issue(w, CommandKind.AttackGround, u, new Cell(240, 160));
            var drops = Drops(w, u, 2);
            Assert.Equal(["incendiary", "bomb"], drops.Select(x => x.Warhead)); // spent, its heavy bombs again
            output.WriteLine($"F-35 mode {mode}: {string.Join(", ", drops.Select(x => x.Warhead))}");
        }
    }

    // ---- The rings ----

    [Fact]
    public void TheCentreKillsInfantryAndLightVehiclesAndTheEdgeHurtsHeavily()
    {
        var w = NewWorld(96, upgraded: false);
        var at = new Cell(48, 48).Center();
        var hit = w.Spawn(2, "Rifleman", 48, 48);
        var inner = w.Spawn(2, "Rifleman", 51, 48);         // 3 cells: the inner ring (4 cells)
        var edge = w.Spawn(2, "Rifleman", 54, 48);          // 6 cells: the outer ring (to 7.5)
        var edgeMedic = w.Spawn(1, "sentinel_medic", 48, 54);
        var outside = w.Spawn(2, "Rifleman", 57, 48);       // 9 cells: untouched
        var (tech, techDriver) = w.SpawnCrewed(2, "technical", 48, 46);
        var truck = w.Spawn(2, "light_rocket_truck", 46, 48);
        var tank = w.Spawn(1, "main_battle_tank", 48, 51);
        var farTech = w.Spawn(2, "technical", 42, 48);
        var farTruck = w.Spawn(2, "light_rocket_truck", 48, 42);
        var farTank = w.Spawn(1, "main_battle_tank", 42, 42); // 8.5 cells: outside
        var edgeTank = w.Spawn(1, "main_battle_tank", 53, 43); // about 7.1 cells: the outer ring
        Blast(w, at);
        int Left(Unit u) => 100 * u.HP / u.Def.MaxHp;
        output.WriteLine($"truck {Left(truck)}% tank {Left(tank)}% edge soldier {Left(edge)}% medic {Left(edgeMedic)}% far tech {Left(farTech)}% far truck {Left(farTruck)}% edge tank {Left(edgeTank)}%");
        Assert.True(hit.HP <= 0, "a direct hit kills a soldier outright");
        Assert.True(inner.HP <= 0, "the inner ring kills soldiers");
        Assert.True(tech.HP <= 0, "the inner ring destroys a light vehicle");
        Assert.Equal(techDriver.Def.MaxHp, techDriver.HP); // inside, safe (the wreck's ejection is the crew system's)
        Assert.Equal(30, Left(truck));                     // a truck loses 70%
        Assert.Equal(60, Left(tank));                       // a tank 40% (fire wraps its hull: its directional armour does not count)
        Assert.Equal(40, Left(edge));                       // the edge: 60% of a soldier
        Assert.Equal(40, Left(edgeMedic));
        Assert.Equal(outside.Def.MaxHp, outside.HP);
        Assert.Equal(40, Left(farTech));                    // 6 cells: a light vehicle loses 60%
        Assert.Equal(58, Left(farTruck));                   // a truck 42%
        Assert.Equal(76, Left(edgeTank));                   // a tank 24%
        Assert.Equal(farTank.Def.MaxHp, farTank.HP);
    }

    // ---- Fire ----

    [Fact]
    public void OpenGroundBurnsTwelveSecondsAndHurtsInfantryStandingInIt()
    {
        var w = NewWorld(64, upgraded: false, (_, _) => 0); // open ground
        var g = w.Ground!;
        var soldier = w.Spawn(2, "Rifleman", 37, 32);
        Blast(w, new Cell(32, 32).Center());
        var lit = w.Fires.Select(f => f.Cell).ToList();
        Assert.InRange(lit.Count, 105, 121); // the cells within 6 cells (about 113)
        Assert.All(w.Fires, f => Assert.Equal((240, 300), (f.Length, f.SpreadPercent)));
        Assert.All(lit, c => Assert.True(w.IsGroundFire(c)));
        Assert.True(lit.Max(c => (c.Center() - new Cell(32, 32).Center()).Len().Raw) <= Fixed.FromInt(6).Raw);
        Assert.Contains(w.Smokes, s => s.Fire); // it smokes
        Assert.Equal(40, 100 * soldier.HP / soldier.Def.MaxHp); // 5 cells: the outer ring
        var hp = soldier.HP;
        w.Step(); // the first burn
        Assert.Equal(hp - soldier.Def.MaxHp * Fire.DamagePercent / 100, soldier.HP); // standing in the burning patch hurts
        Assert.Equal(OrderKind.Move, soldier.Order.Kind); // and he walks out on his own
        Assert.False(w.Ground!.Burning(soldier.Order.Cell));
        w.Run(80);
        Assert.False(g.Burning(Cell.Of(soldier.Pos)), "walked out");
        hp = soldier.HP;
        w.Run(100);
        Assert.Equal(hp, soldier.HP); // out of it, no more burns
        w.Run(58);
        Assert.Equal(lit.Count, w.Fires.Count); // still burning at 11.95 s
        w.Run(2);
        Assert.Empty(w.Fires); // out after 12 s
        Assert.All(lit, c => Assert.Equal(FireState.Burnt, w.FireAt(c))); // scorched ground: embers, then ash
        Assert.All(lit, c => Assert.Equal(0, g.Forest(c)));
        w.Run(Fire.EmberTicks + 1);
        Assert.All(lit, c => Assert.Equal(FireState.Ash, w.FireAt(c)));
        Blast(w, new Cell(32, 32).Center());
        Assert.Empty(w.Fires); // scorched ground burns once
    }

    [Fact]
    public void WithoutTerrainNothingBurnsAndWaterNeverDoes()
    {
        var flat = NewWorld(64, upgraded: false);
        Blast(flat, new Cell(32, 32).Center());
        Assert.Empty(flat.Fires);
        Assert.False(flat.IsGroundFire(new Cell(32, 32)));
    }

    // Spread burns the incendiary's fire over forest of a density from the centre of a 160-cell map (at spread percent pct),
    // returning the cells burnt in 2,400 ticks, the most burning at once and the cells the blast lit.
    private static (int Burnt, int Most, int First) Spread(int pct, int density)
    {
        var w = NewWorld(160, upgraded: false, (_, _) => density);
        var b = Bomb;
        var wf = b.Warhead.Fire!;
        var fire = new WarheadFire
        {
            Radius = wf.Radius, RadiusPercent = wf.RadiusPercent, CountMin = wf.CountMin, CountMax = wf.CountMax, SpreadPercent = pct,
            GroundTicks = wf.GroundTicks,
        };
        w.IgniteByFire(new Cell(80, 80).Center(), fire, b.Splash);
        var first = w.Fires.Count;
        var most = first;
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            most = Math.Max(most, w.Fires.Count);
            Assert.True(w.Fires.Count <= Fire.MaxBurning, $"tick {t}: {w.Fires.Count} burning");
        }
        return (w.BurntCells.Count + w.Fires.Count, most, first);
    }

    [Fact]
    public void InTheForestItSpreadsFarMoreThanANormalFireUnderTheCap()
    {
        // Dense pine: it burns at the cap (150 at once: the cap holds; a normal spreading fire there reaches it too).
        var pine = Spread(300, 3);
        var pineNormal = Spread(100, 3);
        // Scrub, where a normal fire mostly dies out: at 3x it runs on.
        var scrub = Spread(300, 1);
        var scrubNormal = Spread(100, 1);
        output.WriteLine($"dense: 300% {pine.Burnt} burnt, at most {pine.Most}, {pine.First} lit by the blast; 100% {pineNormal.Burnt}, {pineNormal.Most}");
        output.WriteLine($"scrub: 300% {scrub.Burnt} burnt, at most {scrub.Most}, {scrub.First} lit; 100% {scrubNormal.Burnt}, {scrubNormal.Most}");
        Assert.InRange(pine.First, 105, 121); // every forest cell within 6 cells
        Assert.Equal(Fire.MaxBurning, pine.Most); // it reaches the cap, and the cap holds
        Assert.True(pine.Burnt > 600, $"{pine.Burnt} burnt");
        Assert.True(scrub.Burnt > 2 * scrubNormal.Burnt, $"scrub {scrub.Burnt} against {scrubNormal.Burnt}");
    }

    [Fact]
    public void ANormalBombsFireIsUnchanged()
    {
        // The bomb warhead's block has no ground ticks: open ground never burns under it.
        var w = NewWorld(64, upgraded: false, (_, _) => 0);
        var bomb = TestContent.Mvp.Warhead("bomb")!;
        Assert.Equal(0, bomb.Fire!.GroundTicks);
        w.IgniteByBlast(new Cell(32, 32).Center(), bomb, Fixed.FromInt(8));
        Assert.Empty(w.Fires);
    }

    [Fact]
    public void VehiclesRouteRoundTheBurningPatchAndThroughAfter()
    {
        const int n = 64;
        var w = NewWorld(n, upgraded: false, (_, _) => 0);
        Blast(w, new Cell(32, 20).Center());
        var (car, _) = w.SpawnCrewed(2, "technical", 12, 20, Facing.East);
        var cls = Rules.ClassOf(car.Def);
        w.Step(); // tick 0: a terrain tick: the patch becomes a wall to it
        Assert.Equal(0, w.Ground!.Cost(cls, new Cell(32, 20)));
        w.Order(2, CommandKind.Move, [car], cell: new Cell(52, 20));
        var round = false;
        for (var t = 0; t < 400 && Cell.Of(car.Pos) != new Cell(52, 20); t++)
        {
            w.Step();
            Assert.False(w.Ground.Burning(Cell.Of(car.Pos)), $"tick {w.Tick}: in the fire at {Cell.Of(car.Pos)}");
            round |= car.Pos.X.Raw >> Fixed.Shift is >= 30 and <= 34 && Math.Abs((car.Pos.Y.Raw >> Fixed.Shift) - 20) >= 6;
        }
        Assert.True(round, "went round the patch");
        Assert.True((car.Pos - new Cell(52, 20).Center()).Len().Raw < Fixed.FromInt(2).Raw, $"arrived at {Cell.Of(car.Pos)}");
        w.Run(300); // the patch is out
        Assert.Empty(w.Fires);
        Assert.True(w.Ground.Passable(cls, new Cell(32, 20)));
    }

    // ---- End to end, determinism ----

    // Strike runs a multirole with the upgrade dropping its incendiary on a squad at the edge of a wood, hashing every tick.
    private static (ulong[] Hashes, World W) Strike(bool reverse)
    {
        var w = NewWorld(320, upgraded: true, (x, _) => x >= 200 ? 3 : 0);
        w.ReverseMoveOrder = reverse;
        var jet = Fly(w, Multirole, 40, 160);
        var squad = new List<Unit>();
        for (var i = 0; i < 8; i++)
        {
            squad.Add(w.Spawn(2, "Rifleman", 198 + i % 4, 158 + i / 4 * 2));
        }
        Issue(w, CommandKind.AttackGround, jet, new Cell(199, 159));
        var hashes = new List<ulong>();
        for (var t = 0; t < 1600; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        return ([.. hashes], w);
    }

    [Fact]
    public void ARealDropKillsTheSquadSetsTheWoodAlightAndIsDeterministic()
    {
        var (a, w) = Strike(false);
        var drop = w.BurntCells.Count + w.Fires.Count;
        var dead = w.Units.Count(u => u.Owner == 2);
        output.WriteLine($"cells burnt or burning {drop}; soldiers left {dead}; ground scorched {w.BurntCells.Count(b => w.Ground!.Forest(b.Cell) == 0)}");
        Assert.True(drop > 150, $"{drop} cells burnt or burning");
        Assert.Contains(w.BurntCells, b => b.Cell.X < 200);  // open ground scorched
        Assert.Contains(w.BurntCells, b => b.Cell.X >= 200); // the wood burnt
        Assert.True(dead <= 2, $"{dead} soldiers left");
        Assert.Equal(w.Hash(), w.HashReference());
        Assert.Equal(a, Strike(false).Hashes);
        Assert.Equal(a, Strike(true).Hashes);
    }

    // ---- Content refusals ----

    [Theory]
    [InlineData("components/sentinel.json", "\"pointsOnly\": true, \"upgrade\": \"incendiary\",", "\"pointsOnly\": true, \"upgrade\": \"\",", "weapon.upgrade wants a name")]
    [InlineData("warheads.json", "\"maxHpBySplash\": true,", "\"maxHpBySplash\": true, \"bogus\": 1,", "bogus")]
    [InlineData("warheads.json", "\"fire\": { \"radius\": 60, \"spreads\": true, \"spreadPercent\": 300, \"ground\": 240 }", "\"fire\": { \"radius\": 60, \"spreads\": true, \"spreadPercent\": 300, \"ground\": 0 }", "fire.ground")]
    public void BadIncendiaryContentIsRefused(string file, string from, string to, string want)
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == file ? s with { Json = s.Json.Replace(from, to, StringComparison.Ordinal) } : s)
            .ToList();
        Assert.NotEqual(TestContent.MvpSources().Single(s => s.Name == file).Json, sources.Single(s => s.Name == file).Json);
        Assert.Contains(want, Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message);
    }

    [Fact]
    public void PointsOnlyAndUpgradeNeedABombWithAmmoAndMaxHpBySplashAShare()
    {
        static string Refused(string file, string from, string to)
        {
            var sources = TestContent.MvpSources()
                .Select(s => s.Name == file ? s with { Json = s.Json.Replace(from, to, StringComparison.Ordinal) } : s)
                .ToList();
            return Assert.Throws<ContentException>(() => ContentLoader.Load(sources)).Message;
        }
        // A points-only flag on a jet's gun is refused (no delivery).
        Assert.Contains("pointsOnly is for a bomb", Refused("components/sentinel.json",
            "\"warhead\": \"air_gun\", \"targets\": [\"air\", \"ground\"], \"splash\": 10,",
            "\"warhead\": \"air_gun\", \"targets\": [\"air\", \"ground\"], \"splash\": 10, \"pointsOnly\": true,"));
        Assert.Contains("upgrade needs ammo", Refused("components/sentinel.json",
            "\"ammo\": 1, \"ammoPerRocket\": true, \"noResupply\": true,\n                                \"pointsOnly\": true,",
            "\"ammoPerRocket\": true, \"noResupply\": true,\n                                \"pointsOnly\": true,"));
        Assert.Contains("maxHpBySplash needs a maxHpPercent", Refused("warheads.json",
            "\"maxHpPercent\": { \"infantry\": 100, \"light_vehicle\": 100, \"medium_vehicle\": 70, \"armored_vehicle\": 40, \"parked_aircraft\": 100 }, ", ""));
    }
}

using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The mortar soldier (mortar, mortar_tube, mortar_bomb; docs/design.md "Mortar"): indirect fire on a
// high arc from 8 to 30 cells, 5 s to set up and 3 s to pack up, 1 s to lay the tube before every
// round plus 3 s between rounds, scatter growing with the distance, 10 bombs refilled only at a
// depot (one every 2 s, from 5 s after the last round, 10 supply each). Attack ground fires into the fog at any point in range; an
// attack on a unit needs it spotted.
public class MortarTests
{
    private const int Deploy = 100, Stow = 60, Aim = 20, Period = 80, DepotEvery = 40, DepotAfter = 100;
    private const int M = 512; // raw per metre

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static Unit Mortar(World w, int x = 10, int y = 32) => w.Spawn(1, "mortar", x, y, Facing.East);

    // Launches steps the world and returns the ticks new projectiles appeared on.
    private static List<uint> Launches(World w, int ticks)
    {
        var seen = new List<(uint Id, uint Tick)>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (!seen.Exists(s => s.Id == p.Id))
                {
                    seen.Add((p.Id, w.Tick));
                }
            }
        }
        return [.. seen.Select(s => s.Tick)];
    }

    private static List<Vec> Blasts(World w, int ticks)
    {
        var at = new List<Vec>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            at.AddRange(w.Detonations.Select(d => d.At));
        }
        return at;
    }

    private static World Hills(int size, Func<int, int, int> f, bool fog = false)
    {
        var w = fog ? Arena.New(size, edit: s => s with { Fog = true }) : Arena.New(size);
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = f(x, y);
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, new byte[size * size], Rules));
        return w;
    }

    [Fact]
    public void HalfTheRocketTrucksRangeAndBeyondEveryOtherInfantryWeapon()
    {
        var c = TestContent.Mvp;
        var tube = c.Component("mortar_tube")!.Effects.OfType<WeaponEffect>().Single();
        var mrl = c.Component("heavy_rockets")!.Effects.OfType<WeaponEffect>().Single();
        Assert.Equal(Fixed.FromInt(30), tube.Range);
        Assert.Equal(Fixed.FromInt(8), tube.MinRange);
        Assert.Equal(mrl.Range.Raw / 2, tube.Range.Raw);
        foreach (var id in new[] { "rifle", "smg", "lmg", "pistol", "short_rifle", "sniper_rifle", "at_missile", "kornet_atgm" })
        {
            var w = c.Component(id)!.Effects.OfType<WeaponEffect>().Single();
            Assert.True(tube.Range > w.Range, id);
        }
        var a = c.Archetype("mortar")!;
        Assert.Equal(Deploy, a.DeployTicks);
        Assert.Equal(Stow, a.StowTicks);
        foreach (var id in new[] { "assault", "engineer", "anti_armor", "kornet", "manpads", "sniper", "worker" })
        {
            Assert.True(a.Speed < c.Archetype(id)!.Speed, id); // the slowest soldier: he carries the mortar
        }
    }

    [Fact]
    public void SetsUpForFiveSecondsAndLaysTheTubeBeforeTheFirstRound()
    {
        var w = Arena.New(64);
        var m = Mortar(w);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(30, 32));
        var shots = Launches(w, Deploy + Aim + 10);
        Assert.Single(shots);
        Assert.InRange((int)shots[0], Deploy + Aim, Deploy + Aim + 3);
        Assert.Equal(DeployState.Deployed, m.Deploy);
    }

    [Fact]
    public void OneRoundEveryFourSecondsUntilTheTenBombsAreGone()
    {
        var w = Arena.New(64);
        var m = Mortar(w);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(30, 32));
        var shots = Launches(w, Deploy + Aim + 12 * Period);
        Assert.Equal(10, shots.Count);
        for (var i = 1; i < shots.Count; i++)
        {
            Assert.Equal(Period, (int)(shots[i] - shots[i - 1]));
        }
        Assert.Equal(0, m.Charges[0]);
        Assert.Equal(OrderKind.Idle, m.Order.Kind); // out of bombs: the order ends, it stays set up
        Assert.Equal(DeployState.Deployed, m.Deploy);
    }

    [Fact]
    public void NothingInsideEightCellsWhileNineFires()
    {
        var w = Arena.New(64);
        var m = Mortar(w);
        Assert.Equal(World.GroundShot.TooClose, w.ShotAt(m, new Cell(17, 32).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(m, new Cell(19, 32).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(m, new Cell(39, 32).Center()));
        Assert.Equal(World.GroundShot.OutOfRange, w.ShotAt(m, new Cell(41, 32).Center()));
        Assert.Equal(World.GroundShot.NoWeapon, w.ShotAt(w.Spawn(1, "worker", 5, 5), new Cell(19, 32).Center()));

        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(16, 32)); // 6 cells: too close
        Assert.Empty(Launches(w, Deploy + Aim + 2 * Period));
        Assert.Equal(DeployState.Stowed, m.Deploy); // it never even sets up
        Assert.Equal(new Cell(10, 32), Cell.Of(m.Pos)); // nor walks off
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(19, 32));
        Assert.NotEmpty(Launches(w, Deploy + Aim + 10));
    }

    [Fact]
    public void BeyondThirtyCellsItWalksCloserFirst()
    {
        var w = Arena.New(64);
        var m = Mortar(w, 5);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(45, 32)); // 40 cells
        var shots = Launches(w, 600);
        Assert.NotEmpty(shots);
        Assert.InRange(m.Pos.X.Raw, 14 * Fixed.OneRaw, 17 * Fixed.OneRaw); // it stopped just inside 30 cells
    }

    [Fact]
    public void ItCannotWalkSetUpAndPacksUpInThreeSeconds()
    {
        var w = Arena.New(64);
        var m = Mortar(w);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(30, 32));
        w.Run(Deploy + 5);
        Assert.Equal(DeployState.Deployed, m.Deploy);
        w.Order(1, CommandKind.Move, [m], cell: new Cell(10, 50));
        w.Step();
        var start = w.Tick;
        var at = m.Pos;
        while (m.Deploy != DeployState.Stowed && w.Tick < start + 200)
        {
            Assert.Equal(at, m.Pos); // packing up: no step yet
            w.Step();
        }
        Assert.InRange((int)(w.Tick - start), Stow - 1, Stow + 1);
        w.Run(20);
        Assert.NotEqual(at, m.Pos);
    }

    [Fact]
    public void FiresOnlyOnOrders()
    {
        var w = Arena.New(64);
        var m = Mortar(w);
        var enemy = w.Spawn(2, "worker", 25, 32);
        w.Spawn(1, "worker", 22, 34); // the enemy is spotted (an unarmed spotter: nothing else fires at it)
        Assert.Empty(Launches(w, 300));
        Assert.Equal(enemy.Def.MaxHp, enemy.HP);
        w.Order(1, CommandKind.Attack, [m], enemy.Id);
        Assert.NotEmpty(Launches(w, Deploy + Aim + 10));
    }

    [Fact]
    public void AttackGroundFiresIntoTheFogButAnAttackOnAUnitNeedsASpotter()
    {
        var w = Arena.New(64, edit: s => s with { Fog = true });
        var m = Mortar(w);
        var enemy = w.Spawn(2, "worker", 35, 32); // 25 cells off, far outside the mortar's 6-cell sight
        w.Run(3);
        Assert.False(World.IsVisibleTo(enemy, 1));

        w.Order(1, CommandKind.Attack, [m], enemy.Id);
        Assert.Empty(Launches(w, Deploy + Aim + 20));

        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(35, 32)); // nobody sees the point: it still fires
        Assert.NotEmpty(Launches(w, Deploy + Aim + 20));

        var w2 = Arena.New(64, edit: s => s with { Fog = true });
        var m2 = Mortar(w2);
        var e2 = w2.Spawn(2, "worker", 35, 32);
        e2.HP = 10_000;
        w2.Spawn(1, "worker", 31, 32); // anyone of its side 4 cells from it spots it (the sniper, the scout drone, the post further)
        w2.Run(3);
        Assert.True(World.IsVisibleTo(e2, 1));
        w2.Order(1, CommandKind.Attack, [m2], e2.Id);
        Assert.NotEmpty(Launches(w2, Deploy + Aim + 20));
    }

    [Fact]
    public void ABombKillsAnySoldierItLandsOnAndBarelyHurtsVehicles()
    {
        var w = Arena.New(64);
        var bomb = w.Content.Warhead("mortar_bomb")!;
        foreach (var id in new[] { "Rifleman", "Marksman", "Tank hunter", "worker", "mortar", "kornet", "manpads" })
        {
            var s = w.Spawn(2, id, 30, 30);
            w.Hurt(s, bomb, 90, 100);
            Assert.False(s.Alive, id);
        }
        var tech = w.Spawn(2, "technical", 30, 40);
        w.Hurt(tech, bomb, 90, 100);
        Assert.Equal(180 - 40, tech.HP); // 45%: five direct hits destroy a technical
        var mrl = w.Spawn(2, "heavy_rocket_launcher", 40, 40);
        w.Hurt(mrl, bomb, 90, 100);
        Assert.Equal(220 - 40, mrl.HP); // the same 45% (only the ATGM tells light and medium vehicles apart)
        var carrier = w.Spawn(2, "infantry_carrier", 50, 40);
        w.Hurt(carrier, bomb, 90, 100);
        Assert.Equal(carrier.Def.MaxHp - 9, carrier.HP); // 10%
    }

    [Fact]
    public void SplashFallsFromFullToTwentyPercentAtTwoAndAHalfCells()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "mortar")!.Weapons[0];
        var at = new Cell(30, 30).Center();
        Unit Dummy(int tenths)
        {
            var u = w.Spawn(2, "cargo_truck", 30, 30);
            u.Pos = at + new Vec(Fixed.FromRaw(tenths * Fixed.OneRaw / 10), Fixed.Zero);
            u.HP = 10_000;
            return u;
        }
        var (centre, mid, edge, outside) = (Dummy(0), Dummy(10), Dummy(25), Dummy(26));
        w.Explode(at, weapon.Damage, weapon.Warhead, weapon.Effect.Splash, 0, weapon.Effect.SplashCenter, weapon.Effect.SplashEdge,
            weapon.Effect.SplashInner, weapon.Effect.SplashOuterPercent);
        // The cargo truck is a light vehicle: 45% of 90 at the centre.
        Assert.Equal(10_000 - 40, centre.HP);
        Assert.Equal(10_000 - 90 * 68 / 100 * 45 / 100, mid.HP);
        Assert.Equal(10_000 - 90 * 20 / 100 * 45 / 100, edge.HP);
        Assert.Equal(10_000, outside.HP);
    }

    [Fact]
    public void ScatterGrowsWithTheDistance()
    {
        var w = Arena.New(64);
        var weapon = w.Match.Players[0].Find(w.Content, "mortar")!.Weapons[0];
        Assert.Equal(Fixed.FromRaw(8 * Fixed.OneRaw / 10).Raw, World.ScatterAt(weapon, 0));
        Assert.Equal(Fixed.FromRaw(8 * Fixed.OneRaw / 10).Raw, World.ScatterAt(weapon, 8 * Fixed.OneRaw));
        Assert.Equal(Fixed.FromRaw(19 * Fixed.OneRaw / 10).Raw, World.ScatterAt(weapon, 19 * Fixed.OneRaw));
        Assert.Equal(3 * Fixed.OneRaw, World.ScatterAt(weapon, 30 * Fixed.OneRaw));
        Assert.Equal(3 * Fixed.OneRaw, World.ScatterAt(weapon, 40 * Fixed.OneRaw));
        // A weapon without scatterNear keeps its flat scatter.
        var mrl = w.Match.Players[0].Find(w.Content, "heavy_rocket_launcher")!.Weapons[0];
        Assert.Equal(mrl.Effect.Scatter.Raw, World.ScatterAt(mrl, 15 * Fixed.OneRaw));

        static long Spread(int range)
        {
            var w = Arena.New(64, seed: 7);
            var m = w.Spawn(1, "mortar", 10, 32, Facing.East);
            var aim = new Cell(10 + range, 32).Center();
            w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(10 + range, 32));
            var blasts = Blasts(w, Deploy + Aim + 10 * Period + 100);
            Assert.Equal(10, blasts.Count);
            long worst = 0;
            foreach (var b in blasts)
            {
                var d = b - aim;
                worst = Math.Max(worst, Math.Max(Math.Abs(d.X.Raw), Math.Abs(d.Y.Raw)));
            }
            return worst;
        }
        var near = Spread(9);
        var far = Spread(29);
        Assert.True(near <= Fixed.OneRaw, $"near {near}"); // within about 0.9 cell at 9 cells
        Assert.True(far > near * 2, $"near {near}, far {far}");
        Assert.True(far <= 3 * Fixed.OneRaw, $"far {far}");
    }

    [Fact]
    public void ItsHighArcClearsARidgeThatStopsTheRocketTruck()
    {
        // A 14 m ridge over cells 20-21 between the shooter (cell 8) and the point (cell 34, 26 cells).
        static int Tall(int x, int _) => x is >= 20 and <= 22 ? 14 * M : 0;
        var w = Hills(64, Tall);
        var m = w.Spawn(1, "mortar", 8, 32, Facing.East);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(34, 32));
        var blasts = Blasts(w, Deploy + Aim + 200);
        Assert.NotEmpty(blasts);
        Assert.True(blasts[0].X.Raw > 30 * Fixed.OneRaw, $"{blasts[0].X.Raw / Fixed.OneRaw}"); // over the ridge, at the point

        var w2 = Hills(64, Tall);
        var (mrl, _) = w2.SpawnCrewed(1, "heavy_rocket_launcher", 8, 32, Facing.East);
        w2.Order(1, CommandKind.AttackGround, [mrl], cell: new Cell(34, 32));
        var rockets = Blasts(w2, 300);
        Assert.NotEmpty(rockets);
        Assert.True(rockets[0].X.Raw < 24 * Fixed.OneRaw); // the rocket's lower arc meets the ridge
    }

    [Fact]
    public void ADepotRefillsOneBombEveryTwoSecondsForTenSupply()
    {
        var w = Arena.New(64);
        var depot = w.Spawn(1, "supply_depot", 6, 28);
        var m = Mortar(w);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(30, 32));
        w.Run(Deploy + Aim + 3 * Period); // three or four rounds gone
        w.Order(1, CommandKind.Stop, [m]);
        w.Step();
        var left = m.Charges[0];
        Assert.InRange(left, 6, 7);
        var stock = depot.Stock;
        w.Run(DepotAfter - 2);
        Assert.Equal(left, m.Charges[0]); // nothing while it is still firing, nor between rounds
        w.Run(DepotEvery + 4); // 5 s after the last round, then 2 s for the first bomb
        Assert.Equal(left + 1, m.Charges[0]);
        Assert.Equal(stock - 10, depot.Stock);
        w.Run(DepotEvery * 10);
        Assert.Equal(10, m.Charges[0]);
        Assert.Equal(stock - 10 * (10 - left), depot.Stock);
    }

    [Fact]
    public void TheSameOrdersGiveTheSameWorld()
    {
        static ulong Run()
        {
            var w = Arena.New(64, seed: 3);
            var m = w.Spawn(1, "mortar", 10, 32, Facing.East);
            w.Spawn(2, "Rifleman", 30, 31);
            w.Spawn(2, "Rifleman", 31, 33);
            w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(30, 32));
            w.Run(Deploy + Aim + 6 * Period);
            return w.Hash();
        }
        Assert.Equal(Run(), Run());
    }
}

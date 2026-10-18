using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The assault soldier's mines (docs/design.md, "Traps"): the anti-personnel mine (ap_mine) kills
// soldiers and scratches vehicles, its trigger zone smaller than its blast; the anti-tank mine
// (at_mine) destroys the one vehicle on it, whatever it is, and never a second. Mines laid together
// chain (a mine inside another's blast goes off chainTicks after it), so a field acts as one bigger
// trap. Only a sniper, a scout drone or an observation post reveals them. Their owner can set off
// any mine on demand and turn its going off on its own on and off (on the mine itself).
public class MineTests
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    private static void Detonate(World w, byte player, params Unit[] units) =>
        w.Order(player, CommandKind.Detonate, units);

    private static void SetAuto(World w, byte player, bool on, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.SetAutoDetonate, Units = [.. units.Select(u => u.Id)], On = on });

    private static TriggerEffect Trigger(World w, string archetype) =>
        w.Player(1)!.Setup.Find(w.Content, archetype)!.Trigger!;

    [Theory]
    [InlineData("ap_mine_kit", "ap_mine")]
    [InlineData("at_mine_kit", "at_mine")]
    public void AssaultInfantryLayBothMinesHiddenFromTheEnemy(string kit, string mine)
    {
        var w = Arena.New(32);
        var rifleman = w.Spawn(1, "Rifleman", 10, 10);
        var enemy = w.Spawn(2, "Rifleman", 26, 10, Facing.West);
        w.Order(1, CommandKind.UseAbility, [rifleman], cell: new Cell(11, 10), ability: kit);
        w.Run(20);
        Assert.Empty(w.All(mine)); // 1.5 s to lay it
        w.Run(15);
        var m = Assert.Single(w.All(mine));
        Assert.Equal((1, rifleman.Id), (m.Owner, m.Source));
        Assert.Equal(new Cell(11, 10).Center(), m.Pos);
        Assert.True(World.IsVisibleTo(m, 1));
        Assert.False(World.IsVisibleTo(m, 2));
        w.Order(2, CommandKind.Attack, [enemy], m.Id);
        w.Step();
        Assert.Equal(OrderKind.Idle, enemy.Order.Kind); // can't attack what you can't see
    }

    [Fact]
    public void OtherSoldiersHaveNoMines()
    {
        var w = Arena.New(32);
        foreach (var name in new[] { "Marksman", "Sapper", "Tank hunter" })
        {
            var u = w.Spawn(1, name, 10, 10);
            Assert.Null(u.Def.Ability(w.Content.Component("ap_mine_kit")!.Index));
            Assert.Null(u.Def.Ability(w.Content.Component("at_mine_kit")!.Index));
        }
    }

    [Fact]
    public void TriggerZonesAreSmallerThanBlastZones()
    {
        var w = Arena.New(32);
        foreach (var mine in new[] { "ap_mine", "at_mine" })
        {
            var t = Trigger(w, mine);
            Assert.True(t.Radius.Raw < t.Splash.Raw, $"{mine}: trigger {t.Radius.Raw} vs blast {t.Splash.Raw}");
        }

        // A soldier 2.2 cells from an anti-personnel mine stands outside its trigger zone (0.6 cell)
        // but inside its blast (3 cells): it never goes off under him, yet its blast wounds him.
        var ap = w.Spawn(1, "ap_mine", 10, 10);
        var near = w.Spawn(2, "Rifleman", 12, 11);
        w.Run(60);
        Assert.True(ap.Alive);
        Detonate(w, 1, ap);
        w.Step();
        Assert.False(ap.Alive);
        Assert.InRange(near.HP, 1, near.Def.MaxHp - 1);
    }

    [Fact]
    public void AnApMineKillsTheSoldierOnItAndWoundsThoseAround()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        var walker = w.Spawn(2, "Rifleman", 16, 10, Facing.West);
        var close = w.Spawn(2, "Rifleman", 10, 12); // 2 cells: on the edge of the lethal ring
        var off = w.Spawn(2, "Rifleman", 13, 13); // 4.2 cells: outside the blast
        var edge = w.Spawn(2, "Rifleman", 10, 13); // 3 cells: the blast's edge, 25% (30)
        w.Order(2, CommandKind.Move, [walker], cell: new Cell(4, 10));
        w.Run(200);
        Assert.True(mine.Detonated);
        Assert.False(walker.Alive);
        Assert.False(close.Alive); // 120 x (100 - 75 x 2/3)% = 60: a rifleman's 60
        Assert.Equal(off.Def.MaxHp, off.HP);
        Assert.Equal(edge.Def.MaxHp - 30, edge.HP);
    }

    [Theory]
    [InlineData("technical")]
    [InlineData("infantry_carrier")]
    [InlineData("sam_vehicle")]
    public void AnApMineBarelyScratchesAVehicle(string vehicle)
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        var v = w.SpawnCrewed(2, vehicle, 10, 10).Vehicle;
        w.Step();
        Assert.True(mine.Detonated); // a vehicle sets it off too
        Assert.True(v.Alive);
        Assert.True(v.HP * 100 >= v.Def.MaxHp * 90, $"{vehicle}: {v.HP}/{v.Def.MaxHp}");
    }

    [Theory]
    [InlineData("dirt_bike")]
    [InlineData("technical")]
    [InlineData("heavy_rocket_truck")]
    [InlineData("cargo_truck")]
    [InlineData("infantry_carrier")] // 1200 HP of armour
    [InlineData("sam_vehicle")] // the Osa-type air defence vehicle
    public void AnAtMineDestroysAnyVehicle(string vehicle)
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "at_mine", 10, 10);
        var v = w.SpawnCrewed(2, vehicle, 10, 10).Vehicle;
        w.Step();
        Assert.True(mine.Detonated);
        Assert.False(v.Alive);
    }

    [Fact]
    public void AVehicleDrivingOverAnAtMineIsDestroyedButSoldiersWalkOverIt()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "at_mine", 10, 10);
        var walker = w.Spawn(2, "Rifleman", 16, 10, Facing.West);
        w.Order(2, CommandKind.Move, [walker], cell: new Cell(4, 10));
        w.Run(200);
        Assert.True(mine.Alive);
        Assert.Equal(walker.Def.MaxHp, walker.HP);

        var carrier = w.SpawnCrewed(2, "infantry_carrier", 18, 10, Facing.West).Vehicle;
        w.Order(2, CommandKind.Move, [carrier], cell: new Cell(3, 10));
        w.Run(300);
        Assert.True(mine.Detonated);
        Assert.False(carrier.Alive);
    }

    [Fact]
    public void OneAtMineNeverDestroysTwoVehicles()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "at_mine", 10, 10);
        var a = w.SpawnCrewed(2, "technical", 10, 10).Vehicle;
        var b = w.SpawnCrewed(2, "dirt_bike", 10, 10).Vehicle; // both over the mine
        var c = w.SpawnCrewed(2, "technical", 11, 10).Vehicle;
        b.HP = 5;
        c.HP = 5; // the blast alone would finish both
        var soldier = w.Spawn(2, "Rifleman", 11, 11);
        w.Step();
        Assert.True(mine.Detonated);
        Assert.False(a.Alive); // the first in ID order on it
        Assert.Equal((1, 1), (b.HP, c.HP)); // left at 1 HP: never a second vehicle
        Assert.InRange(soldier.HP, 1, soldier.Def.MaxHp - 1); // 1.4 cells: hurt, not killed
    }

    [Fact]
    public void AnAtMineSetOffOnDemandDestroysTheVehicleOnIt()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "at_mine", 10, 10);
        SetAuto(w, 1, false, mine);
        w.Step();
        var a = w.SpawnCrewed(2, "technical", 10, 10).Vehicle;
        var b = w.SpawnCrewed(2, "technical", 11, 11).Vehicle; // 1.4 cells: outside its 1.2-cell trigger zone
        w.Run(10);
        Assert.True(mine.Alive && a.Alive);
        Detonate(w, 1, mine);
        w.Step();
        Assert.False(a.Alive);
        Assert.True(b.Alive);
    }

    // Chains: a mine inside another's blast goes off chainTicks (2) after it, link by link; one
    // outside every blast stays.
    [Fact]
    public void MinesInsideABlastGoOffInAChain()
    {
        var w = Arena.New(40);
        var a = w.Spawn(1, "ap_mine", 10, 10);
        var b = w.Spawn(1, "ap_mine", 12, 10); // 2 cells from a: inside its 3-cell blast
        var c = w.Spawn(1, "ap_mine", 15, 10); // 3 cells from b, 5 from a: b's blast only
        var at = w.Spawn(1, "at_mine", 10, 12); // 2 cells from a
        var far = w.Spawn(1, "ap_mine", 22, 10); // 7 cells from c
        Assert.Equal(2, Trigger(w, "ap_mine").ChainTicks);
        Detonate(w, 1, a);
        var mines = new[] { a, b, c, at, far };
        var gone = new int[mines.Length]; // the tick each went off, -1 never
        Array.Fill(gone, -1);
        for (var t = 0; t < 20; t++)
        {
            w.Step();
            for (var i = 0; i < mines.Length; i++)
            {
                if (mines[i].Detonated && gone[i] < 0)
                {
                    gone[i] = t;
                }
            }
        }
        Assert.Equal([0, 2, 4, 2, -1], gone);
        Assert.True(far.Alive);
    }

    // Stacking: a field acts as one trap whose trigger zone and blast are the union of its mines'
    // and whose damage adds up where blasts overlap.
    [Fact]
    public void MinesLaidTogetherMakeABiggerTrap()
    {
        // The trigger zone: stepping on the last mine of a row sets off the whole row.
        var w = Arena.New(40);
        var row = new[] { w.Spawn(1, "ap_mine", 10, 10), w.Spawn(1, "ap_mine", 12, 10), w.Spawn(1, "ap_mine", 14, 10) };
        var far = w.Spawn(2, "Rifleman", 7, 10); // 3 cells from the first mine, 7 from the last: untouched by one
        w.Spawn(2, "Rifleman", 14, 10); // steps on the last
        w.Run(10);
        Assert.All(row, m => Assert.True(m.Detonated));
        Assert.InRange(far.HP, 0, far.Def.MaxHp - 1); // the first mine's blast reached him

        // The damage: a soldier 2.2 cells from each of two mines survives one, not both.
        foreach (var two in new[] { false, true })
        {
            var v = Arena.New(40);
            var m1 = v.Spawn(1, "ap_mine", 10, 10);
            if (two)
            {
                v.Spawn(1, "ap_mine", 12, 10);
            }
            var s = v.Spawn(2, "Rifleman", 11, 12);
            Detonate(v, 1, m1);
            v.Run(5);
            Assert.Equal(two, !s.Alive);
        }
    }

    [Fact]
    public void ItsOwnerSetsAMineOffOnDemandNobodyElse()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        var enemy = w.Spawn(2, "Rifleman", 12, 10);
        Detonate(w, 2, mine);
        w.Run(3);
        Assert.True(mine.Alive);
        Detonate(w, 1, mine);
        w.Step();
        Assert.True(mine.Detonated);
        Assert.Null(w.Unit(mine.Id));
        Assert.True(enemy.HP < enemy.Def.MaxHp);
    }

    [Fact]
    public void AutoDetonationTurnsOffOnTheMineItself()
    {
        var w = Arena.New(32);
        var layer = w.Spawn(1, "Rifleman", 28, 28); // far from the enemy's walk
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        SetAuto(w, 1, false, layer); // on the soldier: nothing
        SetAuto(w, 2, false, mine); // the enemy can't
        w.Step();
        Assert.False(mine.AutoOff);

        SetAuto(w, 1, false, mine);
        w.Step();
        Assert.True(mine.AutoOff);
        var walker = w.Spawn(2, "Rifleman", 16, 10, Facing.West);
        w.Order(2, CommandKind.Move, [walker], cell: new Cell(4, 10));
        w.Run(200);
        Assert.True(mine.Alive); // walked right over it
        Assert.Equal(walker.Def.MaxHp, walker.HP);

        var back = w.Spawn(2, "Rifleman", 10, 10); // stands on it
        w.Run(10);
        Assert.True(mine.Alive);
        SetAuto(w, 1, true, mine);
        w.Step();
        Assert.True(mine.Detonated); // on again: goes off under him
        Assert.False(back.Alive);
    }

    [Fact]
    public void AMineSetToOnDemandStillGoesOffOnOrderAndInAChain()
    {
        var w = Arena.New(32);
        var a = w.Spawn(1, "at_mine", 10, 10);
        var b = w.Spawn(1, "ap_mine", 11, 10);
        var c = w.Spawn(1, "ap_mine", 20, 10);
        SetAuto(w, 1, false, a, b, c);
        w.Step();
        Detonate(w, 1, a, c);
        w.Run(4);
        Assert.True(a.Detonated && b.Detonated && c.Detonated);
    }

    // Spotting: only a sniper (5 cells), a scout drone (25 cells) or an observation post (10 cells)
    // reveals a mine; then every unit of that side sees and may shoot it.
    [Theory]
    [InlineData("Marksman", 14, 10, true)]
    [InlineData("Marksman", 16, 10, false)]
    [InlineData("Rifleman", 11, 10, false)]
    [InlineData("Sapper", 11, 10, false)]
    [InlineData("Tank hunter", 11, 10, false)]
    [InlineData("kornet", 11, 10, false)]
    [InlineData("manpads", 11, 10, false)]
    [InlineData("scout_drone", 30, 10, true)]
    [InlineData("scout_drone", 37, 10, false)]
    [InlineData("observation_post", 17, 10, true)]
    [InlineData("observation_post", 22, 10, false)]
    public void OnlySnipersScoutDronesAndPostsSpotMines(string spotter, int x, int y, bool spots)
    {
        var w = Arena.New(48);
        var ap = w.Spawn(1, "ap_mine", 10, 10);
        var at = w.Spawn(1, "at_mine", 10, 9);
        var s = w.Spawn(2, spotter, x, y, Facing.West);
        if (s.Def.IsStructure)
        {
            Assert.True(World.Within(s.Pos, ap.Pos, s.Def.Detect) == spots, $"post centre {s.Pos}");
        }
        w.Step();
        Assert.Equal(spots, World.IsVisibleTo(ap, 2));
        Assert.Equal(spots, World.IsVisibleTo(at, 2));
    }

    [Fact]
    public void AVehicleSpotsNoMine()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        w.SpawnCrewed(2, "technical", 12, 10);
        w.SpawnCrewed(2, "sam_vehicle", 10, 12);
        w.Run(5);
        Assert.False(World.IsVisibleTo(mine, 2));
    }

    [Fact]
    public void ASpottedMineCanBeShotFromAfar()
    {
        var w = Arena.New(32);
        var mine = w.Spawn(1, "ap_mine", 10, 10);
        var rifleman = w.Spawn(2, "Rifleman", 14, 10, Facing.West);
        w.Spawn(2, "Marksman", 13, 12, Facing.West); // 3.6 cells: reveals it for the side
        w.Step();
        w.Order(2, CommandKind.Attack, [rifleman], mine.Id);
        w.Run(200);
        Assert.False(mine.Alive);
        Assert.False(mine.Detonated); // shot and cleared: it never went off
        Assert.Equal(rifleman.Def.MaxHp, rifleman.HP);
    }

    // Under fog of war a detector must also see the mine's cell: a ridge hides it from a sniper, not
    // from a scout drone above.
    [Theory]
    [InlineData("Marksman", 0, true)]
    [InlineData("Marksman", 6, false)]
    [InlineData("scout_drone", 6, true)]
    public void UnderFogADetectorNeedsLineOfSight(string spotter, int ridgeM, bool spots)
    {
        const int size = 40, m = 512;
        var w = Arena.New(size, edit: s => s with { Fog = true });
        var corners = new int[(size + 1) * (size + 1)];
        for (var y = 0; y <= size; y++)
        {
            for (var x = 0; x <= size; x++)
            {
                corners[y * (size + 1) + x] = x is >= 10 and <= 11 ? ridgeM * m : 0;
            }
        }
        w.AttachTerrain(new TerrainMap(size, size, corners, new byte[size * size], Rules));
        var mine = w.Spawn(1, "ap_mine", 14, 20);
        w.Spawn(2, spotter, 9, 20, Facing.East); // 5 cells, the ridge between
        w.Run(3);
        Assert.Equal(spots, World.IsVisibleTo(mine, 2));
    }

    [Fact]
    public void TrapStateIsHashed()
    {
        var a = Arena.New(32);
        var b = Arena.New(32);
        var ma = a.Spawn(1, "ap_mine", 10, 10);
        b.Spawn(1, "ap_mine", 10, 10);
        a.Step();
        b.Step();
        Assert.Equal(a.Hash(), b.Hash());
        SetAuto(a, 1, false, ma);
        a.Step();
        b.Step();
        Assert.True(ma.AutoOff);
        Assert.NotEqual(a.Hash(), b.Hash());
    }
}

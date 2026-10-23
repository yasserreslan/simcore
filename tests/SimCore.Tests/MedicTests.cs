using SimCore.Content;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// The Sentinel's assault medic (sentinel_medic) and the aura system that runs his medic_kit: healing friendly people on
// foot near him, at most one heal per interval however many medics stand near.
public class MedicTests
{
    private const string Medic = "sentinel_medic";

    private static AuraEffect Kit(World w) => w.Player(1)!.Setup.Find(w.Content, Medic)!.Auras.Single();

    // Wounded: a unit of player's at cell (x, y) with hp hit points.
    private static Unit Wounded(World w, byte player, string name, int x, int y, int hp)
    {
        var u = w.Spawn(player, name, x, y);
        u.HP = hp;
        return u;
    }

    private static void HoldFire(World w, params Unit[] units)
    {
        foreach (var u in units)
        {
            w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.HoldFire });
        }
    }

    // ---- Content ----

    [Fact]
    public void TheMedicIsSentinelContent()
    {
        var c = TestContent.Mvp;
        var medic = c.Archetype(Medic)!;
        var rifleman = c.Archetype("assault")!;
        Assert.True(medic.Tags.HasAll(c.TagsOf("infantry", "soldier"))); // a soldier: he counts against defeat
        Assert.True(medic.Tags.Matches(c.Faction("sentinel")!.DefeatedWithout));
        Assert.True(c.Faction("sentinel")!.Archetypes[medic.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[medic.Index]); // the Sentinel's own
        Assert.False(medic.HasSlots); // a fixed loadout: the rifle and the aid bag

        // A little tougher than the Resistance rifleman, a little slower, the same eyes, about 1.6 riflemen's cost.
        Assert.True(medic.MaxHp > rifleman.MaxHp);
        Assert.True(medic.Speed.Raw < rifleman.Speed.Raw);
        Assert.Equal(rifleman.Vision, medic.Vision);

        var w = Arena.NewSentinel();
        var def = w.Player(1)!.Setup.Find(w.Content, Medic)!;
        Assert.Equal("rifle", def.Components[0].Id);
        Assert.Single(def.Weapons);
        var rifle = w.Player(1)!.Setup.Find(w.Content, "Rifleman")!;
        Assert.InRange(def.Cost, rifle.Cost * 3 / 2, rifle.Cost * 2); // 260 against 165
        Assert.Null(w.Player(2)!.Setup.Find(w.Content, Medic)); // the Resistance can't field him

        // The kit as content: 3 HP every 1.5 s to the side's people on foot within 6 cells.
        var kit = Kit(w);
        Assert.Equal(3, kit.Heal);
        Assert.Equal(30, kit.EveryTicks);
        Assert.Equal(FixedPoint.Fixed.FromInt(6), kit.Radius);
        Assert.Equal(Relation.Ally, kit.Affects);
        Assert.Equal(c.TagsOf("infantry"), kit.AffectsTags);

        // The Sentinel lobby fields two.
        var army = TestContent.Sentinel(1).Players.Single(p => p.Faction == "sentinel").Army;
        Assert.Equal(2, army.Single(e => e.Unit == Medic).Count);
    }

    [Fact]
    public void TheHealIsContent()
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("components/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, sources[i].Json.Replace(
            "\"heal\": 3, \"everyTicks\": 30, \"radius\": 60", "\"heal\": 7, \"everyTicks\": 10, \"radius\": 20", StringComparison.Ordinal));
        var content = ContentLoader.Load(sources);
        var w = PatchedWorld(content);
        w.Spawn(1, Medic, 10, 10);
        var near = Wounded(w, 1, "Rifleman", 12, 10, 10);
        var far = Wounded(w, 1, "Rifleman", 13, 10, 10); // 3 cells: beyond the patched 2
        w.Run(11); // ticks 0 and 10
        Assert.Equal(24, near.HP);
        Assert.Equal(10, far.HP);
    }

    // ---- Healing ----

    [Fact]
    public void AWoundedSoldierHealsAtTheKitsRateUpToMax()
    {
        var w = Arena.NewSentinel();
        var kit = Kit(w);
        var medic = w.Spawn(1, Medic, 10, 10);
        var s = Wounded(w, 1, "Rifleman", 16, 10, 20); // 6 cells: on the radius's edge
        w.Run(1);
        Assert.Equal(20 + kit.Heal, s.HP); // the first heal at once
        w.Run(kit.EveryTicks - 1);
        Assert.Equal(20 + kit.Heal, s.HP); // none until the interval is up
        w.Run(1);
        Assert.Equal(20 + 2 * kit.Heal, s.HP);
        w.Run(10 * kit.EveryTicks);
        Assert.Equal(20 + 12 * kit.Heal, s.HP); // 2 HP a second

        w.Run(40 * kit.EveryTicks);
        Assert.Equal(s.Def.MaxHp, s.HP); // never above max
        Assert.Equal(medic.Def.MaxHp, medic.HP);
    }

    [Fact]
    public void TwoMedicsDoNotHealTwice()
    {
        var w = Arena.NewSentinel();
        var kit = Kit(w);
        w.Spawn(1, Medic, 8, 10);
        w.Spawn(1, Medic, 12, 10);
        w.Spawn(1, Medic, 10, 12);
        var s = Wounded(w, 1, "Rifleman", 10, 10, 10); // within reach of all three
        w.Run(1);
        Assert.Equal(10 + kit.Heal, s.HP);
        w.Run(kit.EveryTicks);
        Assert.Equal(10 + 2 * kit.Heal, s.HP);
        w.Run(3 * kit.EveryTicks);
        Assert.Equal(10 + 5 * kit.Heal, s.HP);
    }

    [Fact]
    public void AMedicDoesNotHealHimselfButAnotherMedicDoes()
    {
        var w = Arena.NewSentinel();
        var kit = Kit(w);
        var a = Wounded(w, 1, Medic, 10, 10, 30);
        w.Run(3 * kit.EveryTicks);
        Assert.Equal(30, a.HP); // alone: nobody to treat him
        var b = Wounded(w, 1, Medic, 12, 10, 30);
        w.Run(1);
        Assert.Equal(30 + kit.Heal, a.HP); // each treats the other
        Assert.Equal(30 + kit.Heal, b.HP);
    }

    [Fact]
    public void NothingOutOfRadius()
    {
        var w = Arena.NewSentinel();
        var kit = Kit(w);
        w.Spawn(1, Medic, 10, 10);
        var s = Wounded(w, 1, "Rifleman", 17, 10, 20); // 7 cells
        var diagonal = Wounded(w, 1, "Rifleman", 15, 15, 20); // 7.07 cells
        w.Run(4 * kit.EveryTicks);
        Assert.Equal(20, s.HP);
        Assert.Equal(20, diagonal.HP);
    }

    [Fact]
    public void EnemiesVehiclesAndStructuresAreNotHealed()
    {
        var w = Arena.NewSentinel(48);
        var kit = Kit(w);
        var medic = w.Spawn(1, Medic, 10, 10);
        var enemy = Wounded(w, 2, "Rifleman", 14, 10, 20);
        var car = Wounded(w, 1, "humvee", 10, 13, 50);
        var worker = Wounded(w, 1, "worker", 8, 10, 10); // a person on foot: healed
        var depot = w.Spawn(1, "supply_depot", 10, 4);
        depot.HP = depot.Def.MaxHp / 2;
        HoldFire(w, medic, enemy);
        w.Run(4 * kit.EveryTicks);
        Assert.Equal(20, enemy.HP);
        Assert.Equal(50, car.HP);
        Assert.Equal(depot.Def.MaxHp / 2, depot.HP);
        Assert.Equal(10 + 4 * kit.Heal, worker.HP);
    }

    [Fact]
    public void NoHealInsideAVehicle()
    {
        var w = Arena.NewSentinel();
        var kit = Kit(w);
        w.Spawn(1, Medic, 10, 10);
        var (car, driver) = w.SpawnCrewed(1, "humvee", 11, 12);
        driver.HP = 20;
        w.Run(4 * kit.EveryTicks);
        Assert.Equal(car.Id, driver.Inside);
        Assert.Equal(20, driver.HP); // in the seat: out of the medic's reach

        // A medic driving heals nobody either.
        var w2 = Arena.NewSentinel();
        var car2 = w2.Spawn(1, "humvee", 10, 10);
        var m = w2.Spawn(1, Medic, 10, 10);
        World.Board(m, car2);
        var s = Wounded(w2, 1, "Rifleman", 12, 10, 20);
        w2.Run(4 * kit.EveryTicks);
        Assert.NotEqual(0u, m.Inside);
        Assert.Equal(20, s.HP);
    }

    // ---- Determinism ----

    // Play: two medics and wounded soldiers (spawned in order or reversed), some within both medics' reach, one within
    // neither; each soldier's HP every tick, by spawn position, and the world hashes.
    private static (List<ulong> Hashes, List<string> Hp) Play(bool reversed)
    {
        var w = Arena.NewSentinel();
        var spots = new List<(string Name, int X, int Y, int Hp)>
        {
            (Medic, 8, 10, 40), (Medic, 13, 10, 75), ("Rifleman", 10, 10, 12), ("Rifleman", 11, 11, 31),
            ("Gunner", 4, 10, 5), ("Raider", 18, 12, 44), ("Rifleman", 25, 25, 9), ("worker", 10, 14, 7),
        };
        if (reversed)
        {
            spots.Reverse();
        }
        var units = spots.Select(s => Wounded(w, 1, s.Name, s.X, s.Y, s.Hp)).ToList();
        var order = Enumerable.Range(0, spots.Count).OrderBy(i => spots[i].X).ThenBy(i => spots[i].Y).ToList();
        var hashes = new List<ulong>();
        var hp = new List<string>();
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
            hp.Add(string.Join(",", order.Select(i => units[i].HP)));
        }
        return (hashes, hp);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Play(false);
        Assert.Equal(a.Hashes, Play(false).Hashes); // run twice
        var r = Play(true);
        Assert.Equal(a.Hp, r.Hp); // the spawn order (the IDs) changes nothing
        // 14 heals of 3 in 20 s for everyone within 6 cells of the other medic or him, capped at max HP; (25, 25) is beyond.
        Assert.Equal("47,75,54,40,60,75,60,9", a.Hp[^1]);
    }

    private static World PatchedWorld(ContentSet content)
    {
        var setup = Replay.LoadSetup(Path.Combine(TestPaths.RepoRoot(), "content", "setups", "sentinel.json")).For(content, 1);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(32, 32), MatchCompiler.Compile(content, setup));
    }
}

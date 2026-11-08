using SimCore.Bots;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Decoys (docs/design.md, "Decoys"): the Resistance's fakes of the mobile radar, the heavy rocket truck, the tactical missile
// truck and the cargo truck. Built at the vehicle garage, they only move (no weapon, ability, set-up, cargo or seat), drive
// themselves, and pass for the real one with every other player (World.ShownArchetype / ShownDef): cards, the units choosing
// targets and the skirmish AI. 60 HP: small arms take a while, one RPG, missile or shell kills.
public class DecoyTests
{
    private static readonly ContentSet C = TestContent.Mvp;

    public static TheoryData<string, string> Pairs => new()
    {
        { "decoy_mobile_radar", "mobile_radar" },
        { "decoy_heavy_rocket_truck", "heavy_rocket_truck" },
        { "decoy_ballistic_truck", "ballistic_truck" },
        { "decoy_cargo_truck", "cargo_truck" },
    };

    private static void Produce(World w, Unit g, string unit, byte player) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.Produce, Units = [g.Id], Unit = unit });

    private static bool Within(Vec a, Vec b, int cells) => (a - b).LenSq() <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ItsNumbersMatchTheRealOnesBodyAndNothingElse(string decoy, string real)
    {
        var a = C.Archetype(decoy)!;
        var r = C.Archetype(real)!;
        Assert.Equal(r.Index, a.Disguise);
        Assert.Equal(-1, r.Disguise);
        // The body and the pace: nothing to tell them apart by on the move.
        Assert.Equal((r.Locomotor, r.Armor, r.Radius, r.HeightRaw, r.Crusher, r.TunnelSize, r.TunnelSlots, r.AccelTicks),
            (a.Locomotor, a.Armor, a.Radius, a.HeightRaw, a.Crusher, a.TunnelSize, a.TunnelSlots, a.AccelTicks));
        var w = Arena.New(8);
        var (dd, rd) = (w.Player(1)!.Setup.Find(C, decoy)!, w.Player(1)!.Setup.Find(C, real)!);
        Assert.Equal((rd.Speed, rd.TurnRate), (dd.Speed, dd.TurnRate));
        // Move only: no weapon, ability, component, set-up, radar, cargo, seat; vision 2 cells; 60 HP.
        Assert.Empty(dd.Weapons);
        Assert.Empty(dd.Abilities);
        Assert.Empty(dd.Components);
        Assert.Null(dd.Cargo);
        Assert.Null(dd.Crew);
        Assert.Equal((0, 0, 0, 0), (a.DeployTicks, a.AirVision.Raw, a.CueLock, a.SweepTicks));
        Assert.Equal(Fixed.FromInt(3), dd.Vision);
        Assert.Equal(60, dd.MaxHp);
        // Very cheap and quick: about a tenth of the real one's cost, a fraction of its build time (placeholders).
        Assert.InRange(a.Cost * 10, r.Cost - 10, r.Cost + 10);
        Assert.True(a.BuildTime > 0 && a.BuildTime * 3 <= r.BuildTime, $"{a.BuildTime} vs {r.BuildTime}");
        // Tags: a vehicle and a decoy; not a launcher, heavy, radar or air radar (it emits nothing, links to no battery).
        Assert.True(a.Tags.Matches(C.TagsOf("vehicle")) && a.Tags.Matches(C.TagsOf("decoy")));
        foreach (var t in new[] { "launcher", "heavy", "radar", "air_radar", "soldier" })
        {
            Assert.False(a.Tags.Matches(C.TagsOf(t)), t);
        }
        // The Resistance's, on its own line, built at the vehicle garage; not the Sentinel's.
        Assert.True(C.Faction("resistance")!.Archetypes[a.Index]);
        Assert.False(C.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.True(C.Archetype("vehicle_garage")!.Production!.Builds(a.Tags));
        Assert.False(C.Archetype("resistance_barracks")!.Production!.Builds(a.Tags));
    }

    // Produced at the garage like any vehicle, but nobody comes with it: it drives itself out to the rally point, and then
    // wherever it is sent at the real one's speed.
    [Theory]
    [MemberData(nameof(Pairs))]
    public void TheGarageBuildsItAndItDrivesItself(string decoy, string real)
    {
        _ = real;
        var w = Arena.New(64);
        var g = w.Spawn(1, "vehicle_garage", 20, 10);
        Produce(w, g, decoy, 1);
        w.Step();
        Assert.Equal(decoy, w.Match.Defs[g.Bays[0].Def].Archetype.Id);
        var build = C.Archetype(decoy)!.BuildTime;
        w.Run(build + 30 + 1);
        var d = Assert.Single(w.All(decoy));
        Assert.Equal((1, 0u, 0u), (d.Owner, d.Driver, d.Inside));
        Assert.True(d.CanAct);
        Assert.Empty(w.All("assault")); // no driver made with it
        w.Run(600);
        Assert.True(Within(d.Pos, new Cell(28, 26).Center(), 4), $"{d.Pos}");
        var from = d.Pos;
        w.Order(1, CommandKind.Move, [d], cell: new Cell(28, 50));
        w.Run(20 * 6);
        Assert.True(d.Pos.Y.Raw > from.Y.Raw + Fixed.OneRaw, $"{from} -> {d.Pos}");
    }

    // It cannot fight, set up, launch or carry anyone: an attack order hurts nobody, Deploy does nothing, a soldier cannot get in.
    [Fact]
    public void ItCannotAttackSetUpOrCarry()
    {
        var w = Arena.New(40);
        var d = w.Spawn(1, "decoy_heavy_rocket_truck", 10, 10);
        var e = w.Spawn(2, "Rifleman", 13, 10);
        e.Stance = Stance.HoldFire;
        var s = w.Spawn(1, "Rifleman", 10, 13);
        s.Stance = Stance.HoldFire;
        w.Order(1, CommandKind.Attack, [d], target: e.Id);
        w.Order(1, CommandKind.Enter, [s], target: d.Id);
        w.Run(2);
        w.Order(1, CommandKind.Deploy, [d]);
        w.Order(1, CommandKind.AttackGround, [d], cell: new Cell(30, 30));
        w.Run(400);
        Assert.Equal(e.Def.MaxHp, e.HP);
        Assert.Equal(DeployState.Stowed, d.Deploy);
        Assert.Equal((0u, 0u), (s.Inside, d.Driver));
        Assert.Equal(0u, d.AutoTarget);
    }

    // Its owner sees the decoy; every other player (fog on or off) the real archetype, with the owner's definition of it; HP a
    // percent of its own max. Viewer 0 (no player) sees the truth.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEnemySeesTheRealOneItsOwnerTheDecoy(bool fog)
    {
        var w = Arena.NewSentinel(40, 1, s => s with { Fog = fog });
        var d = w.Spawn(2, "decoy_heavy_rocket_truck", 10, 10); // player 2: the Resistance
        var real = w.Spawn(2, "heavy_rocket_truck", 14, 10);
        w.Spawn(1, "Rifleman", 10, 16); // player 1, the Sentinel, sees them
        w.Run(8);
        Assert.True(World.IsVisibleTo(d, 1));
        Assert.Equal("heavy_rocket_truck", w.ShownArchetype(d, 1).Id);
        Assert.Equal("Heavy rocket truck", w.ShownArchetype(d, 1).Name);
        Assert.Same(real.Def, w.ShownDef(d, 1)); // the owner's own definition of the real one
        Assert.Equal(real.Def.Weapons.Count, w.ShownDef(d, 1).Weapons.Count);
        Assert.False(w.KnownDecoy(d, 1));
        Assert.Equal("decoy_heavy_rocket_truck", w.ShownArchetype(d, 2).Id);
        Assert.Same(d.Def, w.ShownDef(d, 2));
        Assert.True(w.KnownDecoy(d, 2));
        Assert.Same(d.Def, w.ShownDef(d, 0));
        Assert.Same(real.Def, w.ShownDef(real, 1)); // a real unit is itself to everyone
        Assert.False(w.KnownDecoy(real, 2));
        // HP as a percent: the same share looks the same.
        d.HP = 30;
        real.HP = 150;
        Assert.Equal((50, 50), (World.ShownHpPercent(d), World.ShownHpPercent(real)));
        d.HP = 1;
        Assert.Equal(1, World.ShownHpPercent(d));
    }

    // A unit choosing its own target puts a threat before a harmless unit: a decoy launcher is the threat the real one would be,
    // picked over a nearer harmless (real) cargo truck, as the real launcher is.
    [Theory]
    [InlineData("heavy_rocket_truck")]
    [InlineData("decoy_heavy_rocket_truck")]
    public void UnitsTakeItForTheThreatTheRealOneIs(string launcher)
    {
        var w = Arena.NewSentinel(40);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        u.Stance = Stance.HoldGround;
        var truck = w.Spawn(2, "cargo_truck", 12, 10);
        var l = w.Spawn(2, launcher, 10, 14);
        w.Run(2);
        Assert.Equal(l.Id, u.AutoTarget);
        _ = truck;
    }

    // The skirmish AI is fog-honest and reads what its side is shown: the same game with a decoy in place of the real launcher
    // gives the same commands (until something hits it).
    [Fact]
    public void TheSkirmishAiTakesItForTheRealOne()
    {
        List<Command> Play(string intruder)
        {
            var w = Arena.New(96, 3, s => s with { Fog = true });
            w.Spawn(1, "supply_depot", 20, 20);
            for (var i = 0; i < 5; i++)
            {
                w.Spawn(1, "Rifleman", 22 + i, 28);
            }
            w.Spawn(2, intruder, 40, 30); // inside its hold radius, out of rifle range
            w.Spawn(2, "Rifleman", 80, 80);
            var ai = new SkirmishAi(1) { UseSquads = false };
            var all = new List<Command>();
            for (var t = 0; t < 61; t++)
            {
                foreach (var c in ai.Think(w))
                {
                    all.Add(c);
                    w.Issue(c);
                }
                w.Step();
            }
            return all;
        }
        var real = Play("heavy_rocket_truck");
        var fake = Play("decoy_heavy_rocket_truck");
        Assert.NotEmpty(real);
        Assert.Equal(real.Count, fake.Count);
        for (var i = 0; i < real.Count; i++)
        {
            Assert.Equal((real[i].Tick, real[i].Kind, real[i].Target, real[i].Cell, real[i].Stance), (fake[i].Tick, fake[i].Kind, fake[i].Target, fake[i].Cell, fake[i].Stance));
            Assert.Equal(real[i].Units, fake[i].Units);
        }
    }

    // Flimsy: one RPG, Kornet, tank shell or heavy rocket kills it; a mortar bomb takes two; a rifle 15 shots, a machine gun 20
    // rounds. The real one lives through a single RPG, Kornet or mortar hit (a heavy rocket kills it too).
    [Theory]
    [InlineData("at_missile", 1, true)]
    [InlineData("kornet_atgm", 1, true)]
    [InlineData("heavy_artillery_rockets", 1, false)]
    [InlineData("tank_120mm", 1, true)]
    [InlineData("mortar_tube", 2, true)]
    [InlineData("rifle", 15, true)]
    [InlineData("lmg", 20, true)]
    public void OneHeavyHitKillsSmallArmsTakeAWhile(string weapon, int hits, bool realLives)
    {
        var e = C.Component(weapon)!.Effects.OfType<WeaponEffect>().First();
        var w = Arena.New(40);
        var d = w.Spawn(2, "decoy_heavy_rocket_truck", 10, 10);
        var real = w.Spawn(2, "heavy_rocket_truck", 20, 10);
        var n = 0;
        while (d.HP > 0 && n < 100)
        {
            w.Hurt(d, e.Warhead, e.Damage, 100);
            n++;
        }
        Assert.Equal(hits, n);
        w.Hurt(real, e.Warhead, e.Damage, 100);
        Assert.Equal(realLives, real.HP > 0);
    }

    // Shot for real: one Sentinel RPG round kills a decoy launcher at the first hit; the real one takes it and lives.
    [Theory]
    [InlineData("decoy_ballistic_truck", true)]
    [InlineData("ballistic_truck", false)]
    public void AnRpgRoundKillsItOutright(string target, bool dies)
    {
        var w = Arena.NewSentinel(40);
        var t = w.Spawn(2, target, 10, 10);
        w.Spawn(1, "Tank hunter", 10, 16);
        var max = t.Def.MaxHp;
        var first = -1;
        for (var i = 0; i < 400 && first < 0; i++)
        {
            w.Step();
            first = t.HP < max ? t.HP : -1;
        }
        Assert.True(first != -1, "never hit");
        Assert.Equal(dies, first <= 0);
    }

    // The same game twice, decoys and all, ends in the same state.
    [Fact]
    public void Deterministic()
    {
        ulong Game()
        {
            var w = Arena.NewSentinel(64, 5, s => s with { Fog = true });
            var g = w.Spawn(2, "vehicle_garage", 20, 10);
            foreach (var id in new[] { "decoy_mobile_radar", "decoy_heavy_rocket_truck", "decoy_ballistic_truck", "decoy_cargo_truck" })
            {
                Produce(w, g, id, 2);
            }
            for (var i = 0; i < 4; i++)
            {
                w.Spawn(1, "Rifleman", 26 + i, 40);
            }
            w.Run(900);
            return w.Hash();
        }
        Assert.Equal(Game(), Game());
    }

    // The loader holds a disguise to a real unit of the same faction with the same body.
    public static TheoryData<string, string, string> BadDisguises => new()
    {
        { "no such archetype", """{"archetypes":[{"id":"fake_x","disguise":"nope","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"medium_vehicle","hp":60,"speed":16,"radius":12,"turnRate":1,"vision":20}]}""", "no such archetype" },
        { "itself", """{"archetypes":[{"id":"fake_x","disguise":"fake_x","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"medium_vehicle","hp":60,"speed":16,"radius":12,"turnRate":1,"vision":20}]}""", "names itself" },
        { "a structure", """{"archetypes":[{"id":"fake_x","disguise":"vehicle_garage","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"medium_vehicle","hp":60,"speed":16,"radius":12,"turnRate":1,"vision":20}]}""", "not structures" },
        { "another armour", """{"archetypes":[{"id":"fake_x","disguise":"heavy_rocket_truck","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"light_vehicle","hp":60,"speed":16,"radius":12,"turnRate":1,"vision":20}]}""", "same layer, locomotor, armor class and radius" },
        { "a decoy", """{"archetypes":[{"id":"fake_x","disguise":"decoy_cargo_truck","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"light_vehicle","hp":60,"speed":22,"radius":10,"turnRate":2,"vision":20}]}""", "real unit without slots" },
        { "another faction", """{"archetypes":[{"id":"fake_x","disguise":"humvee","name":"F","glyph":"f","tags":["vehicle"],"locomotor":"wheeled","armor":"medium_vehicle","hp":60,"speed":40,"radius":8,"turnRate":2,"vision":20}],"factions":[{"id":"fakers","name":"Fakers","archetypes":["fake_x"],"components":[],"maxPresetsPerArchetype":3,"defeatedWithout":["soldier"]}]}""", "same faction" },
    };

    [Theory]
    [MemberData(nameof(BadDisguises))]
    public void TheLoaderRejectsABadDisguise(string name, string json, string want)
    {
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("extra/bad.json", json));
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.True(e.Message.Contains(want, StringComparison.Ordinal) && e.Message.Contains("extra/bad.json", StringComparison.Ordinal), $"{name}: {e.Message}");
    }
}

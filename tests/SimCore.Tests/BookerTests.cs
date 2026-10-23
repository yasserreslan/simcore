using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Sentinel's light tank (light_tank, "Booker", an M10 Booker type; docs/design.md, "Light tank"): faster than the Abrams, weaker
// against tanks (the 105 mm gun: 35 rounds, HE or armour-piercing by target, two thirds of the Abrams' punch; lighter directional
// armour 60/100/140/160%), the launcher hunter (every Resistance launcher in one or two hits), smoke, no Trophy. The airlift lives in
// AirliftTests.
public class BookerTests(ITestOutputHelper output)
{
    private const string Booker = "light_tank";
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

    private static int Rounds(Unit t) => t.Charges[t.Def.Weapons[0].Stock];

    private static int HitsToKill(int hp, int damage) => (hp + damage - 1) / damage;

    // ---- Content ----

    [Fact]
    public void ItIsASentinelTankFromTheMilitaryGarageOnly()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Booker)!;
        Assert.True(c.Faction("sentinel")!.Archetypes[a.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[a.Index]);
        Assert.True(c.Archetype("military_garage")!.Production!.Builds(a.Tags));
        Assert.False(c.Archetype("vehicle_garage")!.Production!.Builds(a.Tags)); // excludeTags: tank

        var w = Arena.NewSentinel(96);
        var mg = w.Spawn(1, "military_garage", 20, 10);
        var vg = w.Spawn(1, "vehicle_garage", 70, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [mg.Id, vg.Id], Unit = Booker });
        var built = 0u;
        for (var t = 0; t < 1000 && built == 0; t++)
        {
            w.Step();
            if (w.All(Booker).FirstOrDefault() is not null)
            {
                built = w.Tick;
            }
        }
        output.WriteLine($"built at tick {built}");
        Assert.InRange(built, 600u, 700u); // 30 s in a bay, then out of the door
        Assert.Single(w.All(Booker));
    }

    [Fact]
    public void ItsStatsAreTheSpecs()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Booker)!;
        var m = c.Archetype(Abrams)!;
        Assert.Equal((1400, 1500, 600), (a.MaxHp, a.Cost, a.BuildTime));
        Assert.Equal(c.ArmorClass("armored_vehicle"), a.Armor);
        Assert.Equal(Locomotor.Tracked, a.Locomotor);
        Assert.Equal(Fixed.Frac(38, 200), a.Speed); // 3.8 cells/s
        Assert.True(a.Speed.Raw > m.Speed.Raw, "faster than the Abrams");
        Assert.Equal(3, a.TurnRate);
        Assert.Equal(Fixed.FromInt(9), a.Vision);
        Assert.True(a.Vision.Raw > m.Vision.Raw, "better sights than the Abrams");
        Assert.Equal(Fixed.Frac(11, 10), a.Radius);
        Assert.True(a.Turret);
        Assert.Equal(3, a.Crusher);
        Assert.Equal(c.TagsOf("soldier"), a.Crew!.Tags);
        Assert.Equal(0, a.Crew.Passengers);
        Assert.True(a.Tags.Matches(c.TagsOf("tank")) && a.Tags.Matches(c.TagsOf("airliftable")));
        Assert.False(m.Tags.Matches(c.TagsOf("airliftable")), "only the light tank flies");
        Assert.Equal(new DirectionalArmor(60, 100, 140, 160, 4, 4), a.DirectionalArmor);

        var w = Sentinel();
        var def = w.Player(1)!.Setup.Find(w.Content, Booker)!;
        Assert.Equal(["light_tank_105mm", "tank_mgs", "smoke_launchers"], def.Components.Select(x => x.Id));
        var (he, ap, mg) = (def.Weapons[0], def.Weapons[1], def.Weapons[2]);
        Assert.Equal(("he_mpat", 170, Fixed.Frac(18, 1), 120, Fixed.Frac(25, 10)), (he.Warhead.Id, he.Damage, he.Range, he.Reload, he.Effect.Splash));
        Assert.Equal(("apfsds", 600, Fixed.FromInt(18), 120), (ap.Warhead.Id, ap.Damage, ap.Range, ap.Reload));
        Assert.Equal(he.Stock, ap.Stock); // one magazine
        Assert.Equal(35, def.StockMax[he.Stock]);
        Assert.True(ap.Effect.SharedAmmo);
        Assert.True(mg.Effect.Coaxial && def.HasCoaxial);
        Assert.Equal(Layers.Ground, he.Effect.Targets | ap.Effect.Targets | mg.Effect.Targets);
        Assert.Null(def.Aps); // no Trophy: that stays the Abrams'
        Assert.Equal(2, def.StockMax[def.SmokeStock]);
        Assert.Equal(2, def.StockMax.Count); // rounds and smoke: nothing else

        // Shorter-ranged than the Abrams' 120 mm, two thirds of its punch.
        var mdef = w.Player(1)!.Setup.Find(w.Content, Abrams)!;
        Assert.True(he.Range.Raw < mdef.Weapons[0].Range.Raw);
        Assert.Equal(mdef.Weapons[1].Damage * 2 / 3, ap.Damage);
    }

    // It outruns the Abrams: the same 40-cell drive on open ground, side by side.
    [Fact]
    public void ItOutrunsTheAbrams()
    {
        var w = Sentinel(96);
        var (b, _) = w.SpawnCrewed(1, Booker, 10, 20, Facing.East);
        var (m, _) = w.SpawnCrewed(1, Abrams, 10, 60, Facing.East);
        w.Order(1, CommandKind.Move, [b], cell: new Cell(50, 20));
        w.Order(1, CommandKind.Move, [m], cell: new Cell(50, 60));
        uint bAt = 0, mAt = 0;
        for (var t = 0; t < 600 && (bAt == 0 || mAt == 0); t++)
        {
            w.Step();
            if (bAt == 0 && Cell.Of(b.Pos) == new Cell(50, 20))
            {
                bAt = w.Tick;
            }
            if (mAt == 0 && Cell.Of(m.Pos) == new Cell(50, 60))
            {
                mAt = w.Tick;
            }
        }
        output.WriteLine($"40 cells: Booker {bAt / 20.0:F1} s, Abrams {mAt / 20.0:F1} s");
        Assert.True(bAt > 0 && mAt > 0);
        Assert.True(bAt * 100 <= mAt * 85, "at least 15% quicker");
    }

    // ---- The gun ----

    private static Projectile FirstShot(World w, Unit tank, Unit target)
    {
        w.Order(tank.Owner, CommandKind.Attack, [tank], target.Id);
        for (var i = 0; i < 200 && !w.Projectiles.Any(p => p.Shooter == tank.Id); i++)
        {
            w.Step();
        }
        return w.Projectiles.First(p => p.Shooter == tank.Id);
    }

    [Fact]
    public void ArmourPiercingAgainstVehiclesHighExplosiveAgainstSoldiersAndBuildings()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        var (carrier, _) = w.SpawnCrewed(2, "infantry_carrier", 35, 40);
        HoldFire(w, carrier);
        Assert.Equal("apfsds", FirstShot(w, t, carrier).Weapon.Warhead.Id);
        Assert.Equal(34, Rounds(t));

        var w2 = Sentinel();
        var (t2, _) = w2.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        var men = Enumerable.Range(0, 3).Select(i => w2.Spawn(2, "Rifleman", 28, 39 + i)).ToList();
        HoldFire(w2, [.. men]);
        Assert.Equal("he_mpat", FirstShot(w2, t2, men[1]).Weapon.Warhead.Id);
        Assert.Equal(34, Rounds(t2));

        var w3 = Sentinel();
        var (t3, _) = w3.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        var depot = w3.Spawn(2, "supply_depot", 35, 40);
        Assert.Equal("he_mpat", FirstShot(w3, t3, depot).Weapon.Warhead.Id);
    }

    [Fact]
    public void ItFiresOnTheMoveEverySixSeconds()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 20, 10, Facing.South);
        var (truck, _) = w.SpawnCrewed(2, "cargo_truck", 26, 40);
        var spotter = w.Spawn(1, "Rifleman", 27, 43);
        truck.HP = 100_000;
        HoldFire(w, truck, spotter);
        w.Order(1, CommandKind.AttackMove, [t], cell: new Cell(20, 80));
        var firedAt = new List<uint>();
        var moving = true;
        for (var i = 0; i < 400; i++)
        {
            var before = Rounds(t);
            w.Step();
            if (Rounds(t) < before)
            {
                firedAt.Add(w.Tick);
                moving &= t.Moving;
            }
        }
        output.WriteLine("fired at " + string.Join(", ", firedAt));
        Assert.True(firedAt.Count >= 2);
        Assert.True(moving, "it fires while it drives");
        Assert.True(firedAt.Zip(firedAt.Skip(1)).All(p => p.Second - p.First >= 120), "a round every 6 s at most");
    }

    [Fact]
    public void TheMachineGunsCarryOnWhenTheRoundsAreGone()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        t.MutableCharges[t.Def.Weapons[0].Stock] = 0;
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(2, "Rifleman", 25, 38 + i)).ToList();
        HoldFire(w, [.. men]);
        w.Run(200);
        Assert.All(men, m => Assert.False(m.Alive));
        Assert.Equal(0, Rounds(t));
    }

    // ---- Against tanks ----

    // HitOn: the damage the first hit of shooter (ordered at target) deals to target standing still, and where it landed.
    private static (int Damage, HitFacing Facing) HitOn(World w, Unit shooter, Unit target)
    {
        target.HP = 100_000;
        HoldFire(w, target);
        w.Order(shooter.Owner, CommandKind.Attack, [shooter], target.Id);
        for (var i = 0; i < 400 && target.HP == 100_000; i++)
        {
            w.Step();
        }
        var hit = w.ArmorHits.First(h => h.Unit == target.Id);
        return (100_000 - target.HP, hit.Facing);
    }

    // The Booker against the Abrams: 600 through 40 / 80 / 130%: 11 hits head-on (hopeless), 6 on a side, 4 from behind. The
    // Abrams against the Booker: 900 through 60 / 100 / 140%: 3 head-on, 2 on a side or the rear.
    [Theory]
    [InlineData(Booker, Abrams, Facing.West, HitFacing.Front, 240, 11)]
    [InlineData(Booker, Abrams, Facing.North, HitFacing.Side, 480, 6)]
    [InlineData(Booker, Abrams, Facing.East, HitFacing.Rear, 780, 4)]
    [InlineData(Abrams, Booker, Facing.West, HitFacing.Front, 540, 3)]
    [InlineData(Abrams, Booker, Facing.South, HitFacing.Side, 900, 2)]
    [InlineData(Abrams, Booker, Facing.East, HitFacing.Rear, 1260, 2)]
    [InlineData(Booker, Booker, Facing.West, HitFacing.Front, 360, 4)]
    [InlineData(Booker, Booker, Facing.North, HitFacing.Side, 600, 3)]
    [InlineData(Booker, Booker, Facing.East, HitFacing.Rear, 840, 2)]
    public void HitsToKillByFacing(string shooter, string target, int facing, HitFacing where, int damage, int hits)
    {
        var w = Duel();
        var (a, _) = w.SpawnCrewed(1, shooter, 20, 40, Facing.East);
        var (b, _) = w.SpawnCrewed(2, target, 35, 40, facing);
        var hp = b.Def.MaxHp;
        var (d, f) = HitOn(w, a, b);
        output.WriteLine($"{shooter} on {target} ({where}): {d} a hit, {HitsToKill(hp, d)} to kill");
        Assert.Equal((damage, where), (d, f));
        Assert.Equal(hits, HitsToKill(hp, d));
    }

    // A whole head-on duel at 15 cells, both firing from the start: the Abrams wins, with most of its HP.
    [Fact]
    public void HeadOnItLosesToTheAbrams()
    {
        var w = Duel();
        var (b, _) = w.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        var (m, _) = w.SpawnCrewed(2, Abrams, 35, 40, Facing.West);
        w.Order(1, CommandKind.Attack, [b], m.Id);
        w.Order(2, CommandKind.Attack, [m], b.Id);
        for (var i = 0; i < 2000 && b.Alive && m.Alive; i++)
        {
            w.Step();
        }
        output.WriteLine($"head-on: Booker {(b.Alive ? "alive" : "dead")} {b.HP}, Abrams {m.HP} of 2500 at {w.Tick / 20.0:F1} s");
        Assert.False(b.Alive);
        Assert.True(m.HP > 1500);
    }

    // Two Bookers (3,000 to the Abrams' 2,500) on its flanks, from behind and a side, kill it before it kills both.
    [Fact]
    public void TwoOnTheFlanksKillAnAbrams()
    {
        var w = Duel();
        var (m, _) = w.SpawnCrewed(2, Abrams, 40, 40, Facing.East);
        var (b1, _) = w.SpawnCrewed(1, Booker, 25, 40, Facing.East); // behind it
        var (b2, _) = w.SpawnCrewed(1, Booker, 40, 25, Facing.South); // on its side
        w.Order(1, CommandKind.Attack, [b1, b2], m.Id);
        w.Order(2, CommandKind.Attack, [m], b1.Id);
        for (var i = 0; i < 2000 && m.Alive && (b1.Alive || b2.Alive); i++)
        {
            w.Step();
        }
        output.WriteLine($"flanked: Abrams {(m.Alive ? "alive" : "dead")} at {w.Tick / 20.0:F1} s; Bookers {b1.HP}, {b2.HP}");
        Assert.False(m.Alive);
        Assert.True(b1.Alive || b2.Alive);
    }

    // ---- Against the launchers ----

    // Every Resistance launcher (and the radar and the SA-6 that hide among them) dies to one armour-piercing round (or two).
    [Theory]
    [InlineData("light_rocket_truck")]
    [InlineData("medium_rocket_launcher")]
    [InlineData("heavy_rocket_launcher")]
    [InlineData("heavy_rocket_truck")]
    [InlineData("rocket_jeep")]
    [InlineData("missile_truck")]
    [InlineData("kaman_truck")]
    [InlineData("drone_truck")]
    [InlineData("ballistic_truck")]
    [InlineData("kub_launcher")]
    [InlineData("mobile_radar")]
    public void EveryLauncherDiesInOneOrTwoShots(string launcher)
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 20, 40, Facing.East);
        var foe = w.Spawn(2, launcher, 34, 40, Facing.North); // side on, 14 cells off
        HoldFire(w, foe);
        w.Order(1, CommandKind.Attack, [t], foe.Id);
        for (var i = 0; i < 600 && foe.Alive; i++)
        {
            w.Step();
        }
        var shots = 35 - Rounds(t);
        output.WriteLine($"{launcher} ({foe.Def.MaxHp} HP, {foe.Def.Archetype.Armor}): {shots} shot(s)");
        Assert.False(foe.Alive);
        Assert.InRange(shots, 1, 2);
    }

    // ---- Protection ----

    [Fact]
    public void ItPopsSmokeTwiceAndHasNoTrophy()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 40, 40, Facing.West);
        Assert.Null(w.ApsOf(t));
        Assert.Equal(new SmokeStatus(2, 2, 0), w.SmokeOf(t));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Smoke, Units = [t.Id] });
        w.Step();
        var cloud = Assert.Single(w.Smokes);
        Assert.Equal(t.Id, cloud.Source);
        Assert.True(w.InSmoke(t));
        Assert.Equal(1, w.SmokeOf(t)!.Value.Charges);

        // A Kornet at it flies in untouched (no Trophy) and hits the front.
        var w2 = Sentinel();
        var (t2, _) = w2.SpawnCrewed(1, Booker, 40, 40, Facing.West);
        var k = w2.Spawn(2, "kornet", 25, 40, Facing.East);
        var (d, f) = HitOn(w2, k, t2);
        output.WriteLine($"Kornet head-on: {d} ({HitsToKill(1400, d)} to kill)");
        Assert.Empty(w2.ApsEvents);
        Assert.Equal(HitFacing.Front, f);
        Assert.Equal(126 * 60 / 100, d);
    }

    // A Kornet's lock breaks when the Booker pops smoke (the Humvee's screen).
    [Fact]
    public void SmokeBreaksAKornetsLock()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Booker, 40, 40, Facing.West);
        HoldFire(w, t);
        var k = w.Spawn(2, "kornet", 15, 40, Facing.East);
        w.Order(2, CommandKind.Attack, [k], t.Id);
        for (var i = 0; i < 200 && !w.Projectiles.Any(p => p.Shooter == k.Id); i++)
        {
            w.Step();
        }
        Assert.Contains(w.Projectiles, p => p.Shooter == k.Id);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Smoke, Units = [t.Id] });
        var broke = false;
        for (var i = 0; i < 60; i++)
        {
            w.Step();
            broke |= w.SmokeEvents.Any(e => e.Kind == SmokeEventKind.LockBroken);
        }
        Assert.True(broke);
        Assert.Equal(t.Def.MaxHp, t.HP);
    }

    // ---- Rearm ----

    [Fact]
    public void TheFieldAmmoPointAndTheGarageRearmIt()
    {
        var w = Sentinel();
        var facp = w.Spawn(1, "facp", 40, 40);
        facp.Stock = 6000;
        var (t, _) = w.SpawnCrewed(1, Booker, 44, 50);
        for (var i = 0; i < t.MutableCharges.Length; i++)
        {
            t.MutableCharges[i] = 0;
        }
        w.Run(35 * 40 + 1);
        Assert.Equal(35, Rounds(t));
        Assert.Equal(2, w.SmokeOf(t)!.Value.Charges);
        Assert.Equal(6000 - (35 + 2) * 20, facp.Stock);

        var w2 = Arena.NewSentinel(96);
        var g = w2.Spawn(1, "military_garage", 20, 10);
        w2.Step();
        var (t2, _) = w2.SpawnCrewed(1, Booker, 40, 40);
        t2.MutableCharges[t2.Def.Weapons[0].Stock] = 0;
        w2.Issue(new Command { Tick = w2.Tick, Player = 1, Kind = CommandKind.Service, Units = [t2.Id], Target = g.Id });
        var inBay = false;
        for (var i = 0; i < 1200 && !(inBay && !t2.InGarage && t2.ServiceAt == 0); i++)
        {
            w2.Step();
            inBay |= t2.InGarage;
        }
        Assert.True(inBay);
        Assert.Equal(35, Rounds(t2));
    }

    // ---- Determinism ----

    // A scripted hunt: two Bookers race in on rocket trucks and a missile truck guarded by a Kornet team, pop smoke; the commands
    // in order or reversed; the hash every tick.
    private static List<ulong> Script(bool reversed, bool fog)
    {
        var w = Arena.NewSentinel(64, 7, fog ? s => s with { Fog = true } : null);
        var (b1, _) = w.SpawnCrewed(1, Booker, 8, 24, Facing.East);
        var (b2, _) = w.SpawnCrewed(1, Booker, 8, 36, Facing.East);
        var r1 = w.SpawnCrewed(2, "light_rocket_truck", 44, 26, Facing.West).Vehicle;
        w.SpawnCrewed(2, "missile_truck", 48, 34, Facing.West);
        var k = w.Spawn(2, "kornet", 50, 30, Facing.West);
        w.Spawn(2, "Rifleman", 42, 30);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackMove, Units = [b1.Id], Cell = new Cell(40, 26) },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackMove, Units = [b2.Id], Cell = new Cell(40, 36) },
            new Command { Tick = 80, Player = 1, Seq = 3, Kind = CommandKind.Smoke, Units = [b2.Id, b1.Id] },
            new Command { Tick = 2, Player = 2, Seq = 1, Kind = CommandKind.Attack, Units = [k.Id], Target = b1.Id },
            new Command { Tick = 3, Player = 2, Seq = 2, Kind = CommandKind.Move, Units = [r1.Id], Cell = new Cell(30, 10) },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 800; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        return hashes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItIsDeterministic(bool fog)
    {
        var a = Script(false, fog);
        Assert.Equal(a, Script(false, fog));
        Assert.Equal(a, Script(true, fog));
    }
}

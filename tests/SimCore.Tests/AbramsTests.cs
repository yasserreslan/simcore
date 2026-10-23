using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The Sentinel's main battle tank (main_battle_tank, "Abrams"; docs/design.md, "Main battle tank"): the 120 mm gun's 40 rounds
// (tank_120mm: high explosive or armour-piercing by target from one magazine, sharedAmmo), the machine guns (tank_mgs: coaxial,
// unlimited), directional armour (directionalArmor: front 40%, sides 80%, rear 130%, top 150%), Trophy active protection
// (trophy_aps: 3 charges, a chance by the threat's speed and launch distance, one engagement a second), smoke, crushing
// (crusher 3: soldiers and the light cars) and rearming.
public class AbramsTests(ITestOutputHelper output)
{
    private const string Tank = "main_battle_tank";

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

    private static int Trophy(World w, Unit t) => w.ApsOf(t)!.Value.Charges;

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
        Assert.True(c.Archetype("vehicle_garage")!.Production!.Builds(c.Archetype("humvee")!.Tags)); // the rest as before

        // The military garage builds one in 45 s; the vehicle garage refuses the order.
        var w = Arena.NewSentinel(96);
        var mg = w.Spawn(1, "military_garage", 20, 10);
        var vg = w.Spawn(1, "vehicle_garage", 70, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [mg.Id, vg.Id], Unit = Tank });
        var built = 0u;
        for (var t = 0; t < 1400 && built == 0; t++)
        {
            w.Step();
            if (w.All(Tank).FirstOrDefault() is { } tank)
            {
                built = w.Tick;
                Assert.Equal(1, tank.Owner);
            }
        }
        output.WriteLine($"built at tick {built}");
        Assert.InRange(built, 900u, 1000u); // 900 ticks in a bay, then out of the door
        Assert.Single(w.All(Tank)); // only the military garage built one
    }

    [Fact]
    public void ItsStatsAreTheSpecs()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype(Tank)!;
        Assert.Equal((2500, 2500, 900), (a.MaxHp, a.Cost, a.BuildTime));
        Assert.Equal(c.ArmorClass("armored_vehicle"), a.Armor);
        Assert.Equal(Locomotor.Tracked, a.Locomotor);
        Assert.Equal(Fixed.Frac(30, 200), a.Speed); // 3 cells/s
        Assert.Equal(Fixed.FromInt(8), a.Vision);
        Assert.Equal(Fixed.Frac(12, 10), a.Radius); // the heavy trucks'
        Assert.True(a.Turret);
        Assert.Equal(3, a.Crusher);
        Assert.Equal(c.TagsOf("soldier"), a.Crew!.Tags);
        Assert.Equal(0, a.Crew.Passengers);
        Assert.True(a.Tags.Matches(c.TagsOf("heavy")));
        Assert.Equal(new DirectionalArmor(40, 80, 130, 150, 4, 4), a.DirectionalArmor);

        var w = Sentinel();
        var def = w.Player(1)!.Setup.Find(w.Content, Tank)!;
        Assert.Equal(["tank_120mm", "tank_mgs", "trophy_aps", "smoke_launchers"], def.Components.Select(x => x.Id));
        var (he, ap, mg) = (def.Weapons[0], def.Weapons[1], def.Weapons[2]);
        Assert.Equal(("he_mpat", 250, Fixed.FromInt(20), 120, Fixed.FromInt(3)), (he.Warhead.Id, he.Damage, he.Range, he.Reload, he.Effect.Splash));
        Assert.Equal(("apfsds", 900, Fixed.FromInt(20), 120), (ap.Warhead.Id, ap.Damage, ap.Range, ap.Reload));
        Assert.True(ap.Effect.ProjectileSpeed.Raw > he.Effect.ProjectileSpeed.Raw); // the dart is the fast one
        Assert.Equal(he.Stock, ap.Stock); // one magazine
        Assert.Equal(40, def.StockMax[he.Stock]);
        Assert.True(ap.Effect.SharedAmmo);
        Assert.Equal(("heavy_bullet", 12, Fixed.FromInt(7), 5, -1), (mg.Warhead.Id, mg.Damage, mg.Range, mg.Reload, mg.Stock));
        Assert.True(mg.Effect.Coaxial && def.HasCoaxial);
        Assert.Equal(Layers.Ground, he.Effect.Targets | ap.Effect.Targets | mg.Effect.Targets);
        var aps = def.Aps!;
        Assert.Equal(3, def.StockMax[def.ApsStock]);
        Assert.Equal((Fixed.FromInt(4), 20, 90), (aps.Range, aps.Cooldown, aps.Chance));
        Assert.Equal(2, def.StockMax[def.SmokeStock]);
        Assert.Equal(3, def.StockMax.Count); // rounds, Trophy, smoke: nothing else
    }

    [Fact]
    public void OnlyGuidedMissilesAndDirectFireRocketsAreTrophyThreats()
    {
        var c = TestContent.Mvp;
        var threats = c.Components.Where(x => x.Effects.OfType<WeaponEffect>().Any(e => e.ApsThreat)).Select(x => x.Id).Order(StringComparer.Ordinal);
        Assert.Equal(["at_missile", "heli_missiles", "heli_missiles_light", "kaman_missiles", "kornet_atgm", "watcher_missiles"], threats);
        foreach (var comp in c.Components)
        {
            foreach (var e in comp.Effects.OfType<WeaponEffect>().Where(e => e.ApsThreat))
            {
                Assert.True(e.ProjectileSpeed.Raw > 0 && !e.Lobbed && e.Delivery is null, $"{comp.Id}: a threat is a direct-fire projectile");
            }
        }
    }

    // ---- The gun: rounds by target ----

    // FirstShot: the projectile tank fires first at target (ordered).
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
        var (t, _) = w.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var (carrier, _) = w.SpawnCrewed(2, "infantry_carrier", 35, 40);
        HoldFire(w, carrier);
        Assert.Equal("apfsds", FirstShot(w, t, carrier).Weapon.Warhead.Id);
        Assert.Equal(39, Rounds(t));

        var w2 = Sentinel();
        var (t2, _) = w2.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var men = Enumerable.Range(0, 3).Select(i => w2.Spawn(2, "Rifleman", 35, 39 + i)).ToList();
        HoldFire(w2, [.. men]);
        Assert.Equal("he_mpat", FirstShot(w2, t2, men[1]).Weapon.Warhead.Id);

        var w3 = Sentinel();
        var (t3, _) = w3.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var depot = w3.Spawn(2, "supply_depot", 35, 40);
        Assert.Equal("he_mpat", FirstShot(w3, t3, depot).Weapon.Warhead.Id);

        // Both rounds come out of the same 40.
        var w4 = Sentinel();
        var (t4, _) = w4.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var (truck, _) = w4.SpawnCrewed(2, "cargo_truck", 35, 38);
        var rifle = w4.Spawn(2, "Rifleman", 35, 42);
        truck.HP = rifle.HP = 100_000;
        HoldFire(w4, truck, rifle);
        Assert.Equal("apfsds", FirstShot(w4, t4, truck).Weapon.Warhead.Id);
        w4.Run(5); // the dart lands
        w4.Order(1, CommandKind.Attack, [t4], rifle.Id);
        for (var i = 0; i < 200 && Rounds(t4) == 39; i++)
        {
            w4.Step();
        }
        Assert.Equal("he_mpat", w4.Projectiles.Single(p => p.Shooter == t4.Id).Weapon.Warhead.Id);
        Assert.Equal(38, Rounds(t4));
    }

    [Fact]
    public void HighExplosiveShredsAGroupOfSoldiers()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var men = new List<Unit>();
        for (var i = 0; i < 6; i++)
        {
            men.Add(w.Spawn(2, "Rifleman", 36 + i % 2, 39 + i / 2));
        }
        HoldFire(w, [.. men]);
        w.Order(1, CommandKind.Attack, [t], men[2].Id);
        var shots = 0;
        for (var i = 0; i < 40; i++)
        {
            w.Step();
            shots = Math.Max(shots, 40 - Rounds(t));
        }
        var dead = men.Count(m => !m.Alive);
        output.WriteLine($"one HE round: {dead} of 6 soldiers dead, HP left {string.Join(", ", men.Select(m => m.HP))}");
        Assert.Equal(1, shots);
        Assert.True(dead >= 4, "a round kills most of a tight group");
    }

    [Fact]
    public void ItFiresOnTheMoveEverySixSeconds()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 20, 10, Facing.South);
        var (truck, _) = w.SpawnCrewed(2, "cargo_truck", 26, 40); // 6 cells off its path
        var spotter = w.Spawn(1, "Rifleman", 27, 43); // the tank sees 8 cells; its gun reaches 20
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

    // ---- The machine guns ----

    [Fact]
    public void TheMachineGunsFireAlongsideTheGunAndCarryOnWhenItIsEmpty()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var soldier = w.Spawn(2, "Rifleman", 25, 40);
        soldier.HP = 100_000;
        HoldFire(w, soldier);
        w.Order(1, CommandKind.Attack, [t], soldier.Id);
        var bursts = 0;
        for (var i = 0; i < 120; i++)
        {
            w.Step();
            bursts += t.CoaxShot == w.Tick - 1 ? 1 : 0;
        }
        output.WriteLine($"6 s at a soldier 5 cells off: {40 - Rounds(t)} round(s), {bursts} bursts");
        Assert.Equal(1, 40 - Rounds(t)); // one HE round in 6 s
        Assert.InRange(bursts, 20, 24); // and the MGs every 0.25 s alongside

        // Empty: the MGs alone, unlimited.
        var w2 = Sentinel();
        var (t2, _) = w2.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        t2.MutableCharges[t2.Def.Weapons[0].Stock] = 0;
        var men = Enumerable.Range(0, 4).Select(i => w2.Spawn(2, "Rifleman", 25, 38 + i)).ToList();
        HoldFire(w2, [.. men]);
        w2.Run(200);
        Assert.All(men, m => Assert.False(m.Alive));
        Assert.Equal(0, Rounds(t2));
        Assert.Equal(15, w2.Content.Warhead("heavy_bullet")!.Damage(12, w2.Content.ArmorClass("infantry"), 100)); // 15 a burst
    }

    [Fact]
    public void TheMachineGunsNeverFireAtAircraft()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 20, 20);
        var drone = w.Spawn(2, "scout_drone", 23, 20);
        drone.HP = 10_000;
        w.Order(1, CommandKind.Attack, [t], drone.Id);
        w.Run(100);
        Assert.Equal(10_000, drone.HP);
    }

    // ---- Directional armour ----

    [Fact]
    public void TheHitFacingFollowsTheHull()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 40, 40, Facing.North);
        var da = t.Def.Archetype.DirectionalArmor!;
        Vec At(int dx, int dy) => new Cell(40 + dx, 40 + dy).Center();
        Assert.Equal(HitFacing.Front, World.FacingFrom(t, da, At(0, -10)));
        Assert.Equal(HitFacing.Front, World.FacingFrom(t, da, At(9, -10))); // 42 degrees off the nose
        Assert.Equal(HitFacing.Side, World.FacingFrom(t, da, At(10, -5)));
        Assert.Equal(HitFacing.Side, World.FacingFrom(t, da, At(-10, 0)));
        Assert.Equal(HitFacing.Side, World.FacingFrom(t, da, At(10, 5)));
        Assert.Equal(HitFacing.Rear, World.FacingFrom(t, da, At(-9, 10)));
        Assert.Equal(HitFacing.Rear, World.FacingFrom(t, da, At(0, 10)));
        Assert.Equal(HitFacing.Side, World.FacingFrom(t, da, t.Pos)); // no direction: side
        Assert.Equal(40, World.ArmorPercentFrom(t, At(0, -10)));
        Assert.Equal(130, World.ArmorPercentFrom(t, At(0, 10)));
        t.Facing = Facing.East; // turned: the east is its front now
        Assert.Equal(HitFacing.Front, World.FacingFrom(t, da, At(10, 0)));
        Assert.Equal(HitFacing.Rear, World.FacingFrom(t, da, At(-10, 0)));
        Assert.Equal(100, World.ArmorPercentFrom(w.Spawn(1, "humvee", 50, 50), At(0, 10))); // without directional armour
    }

    // HitOn: the damage the first hit of shooter (ordered at target) deals to target, standing still facing `facing`, and where it landed.
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

    [Theory]
    [InlineData(Facing.West, HitFacing.Front, 360)] // facing the shooter: 900 x 40%
    [InlineData(Facing.North, HitFacing.Side, 720)] // 80%
    [InlineData(Facing.South, HitFacing.Side, 720)]
    [InlineData(Facing.East, HitFacing.Rear, 1170)] // its back to the shooter: 130%
    public void TheDartHitsHardestFromBehind(int facing, HitFacing where, int damage)
    {
        var w = Duel();
        var (a, _) = w.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var (b, _) = w.SpawnCrewed(2, Tank, 35, 40, facing);
        var (d, f) = HitOn(w, a, b);
        output.WriteLine($"{where}: {d} a dart, {(2500 + d - 1) / d} to kill a tank");
        Assert.Equal((damage, where), (d, f));
    }

    [Fact]
    public void HitsToKillATankByFacing()
    {
        int Hit(string shooter, int facing, out HitFacing f)
        {
            var w = Sentinel();
            var (t, _) = w.SpawnCrewed(1, Tank, 40, 40, facing);
            var s = w.Spawn(2, shooter, 32, 40, Facing.East); // 8 cells west of it
            (var d, f) = HitOn(w, s, t);
            return d;
        }
        var rows = new List<string>();
        foreach (var (shooter, front, side, rear) in new[] { ("kornet", 50, 100, 163), ("Tank hunter", 33, 67, 109) })
        {
            var df = Hit(shooter, Facing.West, out var ff);
            var ds = Hit(shooter, Facing.North, out var fs);
            var dr = Hit(shooter, Facing.East, out var fr);
            Assert.Equal((HitFacing.Front, HitFacing.Side, HitFacing.Rear), (ff, fs, fr));
            Assert.Equal((front, side, rear), (df, ds, dr));
            rows.Add($"{shooter}: front {df} ({(2500 + df - 1) / df} hits), side {ds} ({(2500 + ds - 1) / ds}), rear {dr} ({(2500 + dr - 1) / dr})");
        }
        foreach (var r in rows)
        {
            output.WriteLine(r);
        }
    }

    [Fact]
    public void AnFpvDroneHitsTheRoof()
    {
        var w = Sentinel(48);
        var (t, _) = w.SpawnCrewed(1, Tank, 30, 20, Facing.West); // facing the pilot: the roof still takes 150%
        HoldFire(w, t);
        var pilot = w.Spawn(2, "Drone pilot", 5, 20);
        w.Order(2, CommandKind.UseAbility, [pilot], t.Id, ability: "drone_kit");
        var hits = new List<ArmorHit>();
        for (var i = 0; i < 200 && hits.Count == 0; i++)
        {
            w.Step();
            hits.AddRange(w.ArmorHits.Where(h => h.Unit == t.Id));
        }
        var hit = Assert.Single(hits);
        output.WriteLine($"FPV on the roof: {hit.Damage} ({hit.Percent}%)");
        Assert.Equal((HitFacing.Top, 150), (hit.Facing, hit.Percent));
        Assert.InRange(hit.Damage, 80, 105); // 70 explosive x 150%, less at the splash's edge
    }

    [Fact]
    public void AMortarRoundComesDownOnTheRoof()
    {
        var w = Sentinel(64);
        var (t, _) = w.SpawnCrewed(1, Tank, 40, 30, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var m = w.Spawn(2, "mortar", 20, 30, Facing.East);
        w.Order(2, CommandKind.Deploy, [m]);
        w.Run(110);
        w.Order(2, CommandKind.Attack, [m], t.Id);
        var hits = new List<ArmorHit>();
        for (var i = 0; i < 600 && hits.Count == 0; i++)
        {
            w.Step();
            hits.AddRange(w.ArmorHits.Where(h => h.Unit == t.Id));
        }
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(HitFacing.Top, h.Facing));
    }

    // ---- Trophy ----

    [Fact]
    public void TheChanceFallsForFastThreatsAndCloseLaunches()
    {
        var aps = TestContent.Mvp.Component("trophy_aps")!.Effects.OfType<ApsEffect>().Single();
        Fixed Speed(int tenthsPerSecond) => Fixed.Frac(tenthsPerSecond, 200);
        Fixed Cells(int n) => Fixed.FromInt(n);
        Assert.Equal(90, World.ApsChance(aps, Speed(200), Cells(28))); // a Kornet from afar
        Assert.Equal(90, World.ApsChance(aps, Speed(250), Cells(20))); // at the thresholds
        Assert.Equal(80, World.ApsChance(aps, Speed(200), Cells(15))); // a Kornet from 15 cells
        Assert.Equal(68, World.ApsChance(aps, Speed(200), Cells(9))); // an RPG at its full 9 cells
        Assert.Equal(60, World.ApsChance(aps, Speed(200), Cells(5)));
        Assert.Equal(50, World.ApsChance(aps, Speed(200), Cells(0))); // point blank: 40 off
        Assert.Equal(80, World.ApsChance(aps, Speed(300), Cells(30))); // one speed step
        Assert.Equal(70, World.ApsChance(aps, Speed(400), Cells(30))); // a helicopter's or the Kaman's missile: two
        Assert.Equal(50, World.ApsChance(aps, Speed(400), Cells(10)));
        Assert.Equal(10, World.ApsChance(aps, Speed(5000), Cells(0))); // never below 10
    }

    // KornetAt: a Kornet team of player 2 `cells` west of a crewed tank (facing it, HP huge), ordered to fire.
    private static (World W, Unit Kornet, Unit Tank) KornetAt(int cells, ulong seed = 1)
    {
        var w = Sentinel(96, seed);
        var (t, _) = w.SpawnCrewed(1, Tank, 60, 40, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var k = w.Spawn(2, "kornet", 60 - cells, 40, Facing.East);
        w.Order(2, CommandKind.Attack, [k], t.Id);
        return (w, k, t);
    }

    // Engagements: runs until `missiles` threats are gone (or the limit), collecting the APS events.
    private static List<ApsEvent> Engagements(World w, int ticks)
    {
        var events = new List<ApsEvent>();
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            events.AddRange(w.ApsEvents);
        }
        return events;
    }

    [Fact]
    public void TrophyStopsAKornetFourCellsOutOrFailsAndItHits()
    {
        var (w, k, t) = KornetAt(25);
        var ev = Engagements(w, 120);
        var e = Assert.Single(ev);
        output.WriteLine($"{e.Kind} at {e.Chance}%, {e.ChargesLeft} left");
        Assert.Equal(90, e.Chance);
        Assert.Equal(2, e.ChargesLeft);
        Assert.Equal(2, Trophy(w, t));
        Assert.Empty(w.Projectiles);
        if (e.Kind == ApsEventKind.Intercepted)
        {
            Assert.Equal(100_000, t.HP);
            Assert.InRange((e.At - t.Pos).Len().Raw, Fixed.FromInt(4).Raw - 2, Fixed.FromInt(4).Raw + 2); // 4 cells out
            Assert.Equal(0u, k.Guiding); // its operator reloads
        }
        else
        {
            Assert.Equal(ApsEventKind.Failed, e.Kind);
            Assert.Equal(100_000 - 50, t.HP); // the front's 40% of 126
        }
    }

    // Rate: over 200 seeds, how often Trophy stops one threat fired by `shooter` from `cells` away.
    private (int Stopped, int Engaged, int Chance) Rate(string shooter, int cells)
    {
        int stopped = 0, engaged = 0, chance = 0;
        for (var seed = 1UL; seed <= 200; seed++)
        {
            var w = Sentinel(96, seed);
            var (t, _) = w.SpawnCrewed(1, Tank, 60, 40, Facing.West);
            t.HP = 100_000;
            HoldFire(w, t);
            var s = w.Spawn(2, shooter, 60 - cells, 40, Facing.East);
            w.Order(2, CommandKind.Attack, [s], t.Id);
            for (var i = 0; i < 150; i++)
            {
                w.Step();
                if (w.ApsEvents.FirstOrDefault(e => e.Kind is ApsEventKind.Intercepted or ApsEventKind.Failed) is { Unit: not 0 } e)
                {
                    engaged++;
                    chance = e.Chance;
                    stopped += e.Kind == ApsEventKind.Intercepted ? 1 : 0;
                    break;
                }
            }
        }
        output.WriteLine($"{shooter} from {cells} cells: stopped {stopped} of {engaged} ({chance}% each)");
        return (stopped, engaged, chance);
    }

    [Fact]
    public void TrophyRatesMatchTheChance()
    {
        var (ks, ke, kc) = Rate("kornet", 25);
        Assert.Equal((200, 90), (ke, kc));
        Assert.InRange(ks, 166, 194); // 90% of 200, within about 3 standard deviations
        var (rs, re, rc) = Rate("Tank hunter", 8);
        Assert.Equal((200, 66), (re, rc)); // an RPG from 8 cells
        Assert.InRange(rs, 112, 152);
        Assert.True(rs < ks, "a close launch is harder to stop");
    }

    [Fact]
    public void ThreeChargesThenItIsEmpty()
    {
        var (w, _, t) = KornetAt(25);
        var ev = Engagements(w, 6 * 140); // a missile every 6 s (5 s reload + 1 s aim) and its flight
        output.WriteLine(string.Join(", ", ev.Select(e => e.Kind)));
        Assert.Equal(3, ev.Count(e => e.Kind is ApsEventKind.Intercepted or ApsEventKind.Failed));
        Assert.Equal(3, ev.Count(e => e.Kind == ApsEventKind.Empty));
        Assert.True(ev.FindIndex(e => e.Kind == ApsEventKind.Empty) > ev.FindLastIndex(e => e.Kind != ApsEventKind.Empty));
        Assert.Equal(new ApsStatus(0, 3, 0), w.ApsOf(t));
        Assert.True(t.HP <= 100_000 - 3 * 50, "the missiles it could not engage hit");
    }

    [Fact]
    public void ASalvoOverwhelmsIt()
    {
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 60, 40, Facing.West);
        t.HP = 100_000;
        HoldFire(w, t);
        var a = w.Spawn(2, "Tank hunter", 53, 38, Facing.East);
        var b = w.Spawn(2, "Tank hunter", 53, 42, Facing.East);
        w.Order(2, CommandKind.Attack, [a, b], t.Id);
        var ev = Engagements(w, 50); // their first rockets (the next 3.5 s later)
        output.WriteLine(string.Join(", ", ev.Select(e => $"{e.Kind} {e.Projectile}")));
        Assert.Equal(2, ev.Count);
        Assert.True(ev[0].Kind is ApsEventKind.Intercepted or ApsEventKind.Failed);
        Assert.Equal(ApsEventKind.Overwhelmed, ev[1].Kind); // the second came in within the second
        Assert.Equal(2, Trophy(w, t)); // one charge spent
        Assert.True(t.HP < 100_000, "the second rocket hit");
    }

    [Fact]
    public void ItIgnoresShellsBulletsAndDrones()
    {
        // A tank's darts, a rifleman's bullets and an FPV drone draw nothing from it.
        var w = Duel();
        var (a, _) = w.SpawnCrewed(1, Tank, 20, 40, Facing.East);
        var (b, _) = w.SpawnCrewed(2, Tank, 35, 40, Facing.West);
        b.HP = 100_000;
        HoldFire(w, b);
        w.Order(1, CommandKind.Attack, [a], b.Id);
        Assert.Empty(Engagements(w, 300));
        Assert.True(b.HP < 100_000);
        Assert.Equal(3, Trophy(w, b));

        var w2 = Sentinel(48);
        var (t, _) = w2.SpawnCrewed(1, Tank, 30, 20, Facing.West);
        t.HP = 100_000;
        HoldFire(w2, t);
        var pilot = w2.Spawn(2, "Drone pilot", 5, 20);
        var rifle = w2.Spawn(2, "Rifleman", 26, 24);
        w2.Order(2, CommandKind.UseAbility, [pilot], t.Id, ability: "drone_kit");
        w2.Order(2, CommandKind.Attack, [rifle], t.Id);
        Assert.Empty(Engagements(w2, 200));
        Assert.True(t.HP < 100_000);
        Assert.Equal(3, Trophy(w2, t));
    }

    [Fact]
    public void ItOnlyGuardsItsOwnUnit()
    {
        // A Kornet aimed at a Humvee beside the tank flies past the tank's Trophy.
        var w = Sentinel();
        var (t, _) = w.SpawnCrewed(1, Tank, 60, 40, Facing.West);
        var (h, _) = w.SpawnCrewed(1, "humvee", 60, 43);
        h.HP = 100_000;
        HoldFire(w, t, h);
        var k = w.Spawn(2, "kornet", 35, 42, Facing.East);
        w.Order(2, CommandKind.Attack, [k], h.Id);
        Assert.Empty(Engagements(w, 150));
        Assert.True(h.HP < 100_000);
    }

    // ---- Rearm ----

    // Spent: a crewed tank of player 1 at (x, y) with no rounds, no Trophy charge and no smoke left.
    private static Unit Spent(World w, int x, int y)
    {
        var (t, _) = w.SpawnCrewed(1, Tank, x, y);
        var ch = t.MutableCharges;
        for (var i = 0; i < ch.Length; i++)
        {
            ch[i] = 0;
        }
        return t;
    }

    [Fact]
    public void TheMilitaryGarageRearmsItAll()
    {
        var w = Arena.NewSentinel(96);
        var g = w.Spawn(1, "military_garage", 20, 10);
        w.Step();
        var t = Spent(w, 40, 40);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Service, Units = [t.Id], Target = g.Id });
        var inBay = false;
        var t0 = w.Tick;
        for (var i = 0; i < 1200 && !(inBay && !t.InGarage && t.ServiceAt == 0); i++)
        {
            w.Step();
            inBay |= t.InGarage;
        }
        output.WriteLine($"serviced in {w.Tick - t0} ticks");
        Assert.True(inBay, "it fits a bay");
        Assert.Equal(40, Rounds(t));
        Assert.Equal(3, Trophy(w, t));
        Assert.Equal(2, w.SmokeOf(t)!.Value.Charges);
    }

    [Fact]
    public void TheFieldAmmoPointAndADepotRearmItRoundByRound()
    {
        var w = Sentinel();
        var facp = w.Spawn(1, "facp", 40, 40);
        facp.Stock = 6000;
        var t = Spent(w, 44, 50);
        w.Run(1); // a pulse: one of each
        Assert.Equal((1, 1), (Rounds(t), Trophy(w, t)));
        w.Run(40 * 40);
        Assert.Equal((40, 3), (Rounds(t), Trophy(w, t)));
        Assert.Equal(6000 - (40 + 3 + 2) * 20, facp.Stock); // a round, a Trophy charge or a smoke pop: 20 each

        var w2 = Sentinel();
        w2.Spawn(1, "supply_depot", 40, 40);
        var t2 = Spent(w2, 44, 46);
        w2.Run(40 * 40 + 1);
        Assert.Equal((40, 3), (Rounds(t2), Trophy(w2, t2)));
    }

    // ---- Crushing ----

    [Fact]
    public void ItCrushesSoldiersAndLightCarsButNotTrucksOrArmour()
    {
        foreach (var (victim, crushed) in new[] { ("Rifleman", true), ("technical", true), ("dirt_bike", true), ("rocket_jeep", true),
                     ("cargo_truck", false), ("infantry_carrier", false), ("kub_launcher", false) })
        {
            var w = Sentinel(64);
            var (t, _) = w.SpawnCrewed(1, Tank, 10, 30, Facing.East);
            HoldFire(w, t);
            var foe = w.Spawn(2, victim, 22, 30);
            foe.Stance = Stance.HoldFire; // (a vehicle is empty: it neither moves nor fires)
            w.Order(1, CommandKind.Move, [t], cell: new Cell(40, 30));
            w.Run(300);
            output.WriteLine($"{victim}: {(foe.Alive ? "alive" : "crushed")}");
            Assert.Equal(crushed, w.Crushes.Count > 0 || !foe.Alive);
            Assert.Equal(!crushed, foe.Alive);
        }
    }

    // ---- Determinism ----

    // A scripted fight: two tanks advance on Resistance infantry, a Kornet team, two RPG teams and a technical; the commands in
    // order or reversed; the world's hash every tick.
    private static List<ulong> Script(bool reversed, bool fog)
    {
        var w = Arena.NewSentinel(64, 7, fog ? s => s with { Fog = true } : null);
        var (t1, _) = w.SpawnCrewed(1, Tank, 8, 24, Facing.East);
        var (t2, _) = w.SpawnCrewed(1, Tank, 8, 36, Facing.East);
        for (var i = 0; i < 6; i++)
        {
            w.Spawn(2, "Rifleman", 40 + i % 3, 28 + i);
        }
        var k = w.Spawn(2, "kornet", 50, 30, Facing.West);
        var r1 = w.Spawn(2, "Tank hunter", 36, 26, Facing.West);
        var r2 = w.Spawn(2, "Tank hunter", 36, 34, Facing.West);
        w.SpawnCrewed(2, "technical", 46, 40, Facing.West);
        w.Spawn(2, "Marksman", 46, 34);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.AttackMove, Units = [t1.Id], Cell = new Cell(40, 26) },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.AttackMove, Units = [t2.Id], Cell = new Cell(40, 36) },
            new Command { Tick = 90, Player = 1, Seq = 3, Kind = CommandKind.Smoke, Units = [t2.Id, t1.Id] },
            new Command { Tick = 2, Player = 2, Seq = 1, Kind = CommandKind.Attack, Units = [k.Id], Target = t1.Id },
            new Command { Tick = 3, Player = 2, Seq = 2, Kind = CommandKind.Attack, Units = [r1.Id, r2.Id], Target = t2.Id },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var events = 0;
        var armour = 0;
        for (var t = 0; t < 900; t++)
        {
            w.Step();
            events += w.ApsEvents.Count;
            armour += w.ArmorHits.Count;
            hashes.Add(w.Hash());
        }
        Assert.True(events >= 1, "Trophy engaged");
        Assert.True(armour >= 1, "the armour took hits");
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

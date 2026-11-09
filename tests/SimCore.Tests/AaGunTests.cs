using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The anti-air guns (docs/design.md, "Anti-air guns"; the game repo's docs/plans/aa-guns-spec.md): the ZU-23 carrier and the
// Korkut battery. Each row of the spec's tables is measured over 200 seeds (the first burst at the row's geometry; the gun holds
// fire until the target is in place) and the number printed; the asserts hold each row to a band round the spec's value.
public class AaGunTests(ITestOutputHelper output)
{
    private const int Seeds = 200;
    private const long Metre = Fixed.OneRaw / 2;

    // A flat strip of width x height cells from the Sentinel lobby: player 1 Sentinel, player 2 Resistance (or, with sentinels,
    // Sentinel too: its helicopters and jets as the Korkut's targets), no armies.
    private static World Strip(int width, int height, ulong seed, bool sentinels = false)
    {
        var setup = TestContent.Sentinel(seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        if (sentinels)
        {
            setup = setup with { Players = [setup.Players[0], setup.Players[1] with { Faction = "sentinel", Presets = setup.Players[0].Presets }] };
        }
        return new World(new Grid(width, height), MatchCompiler.Compile(TestContent.Mvp, setup));
    }

    private static void Stance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    private static void Ground(World w, Unit u, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.AttackGround, Units = [u.Id], Cell = at, Queue = queue });

    private static long Cells(GunBurst b) =>
        World.Len3(b.At.X.Raw - b.From.X.Raw, b.At.Y.Raw - b.From.Y.Raw, b.AtAltitude.Raw - b.FromAltitude.Raw) / Fixed.OneRaw;

    // Row measures one table row: per seed, scene builds the world and returns the gun (on Hold fire); once ready, the gun goes to
    // Guard and its first burst counts. Returns the hit percent (tenths), the mean chance (tenths of a percent) and the mean slant.
    private (int Hit10, int Pk10, long Slant) Row(string name, Func<ulong, (World W, Unit Gun)> scene, Func<World, bool> ready,
        Action<World>? each = null, int max = 3000)
    {
        int hits = 0, n = 0;
        long pk = 0, slant = 0;
        for (var s = 1UL; s <= Seeds; s++)
        {
            var (w, gun) = scene(s);
            Stance(w, gun, Sim.Stance.HoldFire);
            var on = false;
            for (var t = 0; t < max; t++)
            {
                each?.Invoke(w);
                w.Step();
                if (!on && ready(w))
                {
                    Stance(w, gun, Sim.Stance.Guard);
                    on = true;
                }
                if (on && w.GunBursts.FirstOrDefault(b => b.Shooter == gun.Id) is { Shooter: > 0 } b)
                {
                    (hits, n, pk, slant) = (hits + (b.Hit ? 1 : 0), n + 1, pk + b.HitPermille, slant + Cells(b));
                    break;
                }
            }
        }
        Assert.True(n == Seeds, $"{name}: a first burst in {n} of {Seeds} seeds");
        var row = (hits * 1000 / n, (int)(pk / n), slant / n);
        output.WriteLine($"{name}: hit {row.Item1 / 10.0:0.0}% of {n} seeds (mean chance {row.Item2 / 10.0:0.0}%, slant {row.Item3} cells)");
        return row;
    }

    private static Unit Crewed(World w, byte player, string id, int x, int y, int facing = Facing.North) => w.SpawnCrewed(player, id, x, y, facing).Vehicle;

    private static Unit Hover(World w, byte player, string id, int x, int y)
    {
        var u = w.Spawn(player, id, x, y, Facing.East);
        u.HP = 1_000_000; // it outlives the row
        return u;
    }

    private static bool Up(Unit u, int metres) => u.Altitude.Raw >= metres * Metre;

    // ---- The ZU-23 carrier (player 2) ----

    [Fact]
    public void ZuWatcherAtItsNormalHeight()
    {
        Unit? wd = null;
        var row = Row("ZU-23, Watcher at its normal height", s =>
        {
            var w = Strip(400, 80, s);
            var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
            wd = Hover(w, 1, "watcher_drone", 140, 40);
            return (w, zu);
        }, w => Up(wd!, 140));
        Assert.InRange(row.Hit10, 50, 160); // ~10%
        // In reach for 5, 10 and 20 s (the spec: ~40%, ~65%, ~88%): the share of seeds with a hit by then.
        int[] by = [0, 0, 0];
        for (var s = 1UL; s <= Seeds; s++)
        {
            var w = Strip(400, 80, s);
            var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
            var d = Hover(w, 1, "watcher_drone", 140, 40);
            Stance(w, zu, Sim.Stance.HoldFire);
            for (var t = 0; t < 3000 && !Up(d, 140); t++)
            {
                w.Step();
            }
            Stance(w, zu, Sim.Stance.Guard);
            for (var t = 0; t < 400; t++)
            {
                w.Step();
                if (w.GunBursts.Any(b => b.Shooter == zu.Id && b.Hit))
                {
                    by[0] += t < 100 ? 1 : 0;
                    by[1] += t < 200 ? 1 : 0;
                    by[2] += 1;
                    break;
                }
            }
        }
        output.WriteLine($"ZU-23, Watcher in reach: hit within 5 s {by[0] * 100 / Seeds}%, 10 s {by[1] * 100 / Seeds}%, 20 s {by[2] * 100 / Seeds}%");
        Assert.InRange(by[0] * 100 / Seeds, 28, 55);
        Assert.InRange(by[1] * 100 / Seeds, 52, 80);
        Assert.InRange(by[2] * 100 / Seeds, 78, 97);
    }

    [Theory]
    [InlineData(400, 300)] // 800 m: ~30%
    [InlineData(200, 450)] // 400 m: ~45%
    public void ZuHelicopterLow(int cells, int spec10)
    {
        Unit? h = null;
        var row = Row($"ZU-23, helicopter low at {cells * 2} m", s =>
        {
            var w = Strip(cells + 80, 80, s);
            var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
            h = Hover(w, 1, "attack_helicopter", 40 + cells, 40);
            return (w, zu);
        }, w => Up(h!, 25));
        Assert.InRange(row.Hit10, spec10 - 90, spec10 + 90);
    }

    [Fact]
    public void ZuJetLowCrossing()
    {
        Unit? j = null;
        var row = Row("ZU-23, jet low crossing (150 cells abeam)", s =>
        {
            var w = Strip(1300, 200, s);
            var zu = Crewed(w, 2, "zu23_carrier", 650, 20);
            j = w.Spawn(1, "Multirole 500 lb", 60, 170, Facing.East);
            j.HP = 1_000_000;
            w.Order(1, CommandKind.Move, [j], cell: new Cell(1290, 170));
            return (w, zu);
        }, w => j!.Pos.X.Raw >= 560 * Fixed.OneRaw);
        Assert.InRange(row.Hit10, 15, 120); // ~6%
    }

    [Fact]
    public void ZuNeverReachesAboveItsCeiling()
    {
        var w = Strip(300, 80, 1);
        var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
        var d = Hover(w, 1, "watcher_drone", 100, 40);
        var bursts = 0;
        for (var t = 0; t < 600; t++)
        {
            d.Altitude = Fixed.FromRaw(1100 * Metre); // 1.1 km: above its 1 km ceiling
            w.Step();
            bursts += w.GunBursts.Count(b => b.Shooter == zu.Id);
        }
        Assert.Equal(0, bursts);
    }

    // ---- The Korkut battery (player 1) ----

    private static (World W, Unit Gun) Battery(int width, int height, ulong seed, int gx, int gy, bool radar = true, bool sentinels = false)
    {
        var w = Strip(width, height, seed, sentinels);
        var gun = Crewed(w, 1, "korkut_gun", gx, gy);
        if (radar)
        {
            Crewed(w, 1, "korkut_command", gx - 15, gy);
        }
        return (w, gun);
    }

    private static Projectile? Drone(World w) => w.Projectiles.FirstOrDefault(p => !p.Done && p.Interceptible == InterceptClass.Drone);

    [Fact]
    public void KorkutDroneStraightAtItLow500m()
    {
        var row = Row("Korkut linked, drone straight at it, low, 500 m", s =>
        {
            var (w, gun) = Battery(700, 80, s, 40, 40);
            var truck = Crewed(w, 2, "drone_truck", 630, 40, Facing.West);
            Ground(w, truck, new Cell(40, 40));
            return (w, gun);
        }, w => Drone(w) is { } p && p.Pos.X.Raw <= 292 * Fixed.OneRaw);
        Assert.InRange(row.Hit10, 700, 900); // ~80%
    }

    [Fact]
    public void KorkutLowCrossing1km()
    {
        var row = Row("Korkut linked, drone low crossing, 1 km abeam", s =>
        {
            var (w, gun) = Battery(760, 560, s, 400, 20);
            var truck = Crewed(w, 2, "drone_truck", 70, 520, Facing.East);
            Ground(w, truck, new Cell(660, 520));
            return (w, gun);
        }, w => Drone(w) is { } p && p.Pos.X.Raw >= 380 * Fixed.OneRaw);
        Assert.InRange(row.Hit10, 330, 570); // ~45%
    }

    [Fact]
    public void KorkutHighOr2kmOut()
    {
        Unit? h = null;
        var far = Row("Korkut linked, helicopter low, 2 km out", s =>
        {
            var (w, gun) = Battery(1100, 80, s, 40, 40, sentinels: true);
            h = Hover(w, 2, "attack_helicopter", 1035, 40);
            return (w, gun);
        }, w => Up(h!, 25));
        Assert.InRange(far.Hit10, 60, 180); // 10-15%
        Unit? d = null;
        var high = Row("Korkut linked, drone 1 km up", s =>
        {
            var (w, gun) = Battery(300, 80, s, 40, 40, sentinels: true);
            d = Hover(w, 2, "watcher_drone", 140, 40);
            return (w, gun);
        }, w => true, each: _ => d!.Altitude = Fixed.FromRaw(1000 * Metre));
        Assert.InRange(high.Hit10, 60, 200); // 10-15%
    }

    [Fact]
    public void KorkutFastJetCrossing()
    {
        Unit? j = null;
        var row = Row("Korkut linked, fast jet crossing (150 cells abeam)", s =>
        {
            var (w, gun) = Battery(1300, 200, s, 650, 20, sentinels: true);
            j = w.Spawn(2, "Multirole 500 lb", 60, 170, Facing.East);
            j.HP = 1_000_000;
            w.Order(2, CommandKind.Move, [j], cell: new Cell(1290, 170));
            return (w, gun);
        }, w => j!.Pos.X.Raw >= 560 * Fixed.OneRaw);
        Assert.InRange(row.Hit10, 10, 110); // ~5%
    }

    [Fact]
    public void KorkutHelicopterHoveringLow1kmDamagesIt()
    {
        Unit? h = null;
        var linked = Row("Korkut linked, helicopter hovering low, 1 km", s =>
        {
            var (w, gun) = Battery(620, 80, s, 40, 40, sentinels: true);
            h = Hover(w, 2, "attack_helicopter", 540, 40);
            return (w, gun);
        }, w => Up(h!, 25));
        Assert.InRange(linked.Hit10, 400, 600); // ~50%
        var unlinked = Row("Korkut on optics (no radar), helicopter hovering low, 1 km", s =>
        {
            var (w, gun) = Battery(620, 80, s, 40, 40, radar: false, sentinels: true);
            h = Hover(w, 2, "attack_helicopter", 540, 40);
            return (w, gun);
        }, w => Up(h!, 25));
        Assert.InRange(unlinked.Pk10, linked.Pk10 * 6 / 10 - 5, linked.Pk10 * 6 / 10 + 5); // 60% of the chance
        // A hit damages rather than kills: the weapon's 50 of the attack helicopter's 600.
        var (w2, g2) = Battery(300, 80, 3, 40, 40, sentinels: true);
        var heli = w2.Spawn(2, "attack_helicopter", 140, 40, Facing.East);
        var hits = 0;
        for (var t = 0; t < 400 && heli.Alive; t++)
        {
            var hp = heli.HP;
            w2.Step();
            if (w2.GunBursts.Any(b => b.Shooter == g2.Id && b.Hit))
            {
                Assert.Equal(50, hp - heli.HP);
                hits++;
            }
        }
        Assert.True(hits > 0);
    }

    [Fact]
    public void UnlinkedReachIs750CellsAndTheRadarOffSwitchUnlinks()
    {
        // Linked it fires at a helicopter 800 cells out; with the radar switched off (Hide) or no radar it does not (optics: 750).
        foreach (var mode in new[] { "linked", "hidden", "dead" })
        {
            var (w, gun) = Battery(900, 80, 1, 40, 40, sentinels: true);
            var cmd = w.Units.First(u => u.Def.Archetype.Id == "korkut_command");
            if (mode == "hidden")
            {
                Stance(w, cmd, Sim.Stance.Hide);
            }
            if (mode == "dead")
            {
                cmd.HP = 0;
            }
            var h = Hover(w, 2, "attack_helicopter", 840, 40);
            var bursts = new List<GunBurst>();
            for (var t = 0; t < 400; t++)
            {
                w.Step();
                bursts.AddRange(w.GunBursts.Where(b => b.Shooter == gun.Id));
            }
            Assert.True(mode == "linked" ? bursts.Count > 0 : bursts.Count == 0, $"{mode}: {bursts.Count} bursts");
            Assert.Equal(mode == "linked", w.GunRadar(gun) is not null);
        }
    }

    [Fact]
    public void ARadarFeedsAtMostThreeGunsTheNearestFirst()
    {
        var w = Strip(300, 80, 1);
        var cmd = Crewed(w, 1, "korkut_command", 40, 40);
        var guns = new[] { 80, 60, 120, 100 }.Select(x => Crewed(w, 1, "korkut_gun", x, 40)).ToList();
        w.Step();
        Assert.Equal([guns[1].Id, guns[0].Id, guns[3].Id], w.RadarGuns(cmd).Select(u => u.Id));
        Assert.Null(w.GunRadar(guns[2])); // the fourth, farthest: on optics
        var cmd2 = Crewed(w, 1, "korkut_command", 200, 40);
        w.Step();
        Assert.Equal(cmd2.Id, w.GunRadar(guns[2])!.Id); // a second command vehicle feeds it
        Assert.Equal(3, w.RadarGuns(cmd).Count);
    }

    [Fact]
    public void TheSigintJetHearsTheCommandVehicleOnlyWhileItsRadarIsOn()
    {
        var z = Strip(400, 80, 1);
        var zu = Crewed(z, 2, "zu23_carrier", 300, 40);
        Stance(z, zu, Sim.Stance.HoldFire);
        var ears = z.Spawn(1, "sigint_jet", 100, 60, Facing.East);
        ears.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        z.Order(1, CommandKind.Move, [ears], cell: new Cell(100, 60));
        z.Run(ears.Def.Archetype.Listen!.PulseTicks + 30);
        Assert.Empty(z.RadarContacts(1)); // the ZU-23 has no radar
        var w = Strip(400, 80, 1, sentinels: true);
        var cmd = Crewed(w, 1, "korkut_command", 200, 40);
        Crewed(w, 1, "korkut_gun", 220, 40);
        Crewed(w, 1, "korkut_gun", 240, 40);
        var jet = w.Spawn(2, "sigint_jet", 100, 40, Facing.East);
        jet.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        w.Order(2, CommandKind.Move, [jet], cell: new Cell(100, 40));
        w.Run(jet.Def.Archetype.Listen!.PulseTicks + 30);
        var heard = Assert.Single(w.RadarContacts(2)); // the command vehicle only: the guns' optics are no radar
        Assert.Equal("korkut_command", heard.Type);
        Stance(w, cmd, Sim.Stance.Hide);
        w.Run(jet.Def.Archetype.Listen!.PulseTicks * 2);
        Assert.False(w.RadarContacts(2).Single().Live); // switched off: a last-seen mark
    }

    [Fact]
    public void KorkutTargetOrderDronesBeforeHelicoptersOneAtATime()
    {
        var (s, sg) = Battery(400, 80, 1, 40, 40, sentinels: true);
        sg.HP = 1_000_000; // the Watcher's bombs
        Hover(s, 2, "attack_helicopter", 90, 40); // nearer
        var watcher = Hover(s, 2, "watcher_drone", 240, 40);
        s.Run(400);
        Assert.Equal(watcher.Id, sg.AirTarget); // a drone before a helicopter
        var (w, gun) = Battery(400, 80, 1, 40, 40);
        var kaman = Hover(w, 2, "fpv_drone", 70, 40); // a drone that attacks nothing
        kaman.Lifetime = 100_000;
        w.Run(60);
        Assert.Equal(kaman.Id, gun.AirTarget);
        var targets = new List<uint>();
        var truck = Crewed(w, 2, "drone_truck", 380, 40);
        Ground(w, truck, new Cell(40, 40)); // a suicide drone heading at the battery
        for (var t = 0; t < 1200 && Drone(w) is null; t++)
        {
            w.Step();
        }
        w.Run(2); // the next air defence pass picks it
        for (var t = 0; t < 600 && Drone(w) is { } p; t++)
        {
            targets.Add(gun.AirTarget);
            w.Step();
        }
        Assert.NotEmpty(targets);
        Assert.All(targets, id => Assert.NotEqual(kaman.Id, id)); // the suicide drone first, every tick it is in reach
    }

    [Fact]
    public void ASwarmOfSixSuicideDronesGetsSomeThrough()
    {
        // Ten drone trucks 175 cells out fire two drones each at a soldier by a linked gun: about 15 s in its reach, one at a time.
        var (w, gun) = Battery(300, 140, 1, 40, 70);
        gun.HP = 1_000_000;
        var target = w.Spawn(1, "Rifleman", 42, 70);
        target.HP = 1_000_000;
        for (var i = 0; i < 10; i++)
        {
            var truck = Crewed(w, 2, "drone_truck", 217, 16 + (i * 12), Facing.West);
            Ground(w, truck, new Cell(42, 70));
            Ground(w, truck, new Cell(42, 72), queue: true);
        }
        var launched = new List<uint>();
        var shot = 0;
        for (var t = 0; t < 2400; t++)
        {
            w.Step();
            launched.AddRange(w.Projectiles.Where(p => p.Interceptible == InterceptClass.Drone && !launched.Contains(p.Id)).Select(p => p.Id));
            shot += w.GunBursts.Count(b => b.Shooter == gun.Id && b.Killed);
        }
        output.WriteLine($"swarm: {launched.Count} drones from 175 cells, {shot} shot down, {launched.Count - shot} through");
        Assert.True(launched.Count >= 6);
        Assert.InRange(shot, 1, launched.Count - 1);
    }

    // ---- Ground fire ----

    [Fact]
    public void ZuIsAirFirstAndAggressiveFlipsItToGroundFirst()
    {
        foreach (var aggressive in new[] { false, true })
        {
            var w = Strip(300, 80, 1);
            var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
            zu.HP = 1_000_000;
            if (aggressive)
            {
                Stance(w, zu, Sim.Stance.Aggressive);
            }
            var heli = Hover(w, 1, "attack_helicopter", 140, 40);
            w.Run(60); // the helicopter up first
            var man = w.Spawn(1, "Rifleman", 49, 40); // within its 12 cells of sight
            man.HP = 1_000_000;
            var air = 0;
            for (var t = 0; t < 300; t++)
            {
                w.Step();
                air += w.GunBursts.Count(b => b.Shooter == zu.Id);
            }
            var ground = man.HP < 1_000_000;
            Assert.True(aggressive ? ground && air == 0 : !ground && air > 0, $"aggressive {aggressive}: air {air}, ground {ground}");
        }
    }

    [Fact]
    public void ZuGroundFireIsNothingAgainstTanksAndHurtsTrucks()
    {
        var w = Strip(200, 80, 1);
        var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
        var tank = Crewed(w, 1, "main_battle_tank", 47, 34);
        var humvee = Crewed(w, 1, "humvee", 47, 46);
        Stance(w, tank, Sim.Stance.HoldFire);
        Stance(w, humvee, Sim.Stance.HoldFire);
        var (tankHp, humveeHp) = (tank.HP, humvee.HP);
        w.Run(400);
        Assert.Equal(tankHp, tank.HP);
        Assert.True(humvee.HP < humveeHp || !humvee.Alive);
    }

    [Fact]
    public void KorkutGroundFireIsSelfDefenceAgainstInfantryOnlyAircraftFirst()
    {
        var (w, gun) = Battery(300, 80, 1, 40, 40, sentinels: true);
        gun.HP = 1_000_000;
        var truck = Crewed(w, 2, "humvee", 46, 35);
        Stance(w, truck, Sim.Stance.HoldFire);
        var heli = Hover(w, 2, "attack_helicopter", 140, 40);
        w.Run(60); // the helicopter up first
        var man = w.Spawn(2, "Rifleman", 46, 45); // within its 9 cells of sight
        man.HP = 1_000_000;
        var truckHp = truck.HP;
        w.Run(300);
        Assert.Equal(1_000_000, man.HP); // aircraft first
        heli.HP = 0;
        w.Run(300);
        Assert.True(man.HP < 1_000_000); // then the soldier close by
        Assert.Equal(truckHp, truck.HP); // never a vehicle
    }

    // ---- Gunners, ammo, reveal ----

    [Fact]
    public void SmallArmsKillTheGunnersAndADepotSeatsNewOnes()
    {
        var w = Strip(300, 80, 1);
        var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
        zu.HP = 1_000_000;
        Stance(w, zu, Sim.Stance.HoldFire);
        var men = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 44, 36 + (i * 2))).ToList();
        for (var t = 0; t < 4000 && zu.HasGunner; t++)
        {
            w.Step();
        }
        Assert.False(zu.HasGunner);
        Assert.Equal(2, zu.GunnersLost);
        foreach (var m in men)
        {
            m.HP = 0;
        }
        Stance(w, zu, Sim.Stance.Guard);
        var heli = Hover(w, 1, "attack_helicopter", 120, 40);
        var bursts = 0;
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            bursts += w.GunBursts.Count(b => b.Shooter == zu.Id);
        }
        Assert.Equal(0, bursts); // silent; it still drives
        var depot = w.Spawn(2, "supply_depot", 40, 60);
        zu.Pos = depot.Pos + new Vec(Fixed.FromInt(4), Fixed.Zero); // drives up to it (within its 8 cells)
        for (var t = 0; t < 600 && zu.GunnersLost > 0; t++)
        {
            w.Step();
        }
        Assert.Equal(0, zu.GunnersLost);
        for (var t = 0; t < 200 && bursts == 0; t++)
        {
            w.Step();
            bursts += w.GunBursts.Count(b => b.Shooter == zu.Id);
        }
        Assert.True(bursts > 0 && heli.Alive);
    }

    [Fact]
    public void ABurstAMinuteOfSixtyRevealsTheGunAndTheMagazineEmpties()
    {
        var w = Strip(300, 80, 1);
        var zu = Crewed(w, 2, "zu23_carrier", 40, 40);
        zu.HP = 1_000_000;
        var heli = Hover(w, 1, "attack_helicopter", 140, 40);
        var ticks = new List<uint>();
        for (var t = 0; t < 1600; t++)
        {
            w.Step();
            if (w.GunBursts.Any(b => b.Shooter == zu.Id))
            {
                ticks.Add(w.Tick);
                Assert.True(zu.RevealUntil > w.Tick); // the tracers show it
            }
        }
        Assert.Equal(60, ticks.Count); // one load, then a depot
        Assert.All(ticks.Zip(ticks.Skip(1)), p => Assert.Equal(20u, p.Second - p.First)); // one burst a second
        Assert.True(heli.Alive);
    }
}

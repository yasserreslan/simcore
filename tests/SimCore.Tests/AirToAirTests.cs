using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;

namespace SimCore.Tests;

// Air-to-air (E6; the plan's air-combat model, sections 2.8 to 2.11): aircraft as shooters (Guard
// hunts aircraft and drones, a Strike engages its ordered air target, nothing else fires), radar and IR
// missiles from aircraft (launch cones, lock x signature), the gun (cone, range, a roll per burst),
// countermeasures (flares against IR, chaff against radar, a salvo per missile, refilled on a Rearm),
// evasion (the break, real geometry), kill attribution (KilledBy, AirLosses), the FPV drone as an air
// target, determinism.
public class AirToAirTests
{
    private const int N = 240;

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, Cell? cell2 = null, AirTargetKinds filter = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2, Filter = (byte)filter });

    private static string Spawnable(string jet) => jet == "multirole_jet" ? "Multirole 500 lb" : jet;

    private static Unit Jet(World w, string jet, int x, int y, byte player = 1, int facing = Facing.East)
    {
        var d = w.Spawn(player, Spawnable(jet), x, y, facing);
        return d;
    }

    // Both players Sentinel (jet against jet), no starting army.
    private static World Duel(ulong seed = 1, ContentSet? content = null)
    {
        content ??= TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        setup = setup with { Players = [p0, p1] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    // The Sentinel lobby (player 2 Resistance) on content.
    private static World Sentinel(ContentSet content, ulong seed = 1)
    {
        var setup = TestContent.Sentinel(seed).For(content, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    // The MVP content without the jets' evade blocks (a jet that never breaks), for comparison.
    private static readonly Lazy<ContentSet> NoEvade = new(() =>
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(c => c.Name.EndsWith("archetypes/sentinel.json", StringComparison.Ordinal));
        var json = sources[i].Json.Replace("\"evade\": { \"breakG\": 100, \"warn\": 60, \"maxTicks\": 50 },", "", StringComparison.Ordinal);
        Assert.DoesNotContain("\"evade\"", json, StringComparison.Ordinal);
        sources[i] = new ContentSource(sources[i].Name, json);
        return ContentLoader.Load(sources);
    });

    private static void NoMissiles(Unit u)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Effect.Interceptor is not null && w.Stock >= 0)
            {
                u.MutableCharges[w.Stock] = 0;
            }
        }
    }

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    // ---- Guard hunts drones: missiles far out, the gun close in ----

    [Fact]
    public void AFighterOnGuardHuntsAScoutDroneWithItsMissiles()
    {
        var w = Arena.NewSentinel(N);
        var f = Jet(w, "fighter_jet", 60, 120);
        var scout = w.Spawn(2, "scout_drone", 150, 120);
        Give(w, f, AirTaskKind.Guard, new Cell(50, 80), new Cell(190, 160));
        var launches = new List<AirLaunch>();
        var losses = new List<AirLoss>();
        var t = RunUntil(w, () =>
        {
            launches.AddRange(w.AirLaunches);
            losses.AddRange(w.AirLosses);
            return !scout.Alive && losses.Count > 0;
        }, 1200);
        Assert.True(t > 0, "the scout drone is shot down");
        Assert.Contains(launches, l => l.Shooter == f.Id && l.Target == scout.Id);
        var loss = Assert.Single(losses);
        Assert.Equal((scout.Id, KillCause.Missile, f.Id), (loss.Aircraft, loss.Cause, loss.Killer));
        Assert.Equal(KillCause.Missile, scout.KilledBy);
    }

    [Fact]
    public void WithoutMissilesTheGunBringsTheScoutDownAndTheKillIsAGunKill()
    {
        var w = Arena.NewSentinel(N);
        var f = Jet(w, "fighter_jet", 60, 120);
        NoMissiles(f);
        var scout = w.Spawn(2, "scout_drone", 150, 120);
        Give(w, f, AirTaskKind.Guard, new Cell(50, 80), new Cell(190, 160));
        var bursts = new List<GunBurst>();
        var losses = new List<AirLoss>();
        var t = RunUntil(w, () =>
        {
            bursts.AddRange(w.GunBursts);
            losses.AddRange(w.AirLosses);
            return losses.Count > 0;
        }, 3000);
        Assert.True(t > 0, $"the scout drone is gunned down ({bursts.Count} bursts)");
        Assert.All(bursts, b => Assert.InRange(Fixed.FromRaw(World.Len3(b.At.X.Raw - b.From.X.Raw, b.At.Y.Raw - b.From.Y.Raw, b.AtAltitude.Raw - b.FromAltitude.Raw)).Raw,
            0, f.Def.Weapons.Single(x => x.Effect.AirGun is not null).Range.Raw + Fixed.OneRaw * 2));
        Assert.Contains(bursts, b => b.Killed && b.Target == scout.Id);
        Assert.Equal((KillCause.Gun, f.Id), (losses[0].Cause, losses[0].Killer)); // the gun made the killing blow: the pilot would eject
        Assert.DoesNotContain(w.Projectiles, p => p.Shooter == f.Id);
    }

    [Fact]
    public void TheFpvDroneIsAnAirTargetTheJetsGunDownClose()
    {
        var w = Arena.NewSentinel(N);
        var m = Jet(w, "multirole_jet", 60, 120);
        NoMissiles(m);
        var fpv = w.Spawn(2, "fpv_drone", 130, 120);
        fpv.Lifetime = 100_000; // its battery lasts the test
        Give(w, m, AirTaskKind.Guard, new Cell(50, 80), new Cell(190, 160), AirTargetKinds.Drones);
        var bursts = new List<GunBurst>();
        var t = RunUntil(w, () =>
        {
            bursts.AddRange(w.GunBursts);
            return !fpv.Alive;
        }, 3000);
        Assert.True(t > 0, $"the FPV drone is gunned down ({bursts.Count} bursts)");
        Assert.Contains(bursts, b => b.Killed && b.Target == fpv.Id);
        Assert.Equal(KillCause.Gun, fpv.KilledBy);
    }

    [Fact]
    public void AMultiroleOnGuardShootsDownAOneWayAttackDrone()
    {
        var w = Arena.NewSentinel(N);
        var m = Jet(w, "multirole_jet", 120, 60);
        var truck = w.SpawnCrewed(2, "drone_truck", 120, 230, Facing.North).Vehicle;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(120, 10) });
        Give(w, m, AirTaskKind.Guard, new Cell(60, 40), new Cell(180, 200), AirTargetKinds.Drones);
        uint drone = 0;
        var killed = false;
        RunUntil(w, () =>
        {
            drone = drone != 0 ? drone : w.Projectiles.FirstOrDefault(p => p.Phase != DronePhase.None)?.Id ?? 0;
            killed |= w.AirBursts.Any(b => b.Shooter == m.Id && b.Target == drone && b.Kind == AirBurstKind.Kill) ||
                w.GunBursts.Any(b => b.Shooter == m.Id && b.Target == drone && b.Killed);
            return killed;
        }, 1500);
        Assert.NotEqual(0u, drone);
        Assert.True(killed, "the jet brings the one-way attack drone down");
    }

    // ---- The owner's rules: only Guard picks targets; a Strike engages its ordered target ----

    [Fact]
    public void OnPatrolAJetNeverFiresButAStrikeOrderEngagesTheAirTargetAndThePatrolResumes()
    {
        var w = Arena.NewSentinel(N);
        var f = Jet(w, "fighter_jet", 60, 120);
        var scout = w.Spawn(2, "scout_drone", 130, 120);
        Give(w, f, AirTaskKind.Patrol, new Cell(80, 120));
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            Assert.Empty(w.AirLaunches);
            Assert.Empty(w.GunBursts);
        }
        Assert.True(scout.Alive);
        w.Order(1, CommandKind.Attack, [f], target: scout.Id);
        w.Step();
        Assert.Equal((OrderKind.Attack, scout.Id), (f.Order.Kind, f.Order.Target));
        Assert.True(RunUntil(w, () => !scout.Alive, 1500) > 0, "the ordered air target is shot down");
        w.Run(5);
        Assert.Equal(OrderKind.Idle, f.Order.Kind);
        Assert.Equal(AirTaskKind.Patrol, f.AirMission!.Kind);
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(f).Activity);
    }

    [Fact]
    public void GuardEngagesOnlyItsFilterAndOnlyInsideTheAreaPlusTheLeash()
    {
        var w = Arena.NewSentinel(N);
        var m = Jet(w, "multirole_jet", 60, 60);
        var far = w.Spawn(2, "scout_drone", 200, 200); // outside the area plus the 30-cell leash
        Give(w, m, AirTaskKind.Guard, new Cell(40, 40), new Cell(100, 100), AirTargetKinds.Vehicles); // no air kinds
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            Assert.Equal(0u, m.AirTarget);
        }
        Give(w, m, AirTaskKind.Guard, new Cell(40, 40), new Cell(100, 100), AirTargetKinds.Drones);
        for (var t = 0; t < 300; t++)
        {
            w.Step();
            Assert.NotEqual(far.Id, m.AirTarget);
        }
        Assert.True(far.Alive);
    }

    // ---- Jet against jet ----

    private static (List<ulong> Hashes, List<AirLoss> Losses, int Launches, int Bursts, int Salvos) Dogfight(ulong seed, bool reversed = false)
    {
        var w = Duel(seed);
        var a = Jet(w, "multirole_jet", 50, 110, 1, Facing.East);
        var b = Jet(w, "fighter_jet", 190, 130, 2, Facing.West);
        var cmds = new[]
        {
            new Command { Tick = 0, Player = 1, Kind = CommandKind.AirTask, Units = [a.Id], Mission = AirTaskKind.Guard, Cell = new Cell(40, 60), Cell2 = new Cell(200, 180), Filter = (byte)AirTargetKinds.Aircraft },
            new Command { Tick = 0, Player = 2, Kind = CommandKind.AirTask, Units = [b.Id], Mission = AirTaskKind.Guard, Cell = new Cell(40, 60), Cell2 = new Cell(200, 180), Filter = (byte)AirTargetKinds.Aircraft },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        var losses = new List<AirLoss>();
        int launches = 0, bursts = 0, salvos = 0;
        for (var t = 0; t < 3600 && losses.Count == 0; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
            losses.AddRange(w.AirLosses);
            launches += w.AirLaunches.Count;
            bursts += w.GunBursts.Count;
            salvos += w.Countermeasures.Count;
        }
        return (hashes, losses, launches, bursts, salvos);
    }

    [Fact]
    public void TwoJetsEngageEachOtherAndTheFightEndsDeterministically()
    {
        var one = Dogfight(1);
        var two = Dogfight(1, reversed: true);
        Assert.Equal(one.Hashes, two.Hashes);
        Assert.True(one.Losses.Count > 0, $"a jet is brought down within 3 minutes ({one.Launches} launches, {one.Bursts} bursts, {one.Salvos} salvos)");
        Assert.True(one.Launches > 0);
        Assert.True(one.Salvos > 0, "the targeted jet dispenses countermeasures");
        Assert.Contains(one.Losses[0].Cause, new[] { KillCause.Gun, KillCause.Missile });
    }

    [Fact]
    public void TheAirSuperiorityJetsSignatureDelaysItsDetectionAndLock()
    {
        // A multirole of player 1 on Guard; an enemy multirole and an enemy air superiority jet each 80
        // cells out (inside its 110-cell radar; the fighter's detect 50 brings that to 55 for it).
        static (uint Target, int LaunchTick) Run(string enemy)
        {
            var w = Duel();
            var a = Jet(w, "multirole_jet", 60, 120, 1, Facing.East);
            var e = Jet(w, enemy, 140, 120, 2, Facing.East);
            Give(w, a, AirTaskKind.Guard, new Cell(40, 60), new Cell(230, 180), AirTargetKinds.Aircraft);
            w.Issue(new Command { Tick = 0, Player = 2, Kind = CommandKind.Move, Units = [e.Id], Cell = new Cell(220, 120) });
            w.Run(12); // Guard scans for air targets twice a second
            var seen = a.AirTarget;
            var launch = RunUntil(w, () => w.AirLaunches.Any(l => l.Shooter == a.Id), 1200);
            return (seen, launch);
        }
        var normal = Run("multirole_jet");
        var stealth = Run("fighter_jet");
        Assert.NotEqual(0u, normal.Target); // seen at 80 cells
        Assert.Equal(0u, stealth.Target);   // not yet: the signature halves the radar's range
        Assert.True(normal.LaunchTick > 0 && stealth.LaunchTick > 0, $"both are engaged ({normal.LaunchTick}, {stealth.LaunchTick})");
        Assert.True(stealth.LaunchTick > normal.LaunchTick, $"the stealthy jet is engaged later ({normal.LaunchTick} vs {stealth.LaunchTick})");
    }

    // ---- Countermeasures ----

    // A multirole of player 1 orbiting over an AA soldier (Igla, IR) of player 2; its first missile's burst.
    private static (AirBurst Burst, Projectile Missile, List<CountermeasureSalvo> Salvos, Unit Jet, World W) IglaShot(ContentSet? content = null, bool emptyFlares = false, string aa = "manpads")
    {
        var w = content is null ? Arena.NewSentinel(N) : Sentinel(content);
        var j = Jet(w, "multirole_jet", 140, 120);
        if (aa == "manpads")
        {
            w.Spawn(2, aa, 120, 120);
        }
        else
        {
            w.SpawnCrewed(2, aa, 120, 120);
        }
        if (emptyFlares)
        {
            for (var i = 0; i < j.Salvos.Count; i++)
            {
                j.MutableSalvos[i] = 0;
            }
        }
        Projectile? missile = null;
        var salvos = new List<CountermeasureSalvo>();
        AirBurst? burst = null;
        RunUntil(w, () =>
        {
            missile ??= w.Projectiles.FirstOrDefault(p => p.Weapon.Effect.Interceptor is not null && p.Target == j.Id);
            salvos.AddRange(w.Countermeasures);
            if (missile is not null && w.AirBursts.FirstOrDefault(b => b.Missile == missile.Id) is { Missile: not 0 } b)
            {
                burst = b;
            }
            return burst is not null;
        }, 1200);
        Assert.NotNull(missile);
        Assert.NotNull(burst);
        return (burst!.Value, missile!, salvos, j, w);
    }

    [Fact]
    public void AFlareSalvoCutsAnIrMissilesChanceAndSpendsOneFlare()
    {
        // Without the break (content without evade) so the missile reaches its fuse in both runs: the
        // jet that breaks makes this Igla overshoot (OnALaunchWarningTheJetBreaks...).
        var with = IglaShot(NoEvade.Value);
        var without = IglaShot(NoEvade.Value, emptyFlares: true);
        Assert.NotEqual(AirBurstKind.SelfDestruct, with.Burst.Kind);
        var flare = with.Jet.Def.Archetype.Countermeasures.Single(c => c.Kind == CountermeasureKind.Flare);
        var cs = Assert.Single(with.Salvos, s => s.Missile == with.Missile.Id);
        Assert.Equal((with.Jet.Id, CountermeasureKind.Flare), (cs.Aircraft, cs.Kind));
        Assert.Equal((1, flare.Pk), (with.Missile.Decoys, with.Missile.DecoyPercent));
        Assert.Equal(flare.Salvos - 1, with.Jet.Salvos[with.Jet.Def.Archetype.Countermeasures.ToList().IndexOf(flare)]);
        Assert.Empty(without.Salvos); // an empty dispenser gives no protection
        Assert.Equal(0, without.Missile.Decoys);
        Assert.Equal(without.Burst.KillPermille * flare.Pk / 100, with.Burst.KillPermille); // the same geometry, x 55%
        Assert.True(with.Burst.KillPermille < without.Burst.KillPermille, $"{with.Burst} vs {without.Burst}");
    }

    [Fact]
    public void ChaffDecoysTheRadarMissileAndFlaresDoNot()
    {
        var shot = IglaShot(aa: "sam_vehicle");
        var cs = Assert.Single(shot.Salvos, s => s.Missile == shot.Missile.Id);
        Assert.Equal(CountermeasureKind.Chaff, cs.Kind);
        var cms = shot.Jet.Def.Archetype.Countermeasures;
        for (var i = 0; i < cms.Count; i++)
        {
            Assert.Equal(cms[i].Kind == CountermeasureKind.Chaff ? cms[i].Salvos - 1 : cms[i].Salvos, shot.Jet.Salvos[i]);
        }
    }

    // ---- Evasion ----

    [Fact]
    public void OnALaunchWarningTheJetBreaksAndTheBreakChangesTheEngagement()
    {
        // The same Igla shot at a jet that breaks and at one that does not (content without evade).
        var w = Arena.NewSentinel(N);
        var j = Jet(w, "multirole_jet", 140, 120);
        w.Spawn(2, "manpads", 120, 120);
        var broke = RunUntil(w, () => j.Wing is { Evade: > 0 }, 1200);
        Assert.True(broke > 0, "it breaks");
        var st = j.Wing!;
        Assert.NotEqual(0u, st.EvadeFrom);
        var alt0 = j.Altitude.Raw;
        var lateral = 0L;
        var afterburner = false;
        for (var t = 0; t < 10; t++)
        {
            w.Step();
            lateral = Math.Max(lateral, Math.Abs(st.Lateral));
            afterburner |= st.Throttle == Throttle.Afterburner;
        }
        Assert.True(j.Altitude.Raw < alt0, "it dives toward its floor");
        Assert.True(afterburner, "on the afterburner");
        Assert.True(lateral > 0, "a hard turn");

        var breaks = IglaShot();
        var straight = IglaShot(NoEvade.Value);
        Assert.Null(straight.Jet.Def.Archetype.Evade);
        // Before E12 the break's dive went from level to 40 m/s down in one tick (about 140 g) and made this
        // Igla overshoot (no fuse). With a bounded vertical acceleration (its g) the missile reaches it here too
        // and rolls; E13 (D-BRK1): breaking, it dispenses its flare at the launch warning and that salvo leaves
        // 30% instead of 55%, so breaking with flares beats flying straight with them (AttackRunTests).
        Assert.True(breaks.Burst.KillPermille < straight.Burst.KillPermille, $"break {breaks.Burst.KillPermille} vs straight {straight.Burst.KillPermille}");
        Assert.NotEqual(AirBurstKind.SelfDestruct, straight.Burst.Kind); // flying on, it is reached and rolled for
    }

    // ---- Kill attribution, the FPV drone and ground air defence ----

    [Fact]
    public void AJetThatRunsDryInTheAirIsAFuelLoss()
    {
        var w = Arena.NewSentinel(N);
        var j = Jet(w, "fighter_jet", 120, 120);
        w.Run(5);
        j.Lifetime = 1;
        var losses = new List<AirLoss>();
        Assert.True(RunUntil(w, () => { losses.AddRange(w.AirLosses); return losses.Count > 0; }, 100) > 0);
        Assert.Equal((j.Id, KillCause.Fuel, 0u, false), (losses[0].Aircraft, losses[0].Cause, losses[0].Killer, losses[0].Grounded));
    }

    [Fact]
    public void GroundAirDefenceNowEngagesTheFpvDrone()
    {
        var w = Arena.NewSentinel(N);
        w.Spawn(1, "manpads", 120, 120);
        var fpv = w.Spawn(2, "fpv_drone", 150, 120);
        fpv.Lifetime = 100_000;
        var shot = RunUntil(w, () => w.AirBursts.Any(b => b.Target == fpv.Id), 600);
        Assert.True(shot > 0, "the AA soldier fires at the FPV drone (class fpv: 70% at best)");
    }

    // ---- Determinism ----

    [Fact]
    public void AMixedAirBattleIsDeterministic()
    {
        static List<ulong> Run()
        {
            var w = Duel(7);
            var a = Jet(w, "multirole_jet", 50, 100, 1);
            var b = Jet(w, "fighter_jet", 60, 140, 1);
            var c = Jet(w, "fighter_jet", 190, 120, 2, Facing.West);
            w.Spawn(2, "overseer_drone", 120, 120);
            w.Spawn(1, "watcher_drone", 120, 80);
            foreach (var u in new[] { a, b, c })
            {
                w.Issue(new Command { Tick = 0, Player = u.Owner, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Guard, Cell = new Cell(40, 60), Cell2 = new Cell(200, 180), Filter = (byte)(AirTargetKinds.Aircraft | AirTargetKinds.Drones) });
            }
            var h = new List<ulong>();
            for (var t = 0; t < 1500; t++)
            {
                w.Step();
                h.Add(w.Hash());
            }
            return h;
        }
        Assert.Equal(Run(), Run());
    }
}

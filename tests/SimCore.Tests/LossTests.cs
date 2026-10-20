using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Losses (E10; the plan's section 5): a jet lost in the air to anything but a gun sends a wreck down a
// deterministic crash path that goes off where it hits (a small blast: decision D9b; a crater on a runway);
// a gun kill sends the wreck down too and the pilot ejects (seat, canopy, drogue, chute; untargetable while
// he comes down: D8) and lands as a pilot unit of the jet's side. The technical's heavy MG guns down
// aircraft below 40 m (D5). Determinism.
public class LossTests
{
    private const int N = 240;
    private const long Metre = 512;
    private const long Cell1 = Fixed.OneRaw;

    // A multirole of player (default 1) flying east at its patrol height from (x, y), after ticks of flight.
    private static Unit Flying(World w, int x, int y, byte player = 1, int ticks = 40, string preset = "Multirole 500 lb", int facing = Facing.East)
    {
        var j = w.Spawn(player, preset, x, y, facing);
        w.Run(ticks);
        return j;
    }

    // Kills aircraft j this tick by cause (as the combat systems do), optionally put at pos first; steps once.
    private static void Kill(World w, Unit j, KillCause cause, Vec? pos = null)
    {
        if (pos is { } p)
        {
            j.Pos = p;
        }
        j.HP = 0;
        j.KilledBy = cause;
        w.Step();
    }

    private sealed class Log
    {
        public readonly List<WreckImpact> Impacts = [];
        public readonly List<EjectionEvent> Ejections = [];
        public readonly List<AirLoss> Losses = [];
        public readonly List<(Vec Pos, long Alt, long Vx, long Vy)> Path = [];
        public readonly List<(int Age, Vec Pos, long Alt, ChutePhase Phase)> Chute = [];

        public void Take(World w)
        {
            Impacts.AddRange(w.WreckImpacts);
            Ejections.AddRange(w.EjectionEvents);
            Losses.AddRange(w.AirLosses);
            if (w.Wrecks.Count > 0)
            {
                var k = w.Wrecks[0];
                Path.Add((k.Pos, k.Altitude.Raw, k.VelX, k.VelY));
            }
            if (w.Chutes.Count > 0)
            {
                var c = w.Chutes[0];
                Chute.Add((c.Age, c.Pos, c.Altitude.Raw, c.Phase));
            }
        }
    }

    private static Log RunLog(World w, int ticks, Log? log = null)
    {
        log ??= new Log();
        log.Take(w);
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            log.Take(w);
        }
        return log;
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

    private static long Flat(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    // ---- The crash path ----

    [Fact]
    public void AMissileKillSendsAWreckDownACrashPathThatGoesOffOnTheGround()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        var (id, from, alt) = (j.Id, j.Pos, j.Altitude.Raw);
        Assert.InRange(alt, 55 * Metre, 65 * Metre);
        Kill(w, j, KillCause.Missile);
        var k = Assert.Single(w.Wrecks);
        Assert.Equal((id, KillCause.Missile, (byte)1, "multirole_jet"), (k.Id, k.Cause, k.Owner, w.Content.Archetypes[k.Archetype].Id));
        Assert.Empty(w.Chutes); // no ejection: not a gun kill
        Assert.DoesNotContain(w.EjectionEvents, e => e.Aircraft == id);
        Assert.Null(w.Unit(id));
        var log = RunLog(w, 200);
        var hit = Assert.Single(log.Impacts);
        Assert.Empty(w.Wrecks);
        Assert.Equal((id, KillCause.Missile, 0u, CraterKind.None), (hit.Wreck, hit.Cause, hit.Hit, hit.Runway));
        Assert.Equal(0, hit.Altitude.Raw); // the flat ground
        // It falls all the way (never climbs once its upward speed is spent), never steers (it keeps its
        // heading), slows along its track by its drag, and lands well ahead of where it was lost.
        var (v0x, v0y) = (log.Path[0].Vx, log.Path[0].Vy);
        var v0 = World.Len3(v0x, v0y, 0);
        for (var i = 1; i < log.Path.Count; i++)
        {
            var (x, y) = (log.Path[i].Vx, log.Path[i].Vy);
            Assert.True(Math.Abs(x * v0y - y * v0x) <= v0 * 40, "it does not turn"); // within a few hundredths of a radian
            Assert.True(World.Len3(x, y, 0) <= World.Len3(log.Path[i - 1].Vx, log.Path[i - 1].Vy, 0), "drag only slows it");
            var (dx, dy) = (log.Path[i].Pos.X.Raw - log.Path[i - 1].Pos.X.Raw, log.Path[i].Pos.Y.Raw - log.Path[i - 1].Pos.Y.Raw);
            Assert.True(dx * v0x + dy * v0y >= 0, "it keeps its heading");
        }
        var travelled = Flat(hit.At, from);
        Assert.InRange(travelled, 15 * Cell1, 45 * Cell1);
        Assert.InRange(hit.AngleDeg, 20, 85); // a steep impact from patrol height
        Assert.True(hit.VelZ < 0 && World.Len3(hit.VelX, hit.VelY, 0) < v0);
    }

    [Fact]
    public void TheWreckGoesOffWhereItHitsAndHurtsWhatIsUnderIt()
    {
        // Where does it come down? (A first run: the crash path depends only on where and how it was lost.)
        Vec at;
        {
            var w = Arena.NewSentinel(N);
            var j = Flying(w, 60, 120);
            Kill(w, j, KillCause.Blast);
            at = RunLog(w, 200).Impacts.Single().At;
        }
        // Again, with soldiers and a building there.
        {
            var w = Arena.NewSentinel(N);
            var j = Flying(w, 60, 120);
            var c = Cell.Of(at);
            var under = w.Spawn(2, "Rifleman", c.X, c.Y);
            var near = w.Spawn(2, "Rifleman", c.X + 2, c.Y);
            var far = w.Spawn(2, "Rifleman", c.X, c.Y + 6);
            var friend = w.Spawn(1, "Rifleman", c.X - 2, c.Y);
            Kill(w, j, KillCause.Blast);
            var log = RunLog(w, 200);
            var hit = Assert.Single(log.Impacts);
            Assert.False(under.Alive, "150 through the crash warhead kills a soldier under it");
            Assert.True(near.HP < near.Def.MaxHp, "the 3-cell splash reaches 2 cells out");
            Assert.True(friend.HP < friend.Def.MaxHp, "friends too: a wreck picks no side");
            Assert.Equal(far.Def.MaxHp, far.HP);
            Assert.InRange(Flat(hit.At, at), 0, Cell1);
        }
        {
            var w = Arena.NewSentinel(N);
            var j = Flying(w, 60, 120);
            var c = Cell.Of(at);
            var depot = w.Spawn(2, "supply_depot", c.X - 1, c.Y - 1);
            var hp = depot.HP;
            Kill(w, j, KillCause.Blast);
            var hit = Assert.Single(RunLog(w, 200).Impacts);
            Assert.Equal((depot.Id, true), (hit.Hit, hit.Building)); // it came down on the roof
            Assert.True(hit.Altitude.Raw > 0, "on the roof, not the ground");
            Assert.InRange(hp - depot.HP, 75, 150); // crash x building 50%: the direct hit (the splash skips it)
        }
    }

    [Fact]
    public void AMissileKillBreaksSomeAirframesUpAndTheyFallSteeperOtherKillsNever()
    {
        static (List<bool> Broken, List<long> Travel) Lose(KillCause cause)
        {
            var w = Arena.NewSentinel(N);
            var jets = new List<Unit>();
            for (var i = 0; i < 12; i++)
            {
                jets.Add(w.Spawn(1, "Multirole 500 lb", 50, 20 + 16 * i, Facing.East));
            }
            w.Run(40);
            var from = jets.ToDictionary(j => j.Id, j => j.Pos);
            foreach (var j in jets)
            {
                j.HP = 0;
                j.KilledBy = cause;
            }
            w.Step();
            var broken = w.Wrecks.OrderBy(k => k.Id).Select(k => k.BrokenUp).ToList();
            var log = RunLog(w, 300);
            var travel = log.Impacts.OrderBy(i => i.Wreck).Select(i => Flat(i.At, from[i.Wreck])).ToList();
            return (broken, travel);
        }
        var (broken, travel) = Lose(KillCause.Missile);
        Assert.Equal(12, broken.Count);
        Assert.InRange(broken.Count(b => b), 1, 11); // 40%: some, not all
        var steep = travel.Where((_, i) => broken[i]).Max();
        var whole = travel.Where((_, i) => !broken[i]).Min();
        Assert.True(steep < whole, $"broken up it falls shorter ({steep} vs {whole})");
        foreach (var cause in new[] { KillCause.Gun, KillCause.Fuel, KillCause.Blast })
        {
            Assert.DoesNotContain(true, Lose(cause).Broken);
        }
    }

    [Fact]
    public void AJetThatRunsDryCrashesNobodyEjects()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        j.Lifetime = 1;
        var log = RunLog(w, 300);
        Assert.Equal(KillCause.Fuel, Assert.Single(log.Losses).Cause);
        Assert.Equal(KillCause.Fuel, Assert.Single(log.Impacts).Cause);
        Assert.Empty(log.Ejections);
    }

    [Fact]
    public void AWreckOnARunwayDigsACrater()
    {
        // The crash path's offset from where the jet is lost (a first run, no airfield).
        Vec from, at;
        {
            var w = AirToGroundDuel();
            var j = Flying(w, 60, 100);
            from = j.Pos;
            Kill(w, j, KillCause.Blast); // never broken up: the same path wherever it is lost
            at = RunLog(w, 200).Impacts.Single().At;
        }
        {
            var w = AirToGroundDuel();
            var af = AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(80, 150), player: 2);
            var mid = World.AirfieldPoint(af, 300, 50); // the runway's middle
            var j = Flying(w, 60, 100);
            Kill(w, j, KillCause.Blast, new Vec(mid.X - (at.X - from.X), mid.Y - (at.Y - from.Y)));
            var hit = Assert.Single(RunLog(w, 200).Impacts);
            Assert.Equal(CraterKind.Small, hit.Runway);
            var c = Assert.Single(af.Craters);
            Assert.Equal(CraterKind.Small, c.Kind);
            Assert.InRange(Flat(c.At, hit.At), 0, 4 * Cell1);
        }
    }

    // Both players Sentinel (as the air-to-ground tests' duel), no starting army.
    private static World AirToGroundDuel(ulong seed = 1)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        setup = setup with { Players = [p0, p1] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    [Fact]
    public void AnAircraftWithoutALossBlockStillJustDisappears()
    {
        var w = Arena.NewSentinel(N);
        var d = w.Spawn(1, "watcher_drone", 120, 120);
        w.Run(40);
        Kill(w, d, KillCause.Missile);
        Assert.Empty(w.Wrecks);
        Assert.Empty(w.Chutes);
        Assert.Equal(KillCause.Missile, Assert.Single(w.AirLosses).Cause);
    }

    // ---- The ejection ----

    [Fact]
    public void AGunKillEjectsThePilotWhoRisesDescendsUnderHisChuteAndLandsAsAPilot()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        var (jid, from, alt) = (j.Id, j.Pos, j.Altitude.Raw);
        var hv = (X: j.Wing!.VelX, Y: j.Wing.VelY);
        var units = w.Units.Count - 1;
        Kill(w, j, KillCause.Gun);
        Assert.Single(w.Wrecks); // the empty jet falls separately
        var c = Assert.Single(w.Chutes);
        Assert.Equal((jid, (byte)1, "pilot", 0, ChutePhase.Seat), (c.Aircraft, c.Owner, w.Content.Archetypes[c.Unit].Id, c.Age, c.Phase));
        Assert.Equal((from, alt), (c.Pos, c.Altitude.Raw));
        Assert.Equal([EjectionEventKind.Canopy, EjectionEventKind.Seat], w.EjectionEvents.Select(e => e.Kind));
        Assert.Null(w.Unit(c.Id)); // not a unit while he comes down: nothing can target him
        var cid = c.Id;
        var log = new Log();
        var landed = RunUntil(w, () =>
        {
            log.Take(w);
            Assert.Equal(units, w.Units.Count(u => u.Def.Archetype.Id != "pilot")); // nothing else spawned
            return log.Ejections.Any(e => e.Kind == EjectionEventKind.Landed);
        }, 1000);
        Assert.True(landed > 0, "he lands");
        var kinds = log.Ejections.Select(e => e.Kind).ToList();
        Assert.Equal([EjectionEventKind.SeatAway, EjectionEventKind.ChuteOpen, EjectionEventKind.Landed], kinds);
        // The seat throws him 40 m up in 1 s; out of the seat he comes down at 6 m/s; the chute opens at 2.2 s.
        var top = log.Chute.Single(x => x.Age == 20);
        Assert.Equal(alt + 40 * Metre, top.Alt);
        Assert.Equal(ChutePhase.Drogue, top.Phase);
        Assert.Equal(ChutePhase.Open, log.Chute.Single(x => x.Age == 44).Phase);
        var a100 = log.Chute.Single(x => x.Age == 120).Alt;
        var a200 = log.Chute.Single(x => x.Age == 200).Alt;
        Assert.InRange(a100 - a200, 6 * Metre * 4 - 2, 6 * Metre * 4 + 2); // 4 s at 6 m/s
        // He drifts 1 cell/s along the jet's heading (east) for 3 s, then hangs still (no wind).
        var p60 = log.Chute.Single(x => x.Age == 60).Pos;
        Assert.InRange(Flat(p60, from), 3 * Cell1 - 96, 3 * Cell1 + 64); // (the drift a tick is a whole raw step: up to a raw short a tick off the axes)
        Assert.True((p60.X.Raw - from.X.Raw) * hv.X + (p60.Y.Raw - from.Y.Raw) * hv.Y > 0, "along the jet's heading");
        Assert.Equal(p60, log.Chute[^1].Pos);
        Assert.InRange(landed, 20 + (int)((alt + 40 * Metre) * 20 / (6 * Metre)) - 3, 20 + (int)((alt + 40 * Metre) * 20 / (6 * Metre)) + 3);
        var e = log.Ejections[^1];
        Assert.Equal((cid, jid), (e.Chute, e.Aircraft));
        Assert.Empty(w.Chutes);
        var p = w.Unit(e.Unit)!;
        Assert.Equal(("pilot", (byte)1, 45, OrderKind.Idle), (p.Def.Archetype.Id, p.Owner, p.HP, p.Order.Kind));
        Assert.Equal(p60, p.Pos); // where he came down
        Assert.Equal(e.At, p.Pos);
    }

    [Fact]
    public void ARealGunKillFromAnEnemyJetEjectsThePilot()
    {
        var w = AirToGroundDuel();
        var victim = w.Spawn(1, "Multirole 500 lb", 40, 120, Facing.East);
        var hunter = w.Spawn(2, "fighter_jet", 32, 120, Facing.East); // on its tail, inside gun range
        w.Order(1, CommandKind.Move, [victim], cell: new Cell(200, 120)); // flying straight away
        foreach (var x in hunter.Def.Weapons.Where(x => x.Effect.Interceptor is not null && x.Stock >= 0))
        {
            hunter.MutableCharges[x.Stock] = 0; // guns only
        }
        var gun = hunter.Def.Weapons.Single(x => x.Effect.AirGun is not null);
        hunter.MutableCharges[gun.Stock] = 1000; // bursts until one hits
        victim.HP = 1;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AirTask, Units = [hunter.Id], Mission = AirTaskKind.Guard, Cell = new Cell(30, 60), Cell2 = new Cell(210, 180), Filter = (byte)AirTargetKinds.Aircraft });
        var log = new Log();
        Assert.True(RunUntil(w, () => { log.Take(w); return log.Losses.Count > 0; }, 3000) > 0, "the victim is gunned down");
        Assert.Equal((victim.Id, KillCause.Gun, hunter.Id), (log.Losses[0].Aircraft, log.Losses[0].Cause, log.Losses[0].Killer));
        Assert.Single(w.Chutes);
        Assert.Contains(w.Wrecks, k => k.Id == victim.Id && k.Cause == KillCause.Gun && k.Killer == hunter.Id);
        Assert.True(RunUntil(w, () => { log.Take(w); return log.Ejections.Any(e => e.Kind == EjectionEventKind.Landed); }, 1000) > 0);
        var p = w.Unit(log.Ejections.Single(e => e.Kind == EjectionEventKind.Landed).Unit)!;
        Assert.Equal(("pilot", (byte)1), (p.Def.Archetype.Id, p.Owner));
        Assert.Single(log.Impacts, i => i.Wreck == victim.Id);
    }

    [Fact]
    public void ARealMissileKillCrashesTheJetAndNobodyEjects()
    {
        var w = Arena.NewSentinel(N);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 150, 120).Vehicle;
        var j = w.Spawn(1, "Multirole 500 lb", 60, 120, Facing.East);
        var log = new Log();
        Assert.True(RunUntil(w, () => { log.Take(w); return log.Losses.Count > 0; }, 2000) > 0, "the air defence vehicle shoots it down");
        Assert.Equal((j.Id, KillCause.Missile, sam.Id), (log.Losses[0].Aircraft, log.Losses[0].Cause, log.Losses[0].Killer));
        Assert.Empty(w.Chutes);
        Assert.True(RunUntil(w, () => { log.Take(w); return log.Impacts.Count > 0; }, 400) > 0);
        Assert.Empty(log.Ejections);
    }

    [Fact]
    public void ThePilotUnderHisChuteIsNeverEngagedByAirDefence()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        var c0 = Cell.Of(j.Pos);
        w.Spawn(2, "manpads", c0.X + 10, c0.Y + 4);
        Kill(w, j, KillCause.Gun);
        var cid = w.Chutes.Single().Id;
        var bursts = new List<AirBurst>();
        RunUntil(w, () => { bursts.AddRange(w.AirBursts); return w.Chutes.Count == 0; }, 1000);
        Assert.DoesNotContain(bursts, b => b.Target == cid);
        Assert.DoesNotContain(w.Projectiles, p => p.Target == cid);
    }

    [Fact]
    public void APilotWhoLandsAmongTheEnemyIsAnOrdinarySoldierThere()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        Kill(w, j, KillCause.Gun);
        w.Run(61); // past his drift: he comes straight down from here
        var spot = Cell.Of(w.Chutes.Single().Pos);
        var enemy = w.Spawn(2, "Rifleman", spot.X + 3, spot.Y);
        enemy.HP = 100_000; // he does not die to the pilot first
        var landed = RunUntil(w, () => w.EjectionEvents.Any(e => e.Kind == EjectionEventKind.Landed), 1000);
        Assert.True(landed > 0);
        var p = w.Unit(w.EjectionEvents.Single(e => e.Kind == EjectionEventKind.Landed).Unit)!;
        Assert.Equal(45, p.HP); // untouched while he came down, with an enemy soldier 3 cells away
        Assert.True(RunUntil(w, () => !p.Alive || p.HP < 45, 400) > 0, "on the ground the enemy shoots him");
    }

    // ---- Landing edge cases ----

    [Fact]
    public void APilotComingDownOnABuildingOrRockLandsOnTheNearestFreeCell()
    {
        // Where does he come down? (The pilot's path depends only on where and how the jet was lost.)
        Vec at;
        {
            var w = Arena.NewSentinel(N);
            var j = Flying(w, 60, 120);
            Kill(w, j, KillCause.Gun);
            RunUntil(w, () => w.Chutes.Count == 0, 1000);
            at = w.Units.Single(u => u.Def.Archetype.Id == "pilot").Pos;
        }
        // A building over the spot: he lands just outside its footprint.
        {
            var w = Arena.NewSentinel(N);
            var j = Flying(w, 60, 120);
            var c = Cell.Of(at);
            var depot = w.Spawn(2, "supply_depot", c.X - 1, c.Y - 1);
            Kill(w, j, KillCause.Gun);
            RunUntil(w, () => w.Chutes.Count == 0, 1000);
            var p = w.Units.Single(u => u.Def.Archetype.Id == "pilot");
            var o = World.FootprintOrigin(depot);
            var pc = Cell.Of(p.Pos);
            var inside = pc.X >= o.X && pc.X < o.X + depot.Def.Archetype.FootprintW && pc.Y >= o.Y && pc.Y < o.Y + depot.Def.Archetype.FootprintH;
            Assert.False(inside, "not inside the building");
            Assert.InRange(Flat(p.Pos, at), Cell1 / 2, 8 * Cell1);
        }
        // Rock all round the spot (5 x 5 cells): the nearest open cell.
        {
            var w = Arena.NewSentinel(N);
            var c = Cell.Of(at);
            for (var y = -2; y <= 2; y++)
            {
                for (var x = -2; x <= 2; x++)
                {
                    w.Grid.Set(new Cell(c.X + x, c.Y + y), Terrain.Blocked);
                }
            }
            var j = Flying(w, 60, 120);
            Kill(w, j, KillCause.Gun);
            RunUntil(w, () => w.Chutes.Count == 0, 1000);
            var p = w.Units.Single(u => u.Def.Archetype.Id == "pilot");
            Assert.True(w.Grid.Passable(Cell.Of(p.Pos)));
            Assert.Equal(3, Math.Max(Math.Abs(Cell.Of(p.Pos).X - c.X), Math.Abs(Cell.Of(p.Pos).Y - c.Y)));
        }
        // Nowhere to stand within 16 cells: he is lost.
        {
            var w = Arena.NewSentinel(N);
            var c = Cell.Of(at);
            for (var y = -17; y <= 17; y++)
            {
                for (var x = -17; x <= 17; x++)
                {
                    w.Grid.Set(new Cell(c.X + x, c.Y + y), Terrain.Blocked);
                }
            }
            var j = Flying(w, 60, 120);
            Kill(w, j, KillCause.Gun);
            var events = new List<EjectionEvent>();
            RunUntil(w, () => { events.AddRange(w.EjectionEvents); return w.Chutes.Count == 0; }, 1000);
            Assert.Equal(EjectionEventKind.Lost, events[^1].Kind);
            Assert.DoesNotContain(w.Units, u => u.Def.Archetype.Id == "pilot");
        }
    }

    [Fact]
    public void APilotEjectingAtTheMapsEdgeStaysOnTheMap()
    {
        var w = Arena.NewSentinel(N);
        var j = Flying(w, 60, 120);
        Kill(w, j, KillCause.Gun, new Vec(Fixed.FromInt(N) - Fixed.FromRaw(100), j.Pos.Y)); // at the east edge, heading east
        RunUntil(w, () => w.Chutes.Count == 0 && w.Wrecks.Count == 0, 1000);
        var p = w.Units.Single(u => u.Def.Archetype.Id == "pilot");
        Assert.InRange(p.Pos.X.Raw, 0, (long)N * Cell1 - 1);
    }

    // ---- The technical's heavy MG against low aircraft (decision D5) ----

    [Fact]
    public void TheTechnicalsHeavyMgGunsDownALowJetAndThePilotEjects()
    {
        var w = AirToGroundDuel();
        var tech = w.SpawnCrewed(2, "technical", 140, 120).Vehicle;
        tech.HP = 100_000; // it outlives the strafing
        var m = w.Spawn(1, "Multirole 500 lb", 60, 120, Facing.East);
        foreach (var x in m.Def.Weapons.Where(x => x.Stock >= 0))
        {
            m.MutableCharges[x.Stock] = x.Effect.AirGun is null ? 0 : 1000; // the gun only: strafing runs, low, until it is shot down
        }
        m.HP = 1;
        m.Lifetime = 100_000; // fuel for many passes
        w.Step();
        w.Order(1, CommandKind.Attack, [m], target: tech.Id); // it dives at the technical
        var bursts = new List<GunBurst>();
        var log = new Log();
        Assert.True(RunUntil(w, () =>
        {
            bursts.AddRange(w.GunBursts);
            log.Take(w);
            return log.Losses.Count > 0;
        }, 8000) > 0, $"the technical guns the strafing jet down ({bursts.Count(b => b.Shooter == tech.Id)} bursts)");
        Assert.Equal((m.Id, KillCause.Gun, tech.Id), (log.Losses[0].Aircraft, log.Losses[0].Cause, log.Losses[0].Killer));
        var shots = bursts.Where(b => b.Shooter == tech.Id).ToList();
        Assert.All(shots, b => Assert.True(b.AtAltitude.Raw < 40 * Metre, "only below 40 m"));
        Assert.All(shots, b => Assert.InRange(World.Len3(b.At.X.Raw - b.From.X.Raw, b.At.Y.Raw - b.From.Y.Raw, b.AtAltitude.Raw - b.FromAltitude.Raw), 0, 6 * Cell1 + Cell1));
        Assert.Single(w.Chutes); // a gun kill: the pilot ejects
    }

    [Fact]
    public void TheHeavyMgNeverFiresAtAircraftAboveItsCeiling()
    {
        var w = AirToGroundDuel();
        var tech = w.SpawnCrewed(2, "technical", 120, 120).Vehicle;
        var m = w.Spawn(1, "Multirole 500 lb", 80, 120, Facing.East); // at 60 m, straight over it
        var wd = w.Spawn(1, "watcher_drone", 120, 120);
        var bursts = new List<GunBurst>();
        for (var t = 0; t < 400; t++)
        {
            w.Step();
            bursts.AddRange(w.GunBursts);
        }
        Assert.DoesNotContain(bursts, b => b.Shooter == tech.Id);
        Assert.True(m.Alive && wd.Alive);
    }

    // ---- Content ----

    [Theory]
    [InlineData("\"crash\": { \"warhead\": \"crash\",", "\"crash\": { \"warhead\": \"fireball\",", "loss.crash: unknown warhead")]
    [InlineData("\"eject\": { \"unit\": \"pilot\",", "\"eject\": { \"unit\": \"technical\",", "must be a soldier on foot")]
    [InlineData("\"eject\": { \"unit\": \"pilot\",", "\"eject\": { \"unit\": \"ace\",", "unknown archetype")]
    [InlineData("\"chuteTicks\": 44,", "\"chuteTicks\": 4,", "loss.eject.chuteTicks")]
    public void BadLossDataIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(x => x.Json.Contains(from, StringComparison.Ordinal));
        Assert.True(i >= 0, $"no file has {from}");
        var json = sources[i].Json;
        var at = json.IndexOf(from, StringComparison.Ordinal);
        sources[i] = sources[i] with { Json = string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)) };
        var e = Assert.ThrowsAny<Exception>(() => SimCore.Content.ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothJetsCrashAndEjectWithThePlansNumbers()
    {
        foreach (var id in new[] { "multirole_jet", "fighter_jet" })
        {
            var a = TestContent.Mvp.Archetype(id)!;
            var cr = a.Loss!.Crash!;
            var ej = a.Loss.Eject!;
            Assert.Equal(("crash", 150, 30L * Cell1 / 10, 40, 50), (TestContent.Mvp.Warheads[cr.Warhead].Id, cr.Damage, cr.Splash.Raw, cr.BreakUp, cr.BreakUpDrag));
            Assert.Equal(("pilot", 40 * Metre, 20, 44, 6 * Metre), (TestContent.Mvp.Archetypes[ej.Unit].Id, ej.SeatRiseRaw, ej.RiseTicks, ej.ChuteTicks, ej.ChuteRateRaw));
        }
        Assert.Null(TestContent.Mvp.Archetype("watcher_drone")!.Loss);
    }

    // ---- Determinism ----

    [Fact]
    public void LossesAreDeterministic()
    {
        static (List<ulong> Hashes, int Impacts) Run(bool reversed)
        {
            var w = AirToGroundDuel(3);
            var a = w.Spawn(1, "Multirole 500 lb", 50, 100, Facing.East);
            var b = w.Spawn(1, "fighter_jet", 60, 140, Facing.East);
            var c = w.Spawn(2, "fighter_jet", 190, 120, Facing.West);
            var d = w.Spawn(2, "Multirole 2000 lb", 200, 90, Facing.West);
            w.SpawnCrewed(2, "technical", 120, 120);
            w.SpawnCrewed(1, "sam_vehicle", 40, 120);
            foreach (var u in new[] { a, b, c, d })
            {
                u.HP = 60; // short fights: many losses
            }
            var order = reversed ? new[] { d, c, b, a } : new[] { a, b, c, d };
            foreach (var u in order)
            {
                w.Issue(new Command { Tick = 0, Player = u.Owner, Kind = CommandKind.AirTask, Units = [u.Id], Mission = AirTaskKind.Guard, Cell = new Cell(30, 50), Cell2 = new Cell(210, 190), Filter = (byte)(AirTargetKinds.Aircraft | AirTargetKinds.Drones | AirTargetKinds.Vehicles) });
            }
            var h = new List<ulong>();
            var impacts = 0;
            for (var t = 0; t < 1600; t++)
            {
                w.Step();
                h.Add(w.Hash());
                impacts += w.WreckImpacts.Count;
            }
            return (h, impacts);
        }
        var one = Run(false);
        Assert.True(one.Impacts > 0, "jets are lost and crash");
        Assert.Equal(one.Hashes, Run(false).Hashes);
        Assert.Equal(one.Hashes, Run(true).Hashes);
    }
}

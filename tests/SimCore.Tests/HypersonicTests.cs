using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The hypersonic missile truck (hypersonic_truck; docs/design.md, "Hypersonic missile truck"): the most powerful unit, one a
// player at a time. Two hypersonic missiles, 4 s apart (a Shift-queued second point takes the second), 150 to 1,500 cells, at
// any ground point seen or not; the weapon's terminal profile (H4: one smooth arc at 125 cells/s, its peak a tenth of the
// range between 100 and 250 m, a curving terminal dive over the last 30 cells at 210 cells/s that weaves); 1,400 on the
// ballistic warhead's table in two rings (4 / 10 cells); the forest lit inside 4 cells; only the Patriot and THAAD types engage
// it, about 5% and 13% a fuse, only before the dive and only near their side's units (HYP-INT); a 5 s incoming warning; every
// launch shows the truck to every enemy for 20 s; a depot reload stowed only.
public class HypersonicTests
{
    private const int Deploy = 200, Stow = 160, Interval = 80, Reload = 1800, Cost = 400, Y = 40;

    private static WeaponEffect Missile => TestContent.Mvp.Component("hypersonic_missiles")!.Effects.OfType<WeaponEffect>().Single();

    // Range: player 1's crewed truck at (5, Y) facing east on an open map of the given size (fog optional, a depot beside it).
    private static (World W, Unit Truck) Range(int size, bool fog = false, bool depot = false, ulong seed = 1)
    {
        var w = Arena.New(size, seed, fog ? s => s with { Fog = true } : null);
        if (depot)
        {
            w.Spawn(1, "supply_depot", 3, Y - 10);
        }
        var (t, _) = w.SpawnCrewed(1, "hypersonic_truck", 5, Y, Facing.East);
        return (w, t);
    }

    private static void Ground(World w, Unit t, Cell at, bool queue = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = t.Owner, Kind = CommandKind.AttackGround, Units = [t.Id], Cell = at, Queue = queue });

    // Flight: what a run of ticks shows: each hypersonic missile's launch tick, the events (with the step's tick) and the detonations.
    private sealed class Flight
    {
        public List<(uint Id, uint Tick, Vec Dest)> Launches { get; } = [];
        public List<(uint Tick, IncomingEvent E)> Events { get; } = [];
        public List<(uint Tick, Detonation D)> Blasts { get; } = [];
    }

    private static Flight Watch(World w, int ticks, Action<World>? each = null)
    {
        var f = new Flight();
        for (var i = 0; i < ticks; i++)
        {
            var tick = w.Tick;
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Weapon.Effect.Terminal is not null && !f.Launches.Exists(l => l.Id == p.Id))
                {
                    f.Launches.Add((p.Id, tick, p.Dest));
                }
            }
            f.Events.AddRange(w.IncomingEvents.Select(e => (tick, e)));
            f.Blasts.AddRange(w.Detonations.Select(d => (tick, d)));
            each?.Invoke(w);
        }
        return f;
    }

    // ---- Content ----

    [Fact]
    public void TheContentNumbers()
    {
        var c = TestContent.Mvp;
        var a = c.Archetype("hypersonic_truck")!;
        Assert.Equal("Hypersonic missile truck", a.Name);
        Assert.Equal(4000, a.Cost);
        Assert.Equal(1800, a.BuildTime); // 90 s
        Assert.Equal(500, a.MaxHp);
        Assert.Equal(c.Archetype("ballistic_truck")!.Armor, a.Armor); // medium_vehicle
        Assert.Equal(Fixed.Frac(14, 200), a.Speed); // 1.4 cells a second
        Assert.Equal((Deploy, Stow), (a.DeployTicks, a.StowTicks));
        Assert.Equal('\\', a.Glyph);
        Assert.True(c.Factions.Single(f => f.Id == "resistance").Archetypes[a.Index]);
        Assert.False(c.Factions.Single(f => f.Id == "sentinel").Archetypes[a.Index]);
        var g = c.ExclusiveGroups[a.Exclusive];
        Assert.Equal(("hypersonic", 1, false), (g.Id, g.Max, g.LockType));

        var m = Missile;
        Assert.Equal(1400, m.Damage);
        Assert.Equal("ballistic", m.Warhead.Id);
        Assert.Equal((Fixed.FromInt(150), Fixed.FromInt(1500)), (m.MinRange, m.Range));
        Assert.Equal((Fixed.FromInt(10), Fixed.FromInt(4), 30), (m.Splash, m.SplashInner, m.SplashOuterPercent));
        Assert.Equal(Fixed.FromInt(1), m.Scatter);
        Assert.Equal((2, 1), (m.Ammo, m.SalvoSize));
        Assert.Equal(400, m.RevealTicks); // 20 s
        Assert.Equal(InterceptClass.Hypersonic, m.Intercept);
        Assert.Equal(new DepotReload(Reload, 20, Cost, true), m.DepotReload);
        Assert.Equal(Fixed.Frac(1250, 200), m.ProjectileSpeed);
        var tf = m.Terminal!;
        Assert.Equal((30, 10, 100L * 512, 250L * 512, 20), (tf.ClimbTicks, tf.PeakPercent, tf.PeakMinRaw, tf.PeakMaxRaw, tf.PeakAtPercent));
        Assert.Equal((Fixed.FromInt(30), 1200, Fixed.Frac(2100, 200)), (tf.DiveFrom, tf.DiveSlopePermille, tf.DiveSpeed));
        Assert.Equal((Fixed.Frac(15, 10).Raw, 1, 100), (tf.WeaveRaw, tf.Weaves, tf.WarnTicks));
        var fire = m.Fire!;
        Assert.Equal((Fixed.FromInt(4), WarheadFire.Every, 100), (fire.Radius, fire.CountMin, fire.SpreadPercent));
    }

    [Fact]
    public void TheLongestRangeAndTheFastestProjectile()
    {
        var m = Missile;
        var others = TestContent.Mvp.Components.Where(k => k.Id != "hypersonic_missiles").SelectMany(k => k.Effects.OfType<WeaponEffect>()).ToList();
        // The longest straight shot of the Resistance: the cruise missile's range is its route (up to 2,000 cells of legs), not a
        // reach; the Sentinel's Jericho type (2,500 cells, slower) reaches farther.
        var jericho = TestContent.Mvp.Component("jericho_missile")!.Effects.OfType<WeaponEffect>().Single();
        Assert.All(others.Where(o => o.Cruise is null && o != jericho), o => Assert.True(o.Range < m.Range, $"range {o.Range}"));
        Assert.True(jericho.Terminal!.DiveSpeed < m.ProjectileSpeed, "the Jericho type is slower all the way");
        Assert.All(others, o => Assert.True(o.ProjectileSpeed < m.Terminal!.DiveSpeed, $"speed {o.ProjectileSpeed}")); // top speed above all
        Assert.All(others.Where(o => o.Interceptor is not null), o => Assert.True(o.ProjectileSpeed < m.ProjectileSpeed, "cruise above every interceptor"));
    }

    // ---- Range and orders ----

    [Fact]
    public void ItTakesPointsFromOneHundredFiftyToFifteenHundredCells()
    {
        var (w, t) = Range(1600);
        Assert.Equal(World.GroundShot.TooClose, w.ShotAt(t, new Cell(5 + 148, Y).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(t, new Cell(5 + 152, Y).Center()));
        Assert.Equal(World.GroundShot.InRange, w.ShotAt(t, new Cell(5 + 1498, Y).Center()));
        Assert.Equal(World.GroundShot.OutOfRange, w.ShotAt(t, new Cell(5 + 1502, Y).Center()));

        Ground(w, t, new Cell(5 + 148, Y)); // too close: it never fires
        Assert.Empty(Watch(w, Deploy + 100).Launches);
        Assert.Equal(2, t.Charges[0]);
    }

    [Fact]
    public void ItFiresAtBothEndsOfItsBand()
    {
        foreach (var d in new[] { 152, 1498 })
        {
            var (w, t) = Range(d + 20);
            Ground(w, t, new Cell(5 + d, Y));
            var f = Watch(w, Deploy + 30 + d / 6 + 60);
            Assert.NotEmpty(f.Launches);
            Assert.Contains(f.Blasts, b => b.D.Projectile == f.Launches[0].Id); // and it got there
        }
    }

    [Fact]
    public void ItFiresAtAnUnseenGroundPointButAnUnseenUnitNeedsSight()
    {
        var (w, t) = Range(260, fog: true);
        var enemy = w.Spawn(2, "worker", 205, Y); // 200 cells off, far outside its sight
        w.Run(3);
        Assert.False(World.IsVisibleTo(enemy, 1));
        w.Order(1, CommandKind.Attack, [t], enemy.Id);
        Assert.Empty(Watch(w, Deploy + 60).Launches);
        Ground(w, t, new Cell(205, Y + 20));
        Assert.NotEmpty(Watch(w, Deploy + 60).Launches);
    }

    [Fact]
    public void TwoMissilesFourSecondsApartEachAtItsOwnPoint()
    {
        var (w, t) = Range(260);
        Ground(w, t, new Cell(200, Y));
        w.Step();
        Ground(w, t, new Cell(220, Y + 30), queue: true); // Shift+G: the second point
        var f = Watch(w, Deploy + Interval + 200);
        Assert.Equal(2, f.Launches.Count);
        Assert.True(f.Launches[0].Tick >= Deploy, "it fires once set up");
        Assert.Equal(Interval, (int)(f.Launches[1].Tick - f.Launches[0].Tick));
        Assert.True(Near(f.Launches[0].Dest, new Cell(200, Y).Center(), 1));
        Assert.True(Near(f.Launches[1].Dest, new Cell(220, Y + 30).Center(), 1));
        Assert.Equal(0, t.Charges[0]);
        Assert.Empty(Watch(w, 400).Launches); // two, and no more
    }

    [Fact]
    public void BothAtTheOnePointWithoutAQueue()
    {
        var (w, t) = Range(260);
        Ground(w, t, new Cell(200, Y));
        var f = Watch(w, Deploy + Interval + 200);
        Assert.Equal(2, f.Launches.Count);
        Assert.All(f.Launches, l => Assert.True(Near(l.Dest, new Cell(200, Y).Center(), 1)));
    }

    // ---- The cap ----

    [Fact]
    public void OneAPlayerAtATime()
    {
        var w = Arena.New(80);
        var g = w.Spawn(1, "vehicle_garage", 20, 10);
        var a = w.Content.Archetype("hypersonic_truck")!;
        void Produce() => w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [g.Id], Unit = "hypersonic_truck" });
        Produce();
        Produce(); // refused: one queued or being built counts
        w.Step();
        Assert.Single(g.Bays, b => b.Def >= 0);
        Assert.Empty(g.ProductionQueue);
        Assert.Equal(ExclusiveRefusal.AtMax, w.ExclusiveOf(1, a)!.Value.Refusal);
        Assert.True(w.ExclusiveOf(2, a)!.Value.Allowed); // the other player's own cap
        w.Run(1800 + 200);
        var made = Assert.Single(w.All("hypersonic_truck"));
        Produce();
        w.Step();
        Assert.Empty(g.ProductionQueue);
        Assert.DoesNotContain(g.Bays, b => b.Def >= 0);
        made.HP = 0; // lost: another may be built (no type lock)
        w.Run(2);
        Assert.True(w.ExclusiveOf(1, a)!.Value.Allowed);
        Produce();
        w.Step();
        Assert.Single(g.Bays, b => b.Def >= 0);
    }

    // ---- The flight ----

    [Fact]
    public void TheDrawnPathIsTheFlightAndTheWeaveLandsOnThePoint()
    {
        var (w, t) = Range(420);
        var aim = new Cell(400, Y);
        Ground(w, t, aim);
        Projectile? p = null;
        while (p is null)
        {
            w.Step();
            p = w.Projectiles.FirstOrDefault(q => q.Weapon.Effect.Terminal is not null);
        }
        // From its first tick, the whole drawn path is known: every tick the missile is where the query said it would be.
        var start = w.Tick;
        var impact = w.ImpactTickOf(p)!.Value;
        var drawn = new List<FlightPoint>();
        for (var k = start; k <= impact; k++)
        {
            drawn.Add(w.FlightPointAt(p, k)!.Value);
        }
        var phases = new List<TerminalPhase>();
        Detonation? blast = null;
        for (var k = start; blast is null; k++)
        {
            var at = drawn[(int)(k - start)];
            Assert.Equal((at.Pos, at.Altitude), (p.Pos, p.Altitude));
            phases.Add(World.TerminalPhaseOf(p)!.Value);
            var tick = w.Tick;
            w.Step();
            blast = w.Detonations.Cast<Detonation?>().FirstOrDefault(d => d!.Value.Projectile == p.Id);
            if (blast is not null)
            {
                Assert.Equal(impact, tick);
            }
        }
        Assert.Equal([TerminalPhase.Climb, TerminalPhase.Cruise, TerminalPhase.Dive], phases.Distinct());
        Assert.Equal(Missile.Terminal!.ClimbTicks, phases.Count(x => x == TerminalPhase.Climb)); // ages 1 to 30
        // The climb goes up its arc (no longer straight up); the peak is a tenth of the range, at least 100 m (a flat map).
        Assert.All(drawn.Where(x => x.Phase == TerminalPhase.Climb).Skip(1), x => Assert.True(x.Pos.X > p.Origin.X));
        var peak = p.LaunchHeight.Raw + 100L * 512; // 395 cells: a tenth is 79 m, under the floor
        Assert.Equal(peak, p.ArcRise.Raw);
        Assert.InRange(drawn.Max(x => x.Altitude.Raw), peak - 64, peak); // drawn a tick apart: within a few raw of the peak
        // The weave: off its line in the dive (between ticks too), within 1.5 cells, and back on it at the point (its scatter
        // only: up to a cell per axis).
        long most = 0;
        var line = p.Dest - p.Origin;
        foreach (var x in drawn.Where(x => x.Phase == TerminalPhase.Dive))
        {
            var off = Math.Abs(((x.Pos - p.Origin).X.Raw * line.Y.Raw - (x.Pos - p.Origin).Y.Raw * line.X.Raw) / line.Len().Raw);
            most = Math.Max(most, off);
        }
        Assert.InRange(most, 1, Fixed.Frac(15, 10).Raw);
        Assert.True(Math.Abs(blast!.Value.At.X.Raw - aim.Center().X.Raw) <= Fixed.OneRaw);
        Assert.True(Math.Abs(blast.Value.At.Y.Raw - aim.Center().Y.Raw) <= Fixed.OneRaw);
        Assert.Equal(p.Dest, blast.Value.At);
    }

    [Fact]
    public void TheWeaveIsSeededPerShotAndTheSameEveryRun()
    {
        static List<long> Offsets(ulong seed)
        {
            var (w, t) = Range(300, seed: seed);
            Ground(w, t, new Cell(280, Y));
            Projectile? p = null;
            while (p is null)
            {
                w.Step();
                p = w.Projectiles.FirstOrDefault(q => q.Weapon.Effect.Terminal is not null);
            }
            var line = p.Dest - p.Origin;
            var impact = w.ImpactTickOf(p)!.Value;
            var offs = new List<long>();
            for (var k = w.Tick; k <= impact; k++)
            {
                for (var pm = 0; pm < 1000; pm += 125)
                {
                    var x = w.FlightPointAt(p, k, pm)!.Value;
                    if (x.Phase == TerminalPhase.Dive)
                    {
                        offs.Add(((x.Pos - p.Origin).X.Raw * line.Y.Raw - (x.Pos - p.Origin).Y.Raw * line.X.Raw) / line.Len().Raw);
                    }
                }
            }
            return offs;
        }
        var a = Offsets(1);
        Assert.Equal(a, Offsets(1));
        Assert.Contains(a, o => o != 0);
        var seeds = Enumerable.Range(2, 6).Select(s => Offsets((ulong)s)).ToList();
        Assert.Contains(seeds, o => !o.SequenceEqual(a)); // another shot weaves another way
        Assert.All(seeds.Append(a), o => Assert.All(o, x => Assert.InRange(Math.Abs(x), 0, Fixed.Frac(15, 10).Raw + Fixed.OneRaw / 8)));
    }

    // ---- The arc (H4) ----

    // Fly: player 1's truck fires one missile `cells` east on an open map; the world at its launch and the missile.
    private static (World W, Projectile P) Fly(int cells, ulong seed = 1)
    {
        var (w, t) = Range(cells + 40, seed: seed);
        Ground(w, t, new Cell(5 + cells, Y));
        Projectile? p = null;
        while (p is null)
        {
            w.Step();
            p = w.Projectiles.FirstOrDefault(q => q.Weapon.Effect.Terminal is not null);
        }
        return (w, p);
    }

    // Samples: its drawn flight from now to the impact every tenth of a tick: the way along its line (raw), the height, the phase.
    private static List<(long S, long Z, TerminalPhase Phase)> Samples(World w, Projectile p)
    {
        var line = p.Dest - p.Origin;
        var len = line.Len().Raw;
        var impact = w.ImpactTickOf(p)!.Value; // the step at this tick takes it onto its point
        var list = new List<(long, long, TerminalPhase)>();
        void Add(uint k, int pm)
        {
            var x = w.FlightPointAt(p, k, pm)!.Value;
            var d = x.Pos - p.Origin;
            list.Add((Math.Min(len, (d.X.Raw * line.X.Raw + d.Y.Raw * line.Y.Raw) / len), x.Altitude.Raw, x.Phase));
        }
        for (var k = w.Tick - 1; k <= impact; k++)
        {
            for (var pm = 0; pm < 1000; pm += 100)
            {
                Add(k, pm);
            }
        }
        Add(impact + 1, 0);
        return list;
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(800)]
    [InlineData(1200)]
    [InlineData(1495)]
    public void OneSmoothArcUpThenDownItsPeakATenthOfTheRangeWithinTheCap(int cells)
    {
        var (w, p) = Fly(cells);
        var tf = Missile.Terminal!;
        var path = Samples(w, p);
        var total = (p.Dest - p.Origin).Len().Raw;
        Assert.Equal((0L, p.LaunchHeight.Raw), (path[0].S, path[0].Z)); // the first point is the launcher's
        Assert.Equal((total, p.LandHeight.Raw), (path[^1].S, path[^1].Z)); // the last its point
        // Its peak: a tenth of the range over the ground (flat: over the launch height), at least 100 m, at most 250 m, a fifth
        // of the way to the dive.
        var peak = p.LaunchHeight.Raw + Math.Clamp(total / 10, 100L * 512, 250L * 512);
        var arc = total - tf.DiveFrom.Raw;
        Assert.Equal(peak, World.FlightHeightAt(p, tf, arc / 5));
        Assert.InRange(path.Max(x => x.Z), peak - 16, peak);
        var top = path.FindIndex(x => x.Z == path.Max(y => y.Z));
        Assert.InRange(path[top].S, arc / 5 - arc / 40 - Fixed.OneRaw, arc / 5 + Fixed.OneRaw); // its top is flat to the raw unit a little before
        // Up, then down: never under its point's height (the ground) before the impact.
        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(i <= top ? path[i].Z >= path[i - 1].Z : path[i].Z <= path[i - 1].Z, $"monotonic at {i}");
            Assert.True(path[i].S >= path[i - 1].S, "always forward");
            Assert.True(path[i].Z >= p.LandHeight.Raw);
        }
        // Off the truck steeply but tilted: the first tenth of a tick rises more than it goes forward, and it does go forward.
        var (s1, z1) = (path[1].S, path[1].Z - path[0].Z);
        Assert.True(s1 > 0 && z1 > s1, $"launch {z1} up over {s1}");
        // Smooth: the slope (height over way, thousandths) changes little between neighbouring tenths of a tick.
        long? last = null;
        for (var i = 1; i < path.Count; i++)
        {
            var ds = path[i].S - path[i - 1].S;
            if (ds < Fixed.OneRaw / 4)
            {
                continue;
            }
            var slope = (path[i].Z - path[i - 1].Z) * 1000 / ds;
            Assert.True(last is null || Math.Abs(slope - last.Value) <= 400, $"slope {last} then {slope} at {i}");
            last = slope;
        }
    }

    // The old flight (H1, to 05e72a9): ClimbTicks straight up, then the projectile speed to the dive and the dive speed in it.
    private static int OldImpactAge(long total)
    {
        var tf = Missile.Terminal!;
        long vc = Missile.ProjectileSpeed.Raw, vd = tf.DiveSpeed.Raw, cruise = total - Math.Min(tf.DiveFrom.Raw, total);
        for (var u = 1L; ; u++)
        {
            var reach = u * vc;
            if (Math.Min(reach <= cruise ? reach : cruise + (reach - cruise) * vd / vc, total) >= total)
            {
                return (int)(tf.ClimbTicks + u);
            }
        }
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(800)]
    [InlineData(1495)]
    public void TheFlightIsNoLongerThanBeforeAndNeverSlowerThanItsCruise(int cells)
    {
        var (w, p) = Fly(cells);
        var total = (p.Dest - p.Origin).Len().Raw;
        var flight = (int)(w.ImpactTickOf(p)!.Value - w.Tick + p.Age + 1); // ticks from the launch to the impact
        Assert.Equal(OldImpactAge(total) - Missile.Terminal!.ClimbTicks, flight); // the old climb's 1.5 s shorter
        // Its speed (in space) is never under 125 cells/s: the fastest unit still.
        var impact = w.ImpactTickOf(p)!.Value;
        for (var k = w.Tick - 1; k < impact; k++)
        {
            var (a, b) = (w.FlightPointAt(p, k)!.Value, w.FlightPointAt(p, k + 1)!.Value);
            var d = b.Pos - a.Pos;
            Assert.True(AirDefenseLen(d.X.Raw, d.Y.Raw, b.Altitude.Raw - a.Altitude.Raw) >= Missile.ProjectileSpeed.Raw - 2, $"tick {k}");
        }
    }

    private static long AirDefenseLen(long x, long y, long z) => Fixed.Sqrt(x * x + y * y + z * z);

    [Fact]
    public void TheTerminalDiveStartsAtTheSeparationPointAndEndsSteeperThanItsChord()
    {
        var (w, p) = Fly(800);
        var tf = Missile.Terminal!;
        var path = Samples(w, p);
        var total = (p.Dest - p.Origin).Len().Raw;
        var dive = path.Where(x => x.Phase == TerminalPhase.Dive).ToList();
        Assert.All(dive, x => Assert.True(x.S >= total - tf.DiveFrom.Raw - 2));
        Assert.DoesNotContain(path, x => x.Phase != TerminalPhase.Dive && x.S >= total - tf.DiveFrom.Raw);
        // The separation point: 30 cells out, 36 cells (72 m) over its point (the 1200 thousandths chord).
        var sep = World.FlightHeightAt(p, tf, total - tf.DiveFrom.Raw);
        Assert.Equal(p.LandHeight.Raw + tf.DiveFrom.Raw * 1200 / 1000, sep);
        // Its last stretch is steeper than its chord (50 degrees) and than the arc before the dive.
        var b = dive[^1];
        var a = dive.Last(x => x.S < b.S - Fixed.OneRaw / 2); // the last tenth of a tick that moves (the last tick may reach the point early)
        Assert.True((a.Z - b.Z) * 1000 / (b.S - a.S) > 2000, "over 63 degrees at the end");
        var before = path.FindLastIndex(x => x.Phase != TerminalPhase.Dive);
        Assert.True((path[before - 1].Z - path[before].Z) * 1000 / (path[before].S - path[before - 1].S) < 400, "a gentle fall to the separation point");
        // The warning still fires 5 s before it lands.
        var f = Watch(w, 400);
        var warned = f.Events.Single(e => e.E.Kind == IncomingEventKind.Warned && e.E.Projectile == p.Id);
        Assert.Equal(100u, f.Events.Single(e => e.E.Kind == IncomingEventKind.Struck && e.E.Projectile == p.Id).Tick - warned.Tick);
    }

    // ---- Damage ----

    [Fact]
    public void ATankInTheInnerRingDiesAndTheOuterRingTakesThirtyPercent()
    {
        var w = Arena.NewSentinel(260);
        var (t, _) = w.SpawnCrewed(2, "hypersonic_truck", 5, 100, Facing.East);
        var aim = new Cell(200, 100);
        var tank = w.Spawn(1, "main_battle_tank", 200, 102); // 2 cells off the point (scatter at most 1.4)
        var carrier = w.Spawn(1, "light_tank", 207, 100);    // 7 cells off: the outer ring
        var soldier = w.Spawn(1, "Rifleman", 213, 100);      // 13 cells off: outside
        var hp = carrier.HP;
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [t.Id], Cell = aim });
        var f = Watch(w, Deploy + 80);
        Assert.Single(f.Launches);
        Assert.Single(f.Blasts);
        Assert.False(tank.Alive); // 1,400 x 170% x the top armour's 150%: 3,570 of its 2,500
        Assert.True(carrier.Alive);
        Assert.True(carrier.HP < hp, "the outer ring hurt it");
        Assert.Equal(soldier.Def.MaxHp, soldier.HP);
    }

    [Fact]
    public void ABuildingTakesTwoTwentyPercent()
    {
        var (w, t) = Range(260);
        var depot = w.Spawn(2, "supply_depot", 200, Y);
        depot.HP = 1_000_000;
        Ground(w, t, new Cell(200, Y));
        Watch(w, Deploy + 80);
        Assert.Equal(1_000_000 - 1400 * 220 / 100, depot.HP);
    }

    // ---- Fire ----

    [Fact]
    public void ItSetsTheForestInsideItsFourCellRingOnFire()
    {
        const int n = 240;
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var flags = new byte[n * n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 150; x < n; x++)
            {
                flags[y * n + x] = 3; // dense forest round the point
            }
        }
        var w = Arena.New(n);
        w.AttachTerrain(new TerrainMap(n, n, new int[(n + 1) * (n + 1)], flags, rules));
        var (t, _) = w.SpawnCrewed(1, "hypersonic_truck", 5, Y, Facing.East);
        Ground(w, t, new Cell(200, Y));
        var lit = new List<FireEvent>();
        var f = Watch(w, Deploy + 80, ww => lit.AddRange(ww.FireEvents.Where(e => e.Kind == FireEventKind.Ignited)));
        var at = f.Blasts.Single().D.At;
        var first = lit.Where(e => (e.Cell.Center() - at).Len().Raw <= 5 * Fixed.OneRaw).ToList();
        Assert.True(first.Count >= 30, $"{first.Count} cells lit round the blast"); // every forest cell within 4 cells: about 50
        Assert.All(first, e => Assert.True(e.Spreads)); // normal spread
        w.Run(200);
        Assert.True(w.Fires.Count > first.Count / 2, "the fire burns on and spreads");
        Assert.Contains(w.Fires, x => (x.Cell.Center() - at).Len().Raw > 6 * Fixed.OneRaw);
    }

    // ---- Interception ----

    [Fact]
    public void ShortRangeAirDefenceNeverEngagesIt()
    {
        var c = TestContent.Mvp;
        foreach (var id in new[] { "igla", "sam_missiles", "kub_missiles" })
        {
            var ic = c.Component(id)!.Effects.OfType<WeaponEffect>().Single(e => e.Interceptor is not null).Interceptor!;
            Assert.False(ic.Engages(InterceptClass.Hypersonic), id);
        }
        foreach (var id in new[] { "patriot_missiles", "thaad_missiles" })
        {
            Assert.True(c.Component(id)!.Effects.OfType<WeaponEffect>().Single().Interceptor!.Engages(InterceptClass.Hypersonic), id);
        }

        // Live: the Osa type, the SA-6 type with its radar and an Igla soldier under its path and at its point fire nothing.
        var w = Arena.New(260);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 150, Y + 3).Vehicle;
        var radar = w.SpawnCrewed(2, "mobile_radar", 190, Y + 6).Vehicle;
        var kub = w.SpawnCrewed(2, "kub_launcher", 195, Y + 6).Vehicle;
        w.Spawn(2, "manpads", 198, Y + 3);
        w.Order(2, CommandKind.Deploy, [sam, radar, kub]);
        w.Run(200);
        var (t, _) = w.SpawnCrewed(1, "hypersonic_truck", 5, Y, Facing.East);
        Ground(w, t, new Cell(200, Y));
        var launched = 0;
        var f = Watch(w, Deploy + Interval + 200, ww => launched += ww.Projectiles.Count(p => p.Weapon.Effect.Interceptor is not null));
        Assert.Equal(2, f.Launches.Count);
        Assert.Equal(0, launched);
    }

    // Engage: player 1's Patriot or THAAD type and its radar truck set up at (200, 200), player 2's truck `distance` cells north
    // fires both missiles at the battery's side; per seed the fuses (kill chance, killed) and whether any fuse came after its
    // target's dive started, and any interceptor launched at a target already diving.
    private static (List<(int Pk, bool Kill)> Fuses, int Late, int Launched) Engage(string kind, int distance, ulong seed)
    {
        const int X = 200, Yb = 200;
        var w = Arena.NewSentinel(X + distance + 40, seed);
        var launcher = w.SpawnCrewed(1, kind, X, Yb).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", X + 10, Yb).Vehicle;
        w.Order(1, CommandKind.Deploy, [launcher, r]);
        w.Run(520);
        var truck = w.SpawnCrewed(2, "hypersonic_truck", X, Yb + distance, Facing.South).Vehicle;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(X, Yb + 2));
        var fuses = new List<(int, bool)>();
        var diving = new List<uint>();
        var seen = new List<uint>();
        int late = 0, launched = 0;
        for (var i = 0; i < Deploy + Interval + 400; i++)
        {
            var before = w.Projectiles.Where(p => World.TerminalPhaseOf(p) == TerminalPhase.Dive).Select(p => p.Id).ToList();
            w.Step();
            foreach (var p in w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !seen.Contains(p.Id)))
            {
                seen.Add(p.Id);
                launched++;
                late += before.Contains(p.Target) ? 1 : 0; // launched at a missile already in its dive
            }
            foreach (var b in w.AirBursts.Where(b => b.Kind is AirBurstKind.Kill or AirBurstKind.Miss))
            {
                fuses.Add((b.KillPermille, b.Kind == AirBurstKind.Kill));
                late += diving.Contains(b.Target) ? 1 : 0;
            }
            diving.AddRange(w.IncomingEvents.Where(e => e.Kind == IncomingEventKind.Diving).Select(e => e.Projectile));
        }
        return (fuses, late, launched);
    }

    [Theory]
    [InlineData("patriot_launcher", 30, 70)]
    [InlineData("thaad_launcher", 110, 190)]
    public void ThePatriotAndThaadTypesOnlyBeforeTheDiveAndRarely(string kind, int pkLow, int pkHigh)
    {
        var fuses = new List<(int Pk, bool Kill)>();
        var launched = 0;
        foreach (var (distance, seed) in new[] { 380, 700 }.SelectMany(d => Enumerable.Range(1, 12).Select(s => (d, (ulong)s))))
        {
            var (f, late, n) = Engage(kind, distance, seed);
            Assert.Equal(0, late); // nothing launched at it or going off beside it once it dives
            fuses.AddRange(f);
            launched += n;
        }
        Assert.True(fuses.Count >= 20, $"{fuses.Count} fuses of {launched} launched");
        var mean = fuses.Average(x => x.Pk);
        Assert.InRange(mean, pkLow, pkHigh); // about 5% for the Patriot type, 13% for the THAAD type
        var kills = fuses.Count(x => x.Kill);
        Assert.InRange(kills, 0, (int)(fuses.Count * pkHigh * 2.5 / 1000) + 1);
        Assert.True(fuses.Count - kills > fuses.Count / 2, "most fly on");
    }

    [Theory]
    [InlineData("patriot_launcher")]
    [InlineData("thaad_launcher")]
    public void ABatteryAtThePointEngagesAboutHalfTheMissiles(string kind)
    {
        // The engagement rate over seeds (HYP-INT): with the battery and its radar at the point, it launches at about half the
        // missiles (its single shot at the first is still in the air or reloading when the second, 4 s later, comes in).
        int missiles = 0, engaged = 0;
        foreach (var (distance, seed) in new[] { 380, 700, 1200 }.SelectMany(d => Enumerable.Range(1, 8).Select(s => (d, (ulong)s))))
        {
            var (_, _, launched, targets) = EngageCount(kind, distance, seed);
            missiles += 2;
            engaged += targets;
            Assert.True(launched >= targets);
        }
        Assert.InRange(engaged * 100 / missiles, 35, 85);
    }

    // EngageCount: Engage's run counting the interceptors launched and the missiles they were launched at.
    private static (int Fuses, int Late, int Launched, int Targets) EngageCount(string kind, int distance, ulong seed)
    {
        const int X = 200, Yb = 200;
        var w = Arena.NewSentinel(X + distance + 40, seed);
        var launcher = w.SpawnCrewed(1, kind, X, Yb).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", X + 10, Yb).Vehicle;
        w.Order(1, CommandKind.Deploy, [launcher, r]);
        w.Run(520);
        var truck = w.SpawnCrewed(2, "hypersonic_truck", X, Yb + distance, Facing.South).Vehicle;
        w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(X, Yb + 2));
        var (seen, targets) = (new List<uint>(), new List<uint>());
        for (var i = 0; i < Deploy + Interval + 400; i++)
        {
            w.Step();
            foreach (var p in w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !seen.Contains(p.Id)))
            {
                seen.Add(p.Id);
                if (!targets.Contains(p.Target))
                {
                    targets.Add(p.Target);
                }
            }
        }
        return (0, 0, seen.Count, targets.Count);
    }

    // The client's --demo=hypersonic battery (forest3k, moved 200 cells west and 600 north, flat here): truck B at (70, 100) fires
    // both missiles at (960, 240); the Patriot type at (800, 215), the THAAD type at (800, 232) and the radar truck at (792, 226)
    // stand on its line about 160 cells short of the point. Returns the interceptors launched.
    private static int DemoBattery(ulong seed, bool guardAtThePoint)
    {
        var w = Arena.NewSentinel(1000, seed);
        var pat = w.SpawnCrewed(1, "patriot_launcher", 800, 215).Vehicle;
        var th = w.SpawnCrewed(1, "thaad_launcher", 800, 232).Vehicle;
        var r = w.SpawnCrewed(1, "radar_truck", 792, 226).Vehicle;
        w.Order(1, CommandKind.Deploy, [pat, th, r]);
        if (guardAtThePoint)
        {
            w.Spawn(1, "Rifleman", 962, 244); // a unit of the battery's side within 15 cells of the point
        }
        w.Run(520);
        var b = w.SpawnCrewed(2, "hypersonic_truck", 70, 100, Facing.East).Vehicle;
        w.Order(2, CommandKind.AttackGround, [b], cell: new Cell(960, 240));
        var seen = new List<uint>();
        for (var i = 0; i < Deploy + Interval + 400; i++)
        {
            w.Step();
            seen.AddRange(w.Projectiles.Where(p => p.Weapon.Effect.Interceptor is not null && !seen.Contains(p.Id)).Select(p => p.Id).ToList());
        }
        return seen.Count;
    }

    [Fact]
    public void TheDemoBatteryDefendsOnlyWhatIsNearThePoint()
    {
        // HYP-INT: the Patriot and THAAD types engage a missile or rocket on its arc (the hypersonic missile too) only when it
        // will come down within their defendRadius (15 cells) of a unit or structure of their side (Defended). The demo's
        // second wood has nothing of red's within 15 cells: the battery lets both missiles land, by design, on every seed.
        var seeds = new ulong[] { 2, 3, 4, 5, 42 };
        Assert.All(seeds, s => Assert.Equal(0, DemoBattery(s, false)));
        // One rifleman of red's at the point: the same battery engages on every seed.
        Assert.All(seeds, s => Assert.True(DemoBattery(s, true) > 0, $"seed {s}"));
    }

    [Fact]
    public void AnInterceptedMissileIsGoneAndSaysSo()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var (f, _, _) = Engage("thaad_launcher", 380, seed);
            if (!f.Exists(x => x.Kill))
            {
                continue;
            }
            // The run again, watching the events: an Intercepted event, no Struck for that missile.
            const int X = 200, Yb = 200;
            var w = Arena.NewSentinel(X + 420, seed);
            var launcher = w.SpawnCrewed(1, "thaad_launcher", X, Yb).Vehicle;
            var r = w.SpawnCrewed(1, "radar_truck", X + 10, Yb).Vehicle;
            w.Order(1, CommandKind.Deploy, [launcher, r]);
            w.Run(520);
            var truck = w.SpawnCrewed(2, "hypersonic_truck", X, Yb + 380, Facing.South).Vehicle;
            w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(X, Yb + 2));
            var flight = Watch(w, Deploy + Interval + 400);
            var down = flight.Events.First(e => e.E.Kind == IncomingEventKind.Intercepted).E;
            Assert.DoesNotContain(flight.Events, e => e.E.Kind == IncomingEventKind.Struck && e.E.Projectile == down.Projectile);
            Assert.DoesNotContain(flight.Blasts, b => b.D.Projectile == down.Projectile);
            return;
        }
        Assert.Fail("no THAAD kill in 40 seeds");
    }

    // ---- Counterplay ----

    [Fact]
    public void EveryEnemyIsWarnedFiveSecondsBeforeTheImpact()
    {
        var (w, t) = Range(1060);
        var aim = new Cell(1005, Y);
        Ground(w, t, aim);
        var strikes = new List<(uint Tick, IncomingStrike S)>();
        var f = Watch(w, Deploy + 400, ww => strikes.AddRange(ww.IncomingStrikes.Select(s => (ww.Tick, s))));
        var id = f.Launches[0].Id;
        var launched = f.Events.Single(e => e.E.Kind == IncomingEventKind.Launched && e.E.Projectile == id).Tick;
        var warned = f.Events.Single(e => e.E.Kind == IncomingEventKind.Warned && e.E.Projectile == id);
        var struck = f.Events.Single(e => e.E.Kind == IncomingEventKind.Struck && e.E.Projectile == id).Tick;
        Assert.True(warned.Tick - launched > 50, "a long flight: warned well after the launch");
        Assert.Equal(100u, struck - warned.Tick);
        Assert.Equal(struck, warned.E.ImpactTick);
        Assert.Equal(1u << 2, warned.E.Warned); // every player but the shooter's
        var mine = strikes.Where(s => s.S.Projectile == id).ToList();
        Assert.Equal(100, mine.Count); // listed from the warning to the impact
        Assert.All(mine, s => Assert.Equal((struck, 1u << 2, f.Launches[0].Dest, Fixed.FromInt(10)), (s.S.ImpactTick, s.S.Warned, s.S.At, s.S.Radius)));
        Assert.Equal(warned.Tick + 1, mine[0].Tick);
    }

    [Fact]
    public void AShortFlightIsWarnedAtTheLaunch()
    {
        var (w, t) = Range(260);
        Ground(w, t, new Cell(200, Y));
        var f = Watch(w, Deploy + 80);
        var launched = f.Events.Single(e => e.E.Kind == IncomingEventKind.Launched);
        var warned = f.Events.Single(e => e.E.Kind == IncomingEventKind.Warned);
        Assert.Equal(launched.Tick, warned.Tick);
        Assert.True(f.Events.Single(e => e.E.Kind == IncomingEventKind.Struck).Tick - warned.Tick < 100);
    }

    [Fact]
    public void EveryLaunchShowsTheTruckToEveryEnemyForTwentySeconds()
    {
        var (w, t) = Range(260, fog: true);
        var watcher = w.Spawn(2, "worker", 120, Y + 60); // far off: it never sees the truck itself
        w.Run(3);
        Assert.False(World.IsSpotted(t, 2));
        Ground(w, t, new Cell(200, Y));
        var f = Watch(w, Deploy + 10);
        var first = f.Launches[0].Tick;
        Assert.True(World.IsSpotted(t, 2));
        Assert.True(w.RevealedBy(t, 2));
        w.Run(Interval + 400 - (int)(w.Tick - first) - 2); // the second launch, 4 s later, starts the 20 s again
        Assert.True(World.IsSpotted(t, 2));
        w.Run(20);
        Assert.False(w.RevealedBy(t, 2));
        Assert.False(World.IsSpotted(t, 2));
        Assert.True(watcher.Alive);
    }

    // ---- Reload ----

    [Fact]
    public void ItReloadsOnlyStowedAtADepotNinetySecondsAndFourHundredSupplyEach()
    {
        var (w, t) = Range(260, depot: true);
        var depot = w.All("supply_depot").Single();
        depot.Stock = Math.Max(depot.Stock, 2000);
        Ground(w, t, new Cell(200, Y));
        w.Run(Deploy + Interval + 40);
        Assert.Equal(0, t.Charges[0]);
        w.Run(Stow + 30); // out of missiles at a depot it packs up first
        Assert.Equal(DeployState.Stowed, t.Deploy);
        Assert.Equal(0, t.Charges[0]);
        var stock = depot.Stock;
        w.Run(Reload);
        Assert.Equal(1, t.Charges[0]);
        Assert.Equal(stock - Cost, depot.Stock);
        w.Run(Reload);
        Assert.Equal(2, t.Charges[0]);
        Assert.Equal(stock - 2 * Cost, depot.Stock);
    }

    [Fact]
    public void NoReloadAwayFromADepotOrSetUp()
    {
        var (w, t) = Range(260);
        Ground(w, t, new Cell(200, Y));
        w.Run(Deploy + Interval + 40);
        Assert.Equal(0, t.Charges[0]);
        w.Run(Reload * 2);
        Assert.Equal(0, t.Charges[0]);

        var (w2, t2) = Range(260, depot: true);
        w2.Order(1, CommandKind.Deploy, [t2]);
        w2.Run(Deploy + 2);
        t2.MutableCharges[0] = 1;
        w2.Run(Stow - 20); // idle at a depot it packs up first; nothing reloads until it is stowed
        Assert.NotEqual(DeployState.Stowed, t2.Deploy);
        Assert.Equal(1, t2.Charges[0]);
    }

    // ---- Determinism ----

    [Fact]
    public void ItIsDeterministic()
    {
        static List<ulong> Play(bool fullScan)
        {
            var w = Arena.NewSentinel(460, 5, s => s with { Fog = true });
            w.FullScan = fullScan;
            var launcher = w.SpawnCrewed(1, "thaad_launcher", 200, 200).Vehicle;
            var r = w.SpawnCrewed(1, "radar_truck", 210, 200).Vehicle;
            w.Order(1, CommandKind.Deploy, [launcher, r]);
            w.Spawn(1, "main_battle_tank", 205, 204);
            var truck = w.SpawnCrewed(2, "hypersonic_truck", 200, 420, Facing.South).Vehicle;
            w.Order(2, CommandKind.AttackGround, [truck], cell: new Cell(204, 204));
            w.Step();
            w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.AttackGround, Units = [truck.Id], Cell = new Cell(190, 210), Queue = true });
            var hashes = new List<ulong>();
            for (var i = 0; i < 1000; i++)
            {
                w.Step();
                var h = w.Hash();
                Assert.Equal(w.HashReference(), h); // the batched hash covers the new state as the reference does
                hashes.Add(h);
            }
            return hashes;
        }
        var first = Play(false);
        Assert.Equal(first, Play(false));
        Assert.Equal(first, Play(true));
    }

    private static bool Near(Vec a, Vec b, int cells) =>
        Math.Abs(a.X.Raw - b.X.Raw) <= cells * Fixed.OneRaw && Math.Abs(a.Y.Raw - b.Y.Raw) <= cells * Fixed.OneRaw;
}

using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Air-to-ground (E7; the plan's air-combat model, section 2.10): strike runs (approach, IP, run-in by
// the delivery profile, release, egress, re-attack), the release solution, scatter and guided bombs,
// bomb blasts (direct hits, splash, buildings, runway craters, the airstrike tier), strafing, the owner's
// rules (Guard picks ground targets inside its area only, Patrol never attacks the ground, a strike runs
// on top of the task and the task resumes), determinism.
public class AirToGroundTests
{
    private const int N = 240;

    private static Unit Overhead(World w, string preset, int x, int y, byte player = 1, int facing = Facing.East)
    {
        var d = w.Spawn(player, preset, x, y, facing);
        w.Step();
        return d;
    }

    private static void Give(World w, Unit d, AirTaskKind task, Cell cell, Cell? cell2 = null) =>
        w.Issue(new Command { Tick = w.Tick, Player = d.Owner, Kind = CommandKind.AirTask, Units = [d.Id], Mission = task, Cell = cell, Cell2 = cell2 });

    // A target that survives every bomb (to count passes and damage).
    private static Unit Tough(World w, string kind, int x, int y, byte player = 2)
    {
        var u = kind == "technical" ? w.SpawnCrewed(player, kind, x, y).Vehicle : w.Spawn(player, kind, x, y);
        u.HP = 100_000;
        return u;
    }

    private sealed class Log
    {
        public readonly List<BombRelease> Releases = [];
        public readonly List<BombImpact> Impacts = [];
        public readonly List<StrafeBurst> Strafes = [];
        public readonly List<StrikePhase> Phases = [];
        public bool Afterburner;
        public bool EgressBoost;

        public void Take(World w, Unit u)
        {
            Releases.AddRange(w.BombReleases);
            Impacts.AddRange(w.BombImpacts);
            Strafes.AddRange(w.StrafeBursts);
            if (u.Strike is { } s)
            {
                if (Phases.Count == 0 || Phases[^1] != s.Phase)
                {
                    Phases.Add(s.Phase);
                }
                Afterburner |= s.Phase == StrikePhase.Run && u.Wing?.Throttle == Throttle.Afterburner;
                EgressBoost |= s.Phase == StrikePhase.Break && u.Wing is { Boost: > 0 };
            }
        }
    }

    // Runs w until u's attack order ends (or max ticks), logging its strike.
    private static Log Strike(World w, Unit u, int max = 3000)
    {
        var log = new Log();
        for (var t = 0; t < max; t++)
        {
            w.Step();
            log.Take(w, u);
            if (t > 2 && u.Order.Kind is not (OrderKind.Attack or OrderKind.AttackGround))
            {
                break;
            }
        }
        return log;
    }

    private static long Off(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private const long Cell1 = Fixed.OneRaw;

    // The MVP content with the bombs' delivery edited.
    private static ContentSet Bombs(Func<string, string> edit)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(c => c.Name.EndsWith("components/sentinel.json", StringComparison.Ordinal));
        sources[i] = new ContentSource(sources[i].Name, edit(sources[i].Json));
        return ContentLoader.Load(sources);
    }

    private static string NoScatter(string json)
    {
        var s = json.Replace("\"scatterLevel\": 15, \"scatterDive\": 10, \"scatterToss\": 30", "\"scatterLevel\": 0, \"scatterDive\": 0, \"scatterToss\": 0", StringComparison.Ordinal);
        Assert.NotEqual(json, s);
        return s;
    }

    private static string Profile(string json, string profile) =>
        json.Replace("\"profile\": \"dive\"", $"\"profile\": \"{profile}\"", StringComparison.Ordinal);

    private static World Sentinel(ContentSet content, ulong seed = 1)
    {
        var setup = TestContent.Sentinel(seed).For(content, seed);
        setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup));
    }

    // Both players Sentinel (an enemy airfield to bomb), no starting army.
    private static World Duel(ulong seed = 1)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(N, N), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static int Charges(Unit u, Func<WeaponEffect, bool> which)
    {
        var w = u.Def.Weapons.First(x => which(x.Effect));
        return u.Charges[w.Stock];
    }

    private static void Empty(Unit u, Func<WeaponEffect, bool> which)
    {
        foreach (var w in u.Def.Weapons.Where(x => which(x.Effect) && x.Stock >= 0))
        {
            u.MutableCharges[w.Stock] = 0;
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

    // ---- The tiers and the content ----

    [Theory]
    [InlineData(100, 1)]
    [InlineData(350, 2)] // the air superiority jet's glide bomb
    [InlineData(500, 3)]
    [InlineData(700, 4)] // 500 lb
    [InlineData(1000, 5)]
    [InlineData(1300, 6)] // 2,000 lb
    [InlineData(1800, 7)]
    public void AirstrikeTiersFollowTheDamage(int damage, int tier) => Assert.Equal(tier, World.StrikeTier(damage));

    [Fact]
    public void FreeFallBombsCarryTheirGravityAndTheGunsStrafe()
    {
        foreach (var id in new[] { "bombs_500lb", "bombs_2000lb" })
        {
            var d = TestContent.Mvp.Components.Single(c => c.Id == id).Effects.OfType<WeaponEffect>().Single().Delivery!;
            Assert.Equal(70L * 512, d.GravityRaw); // 70 m/s^2
            Assert.Equal(DeliveryProfile.Dive, d.Profile);
        }
        var glide = TestContent.Mvp.Components.Single(c => c.Id == "glide_bombs").Effects.OfType<WeaponEffect>().Single().Delivery!;
        Assert.NotNull(glide.Guided);
        foreach (var id in new[] { "fighter_gun", "multirole_gun" })
        {
            var w = TestContent.Mvp.Components.Single(c => c.Id == id).Effects.OfType<WeaponEffect>().Single();
            Assert.Equal(Fixed.FromInt(18), w.AirGun!.StrafeRange);
            Assert.Equal(5, w.AirGun.StrafeEvery);
            Assert.Equal(Fixed.FromInt(1), w.Splash);
        }
    }

    // ---- Strikes with each preset ----

    [Theory]
    [InlineData("Multirole 500 lb", 4, 700, 4)]
    [InlineData("Multirole 2000 lb", 2, 1300, 6)]
    public void TheMultiroleDiveBombsATruckWithEachPreset(string preset, int bombs, int damage, int tier)
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, preset, 100, 120);
        var truck = Tough(w, "technical", 135, 125);
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        var log = Strike(w, m, 3000);
        // Every bomb dropped from a dive, about 19 cells short of the truck near its 60 m release height,
        // and landed within its scatter (1 cell an axis) plus half a tick of travel; the truck took each.
        Assert.Equal(bombs, log.Releases.Count);
        Assert.Equal(bombs, log.Impacts.Count);
        Assert.Equal(0, Charges(m, e => e.Delivery is not null));
        foreach (var r in log.Releases)
        {
            Assert.Equal(PassKind.Dive, r.Kind);
            Assert.True(r.VelZ < 0, "released diving");
            Assert.InRange(r.Altitude.Raw / 512, 45, 70);
            Assert.InRange(Off(r.At, r.Predicted), 15 * Cell1, 25 * Cell1);
            Assert.Equal(tier, r.Tier);
        }
        foreach (var i in log.Impacts)
        {
            Assert.InRange(Off(i.At, truck.Pos), 0, 2 * Cell1);
            Assert.Equal((damage, tier, "bomb", CraterKind.None), (i.Damage, i.Tier, i.Warhead, i.Runway));
        }
        Assert.Equal(log.Releases.Select(r => r.Bomb), log.Impacts.Select(i => i.Bomb));
        Assert.True(truck.HP <= 100_000 - bombs * damage * 30 / 100, $"each bomb hurt the truck (HP {truck.HP})");
        Assert.Contains(StrikePhase.Break, log.Phases);
        Assert.True(log.EgressBoost, "the egress is flown on the afterburner (Wing.Boost)");
        // Out of bombs it strafes with its gun until that is empty too, then the strike ends.
        Assert.NotEmpty(log.Strafes);
        Assert.Equal(OrderKind.Idle, m.Order.Kind);
        Assert.Null(m.Strike);
    }

    [Fact]
    public void ABombOnABuildingIsADirectHitAtTwiceItsDamage()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var depot = Tough(w, "supply_depot", 135, 122);
        var hp = depot.HP;
        w.Order(1, CommandKind.Attack, [m], target: depot.Id);
        var log = new Log();
        Assert.True(RunUntil(w, () =>
        {
            log.Take(w, m);
            return log.Impacts.Count > 0;
        }, 1500) > 0);
        var i = log.Impacts[0];
        Assert.Equal((depot.Id, true), (i.Hit, i.Building));
        Assert.Equal(1400, hp - depot.HP); // the bomb warhead: 200% against buildings
    }

    [Fact]
    public void AStrikeOnAPointBombsThePoint()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 90, 120);
        var point = new Cell(130, 124);
        w.Order(1, CommandKind.AttackGround, [m], cell: point);
        var log = Strike(w, m, 3000);
        Assert.Equal(4, log.Impacts.Count);
        Assert.All(log.Impacts, i => Assert.InRange(Off(i.At, point.Center()), 0, 2 * Cell1));
        Assert.All(log.Releases, r => Assert.Equal(0u, r.Target));
    }

    // ---- The release solution, per profile ----

    [Theory]
    [InlineData("level")]
    [InlineData("dive")]
    [InlineData("toss")]
    public void WithoutScatterEachProfileLandsWithinACellOnFlatGround(string profile)
    {
        var content = Bombs(j => Profile(NoScatter(j), profile));
        var w = Sentinel(content);
        var m = Overhead(w, "Multirole 500 lb", 70, 120);
        var point = new Cell(130, 124);
        w.Order(1, CommandKind.AttackGround, [m], cell: point);
        var log = Strike(w, m, 3000);
        Assert.Equal(4, log.Impacts.Count);
        Assert.All(log.Impacts, i => Assert.InRange(Off(i.At, point.Center()), 0, Cell1));
        Assert.All(log.Releases, r => Assert.InRange(Off(r.Predicted, point.Center()), 0, Cell1));
        var want = profile switch { "level" => PassKind.Level, "dive" => PassKind.Dive, _ => PassKind.Toss };
        Assert.All(log.Releases, r => Assert.Equal(want, r.Kind));
        switch (profile)
        {
            case "level":
                Assert.All(log.Releases, r => Assert.InRange(r.Altitude.Raw / 512, 48, 62)); // at its 60 m release height
                break;
            case "dive":
                Assert.All(log.Releases, r => Assert.True(r.VelZ < 0));
                break;
            default:
                Assert.All(log.Releases, r => Assert.True(r.VelZ > 0, "tossed: released climbing"));
                Assert.True(log.Afterburner, "low and fast: the afterburner on the run-in");
                break;
        }
    }

    [Fact]
    public void TheReleasePointDependsOnTheProfile()
    {
        // A 35-degree dive from 60 m lands nearest; level from 60 m farther on; a toss (climbing) farthest.
        long Mean(string profile)
        {
            var w = Sentinel(Bombs(j => Profile(NoScatter(j), profile)));
            var m = Overhead(w, "Multirole 500 lb", 70, 120);
            w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(130, 124));
            var log = Strike(w, m, 3000);
            return (long)log.Releases.Average(r => Off(r.At, r.Predicted));
        }
        var (level, dive, toss) = (Mean("level"), Mean("dive"), Mean("toss"));
        Assert.True(dive < level && level < toss, $"dive {dive / 1024.0:F1}, level {level / 1024.0:F1}, toss {toss / 1024.0:F1} cells");
    }

    [Fact]
    public void ScatterIsSeededAndBoundedByTheProfile()
    {
        var offs = new List<(long X, long Y)>();
        for (ulong seed = 1; seed <= 24; seed++)
        {
            var w = Arena.NewSentinel(N, seed);
            var m = Overhead(w, "Multirole 500 lb", 90, 120);
            var point = new Cell(130, 124);
            w.Order(1, CommandKind.AttackGround, [m], cell: point);
            var log = new Log();
            RunUntil(w, () =>
            {
                log.Take(w, m);
                return log.Impacts.Count > 0;
            }, 1500);
            var i = Assert.Single(log.Impacts);
            offs.Add((i.At.X.Raw - point.Center().X.Raw, i.At.Y.Raw - point.Center().Y.Raw));
        }
        // A dive's scatter is 1 cell an axis; the release solution adds at most about half a tick of travel.
        Assert.All(offs, o => Assert.InRange(Math.Max(Math.Abs(o.X), Math.Abs(o.Y)), 0, Cell1 * 2));
        Assert.True(offs.Distinct().Count() >= 20, "every seed lands its own way");
        Assert.InRange(offs.Average(o => o.X), -Cell1 / 2, Cell1 / 2);
        Assert.InRange(offs.Average(o => o.Y), -Cell1 / 2, Cell1 / 2);
    }

    [Fact]
    public void AMovingTargetIsLed()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 70, 120);
        var truck = Tough(w, "technical", 110, 140);
        w.Spawn(1, "Rifleman", 130, 132); // a spotter along its way
        w.Spawn(1, "Rifleman", 150, 132);
        w.Order(2, CommandKind.Move, [truck], cell: new Cell(170, 140));
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        var log = new Log();
        var moving = false;
        Assert.True(RunUntil(w, () =>
        {
            log.Take(w, m);
            moving |= truck.Vel != default;
            return log.Impacts.Count > 0;
        }, 1500) > 0);
        Assert.True(moving, "the truck was driving");
        Assert.InRange(Off(log.Impacts[0].At, truck.Pos), 0, 3 * Cell1);
    }

    // ---- The 2,000 lb bomb on a runway ----

    [Fact]
    public void A2000lbBombDigsALargeCraterInARunway()
    {
        var w = Duel();
        var af = AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(80, 150), player: 2);
        var point = Cell.Of(World.AirfieldPoint(af, 300, 50)); // the runway's middle
        var m = Overhead(w, "Multirole 2000 lb", 60, 100);
        w.Order(1, CommandKind.AttackGround, [m], cell: point);
        var log = new Log();
        Assert.True(RunUntil(w, () =>
        {
            log.Take(w, m);
            return log.Impacts.Count > 0;
        }, 2000) > 0);
        var i = log.Impacts[0];
        Assert.Equal((CraterKind.Large, 6), (i.Runway, i.Tier));
        var c = Assert.Single(af.Craters);
        Assert.Equal(CraterKind.Large, c.Kind);
        Assert.InRange(Off(c.At, i.At), 0, 4 * Cell1);
    }

    // ---- The fighter's glide bombs ----

    [Fact]
    public void TheFighterReleasesItsGlideBombsFromStandOffAndTheyHome()
    {
        var w = Arena.NewSentinel(N);
        var f = Overhead(w, "fighter_jet", 80, 120);
        var truck = Tough(w, "technical", 120, 120);
        w.Order(1, CommandKind.Attack, [f], target: truck.Id);
        var log = new Log();
        Assert.True(RunUntil(w, () =>
        {
            log.Take(w, f);
            return log.Impacts.Count >= 2;
        }, 3000) > 0);
        Assert.Equal(2, log.Releases.Count);
        Assert.All(log.Releases, r =>
        {
            Assert.Equal((PassKind.Guided, 2), (r.Kind, r.Tier));
            Assert.InRange(Off(r.At, truck.Pos), 10 * Cell1, 51 * Cell1); // stand-off: within its 50-cell reach
        });
        Assert.All(log.Impacts, i => Assert.True(i.Guided));
        var hits = log.Impacts.Count(i => i.Hit == truck.Id);
        Assert.True(hits >= 1, "95% on a target that stands still");
        Assert.All(log.Impacts.Where(i => i.Hit == truck.Id), i => Assert.InRange(Off(i.At, truck.Pos), 0, Cell1));
        Assert.True(100_000 - truck.HP >= hits * 350);
        Assert.Equal(0, Charges(f, e => e.Delivery is not null));
    }

    // ---- Strafing ----

    [Fact]
    public void AStrafingPassWalksItsBurstsAcrossTheTarget()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 90, 120);
        Empty(m, e => e.Delivery is not null); // no bombs: the gun
        var soldiers = new[] { Tough(w, "Rifleman", 128, 124), Tough(w, "Rifleman", 129, 125), Tough(w, "Rifleman", 130, 124) };
        var hp = soldiers.Sum(s => s.HP);
        w.Order(1, CommandKind.Attack, [m], target: soldiers[1].Id);
        var log = new Log();
        var ticks = new List<uint>();
        for (var t = 0; t < 3000 && (t < 3 || m.Order.Kind == OrderKind.Attack); t++)
        {
            w.Step();
            log.Take(w, m);
            ticks.AddRange(w.StrafeBursts.Select(_ => w.Tick));
        }
        Assert.Empty(log.Releases);
        Assert.Equal(8, log.Strafes.Count); // all 8 bursts, then the strike ends
        Assert.Equal(OrderKind.Idle, m.Order.Kind);
        // Passes: bursts a quarter second apart; within each, every burst lands farther along than the last.
        var passes = new List<List<StrafeBurst>>();
        for (var k = 0; k < log.Strafes.Count; k++)
        {
            if (k == 0 || ticks[k] - ticks[k - 1] > 10)
            {
                passes.Add([]);
            }
            passes[^1].Add(log.Strafes[k]);
        }
        Assert.Contains(passes, p => p.Count >= 3);
        foreach (var pass in passes.Where(p => p.Count >= 2))
        {
            long dx = pass[^1].From.X.Raw - pass[0].From.X.Raw, dy = pass[^1].From.Y.Raw - pass[0].From.Y.Raw;
            var along = pass.Select(b => (b.At.X.Raw * dx + b.At.Y.Raw * dy) / Math.Max(1, World.Len3(dx, dy, 0))).ToList();
            Assert.True(along.Zip(along.Skip(1)).All(q => q.First < q.Second), "each burst lands farther along than the last");
        }
        Assert.True(soldiers.Sum(s => Math.Max(0, s.HP)) < hp, "the soldiers were hit");
    }

    // ---- The owner's rules ----

    [Fact]
    public void GuardWithGroundKindsAttacksOnlyInsideItsArea()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var inside = Tough(w, "technical", 125, 125);
        var outside = Tough(w, "technical", 205, 205); // beyond the area plus its 30-cell leash
        w.Spawn(1, "Rifleman", 203, 203); // a spotter: the outside truck is seen
        Give(w, m, AirTaskKind.Guard, new Cell(100, 100), new Cell(150, 150));
        var log = new Log();
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            log.Take(w, m);
        }
        Assert.True(World.IsVisibleTo(outside, 1));
        Assert.NotEmpty(log.Releases);
        Assert.All(log.Releases, r => Assert.Equal(inside.Id, r.Target));
        Assert.All(log.Impacts, i => Assert.True(Off(i.At, outside.Pos) > 30 * Cell1));
        Assert.True(outside.HP > 100_000 - 100, "no bomb near the outside truck (only the spotter's rifle)");
        Assert.True(inside.HP < 100_000);
        Assert.Equal(AirTaskKind.Guard, m.AirMission!.Kind);
    }

    [Fact]
    public void PatrolNeverAttacksTheGround()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var truck = Tough(w, "technical", 130, 121);
        Give(w, m, AirTaskKind.Patrol, new Cell(100, 120));
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [m.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(160, 120), Queue = true });
        var log = new Log();
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            log.Take(w, m);
        }
        Assert.Equal(2, m.AirMission!.Route.Count);
        Assert.Empty(log.Releases);
        Assert.Empty(log.Strafes);
        Assert.Equal(100_000, truck.HP);
        Assert.Null(m.Strike);
    }

    [Fact]
    public void AStrikeRunsOnTopOfThePatrolWhichResumesAfterIt()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var truck = w.SpawnCrewed(2, "technical", 130, 125).Vehicle; // one bomb is enough
        Give(w, m, AirTaskKind.Patrol, new Cell(90, 110));
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Units = [m.Id], Mission = AirTaskKind.Patrol, Cell = new Cell(150, 110), Queue = true });
        w.Step();
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        w.Step();
        Assert.Equal(AirActivity.Striking, w.AirStatusOf(m).Activity);
        var log = Strike(w, m, 3000);
        Assert.False(truck.Alive);
        Assert.Contains(StrikePhase.Run, log.Phases);
        Assert.Null(m.Strike);
        Assert.Equal(AirTaskKind.Patrol, m.AirMission!.Kind);
        w.Step();
        Assert.Equal(AirActivity.Patrolling, w.AirStatusOf(m).Activity);
        Assert.Equal(4 - log.Releases.Count, Charges(m, e => e.Delivery is not null)); // the rest stay aboard
        Assert.InRange(log.Releases.Count, 1, 3);
    }

    [Fact]
    public void AStrikeEndsOutOfEverythingThatHitsTheTarget()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var truck = Tough(w, "technical", 125, 125);
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        w.Run(3);
        Assert.NotNull(m.Strike);
        Empty(m, e => e.Delivery is not null || e.AirGun is not null);
        Assert.True(RunUntil(w, () => m.Order.Kind == OrderKind.Idle, 10) > 0, "out of everything that hits it: the strike ends");
        Assert.Null(m.Strike);
    }

    [Fact]
    public void TheWatcherStrikesAPointOnTheGround()
    {
        var w = Arena.NewSentinel(N);
        var d = w.Spawn(1, "watcher_drone", 120, 120);
        w.Run(3);
        var point = new Cell(126, 122);
        w.Order(1, CommandKind.AttackGround, [d], cell: point);
        var reports = new List<StrikeReport>();
        Assert.True(RunUntil(w, () =>
        {
            reports.AddRange(w.StrikeReports);
            return reports.Any(r => r.Outcome != StrikeOutcome.Launched);
        }, 1200) > 0);
        var launch = reports.First();
        Assert.Equal((StrikeOutcome.Launched, 0u), (launch.Outcome, launch.Target));
        var end = reports.Last();
        Assert.True(end.Outcome is StrikeOutcome.Point or StrikeOutcome.Missed);
        if (end.Outcome == StrikeOutcome.Point)
        {
            Assert.InRange(Off(end.At, point.Center()), 0, Cell1);
        }
        Assert.Equal(1, World.StrikesLeft(d));
        Assert.Equal(OrderKind.Idle, d.Order.Kind);
    }

    // ---- Determinism ----

    // The bingo rule on a run-in (E7, StrikeReleaseDue): an aircraft that reaches bingo fuel on a run-in finishes
    // the release when it is at most 3 s off, then goes home; further out it breaks off for home at once. (The
    // bombs fly a level profile here, their IP moved out to 100 cells so a run-in lasts about 4 s.)
    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    public void AtBingoOnARunInItFinishesAReleaseAtMostThreeSecondsOff(int seconds, bool releases)
    {
        var content = Bombs(json =>
        {
            var s = Profile(json, "level").Replace("\"ipDistance\": 160", "\"ipDistance\": 1000", StringComparison.Ordinal);
            Assert.NotEqual(json, s);
            return s;
        });
        var w = Sentinel(content);
        var home = AirfieldFlightTests.Field(w, [(0, 0)], at: new Cell(30, 170), east: 1);
        var m = Overhead(w, "Multirole 500 lb", 60, 110);
        w.Order(1, CommandKind.AttackGround, [m], cell: new Cell(170, 110));
        long Left() => m.Strike is { Phase: StrikePhase.Run } r ? Off(r.Aim, m.Pos) - r.ReleaseDist : long.MaxValue;
        Assert.True(RunUntil(w, () => Left() <= m.Wing!.Speed * seconds, 3000) > 0, "a run-in");
        var speed = m.Wing!.Speed;
        Assert.Equal(releases, Left() <= speed * 3); // the rule's 3 s (BingoFinishTicks)
        Assert.True(Left() > speed * (seconds - 1), $"{Left() * 10 / Math.Max(1, speed)} tenths of a second off");
        var log = new Log();
        m.Lifetime = 800; // bingo now (a 40 s trip home with its reserve is more), and fuel to get there
        w.Step();
        log.Take(w, m);
        Assert.Equal(!releases, m.AirOps is not null); // home at once, unless the release is due
        for (var t = 0; t < 100; t++)
        {
            w.Step();
            log.Take(w, m);
        }
        Assert.Equal(releases, log.Releases.Count > 0);
        Assert.Equal(home.Id, m.AirOps?.Airfield); // and home it goes
        Assert.True(m.Alive);
    }

    [Fact]
    public void StrikesAreDeterministic()
    {
        static List<ulong> Run()
        {
            var w = Arena.NewSentinel(N, 5);
            var a = Overhead(w, "Multirole 500 lb", 90, 110);
            var b = Overhead(w, "Multirole 2000 lb", 90, 140);
            var f = Overhead(w, "fighter_jet", 80, 125);
            var t1 = w.SpawnCrewed(2, "technical", 130, 115).Vehicle;
            var t2 = w.Spawn(2, "supply_depot", 132, 140);
            w.Spawn(2, "Rifleman", 125, 125);
            Give(w, a, AirTaskKind.Guard, new Cell(100, 100), new Cell(160, 160));
            w.Order(1, CommandKind.Attack, [b], target: t2.Id);
            w.Order(1, CommandKind.Attack, [f], target: t1.Id);
            var hashes = new List<ulong>();
            for (var t = 0; t < 1500; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}

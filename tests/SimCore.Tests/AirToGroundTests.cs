using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Air-to-ground (E7; the plan's air-combat model, section 2.10): strike runs (approach, IP, run-in by
// the delivery profile, release, egress, re-attack), the release solution, scatter and guided bombs,
// bomb blasts (direct hits, splash, buildings, runway craters, the airstrike tier), strafing, the owner's
// rules (Guard picks ground targets inside its area only, Patrol never attacks the ground, a strike runs
// on top of the task and the task resumes), determinism.
public class AirToGroundTests(ITestOutputHelper output)
{
    private const int N = 240;

    private static string Spawnable(string jet) => jet == "multirole_jet" ? "Multirole 500 lb" : jet;

    private static Unit Overhead(World w, string preset, int x, int y, byte player = 1, int facing = Facing.East)
    {
        var d = w.Spawn(player, preset, x, y, facing);
        w.Step();
        return d;
    }

    private sealed class Log
    {
        public readonly List<BombRelease> Releases = [];
        public readonly List<BombImpact> Impacts = [];
        public readonly List<StrafeBurst> Strafes = [];
        public readonly List<StrikePhase> Phases = [];

        public void Take(World w, Unit u)
        {
            Releases.AddRange(w.BombReleases);
            Impacts.AddRange(w.BombImpacts);
            Strafes.AddRange(w.StrafeBursts);
            if (u.Strike is { } s && (Phases.Count == 0 || Phases[^1] != s.Phase))
            {
                Phases.Add(s.Phase);
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

    private static double Cells(Vec a, Vec b) => Math.Sqrt((double)(a - b).LenSq()) / Fixed.OneRaw;

    [Fact]
    public void Explore500lbOnATruck()
    {
        var w = Arena.NewSentinel(N);
        var m = Overhead(w, "Multirole 500 lb", 100, 120);
        var truck = w.SpawnCrewed(2, "technical", 135, 125).Vehicle;
        truck.HP = 100_000;
        w.Order(1, CommandKind.Attack, [m], target: truck.Id);
        var log = new Log();
        for (var t = 0; t < 1800 && (t < 2 || m.Order.Kind == OrderKind.Attack); t++)
        {
            w.Step();
            log.Take(w, m);
            if (m.Strike is { Kind: PassKind.Strafe, Phase: StrikePhase.RunIn } sr && t < 0)
            {
                double ax = (sr.Aim.X.Raw - m.Pos.X.Raw) / 1024.0, ay = (sr.Aim.Y.Raw - m.Pos.Y.Raw) / 1024.0;
                double vx = m.Wing!.VelX, vy = m.Wing.VelY;
                var d = Math.Sqrt(ax * ax + ay * ay);
                var cos = (vx * ax + vy * ay) / Math.Sqrt(vx * vx + vy * vy) / d;
                output.WriteLine($"   strafe d {d:F1} alt {m.Altitude.Raw / 1024.0:F1}c cos {cos:F3} reload {m.Reload} aim {sr.Aim.X.Raw / 1024.0:F1},{sr.Aim.Y.Raw / 1024.0:F1}");
            }
            if (t % 10 == 0)
            {
                output.WriteLine($"t{w.Tick} {m.Strike?.Phase} pos {m.Pos.X.Raw / 1024.0:F1},{m.Pos.Y.Raw / 1024.0:F1} alt {m.Altitude.Raw / 512.0:F1} spd {m.Wing?.Speed / 1024.0:F1} vz {m.VelZ.Raw * 20 / 512.0:F1} order {m.Order.Kind} rel {m.Strike?.ReleaseDist / 1024.0:F1}");
            }
            foreach (var r in w.BombReleases)
            {
                output.WriteLine($"  release t{w.Tick} at {Cells(r.At, truck.Pos):F1} cells, alt {r.Altitude.Raw / 512.0:F1}, pred {Cells(r.Predicted, truck.Pos):F1} off, tick {r.ImpactTick}");
            }
            foreach (var b in w.StrafeBursts)
            {
                output.WriteLine($"  burst t{w.Tick} from {Cells(b.From, truck.Pos):F1} lands {Cells(b.At, truck.Pos):F1} off, truck hp {truck.HP}");
            }
            foreach (var i in w.BombImpacts)
            {
                output.WriteLine($"  impact t{w.Tick} {Cells(i.At, truck.Pos):F1} cells off, tier {i.Tier}, hit {i.Hit}, truck hp {truck.HP}");
            }
        }
        output.WriteLine($"truck {truck.Alive} {truck.HP}; releases {log.Releases.Count} impacts {log.Impacts.Count}");
        Assert.Equal(4, log.Impacts.Count);
    }
}

using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// AIR-LOG-1, the coordinator's call 3: air defence's catch rates against the drones whose speed changed, before and after, with no
// rule change. Before is the content with the old numbers patched back in memory (the high altitude scout drone 300 and vision 1000,
// the Watcher 80, the Overseer 60). One pass a seed: the drone at its cruise height flies straight over the
// shooter, 8 cells to its side, from 250 cells west to 250 cells east (its Move order); the shooter (and its radar) set up first.
// Logged: kills and missiles fired over the seeds. Decision 1352 has the numbers.
public class DroneCatchRateTests(ITestOutputHelper output)
{
    private const int Size = 600, Y = 300, Seeds = 8;

    private static readonly Lazy<ContentSet> Before = new(() => ContentLoader.Load(TestContent.MvpSources().Select(s => s.Name switch
    {
        "archetypes/spawned.json" => s with
        {
            Json = Swap(Swap(s.Json, "\"hp\": 100, \"speed\": 150, \"turnRate\": 2,", "\"hp\": 100, \"speed\": 300, \"turnRate\": 2,"),
                "\"vision\": 850, \"lifetime\": 7200,", "\"vision\": 1000, \"lifetime\": 7200,"),
        },
        "archetypes/sentinel.json" => s with
        {
            Json = Swap(Swap(s.Json, "\"hp\": 200, \"speed\": 120, \"turnRate\": 2,", "\"hp\": 200, \"speed\": 80, \"turnRate\": 2,"),
                "\"hp\": 150, \"speed\": 90, \"turnRate\": 2,", "\"hp\": 150, \"speed\": 60, \"turnRate\": 2,"),
        },
        _ => s,
    })));

    private static string Swap(string s, string from, string to)
    {
        Assert.Equal(1, s.Split(from).Length - 1); // exactly once
        return s.Replace(from, to, StringComparison.Ordinal);
    }

    // Pass: over seeds, how many drone passes shooter (a Sentinel one against the Resistance's high altitude scout drone, a
    // Resistance one against the Sentinel's drones) kills, and the missiles it fires.
    private static (int Kills, int Launches, int Speed) Pass(ContentSet content, string drone, string shooter)
    {
        var a = content.Archetype(drone)!;
        var samad = drone == "samad_drone";
        byte us = samad ? (byte)1 : (byte)2, them = samad ? (byte)2 : (byte)1;
        int kills = 0, launches = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var w = Arena.NewSentinel(Size, seed, content: content);
            var radar = shooter switch
            {
                "patriot_launcher" or "thaad_launcher" => "radar_station",
                "kub_launcher" => "mobile_radar",
                _ => null,
            };
            Unit Crewed(string id, int x) => content.Archetype(id)!.Crew is not null ? w.SpawnCrewed(us, id, x, Y).Vehicle : w.Spawn(us, id, x, Y);
            if (shooter.EndsWith("_jet", StringComparison.Ordinal))
            {
                var j = w.Spawn(us, shooter == "multirole_jet" ? "Multirole 500 lb" : shooter, Size / 2 - 60, Y + 40, Facing.East);
                w.Issue(new Command
                {
                    Tick = w.Tick, Player = us, Kind = CommandKind.AirTask, Units = [j.Id], Mission = AirTaskKind.Guard,
                    Cell = new Cell(Size / 2 - 150, Y - 100), Cell2 = new Cell(Size / 2 + 150, Y + 100), Filter = (byte)(AirTargetKinds.Drones | AirTargetKinds.Aircraft),
                });
            }
            else
            {
                Crewed(shooter, Size / 2);
                if (radar is not null)
                {
                    Crewed(radar, Size / 2 + 15);
                }
                foreach (var x in w.All(shooter).Concat(radar is null ? [] : w.All(radar)))
                {
                    if (x.Def.Archetype.DeployTicks > 0)
                    {
                        w.Order(us, CommandKind.Deploy, [x]);
                    }
                }
                w.Run(560); // every set-up done (the THAAD type's 26 s the longest)
            }
            var d = w.Spawn(them, drone, Size / 2 - 250, Y + 8, Facing.East);
            d.Altitude = Fixed.FromRaw(a.Flight!.HeightRaw);
            w.Order(them, CommandKind.Move, [d], cell: new Cell(Size / 2 + 250, Y + 8));
            var fired = new HashSet<uint>(); // ground interceptors in flight from our side (a jet's missiles: its air launches)
            for (var t = 0; t < 4000 && d.Alive && d.Pos.X.Raw < (Size / 2 + 240) * (long)Fixed.OneRaw; t++)
            {
                w.Step();
                launches += w.AirLaunches.Count(l => l.Target == d.Id);
                foreach (var p in w.Projectiles)
                {
                    if (p.Owner == us && p.ShooterDef.Archetype.Flight is null && p.Weapon.Effect.Interceptor is not null)
                    {
                        fired.Add(p.Id);
                    }
                }
            }
            launches += fired.Count;
            kills += d.Alive ? 0 : 1;
        }
        return (kills, launches, (int)(a.Speed.Raw * World.TicksPerSecond * 10 / Fixed.OneRaw));
    }

    [Theory]
    [InlineData("samad_drone", "patriot_launcher")]
    [InlineData("samad_drone", "thaad_launcher")]
    [InlineData("samad_drone", "fighter_jet")]
    [InlineData("samad_drone", "multirole_jet")]
    [InlineData("watcher_drone", "sam_vehicle")]
    [InlineData("watcher_drone", "manpads")]
    [InlineData("watcher_drone", "zu23_carrier")]
    [InlineData("watcher_drone", "technical")]
    [InlineData("overseer_drone", "sam_vehicle")]
    [InlineData("overseer_drone", "manpads")]
    [InlineData("overseer_drone", "zu23_carrier")]
    [InlineData("overseer_drone", "technical")]
    public void CatchRatesBeforeAndAfter(string drone, string shooter)
    {
        var before = Pass(Before.Value, drone, shooter);
        var after = Pass(TestContent.Mvp, drone, shooter);
        output.WriteLine($"{drone} vs {shooter}: speed {before.Speed} -> {after.Speed}; killed {before.Kills}/{Seeds} -> {after.Kills}/{Seeds}, " +
            $"missiles {before.Launches} -> {after.Launches}");
        Assert.InRange(after.Kills, 0, Seeds);
    }
}

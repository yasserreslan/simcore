using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The escort as a convoy (HARVEST-ENGINE, 2026-11-18; docs/design.md, "Escort"): a cargo truck on Haul between a mine and a bank,
// on a paved road and off-road, alone and with 2 to 4 escorts (a Humvee, a tank, the AA gun): its average speed on both legs, the
// escorts' spacing, contacts with the truck and stalls.
public class ConvoyTests(ITestOutputHelper output)
{
    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    internal readonly record struct Trip(double Speed, double SpeedRoad, int Ticks, int Contacts, int Blocked, int Stuck, double WorstOff, double MinGap,
        double MeanOff, double CruiseGap);

    private static double Cells(long raw) => raw / (double)Fixed.OneRaw;

    // Drive runs one haul round trip (full at the mine, to the bank, unload, back to the mine's spot) with the escorts named, and
    // measures it. road: a paved road joins the two spots.
    internal static Trip Drive(bool road, string[] escorts, Action<string>? log = null)
    {
        const int Size = 200;
        var w = Arena.NewSentinel(Size, 1, s => s with { Economy = true, StartGold = 0 });
        var t = TerrainMap.Flat(Size, Size, Rules);
        if (road)
        {
            for (var x = 20; x < 180; x++)
            {
                for (var r = 46; r <= 50; r++)
                {
                    t.SetRoadKind(new Cell(x, r), RoadKind.Paved);
                }
            }
        }
        w.AttachTerrain(t);
        var m = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "gold_placer")!, new Cell(20, 40), 0, 0))!;
        var bank = w.Spawn(1, "bank_sentinel", 170, 40);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 30, 48, Facing.East).Vehicle;
        var es = escorts.Select((id, i) => w.SpawnCrewed(1, id, 26 - 4 * i, 52, Facing.East).Vehicle).ToList();
        if (es.Count > 0)
        {
            w.Order(1, CommandKind.Escort, es, truck.Id);
        }
        w.Run(300); // formed up
        truck.Econ = new EconState { Carry = 300 };
        w.Order(1, CommandKind.Haul, [truck], m.Id);
        long odo = 0;
        var ticks = 0;
        var contacts = 0;
        var blocked = 0;
        var stuck = 0;
        double worst = 0, minGap = double.MaxValue, offSum = 0, cruiseGap = double.MaxValue;
        var offN = 0;
        long roadOdo = 0;
        var roadTicks = 0;
        var leg = 0; // 0 to the bank, 1 unloading, 2 back
        var last = truck.Pos;
        for (var k = 0; k < 6000; k++)
        {
            w.Step();
            var st = truck.Econ!.Stage;
            if (leg == 0 && st == EconStage.Unloading)
            {
                leg = 1;
            }
            else if (leg == 1 && st != EconStage.Unloading)
            {
                leg = 2;
            }
            else if (leg == 2 && st is EconStage.Parked or EconStage.Loading)
            {
                break;
            }
            var step = (truck.Pos - last).Len().Raw;
            last = truck.Pos;
            if (leg == 1)
            {
                continue;
            }
            odo += step;
            ticks++;
            var cruising = Math.Abs(Cells(truck.Pos.X.Raw) - 100) < 50; // the middle of the way: no starts, stops or turns
            if (cruising)
            {
                roadOdo += step;
                roadTicks++;
            }
            stuck += truck.Intent.Stuck ? 1 : 0;
            foreach (var e in es)
            {
                var touch = truck.Def.Archetype.Radius.Raw + e.Def.Archetype.Radius.Raw;
                var gap = (e.Pos - truck.Pos).Len().Raw - touch;
                minGap = Math.Min(minGap, Cells(gap));
                contacts += gap < Fixed.OneRaw / 4 ? 1 : 0;
                // Blocked: touching an escort ahead of it (in its way).
                var dx = e.Pos.X.Raw - truck.Pos.X.Raw;
                var dy = e.Pos.Y.Raw - truck.Pos.Y.Raw;
                var bl = gap < Fixed.OneRaw / 4 && dx * truck.Heading.X.Raw + dy * truck.Heading.Y.Raw > 0;
                blocked += bl ? 1 : 0;
                if (cruising && e.Escort is { } x)
                {
                    worst = Math.Max(worst, Cells((e.Pos - x.Point).Len().Raw));
                    offSum += Cells((e.Pos - x.Point).Len().Raw);
                    offN++;
                    cruiseGap = Math.Min(cruiseGap, Cells(gap));
                }
            }
        }
        var trip = new Trip(Cells(odo) * World.TicksPerSecond / Math.Max(1, ticks), Cells(roadOdo) * World.TicksPerSecond / Math.Max(1, roadTicks),
            ticks, contacts, blocked, stuck, worst, minGap == double.MaxValue ? 0 : minGap, offSum / Math.Max(1, offN), cruiseGap == double.MaxValue ? 0 : cruiseGap);
        log?.Invoke($"{(road ? "road" : "off-road"),-8} [{string.Join(", ", escorts)}]: {trip.Ticks} ticks, {trip.Speed:0.00} cells/s on average, " +
            $"{trip.SpeedRoad:0.00} cruising; truck stuck {trip.Stuck} ticks; escorts touching it {trip.Contacts} ticks ({trip.Blocked} ahead of it); " +
            $"cruising: {trip.MeanOff:0.0} cells off a spot on average (worst {trip.WorstOff:0.0}), at least {trip.CruiseGap:0.0} cells clear of the truck (bank {bank.Id})");
        return trip;
    }

    internal static readonly string[][] Mixes =
    [
        [],
        ["main_battle_tank"],
        ["humvee", "korkut_gun"],
        ["humvee", "main_battle_tank", "korkut_gun"],
        ["humvee", "main_battle_tank", "korkut_gun", "humvee"],
        ["humvee", "light_tank", "korkut_gun", "humvee"],
    ];

    // The truck within 5 % of its speed alone (the escorts never slow it: they keep pace on their own ground, give way, and the
    // slowest sets the pace only when it is slower than the truck), never stuck, and while cruising no escort touches it.
    [Fact]
    public void TheConvoyKeepsTheTrucksPace()
    {
        foreach (var road in new[] { true, false })
        {
            var alone = Drive(road, Mixes[0], output.WriteLine);
            foreach (var mix in Mixes.Skip(1))
            {
                var t = Drive(road, mix, output.WriteLine);
                Assert.True(t.Ticks * 100 <= alone.Ticks * 105, $"{string.Join(",", mix)}: {t.Ticks} ticks against {alone.Ticks} alone");
                Assert.True(t.SpeedRoad >= alone.SpeedRoad * 0.95, $"{string.Join(",", mix)}: {t.SpeedRoad:0.00} cells/s cruising");
                Assert.Equal(0, t.Stuck);
                Assert.True(t.CruiseGap > 0.25, $"{string.Join(",", mix)}: {t.CruiseGap:0.00} cells clear while cruising");
            }
        }
    }

    // A slower escort sets the convoy's pace: a truck escorted by riflemen goes at their walk.
    [Fact]
    public void TheSlowestMemberSetsThePace()
    {
        var w = Arena.NewSentinel(160, 1);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 20, 40, Facing.East).Vehicle;
        var man = w.Spawn(1, "Rifleman", 26, 40);
        w.Order(1, CommandKind.Escort, [man], truck.Id);
        w.Run(100);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(140, 40));
        w.Run(300);
        var walk = man.Def.Speed.Raw;
        Assert.InRange(truck.Vel.Len().Raw, walk * 7 / 8 - 2, walk + 2); // the rifleman's speed, not the truck's
        Assert.True(Cells((man.Pos - man.Escort!.Point).Len().Raw) < 2);
    }
}

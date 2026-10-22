using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Logistics (2026-10-22; docs/design.md, "Logistics"): the airlifter (a C-5 type), the field ammo point, the supply stash and the
// forward airbase.
public class LogisticsTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw;

    private static World Sentinel(int n, ulong seed = 1) => Arena.NewSentinel(n, seed);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void TheForwardAirbaseLaysItsRunwayStandsAndGarage()
    {
        var w = Sentinel(400);
        var af = w.SpawnAirBase(Def(w, "forward_airbase"), new Cell(100, 200), 0, []) ?? throw new InvalidOperationException("no field");
        Assert.Equal("forward_airbase", af.Type.Id);
        Assert.Equal(7, af.Segments.Count);
        Assert.Equal("forward_airbase_hut", w.Unit(af.Facility)!.Def.Archetype.Id);
        Assert.Equal("forward_airbase_apron", w.Unit(af.Hangar)!.Def.Archetype.Id);
        Assert.Equal(0u, af.Radar);
        var stations = af.Rows.SelectMany(r => r.Stations).ToList();
        Assert.Equal(2, stations.Count);
        Assert.All(stations, s => Assert.Equal("forward_airbase_stand", w.Unit(s.Unit)!.Def.Archetype.Id));
        Assert.Equal(new StoreStock(8000, 20000), w.StockOf(w.Unit(af.Core)!));
        foreach (var s in stations)
        {
            output.WriteLine($"stand {s.Unit} at {w.Unit(s.Unit)!.Pos}");
        }
    }
}

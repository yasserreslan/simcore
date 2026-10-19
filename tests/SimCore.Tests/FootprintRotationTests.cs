using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// Rotated footprints: a non-square structure built a quarter turn (facing East or West) stands with
// its width and height swapped, everywhere the sim reads a footprint: placement and overlap,
// routing blocks, aim points and the spatial index. Square footprints, the half turn and anything
// laid out from its unturned top-left cell (a factory's doors, a garrison's doors) keep the old
// rules; such quarter turns were refused before, so nothing that existed changes.
public class FootprintRotationTests
{
    // The ground control station: 12 x 8 cells, no doors, so it may turn.
    private const string Station = "watcher_station";

    private static (World W, Unit Worker) Yard()
    {
        var w = Arena.NewSentinel(64);
        return (w, w.Spawn(1, "worker", 18, 18));
    }

    private static Unit? Build(World w, Unit worker, string unit, Cell at, int facing)
    {
        var before = w.Units.Count;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = at, Unit = unit, Facing = facing });
        w.Step();
        return w.Units.FirstOrDefault(u => u.Def.Archetype.Id == unit && !u.IsBuilt);
    }

    [Theory]
    [InlineData(Facing.North, 12, 8, false)]
    [InlineData(Facing.East, 8, 12, true)]
    [InlineData(Facing.South, 12, 8, false)]
    [InlineData(Facing.West, 8, 12, true)]
    public void EveryQuarterTurnSwapsOnlyAtOddTurns(int facing, int fw, int fh, bool turned)
    {
        var (w, worker) = Yard();
        var site = Assert.IsType<Unit>(Build(w, worker, Station, new Cell(20, 20), facing));
        Assert.Equal(turned, site.Turned);
        Assert.Equal((fw, fh), (World.FootW(site), World.FootH(site)));
        Assert.Equal(new Cell(20, 20), World.FootprintOrigin(site));
        Assert.Equal(Fixed.FromInt(20).Raw + Fixed.FromInt(fw).Raw / 2, site.Pos.X.Raw);
        Assert.Equal(Fixed.FromInt(20).Raw + Fixed.FromInt(fh).Raw / 2, site.Pos.Y.Raw);
        Assert.Equal(facing, site.Facing);
    }

    [Fact]
    public void OverlapUsesTheTurnedFootprint()
    {
        var (w, worker) = Yard();
        Assert.NotNull(Build(w, worker, Station, new Cell(20, 20), Facing.East)); // cells x 20-27, y 20-31
        var depot = w.Player(1)!.Setup.Find(w.Content, "supply_depot")!;
        Assert.False(w.CanPlace(depot, new Cell(20, 28), 0, [worker])); // inside the turned footprint only
        Assert.True(w.CanPlace(depot, new Cell(28, 20), 0, [worker]));  // inside the unturned footprint only
        Assert.False(w.CanPlace(depot, new Cell(23, 26), 0, [worker]));
    }

    [Fact]
    public void PlacementChecksTheTurnedCells()
    {
        var (w, worker) = Yard();
        var station = w.Player(1)!.Setup.Find(w.Content, Station)!;
        // 12 wide unturned, 8 wide turned: at x 54 only the turned one fits on a 64-cell map.
        Assert.False(w.CanPlace(station, new Cell(54, 20), Facing.North, [worker]));
        Assert.True(w.CanPlace(station, new Cell(54, 20), Facing.East, [worker]));
        // 12 tall turned: at y 54 only the unturned one fits.
        Assert.True(w.CanPlace(station, new Cell(20, 54), Facing.North, [worker]));
        Assert.False(w.CanPlace(station, new Cell(20, 54), Facing.West, [worker]));
    }

    [Fact]
    public void LayoutsFromTheTopLeftNeverTurn()
    {
        var (w, worker) = Yard();
        var garage = w.Player(1)!.Setup.Find(w.Content, "vehicle_garage")!; // doors and exits
        Assert.False(w.CanPlace(garage, new Cell(20, 20), Facing.East, [worker]));
        Assert.False(w.CanPlace(garage, new Cell(20, 20), Facing.West, [worker]));
        Assert.True(w.CanPlace(garage, new Cell(20, 20), Facing.South, [worker]));
        Assert.False(World.CanTurn(garage.Archetype));
        Assert.False(World.CanTurn(w.Content.Archetype("observation_post")!)); // garrison doors
        Assert.False(World.CanTurn(w.Content.Archetype("tunnel_large")!));
        // Square footprints never turn, whatever the facing.
        var depot = w.Content.Archetype("supply_depot")!;
        Assert.False(World.TurnsAt(depot, Facing.East));
        Assert.Equal((6, 6), World.Footprint(depot, Facing.West));
    }

    [Fact]
    public void SpawnedStructuresKeepTheirLayoutWhateverTheirFacing()
    {
        // Scenarios spawn a side's structures facing East or West: they stay unturned, as before.
        var w = Arena.NewSentinel(64);
        var s = w.Spawn(1, Station, 20, 20, Facing.East);
        Assert.False(s.Turned);
        Assert.Equal((12, 8), (World.FootW(s), World.FootH(s)));
    }

    [Theory]
    [InlineData(Facing.North)]
    [InlineData(Facing.East)]
    [InlineData(Facing.South)]
    [InlineData(Facing.West)]
    public void RoutingBlocksTheCellsAsTheyStand(int facing)
    {
        var (w, worker) = Yard();
        var site = Assert.IsType<Unit>(Build(w, worker, Station, new Cell(20, 20), facing));
        var r = w.Routing;
        r.SyncStructures(w.Units);
        var k = r.Nav.Class(0, 1);
        var o = World.FootprintOrigin(site);
        for (var y = 14; y < 40; y++)
        {
            for (var x = 14; x < 40; x++)
            {
                var inside = x >= o.X && x < o.X + World.FootW(site) && y >= o.Y && y < o.Y + World.FootH(site);
                if (inside)
                {
                    Assert.Equal(0, r.Nav.Cost(k, x, y));
                }
                else if (x < o.X - 1 || x > o.X + World.FootW(site) || y < o.Y - 1 || y > o.Y + World.FootH(site))
                {
                    Assert.NotEqual(0, r.Nav.Cost(k, x, y)); // a cell clear of the footprint's edge
                }
            }
        }
    }

    [Theory]
    [InlineData(Facing.North)]
    [InlineData(Facing.East)]
    public void AimPointAndSpatialIndexUseTheTurnedBox(int facing)
    {
        var (w, worker) = Yard();
        var site = Assert.IsType<Unit>(Build(w, worker, Station, new Cell(20, 20), facing));
        long fw = World.FootW(site), fh = World.FootH(site);
        // From far below and right, the nearest footprint point is its bottom-right corner.
        var p = new Vec(Fixed.FromInt(60), Fixed.FromInt(60));
        var aim = World.AimPoint(p, site);
        Assert.Equal((20 + fw) * Fixed.OneRaw, aim.X.Raw);
        Assert.Equal((20 + fh) * Fixed.OneRaw, aim.Y.Raw);

        SpatialIndex.Extent(site, out var x0, out var y0, out var x1, out var y1);
        Assert.Equal((20 * Fixed.OneRaw, 20 * Fixed.OneRaw, (20 + fw) * Fixed.OneRaw, (20 + fh) * Fixed.OneRaw), (x0, y0, x1, y1));
        var index = new SpatialIndex();
        index.Build([.. w.Units], w.Grid);
        var found = new List<Unit>();
        // A point just inside the far corner of the footprint as it stands.
        long qx = (20 + fw) * Fixed.OneRaw - 10, qy = (20 + fh) * Fixed.OneRaw - 10;
        index.Query(qx, qy, qx, qy, found);
        Assert.Contains(site, found);
    }
}

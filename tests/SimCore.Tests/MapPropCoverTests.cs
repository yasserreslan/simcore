using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-ROCK (docs/design.md rows 1354-1358): a town's props (map.json's roadside paint, World.PlaceProp) are cover by the content's
// infantryCombat.coverProps: garden walls, planters, the fountain and parked cars hard low, sheds and hay huts hard high, the bus
// shelter soft, hedges and wooden fences concealment only. None blocks movement.
public class MapPropCoverTests
{
    private static World New(int size = 40, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false; // on before any unit is added (InfantryCombatTests)
        return w;
    }

    private static InfantryCombatRules R(World w) => w.Content.InfantryCombat!;

    private static Vec At(double x, double y) => new(Fixed.FromRaw((long)(x * Fixed.OneRaw)), Fixed.FromRaw((long)(y * Fixed.OneRaw)));

    private static void Stance(World w, Stance st, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.SetStance, Units = [.. units.Select(u => u.Id)], Stance = st });

    [Fact]
    public void TheContentListsEachTownPropKind()
    {
        var r = R(New());
        Assert.Equal(PropCover.Low, r.Prop("garden_wall")!.Value.Cover);
        Assert.Equal(PropCover.Low, r.Prop("planters")!.Value.Cover);
        Assert.Equal(PropCover.Low, r.Prop("fountain")!.Value.Cover);
        Assert.Equal(PropCover.Low, r.Prop("parked_cars")!.Value.Cover);
        Assert.Equal(PropCover.High, r.Prop("garden_shed")!.Value.Cover);
        Assert.Equal(PropCover.High, r.Prop("valley_hay_hut")!.Value.Cover);
        Assert.Equal(PropCover.Soft, r.Prop("bus_shelter")!.Value.Cover);
        Assert.Equal(PropCover.Conceal, r.Prop("hedge")!.Value.Cover);
        Assert.Equal(PropCover.Conceal, r.Prop("fence_wood")!.Value.Cover);
        Assert.Null(r.Prop("street_lamp")); // a kind the content does not list is nothing to the engine
    }

    // The same rock-and-rifleman scene as InfantryThinkTests' low-cover test, with a garden wall piece (4 m, north-south: yaw 90)
    // two cells to his side instead of the rock: under fire he takes the wall's side, not the open cell he stands on.
    [Fact]
    public void AManInContactPicksAGardenWallOverTheOpen()
    {
        var w = New();
        w.PlaceProp("garden_wall", At(9.5, 13.0), 900); // cells (9, 12) and (9, 13): low cover on (10, 11) to (10, 14), facing west
        Assert.Equal(Terrain.Clear, w.Grid.At(new Cell(9, 12))); // it blocks no one
        var blue = w.Spawn(1, "engineer_sapper", 10, 10); // Guard, and he never chases (pursue false)
        var red = w.Spawn(2, "Rifleman", 6, 10);
        Stance(w, Sim.Stance.HoldGround, red);
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(10, 10), 4));
        Assert.Equal(CoverKind.Low, w.CoverSide(new Cell(10, 12), 4));
        w.Run(120);
        var c = Cell.Of(blue.Pos);
        Assert.True(c.X == 10 && c.Y is >= 11 and <= 14, $"at the wall's side (at {c})");
        Assert.True(blue.Fight!.HasSpot);
    }

    [Fact]
    public void AParkedCarIsHardLowCoverAgainstRounds()
    {
        var w = New();
        var r = R(w);
        w.PlaceProp("parked_cars", At(11.5, 10.5), 900); // its long axis north-south, east of (10, 10)
        var c = new Cell(10, 10);
        Assert.Equal(CoverKind.Low, w.CoverSide(c, 0));
        Assert.Equal(r[CombatValue.LowFiring], w.Protection(c, Posture.Kneel, 0, false));
        Assert.Equal(r[CombatValue.LowDucked], w.Protection(c, Posture.Kneel, 0, true));
        Assert.Equal(r[CombatValue.LowFiring] * r[CombatValue.LowStanding] / 100, w.Protection(c, Posture.Stand, 0, false));
        Assert.Equal(0, w.Protection(c, Posture.Kneel, 16, false)); // from the west: flanked
        // The round: the shooter's hit chance on a kneeling man behind the car against one in the open.
        var shooter = w.Spawn(2, "Rifleman", 14, 10);
        var behind = w.Spawn(1, "Rifleman", 10, 10);
        var open = w.Spawn(1, "Rifleman", 10, 20);
        var shooter2 = w.Spawn(2, "Rifleman", 14, 20);
        behind.Fight!.Posture = Posture.Kneel;
        open.Fight!.Posture = Posture.Kneel;
        var hit = w.CombatChance(shooter, behind, 1000);
        Assert.Equal(w.CombatChance(shooter2, open, 1000) * (100 - r[CombatValue.LowFiring]) / 100, hit);
    }

    [Fact]
    public void AHedgeHidesAProneManButStopsNoRounds()
    {
        var w = New();
        var r = R(w);
        w.PlaceProp("hedge", At(11.5, 10.5), 900); // east of (10, 10)
        w.Run(r[CombatValue.ConcealSeenTicks] + 2); // nobody has seen him lately
        var c = new Cell(10, 10);
        Assert.Equal(CoverKind.None, w.CoverSide(c, 0)); // no cover
        Assert.Equal(0, w.Protection(c, Posture.Prone, 0, false)); // no protection: rounds go through
        var u = w.Spawn(1, "Rifleman", 10, 10);
        var e = w.Spawn(2, "Rifleman", 13, 10); // 3 cells east, beyond the hedge: inside 60% of the rifle's range
        u.Fight!.ContactList.Add(new Contact { Enemy = e.Id, At = Cell.Of(e.Pos), Source = ContactSource.SeenNow, Class = CombatClass.Rifle });
        u.Fight.InContact = true;
        var cf = r[CombatValue.ConcealFactor];
        Assert.Equal(10L * 100 * r[CombatValue.HitProne] * cf * 100 / 1_000_000, w.Exposure(u, c, Posture.Prone)); // hidden: 5.3's concealFactor
        Assert.Equal(10L * 100 * r[CombatValue.HitKneel] * cf * 100 / 1_000_000, w.Exposure(u, c, Posture.Kneel));
        Assert.Equal(1000, w.Exposure(u, c, Posture.Stand)); // a standing man shows over it
        u.Fight.SpottedAt = w.Tick; // seen a moment ago: he may be found, the hedge hides him no more
        Assert.Equal(10L * 100 * r[CombatValue.HitProne] * 100 * 100 / 1_000_000, w.Exposure(u, c, Posture.Prone));
        // From the west (away from the hedge): nothing.
        var e2 = w.Spawn(2, "Rifleman", 7, 20);
        u.Fight.SpottedAt = 0;
        u.Fight.ContactList.Clear();
        u.Fight.ContactList.Add(new Contact { Enemy = e2.Id, At = new Cell(7, 10), Source = ContactSource.SeenNow, Class = CombatClass.Rifle });
        Assert.Equal(10L * 100 * r[CombatValue.HitProne] * 100 * 100 / 1_000_000, w.Exposure(u, c, Posture.Prone));
    }

    [Fact]
    public void AShedIsHighCoverAndTheBusShelterSoft()
    {
        var w = New();
        var r = R(w);
        w.PlaceProp("garden_shed", At(11.5, 10.5), 0);
        w.PlaceProp("bus_shelter", At(11.5, 20.5), 900);
        Assert.Equal(CoverKind.High, w.CoverSide(new Cell(10, 10), 0));
        Assert.Equal(r[CombatValue.HighFiring], w.Protection(new Cell(10, 10), Posture.Stand, 0, false)); // high: standing too
        Assert.Equal(CoverKind.Soft, w.CoverSide(new Cell(10, 20), 0));
        Assert.Equal(r[CombatValue.SoftFiring], w.Protection(new Cell(10, 20), Posture.Kneel, 0, false));
    }

    // A piece's long axis runs along (cos yaw, -sin yaw) on the map (y south), as the client draws it: a wall at yaw 0 lies east-west
    // and covers the cells north and south of it; at yaw 90 it lies north-south.
    [Fact]
    public void APiecesTurnLaysItsBoxOnTheCells()
    {
        var r = R(New());
        static List<int> Cells(MapProp p, CoverProp cp)
        {
            var l = new List<int>();
            World.PropCells(p, cp, 40, 40, i => { if (!l.Contains(i)) { l.Add(i); } });
            l.Sort();
            return l;
        }
        var wall = r.Prop("garden_wall")!.Value;
        Assert.Equal(new[] { 10 * 40 + 9, 10 * 40 + 10 }, Cells(new MapProp("garden_wall", At(10.0, 10.5), 0), wall));
        Assert.Equal(new[] { 9 * 40 + 10, 10 * 40 + 10 }, Cells(new MapProp("garden_wall", At(10.5, 10.0), 900), wall));
        Assert.Equal(new[] { 9 * 40 + 10, 10 * 40 + 10 }, Cells(new MapProp("garden_wall", At(10.5, 10.0), -900), wall));
        Assert.Equal(Cells(new MapProp("garden_wall", At(10.5, 10.0), 900), wall), Cells(new MapProp("garden_wall", At(10.5, 10.0), 2700), wall));
    }

    // map.json's paint entries of layer "roadside" placed with "at" are the map's props (TerrainMap.Props); along a line ("points"),
    // another layer or off the map they are the renderer's alone. One of each kind, then the cover they make on an arena.
    [Fact]
    public void AMapsRoadsidePropsLoadAndBecomeCover()
    {
        var rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));
        var dir = Path.Combine(Path.GetTempPath(), "simcore-props-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        TerrainMap t, bare;
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.json"), """
                {
                  "width": 40, "height": 40,
                  "paint": [
                    { "layer": "roadside", "prop": "garden_wall", "at": [11.5, 5.5], "yaw_deg": 90, "state": "intact" },
                    { "layer": "roadside", "prop": "planters", "at": [11.5, 9.5], "yaw_deg": 0 },
                    { "layer": "roadside", "prop": "fountain", "at": [12.0, 14.0] },
                    { "layer": "roadside", "prop": "parked_cars", "at": [11.5, 19.5], "yaw_deg": 90 },
                    { "layer": "roadside", "prop": "garden_shed", "at": [11.5, 23.5], "yaw_deg": 0 },
                    { "layer": "roadside", "prop": "valley_hay_hut", "at": [12.5, 27.5], "yaw_deg": 0 },
                    { "layer": "roadside", "prop": "bus_shelter", "at": [11.5, 31.5], "yaw_deg": 90 },
                    { "layer": "roadside", "prop": "hedge", "at": [11.5, 35.5], "yaw_deg": 90 },
                    { "layer": "roadside", "prop": "fence_wood", "at": [21.5, 35.5], "yaw_deg": -90.0 },
                    { "layer": "roadside", "prop": "street_lamp", "at": [30.5, 30.5] },
                    { "layer": "roadside", "prop": "garden_wall", "points": [[1, 1], [8, 1]] },
                    { "layer": "flowers", "prop": "garden_wall", "at": [30.5, 5.5] },
                    { "layer": "roadside", "prop": "garden_wall", "at": [45.5, 5.5] }
                  ]
                }
                """);
            t = TerrainLoader.LoadMap(Path.Combine(dir, "map.json"), rules);
            File.WriteAllText(Path.Combine(dir, "bare.json"), """{ "width": 40, "height": 40 }""");
            bare = TerrainLoader.LoadMap(Path.Combine(dir, "bare.json"), rules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
        Assert.Equal(10, t.Props.Count);
        Assert.Equal(new[] { "garden_wall", "planters", "fountain", "parked_cars", "garden_shed", "valley_hay_hut", "bus_shelter", "hedge", "fence_wood", "street_lamp" },
            t.Props.Select(p => p.Kind));
        Assert.Equal(At(11.5, 5.5), t.Props[0].At);
        Assert.Equal((900, -900, 0), (t.Props[0].YawTenths, t.Props[8].YawTenths, t.Props[2].YawTenths));
        var w = New();
        w.AttachTerrain(t);
        Assert.Equal(CoverKind.Low, w.CoverSide(new Cell(10, 5), 0)); // the wall
        Assert.Equal(CoverKind.Low, w.CoverSide(new Cell(10, 9), 0)); // the planter
        Assert.Equal(CoverKind.Low, w.CoverSide(new Cell(10, 14), 0)); // the fountain's basin
        Assert.Equal(CoverKind.Low, w.CoverSide(new Cell(10, 19), 0)); // the car
        Assert.Equal(CoverKind.High, w.CoverSide(new Cell(10, 23), 0)); // the shed
        Assert.Equal(CoverKind.High, w.CoverSide(new Cell(10, 27), 0)); // the hay hut
        Assert.Equal(CoverKind.Soft, w.CoverSide(new Cell(10, 31), 0)); // the bus shelter
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(10, 35), 0)); // the hedge: concealment only
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(20, 35), 0)); // the fence: the same
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(29, 30), 0)); // the lamp: nothing
        Assert.Equal(CoverKind.None, w.CoverSide(new Cell(29, 5), 0)); // another layer: nothing
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                Assert.NotEqual(Terrain.Blocked, w.Grid.At(new Cell(x, y))); // no prop blocks movement
            }
        }
        // The terrain digest takes the props (a map without them hashes as before: the goldens).
        Assert.NotEqual(t.Digest(), bare.Digest());
        Assert.Empty(bare.Props);
    }

    [Fact]
    public void PlacedPropsAreHashedAndReplay()
    {
        ulong Run(bool prop)
        {
            var w = New();
            if (prop)
            {
                w.PlaceProp("garden_wall", At(9.5, 13.0), 900);
            }
            var blue = w.Spawn(1, "engineer_sapper", 10, 10);
            var red = w.Spawn(2, "Rifleman", 6, 10);
            Stance(w, Sim.Stance.HoldGround, red);
            w.Run(80);
            _ = blue;
            return w.Hash();
        }
        Assert.Equal(Run(true), Run(true));
        Assert.NotEqual(Run(true), Run(false));
    }
}

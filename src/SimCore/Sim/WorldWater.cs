using SimCore.FixedPoint;

namespace SimCore.Sim;

// Water (docs/design.md, "Water"): rivers, lakes and the sea of the map's terrain (TerrainMap.WaterBodies). Open water is
// Terrain.Water in the world's grid, so everything that asks the grid whether a cell is passable (placement, spawning,
// unloading, landing spots, move orders' goals) keeps ground units out of it, and its terrain cost is 0 for every ground
// class (routing, movement). Shots and sight cross it at its surface (TerrainMap.HeightAt). A blast in open water digs no
// crater and its events carry Water. A map without water changes nothing.
public sealed partial class World
{
    /// <summary>
    /// Reports whether p lies in open water (a river, lake or sea cell without a deck): a blast there goes off in the
    /// water. False on a map without terrain or water. For renderers too: any impact the events do not flag.
    /// </summary>
    public bool InWater(Vec p) => Ground is { HasWater: true } g && g.OpenWater(Cell.Of(p));

    /// <summary>Reports whether cell c is open water (false without terrain or water).</summary>
    public bool InWater(Cell c) => Ground is { HasWater: true } g && g.OpenWater(c);

    // WaterAt is whether a blast at `at` on unit hit (0: the ground, the surface) went off in the water.
    private bool WaterAt(Vec at, uint hit) => hit == 0 && InWater(at);

    /// <summary>
    /// Lays (or lifts) a deck over a water cell, a bridge's deck (BridgeSystem.cs): the terrain's deck
    /// (<see cref="TerrainMap.SetDeck"/>: its cost as flat dry ground, no longer open water) and the grid in step (open water
    /// on the grid becomes clear, and back). A cell a structure holds keeps its block. Ignored without terrain or on a dry
    /// cell. Not a command: bridges lay it when put up and lift it when their span falls.
    /// </summary>
    internal void SetDeck(Cell c, bool on)
    {
        if (Ground is not { } g || !g.Water(c))
        {
            return;
        }
        g.SetDeck(c, on);
        if (on && Grid.At(c) == Terrain.Water)
        {
            Grid.Set(c, Terrain.Clear);
        }
        else if (!on && Grid.At(c) == Terrain.Clear)
        {
            Grid.Set(c, Terrain.Water);
        }
    }

    /// <summary>
    /// Marks a terrain's open water on a grid of its size (clear cells only: rock and buildings keep their block). The
    /// world does it when the terrain is attached; scenarios call it first to form armies up on dry ground.
    /// </summary>
    internal static void MarkWater(Grid grid, TerrainMap terrain)
    {
        if (!terrain.HasWater || grid.Width != terrain.Width || grid.Height != terrain.Height)
        {
            return;
        }
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var c = new Cell(x, y);
                if (terrain.OpenWater(c) && grid.At(c) == Terrain.Clear)
                {
                    grid.Set(c, Terrain.Water);
                }
            }
        }
    }
}

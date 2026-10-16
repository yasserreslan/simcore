// Starting worlds.
using SimCore.FixedPoint;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Scenarios;

/// <summary>Builds starting worlds.</summary>
public static class Scenario
{
    /// <summary>The Skirmish map's width and height in cells.</summary>
    public const int Size = 64;

    // Army is each side's starting force, in spawn (and therefore ID) order.
    private static readonly UnitTypeId[] Army =
    [
        UnitTypeId.BattleTank, UnitTypeId.BattleTank, UnitTypeId.BattleTank, UnitTypeId.BattleTank,
        UnitTypeId.Rifleman, UnitTypeId.Rifleman, UnitTypeId.Rifleman, UnitTypeId.Rifleman, UnitTypeId.Rifleman, UnitTypeId.Rifleman,
        UnitTypeId.RocketTrooper, UnitTypeId.RocketTrooper,
        UnitTypeId.ScoutBuggy, UnitTypeId.ScoutBuggy,
        UnitTypeId.Artillery,
    ];

    /// <summary>How many units each player starts with.</summary>
    public static int ArmySize => Army.Length;

    /// <summary>
    /// Returns a 64×64 two-player map. A ridge splits it down the middle with
    /// two 4-cell gaps, the chokepoints both armies must path through. Rock
    /// clusters are placed from the seed and mirrored, so the map is fair.
    /// Player 1 starts on the left, player 2 on the right.
    /// </summary>
    public static World Skirmish(ulong seed)
    {
        var g = new Grid(Size, Size);
        for (var y = 8; y < Size - 8; y++)
        {
            if ((y >= 20 && y < 24) || (y >= 40 && y < 44))
            {
                continue;
            }
            g.Set(new Cell(Size / 2 - 1, y), Terrain.Blocked);
            g.Set(new Cell(Size / 2, y), Terrain.Blocked);
        }

        var r = new Rand(seed);
        for (var i = 0; i < 12; i++)
        {
            var x = r.Range(14, 26); // x is drawn before y
            var y = r.Range(2, Size - 4);
            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    g.Set(new Cell(x + dx, y + dy), Terrain.Blocked);
                    g.Set(new Cell(Size - 1 - x - dx, y + dy), Terrain.Blocked);
                }
            }
        }

        var w = new World(g, seed, 2);
        SpawnArmy(w, 1, new Cell(7, Size / 2), Facing.East);
        SpawnArmy(w, 2, new Cell(Size - 8, Size / 2), Facing.West);
        return w;
    }

    private static void SpawnArmy(World w, byte player, Cell b, int facing)
    {
        for (var i = 0; i < Army.Length; i++)
        {
            w.Grid.TrySpiral(b, i, out var c);
            w.Spawn(Army[i], player, c, facing);
        }
    }
}

// Starting worlds.
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;
using SimCore.Sim;

namespace SimCore.Scenarios;

/// <summary>Thrown for a scenario name not in the registry.</summary>
public sealed class UnknownScenarioException(string name)
    : MatchSetupException($"match: unknown scenario \"{name}\"");

/// <summary>Builds starting worlds from compiled match setups.</summary>
public static class Scenario
{
    /// <summary>The Battle map's width and height in cells.</summary>
    public const int BattleSize = 96;

    // Where each army forms up and where its structures stand, for player 1
    // (left); player 2's are mirrored.
    private static readonly Cell Rally = new(14, BattleSize / 2);
    private static readonly Cell Base = new(4, BattleSize / 2 - 6);

    /// <summary>Builds the starting world of the setup's scenario.</summary>
    /// <exception cref="UnknownScenarioException">The scenario name is not registered.</exception>
    /// <exception cref="MatchSetupException">The setup does not fit the scenario.</exception>
    public static World Create(CompiledMatch match) => match.Setup.Scenario switch
    {
        "battle" => Battle(match),
        var name => throw new UnknownScenarioException(name ?? ""),
    };

    /// <summary>
    /// Returns the Phase 1 battle: a 96×96 two-player map with no bases and
    /// no economy. A ridge splits it down the middle with three 4-cell gaps;
    /// rock clusters are placed from the seed and mirrored, so the map is
    /// fair. Each player's army from the setup forms up on its side, player 1
    /// on the left, with its structures (a supply depot) behind it. Vehicles
    /// listed with a driver spawn with the driver inside; the rest spawn empty.
    /// </summary>
    /// <exception cref="MatchSetupException">The setup does not have exactly two players.</exception>
    public static World Battle(CompiledMatch match)
    {
        if (match.Players.Count != 2)
        {
            throw new MatchSetupException($"match: scenario \"battle\" wants 2 players, got {match.Players.Count}");
        }
        const int n = BattleSize;
        var g = new Grid(n, n);
        for (var y = 10; y < n - 10; y++)
        {
            if ((y >= 22 && y < 26) || (y >= 46 && y < 50) || (y >= 70 && y < 74))
            {
                continue;
            }
            g.Set(new Cell(n / 2 - 1, y), Terrain.Blocked);
            g.Set(new Cell(n / 2, y), Terrain.Blocked);
        }

        var r = new Rand(match.Setup.Seed);
        for (var i = 0; i < 18; i++)
        {
            var x = r.Range(22, 40); // x is drawn before y
            var y = r.Range(2, n - 4);
            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    g.Set(new Cell(x + dx, y + dy), Terrain.Blocked);
                    g.Set(new Cell(n - 1 - x - dx, y + dy), Terrain.Blocked);
                }
            }
        }

        var w = new World(g, match);
        SpawnArmy(w, match.Players[0], false);
        SpawnArmy(w, match.Players[1], true);
        return w;
    }

    private static void SpawnArmy(World w, CompiledPlayer p, bool mirrored)
    {
        var facing = mirrored ? Facing.West : Facing.East;
        int spot = 0, baseY = Base.Y;
        foreach (var (def, driver) in p.Army)
        {
            if (def.IsStructure)
            {
                var a = def.Archetype;
                var x = mirrored ? BattleSize - Base.X - a.FootprintW : Base.X;
                w.Spawn(def, new Cell(x, baseY), facing);
                baseY += a.FootprintH + 1;
                continue;
            }
            // Player 1's formation, reflected for player 2: the rock is
            // mirrored too, so the same cells are free on both sides.
            w.Grid.TrySpiral(Rally, spot++, out var c);
            if (mirrored)
            {
                c = new Cell(BattleSize - 1 - c.X, c.Y);
            }
            var id = w.Spawn(def, c, facing);
            if (driver is not null)
            {
                World.Board(w.Unit(w.Spawn(driver, c, facing))!, w.Unit(id)!);
            }
        }
    }
}

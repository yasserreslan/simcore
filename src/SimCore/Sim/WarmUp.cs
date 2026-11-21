using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-PERF (docs/design.md, "Squads: the order tick"): the first squad order of each kind in a session paid the .NET JIT on its
// tick: the order-time code (final positions, Hold's and the ambush's picking, the mines) runs only when an order lands, so it was
// still uncompiled when the player gave it, and then ran unoptimised (tier 0) until the runtime got round to it. Measured on
// SimRun -order-bench: a squad of 10's first Hold 13 ms, of which 10.5 ms compiling 253 methods; its second 11 ms (compiling
// loops); fully compiled code 0.4 ms. WarmUp plays those orders once on a world of its own at load, like a loading screen.
public sealed partial class World
{
    private const int WarmSize = 48;

    /// <summary>
    /// Plays a squad's orders (March, Assault, Attack-move, Hold, Ambush) on a small world of its own made from this world's match,
    /// so the code they run is compiled before a player gives one (call it once at load, after <see cref="PrepareRouting()"/>; a
    /// few tens of ms the first time in a process, little after). This world is not touched: its state and hash stay the same.
    /// </summary>
    public void WarmUp()
    {
        if (Content.Squad is not { } rules || Match.Players.Count == 0)
        {
            return;
        }
        var w = new World(new Grid(WarmSize, WarmSize), Match);
        if (Ground is { } g)
        {
            w.AttachTerrain(WarmGround(g.Rules));
        }
        var p = Match.Players[0];
        var kinds = new List<UnitDef>();
        UnitDef? house = null;
        foreach (var d in p.Presets.Concat(p.Defaults.Where(d => d is not null).Select(d => d!)))
        {
            if (!d.IsStructure && d.Layer == Layers.Ground && d.Speed.Raw > 0 && d.Archetype.Flight is null && rules.Joins(d.Tags))
            {
                kinds.Add(d);
            }
            else if (house is null && d.IsStructure && d.Archetype.Garrison is not null)
            {
                house = d;
            }
        }
        if (kinds.Count == 0)
        {
            return;
        }
        if (house is not null)
        {
            w.Spawn(house, new Cell(24, 14), Facing.South);
        }
        if (Match.Players.Count > 1 && Match.Players[1].Presets.Concat(Match.Players[1].Defaults.Where(d => d is not null).Select(d => d!))
            .FirstOrDefault(d => !d.IsStructure && rules.Joins(d.Tags)) is { } red)
        {
            w.Spawn(red, new Cell(WarmSize - 2, WarmSize - 2), Facing.North); // the other side, far off: the match stays open
        }
        var men = new List<uint>();
        for (var i = 0; i < 10; i++)
        {
            men.Add(w.Spawn(kinds[i % kinds.Count], new Cell(6 + i % 5, 22 + i / 5 * 2), Facing.East));
        }
        var lead = men[0];
        void Order(Command c, int ticks)
        {
            w.Issue(c with { Tick = w.Tick, Player = p.Id, Units = c.Units.Count == 0 ? [lead] : c.Units });
            for (var t = 0; t < ticks; t++)
            {
                w.Step();
            }
        }
        Order(new Command { Kind = CommandKind.FormSquad, Units = [.. men] }, 4);
        Order(new Command { Kind = CommandKind.Move, Cell = new Cell(30, 24) }, 12);
        Order(new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Assault }, 2);
        Order(new Command { Kind = CommandKind.Move, Cell = new Cell(14, 26) }, 12);
        Order(new Command { Kind = CommandKind.AttackMove, Cell = new Cell(30, 30) }, 8);
        Order(new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Hold, Cell = new Cell(22, 22), Cell2 = new Cell(34, 22) }, 12);
        Order(new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.Ambush, Cell = new Cell(34, 34), Cell2 = new Cell(34, 12) }, 12);
        Order(new Command { Kind = CommandKind.SetSquadMode, Index = (int)SquadMode.March }, 2);
    }

    // WarmGround: rolling ground, a grove and a wood, so the order code's terrain paths (heights, canopy, cover) are taken.
    private static TerrainMap WarmGround(TerrainRules rules)
    {
        const int n = WarmSize;
        var corners = new int[(n + 1) * (n + 1)];
        var flags = new byte[n * n];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = 1024 + Math.Abs(x % 16 - 8) * 48 + Math.Abs(y % 12 - 6) * 32;
                if (x < n && y < n && x is >= 26 and < 40 && (y is >= 6 and < 14 || y is >= 30 and < 38))
                {
                    flags[y * n + x] = (byte)(1 + (x + y) % 3); // forest density 1 to 3
                }
            }
        }
        return new TerrainMap(n, n, corners, flags, rules);
    }
}

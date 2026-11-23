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
        WarmFight();
        WarmRoute(kinds[0]);
        WarmSquadRoutes();
    }

    /// <summary>What <see cref="WarmFight()"/>'s fight reached (INF-WARM-2's test: the squad think really ran).</summary>
    internal sealed class WarmSeen
    {
        public bool Contact, Choice, Line, Bound, Marching, Hit, Wound, Kill, Morale, Call, Withdraw, Consolidate, Scavenge, Building;
        public uint Ticks;

        public bool Fought => Contact && Choice && Line && (Bound || Marching) && Hit && Wound && Kill && Morale && Call;
    }

    // INF-WARM-2 (docs/design.md, decision 1309): the first fight of a session paid the JIT too (the game's --demo=firefight: a 24
    // ms sim step at its first contact): the squad think (the picture, R, the choice, the line, bounds, marching fire, morale, calls,
    // withdrawals) and the soldiers' fight (cover search, wounds, bodies) run only in contact. WarmFight plays a small fight on a
    // world of its own: blue's 6 on Guard Attack-move onto red's 3 on Guard behind a short wall with 2 more behind them (red is
    // outgunned: it calls, the second answers, it withdraws or holds), until both sides have hit, wounded, killed and changed
    // morale and blue has bounded or marched firing, at most WarmFightTicks; then blue scavenges the dead.
    private const int WarmFightTicks = 700;

    internal WarmSeen WarmFight()
    {
        var seen = new WarmSeen();
        if (Content.Squad is not { } rules || Match.Players.Count < 2)
        {
            return seen;
        }
        var w = new World(new Grid(WarmSize, WarmSize), Match);
        if (Ground is { } g)
        {
            w.AttachTerrain(WarmGround(g.Rules));
        }
        List<UnitDef> Kinds(int i) => [.. Match.Players[i].Presets.Concat(Match.Players[i].Defaults.Where(d => d is not null).Select(d => d!))
            .Where(d => !d.IsStructure && d.Layer == Layers.Ground && d.Speed.Raw > 0 && d.Archetype.Flight is null && rules.Joins(d.Tags))];
        var (bk, rk) = (Kinds(0), Kinds(1));
        if (bk.Count == 0 || rk.Count == 0)
        {
            return seen;
        }
        for (var y = 19; y <= 25; y++)
        {
            w.Grid.Set(new Cell(21, y), Terrain.Blocked); // a low wall in front of red: cover, its edge, a wall across a walk
        }
        if (Match.Players[0].Find(Content, "house_small") is { Archetype.Garrison: not null } house)
        {
            w.Spawn(house, new Cell(12, 14), Facing.South); // INF-E1b-8 (6.6): an empty house by blue's way, its door at (14, 19): the building candidates
        }
        byte bp = Match.Players[0].Id, rp = Match.Players[1].Id;
        List<uint> Squad(byte player, List<UnitDef> kinds, int n, int x, int y, int facing)
        {
            var ids = new List<uint>();
            for (var i = 0; i < n; i++)
            {
                ids.Add(w.Spawn(kinds[i % kinds.Count], new Cell(x + i / 4, y + i % 4), facing));
            }
            w.Issue(new Command { Tick = w.Tick, Player = player, Kind = CommandKind.FormSquad, Units = ids });
            return ids;
        }
        var blue = Squad(bp, bk, 6, 10, 20, Facing.East);
        var red = Squad(rp, rk, 3, 22, 21, Facing.West);
        var reserve = Squad(rp, rk, 2, 30, 22, Facing.West);
        w.Step();
        w.Issue(new Command { Tick = w.Tick, Player = bp, Kind = CommandKind.AttackMove, Units = [blue[0]], Cell = new Cell(34, 22) });
        var morale = new List<(uint Squad, int Value)>();
        for (var t = 0; t < WarmFightTicks && !seen.Fought; t++)
        {
            w.Step();
            foreach (var q in w.Squads)
            {
                if (q.Mind is not { } m)
                {
                    continue;
                }
                seen.Contact |= m.InContact;
                seen.Choice |= m.Choice != SquadChoice.None;
                seen.Line |= m.HasLine;
                seen.Bound |= m.Moving != 0;
                seen.Marching |= m.Marching;
                seen.Call |= m.Calling;
                seen.Withdraw |= m.Choice == SquadChoice.Withdraw;
                seen.Consolidate |= m.ConsolidateUntil > w.Tick;
                var i = morale.FindIndex(x => x.Squad == q.Id);
                if (i < 0)
                {
                    morale.Add((q.Id, m.Morale100));
                }
                else
                {
                    seen.Morale |= morale[i].Value >= 0 && m.Morale100 >= 0 && m.Morale100 != morale[i].Value;
                    morale[i] = (q.Id, m.Morale100);
                }
            }
            seen.Hit |= w.Units.Any(u => u.Alive && u.Fight is not null && u.HP < u.Def.MaxHp);
            seen.Wound |= w.Units.Any(u => u.Alive && u.Life is { Wound: WoundState.Wounded or WoundState.Down });
            seen.Kill |= w.Bodies.Count > 0;
            seen.Building |= w.Units.Any(u => u.Fight is { Building: not 0 });
        }
        // (INF-E1b-7, row 1322: a short fight can leave every blue man's load full, nothing to take from any body: the first healthy
        // one has then spent half his stocks, so the order's walk and take still run)
        if (!blue.Any(id => w.Unit(id) is { Alive: true, Life: not null } u && w.Bodies.Any(b => Takes(u, b) > 0)) &&
            blue.Select(w.Unit).FirstOrDefault(u => u is { Alive: true, Life.Wound: WoundState.Healthy }) is { } spender)
        {
            foreach (var wp in spender.Def.Weapons)
            {
                if (wp.Stock >= 0)
                {
                    spender.MutableCharges[wp.Stock] = spender.Charges[wp.Stock] / 2;
                }
            }
        }
        foreach (var id in blue)
        {
            // Each able man to the nearest body with ammo he can take (its ID: the order's search reaches only 10 cells).
            if (w.Unit(id) is { Alive: true, Life: not null } u && w.Bodies.Where(b => Takes(u, b) > 0).OrderBy(b => (b.Pos - u.Pos).LenSq()).ThenBy(b => b.Id)
                .FirstOrDefault() is { } body)
            {
                w.Issue(new Command { Tick = w.Tick, Player = bp, Kind = CommandKind.Scavenge, Units = [id], Target = body.Id });
            }
        }
        for (var t = 0; t < 10; t++)
        {
            w.Step(); // the order's tick and his first steps to the body
            seen.Scavenge |= blue.Any(id => w.Unit(id) is { Life.Scavenging: not 0 });
        }
        _ = reserve;
        seen.Ticks = w.Tick;
        return seen;
    }

    // WarmRoute: a long route (the first HPA* search, 1.2 ms in row 1220): a soldier sent 56 cells over an open 64 x 64 world with no
    // terrain map, beyond the near search's 40 cells, so the router's portal graph is built and searched.
    private void WarmRoute(UnitDef kind)
    {
        var w = new World(new Grid(64, 64), Match);
        var id = w.Spawn(kind, new Cell(3, 4), Facing.East);
        w.Issue(new Command { Tick = w.Tick, Player = Match.Players[0].Id, Kind = CommandKind.Move, Units = [id], Cell = new Cell(59, 58) });
        w.Step();
        w.Step();
    }

    // ROUTE-WARM-2 (docs/design.md, row 1376): the battle's order tick plans 60 squads' long routes at once (29.6 ms in SimRun, 46.8
    // in the game), half of it the JIT: WarmRoute's lone soldier on open ground takes neither the squad's plan (PlanSquad: the
    // portal search, Refine, SmoothRoute over terrain costs) nor sectors built on demand. WarmSquadRoutes plays it on a world of
    // its own as a light-prepared map does: rolling ground with a wood and a road, routing light (sectors built where the routes
    // reach them), three squads sent across it with an Attack-move of all three and a Hold facing a far threat, a few ticks.
    private const int WarmRouteSize = 128;

    internal (int Plans, long Sectors) WarmSquadRoutes()
    {
        if (Ground is not { } g || Content.Squad is not { } rules || Match.Players.Count == 0)
        {
            return (0, 0); // no terrain map: WarmRoute's open world is the path taken
        }
        List<UnitDef> kinds = [.. Match.Players[0].Presets.Concat(Match.Players[0].Defaults.Where(d => d is not null).Select(d => d!))
            .Where(d => !d.IsStructure && d.Layer == Layers.Ground && d.Speed.Raw > 0 && d.Archetype.Flight is null && rules.Joins(d.Tags))];
        if (kinds.Count == 0)
        {
            return (0, 0);
        }
        var w = new World(new Grid(WarmRouteSize, WarmRouteSize), Match);
        w.AttachTerrain(WarmGround(g.Rules, WarmRouteSize));
        w.PrepareRouting(everySector: false);
        var p = Match.Players[0].Id;
        var squads = new List<List<uint>>();
        for (var q = 0; q < 3; q++)
        {
            var men = new List<uint>();
            for (var i = 0; i < 6; i++)
            {
                men.Add(w.Spawn(kinds[i % kinds.Count], new Cell(4 + (i / 3 * 2), 8 + (q * 12) + (i % 3 * 2)), Facing.East));
            }
            w.Issue(new Command { Tick = w.Tick, Player = p, Kind = CommandKind.FormSquad, Units = men });
            squads.Add(men);
        }
        for (var t = 0; t < 3; t++)
        {
            w.Step();
        }
        var plans = 0;
        w.Issue(new Command { Tick = w.Tick, Player = p, Kind = CommandKind.AttackMove, Units = [.. squads[0], .. squads[1]], Cell = new Cell(118, 112) });
        w.Issue(new Command
        {
            Tick = w.Tick, Player = p, Kind = CommandKind.SetSquadMode, Units = squads[2], Index = (int)SquadMode.Hold,
            Cell = new Cell(100, 40), Cell2 = new Cell(122, 60),
        });
        for (var t = 0; t < 8; t++)
        {
            w.Step();
            plans += w._squadPlans;
        }
        return (plans, w.Routing.BuildTotals().Sectors);
    }

    // WarmGround: rolling ground, a grove and a wood, so the order code's terrain paths (heights, canopy, cover) are taken; on a
    // bigger world (WarmSquadRoutes) more woods down the same columns and a road along its middle row.
    private static TerrainMap WarmGround(TerrainRules rules, int n = WarmSize)
    {
        var corners = new int[(n + 1) * (n + 1)];
        var flags = new byte[n * n];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                corners[y * (n + 1) + x] = 1024 + Math.Abs(x % 16 - 8) * 48 + Math.Abs(y % 12 - 6) * 32;
                if (x < n && y < n && x % 48 is >= 26 and < 40 && (y % 48 is >= 6 and < 14 || y % 48 is >= 30 and < 38))
                {
                    flags[y * n + x] = (byte)(1 + (x + y) % 3); // forest density 1 to 3
                }
                else if (x < n && y < n && n > WarmSize && y == n / 2)
                {
                    flags[y * n + x] = TerrainMap.RoadFlag;
                }
            }
        }
        return new TerrainMap(n, n, corners, flags, rules);
    }
}

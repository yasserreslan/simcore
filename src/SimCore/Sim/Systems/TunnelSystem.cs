using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The tunnel system (docs/design.md, "Tunnels"). Workers build tunnel entrances (structures with
// Archetype.Tunnel); a finished one is a node of its owner's network, linked to every other of
// the owner's entrances within reach. Each tick, in this order:
//  1. Entrances: door gates and exposure count down; a destroyed site gives its ground back; a
//     destroyed entrance becomes rubble (units coming out of it are hurt); destroyed rubble leaves
//     the network: units at it move to a linked entrance they may use or die (the survival rules),
//     units heading for it stop where they are next, and its ground starts returning.
//  2. Travel: units underground move along their legs (one leg per link, at the entrance's speed).
//  3. Going in: units on an Enter order at an entrance's door go in, one per throughput, while
//     their network has room (overflow never kills: they wait).
//  4. Coming out: at each entrance, the unit that has waited longest to come out there (then the
//     lowest ID) comes up, one per throughput, unless something stands in the door.
//  5. The ground of removed entrances eases back to its old shape.
// Vehicles (size 2 and 3) travel only through entrances of size 2 or more; a medium entrance is a
// junction heavy vehicles pass but can't come out of; hatches carry infantry only. Everything runs
// over the entrances in ID order with integer distances; no hash collections.
public sealed partial class World
{
    // How often (ticks) the ground of a removed entrance takes a step back to its old shape.
    private const int RestoreEvery = TicksPerSecond;

    // How close (tenths of a cell beyond the door point) a unit must come to go in.
    private static readonly Fixed EnterReach = Fixed.Frac(3, 2);

    // The ground each tunnel entrance reshaped (live ones and ones being restored), by creation
    // order: it decides the terrain to come, so it is hashed.
    private readonly List<TunnelGround> _tunnelGround = [];

    // Scratch: the present entrances of the tick (rebuilt when asked), not game state.
    private readonly List<Unit> _tnNodes = [];

    private sealed class TunnelGround
    {
        public uint Entrance; // 0 once the entrance is gone and the ground is returning
        public int X0, Y0, W, H; // the corners touched: a box from corner (X0, Y0)
        public int[] Orig = [], Cut = [];
        public int PadX0, PadY0, PadW, PadH; // the pad cells (footprint and apron)
        public int Step, Steps, Wait; // returning: steps done of Steps (Steps 0: not yet)
    }

    private void RunTunnels()
    {
        // 1. Entrances.
        List<Unit>? removed = null;
        foreach (var e in _units)
        {
            if (e.Def.Archetype.Tunnel is not { } t)
            {
                continue;
            }
            if (e.Gate > 0)
            {
                e.Gate--;
            }
            if (e.ExposeLeft > 0)
            {
                e.ExposeLeft--;
            }
            if (e.Alive)
            {
                continue;
            }
            if (!e.IsBuilt)
            {
                StartRestore(e.Id); // a site destroyed: it never was a node
            }
            else if (!e.Rubble)
            {
                e.Rubble = true;
                e.HP = t.RubbleHp;
                e.Gate = 0;
                foreach (var u in _units)
                {
                    if (u.EmergeFrom == e.Id && u.EmergeLeft > 0 && u.Alive)
                    {
                        u.HP -= Math.Max(1, (int)((long)u.Def.MaxHp * t.EmergeDamage / 100));
                    }
                }
            }
            else
            {
                (removed ??= []).Add(e);
            }
        }
        if (removed is not null)
        {
            foreach (var x in removed)
            {
                RemoveNode(x);
            }
        }
        // Destinations that became rubble: those heading there to come out wait below instead.
        foreach (var u in _units)
        {
            if (u.InTunnel && u.TunnelTo != 0 && Unit(u.TunnelTo) is { Rubble: true })
            {
                StopTravelPlan(u);
            }
        }

        // 2. Travel.
        foreach (var u in _units)
        {
            if (u.InTunnel && u.Alive && u.TunnelNext != 0 && --u.TunnelLeft <= 0)
            {
                Arrive(u);
            }
        }

        // 3. Going in.
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.Enter && u.Inside == 0 && Unit(u.Order.Target) is { } e && e.Def.Archetype.Tunnel is not null)
            {
                TryEnter(u, e);
            }
        }

        // 4. Coming out.
        foreach (var e in TunnelNodesAll())
        {
            if (!e.Rubble && e.Gate == 0)
            {
                TryEmerge(e);
            }
        }

        // 5. Emerging, and the ground coming back.
        foreach (var u in _units)
        {
            if (u.EmergeLeft > 0 && --u.EmergeLeft == 0)
            {
                u.EmergeFrom = 0;
            }
        }
        RunRestores();
    }

    // ---- The network ----

    // TunnelNodesAll returns every present entrance (finished, standing or rubble), in ID order.
    private List<Unit> TunnelNodesAll()
    {
        _tnNodes.Clear();
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Tunnel is not null && u.IsBuilt && u.Alive)
            {
                _tnNodes.Add(u);
            }
        }
        return _tnNodes;
    }

    /// <summary>The player's tunnel entrances in the network (finished, standing or rubble), in ID order.</summary>
    public List<Unit> TunnelNodes(byte player)
    {
        var list = new List<Unit>();
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Tunnel is not null && u.IsBuilt && u.Alive && u.Owner == player)
            {
                list.Add(u);
            }
        }
        return list;
    }

    /// <summary>Whether two entrances are linked: the same owner's, both in the network, within the smaller of their link ranges.</summary>
    public static bool TunnelLinked(Unit a, Unit b) =>
        a != b && a.Owner == b.Owner && a.Def.Archetype.Tunnel is { } ta && b.Def.Archetype.Tunnel is { } tb &&
        Within(a.Pos, b.Pos, ta.LinkRange < tb.LinkRange ? ta.LinkRange : tb.LinkRange);

    // Passable reports whether a unit of the size may travel through entrance n: infantry through
    // any, vehicles only through entrances of size 2 or more (rubble too: it is still a junction).
    private static bool TunnelPassable(Unit n, int size) => size <= 1 || n.Def.Archetype.Tunnel!.Size >= 2;

    // Exitable reports whether a unit of the size may come out of entrance n.
    private static bool TunnelExitable(Unit n, int size) => !n.Rubble && n.Def.Archetype.Tunnel!.Size >= size;

    /// <summary>The entrances connected to e (itself included) through links of any kind, in ID order: the network sharing its capacity.</summary>
    public IReadOnlyList<Unit> TunnelNetwork(Unit e)
    {
        var nodes = TunnelNodes(e.Owner);
        var seen = new bool[nodes.Count];
        var start = nodes.IndexOf(e);
        if (start < 0)
        {
            return [];
        }
        var stack = new List<int> { start };
        seen[start] = true;
        while (stack.Count > 0)
        {
            var i = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            for (var j = 0; j < nodes.Count; j++)
            {
                if (!seen[j] && TunnelLinked(nodes[i], nodes[j]))
                {
                    seen[j] = true;
                    stack.Add(j);
                }
            }
        }
        var list = new List<Unit>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (seen[i])
            {
                list.Add(nodes[i]);
            }
        }
        return list;
    }

    /// <summary>The slots a unit takes underground: its own, plus its riders'.</summary>
    public int TunnelSlotsOf(Unit u)
    {
        var n = u.Def.Archetype.TunnelSlots;
        if (u.Def.Crew is { Passengers: > 0 })
        {
            foreach (var p in PassengersOf(u))
            {
                n += Math.Max(1, p.Def.Archetype.TunnelSlots);
            }
        }
        return n;
    }

    /// <summary>The units underground in e's network (at or travelling between its entrances), in ID order.</summary>
    public IReadOnlyList<Unit> TunnelOccupants(Unit e)
    {
        var net = TunnelNetwork(e);
        var list = new List<Unit>();
        foreach (var u in _units)
        {
            if (u.InTunnel && u.Alive && In(net, u.Tunnel))
            {
                list.Add(u);
            }
        }
        return list;
    }

    /// <summary>How many slots e's network uses and holds (the sum of its entrances' capacities, rubble included).</summary>
    public (int Used, int Capacity) TunnelLoad(Unit e)
    {
        var net = TunnelNetwork(e);
        var cap = 0;
        foreach (var n in net)
        {
            cap += n.Def.Archetype.Tunnel!.Capacity;
        }
        var used = 0;
        foreach (var u in _units)
        {
            if (u.InTunnel && u.Alive && In(net, u.Tunnel))
            {
                used += TunnelSlotsOf(u);
            }
        }
        return (used, cap);
    }

    private static bool In(IReadOnlyList<Unit> nodes, uint id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id)
            {
                return true;
            }
        }
        return false;
    }

    // Route finds the shortest way underground for a unit of the size from entrance from to
    // entrance to (Dijkstra over the owner's entrances it may pass, ties to the lower ID): the
    // next entrance on it and its total length, or (0, 0) when there is none (to == from: (from, 0)).
    private (uint Next, long Dist) Route(Unit from, Unit to, int size)
    {
        if (from == to)
        {
            return (from.Id, 0);
        }
        var nodes = TunnelNodes(from.Owner);
        var n = nodes.Count;
        var s = nodes.IndexOf(from);
        var g = nodes.IndexOf(to);
        if (s < 0 || g < 0 || !TunnelPassable(from, size) || !TunnelPassable(to, size))
        {
            return (0, 0);
        }
        var dist = new long[n];
        var first = new int[n]; // the first hop from s on the best way to each node
        var done = new bool[n];
        Array.Fill(dist, long.MaxValue);
        dist[s] = 0;
        first[s] = s;
        for (var k = 0; k < n; k++)
        {
            var i = -1;
            for (var j = 0; j < n; j++)
            {
                if (!done[j] && dist[j] != long.MaxValue && (i < 0 || dist[j] < dist[i]))
                {
                    i = j;
                }
            }
            if (i < 0 || i == g)
            {
                break;
            }
            done[i] = true;
            for (var j = 0; j < n; j++)
            {
                if (done[j] || !TunnelPassable(nodes[j], size) || !TunnelLinked(nodes[i], nodes[j]))
                {
                    continue;
                }
                var d = dist[i] + (nodes[j].Pos - nodes[i].Pos).Len().Raw;
                if (d < dist[j])
                {
                    dist[j] = d;
                    first[j] = i == s ? j : first[i];
                }
            }
        }
        return dist[g] == long.MaxValue ? (0, 0) : (nodes[first[g]].Id, dist[g]);
    }

    /// <summary>
    /// Whether underground unit u is trapped: no entrance it may come out of (standing, at least its
    /// size) can be reached from where it is (heavy vehicles with only medium entrances left).
    /// </summary>
    public bool TunnelTrapped(Unit u) => u.InTunnel && NearestExit(u) is null;

    // NearestExit returns the entrance u may come out of that is nearest along the tunnels (its own
    // first), or null.
    private Unit? NearestExit(Unit u)
    {
        if (Unit(u.TunnelNext != 0 ? u.TunnelNext : u.Tunnel) is not { } at)
        {
            return null;
        }
        var size = u.Def.Archetype.TunnelSize;
        Unit? best = null;
        long bestD = 0;
        foreach (var n in TunnelNodes(u.Owner))
        {
            if (!TunnelExitable(n, size))
            {
                continue;
            }
            var (next, d) = Route(at, n, size);
            if (next != 0 && (best is null || d < bestD))
            {
                (best, bestD) = (n, d);
            }
        }
        return best;
    }

    // ---- Travelling ----

    // SendUnderground sends u toward entrance to, to come out there or wait below.
    private void SendUnderground(Unit u, Unit to, bool exit)
    {
        u.TunnelTo = to.Id;
        u.TunnelOut = exit;
        if (u.TunnelNext == 0)
        {
            if (u.Tunnel == to.Id)
            {
                return; // already there
            }
            NextLeg(u);
        }
    }

    // NextLeg starts u's next leg toward its destination, or stops its plan when there is no way.
    private void NextLeg(Unit u)
    {
        var at = Unit(u.Tunnel);
        var to = Unit(u.TunnelTo);
        if (at is null || to is null)
        {
            StopTravelPlan(u);
            return;
        }
        var (next, _) = Route(at, to, u.Def.Archetype.TunnelSize);
        if (next == 0 || next == at.Id || Unit(next) is not { } n)
        {
            StopTravelPlan(u);
            return;
        }
        var speed = at.Def.Archetype.Tunnel!.Speed.Raw;
        var ticks = (int)Math.Max(1, ((n.Pos - at.Pos).Len().Raw + speed - 1) / speed);
        u.TunnelNext = n.Id;
        u.TunnelLeg = ticks;
        u.TunnelLeft = ticks;
    }

    // StopTravelPlan drops u's destination: it waits underground where it is (or where its leg ends).
    private static void StopTravelPlan(Unit u)
    {
        u.TunnelTo = 0;
        u.TunnelOut = false;
    }

    // Arrive ends u's leg at its next entrance and starts the next one, if it goes on.
    private void Arrive(Unit u)
    {
        u.Tunnel = u.TunnelNext;
        u.TunnelNext = 0;
        u.TunnelLeft = 0;
        u.TunnelLeg = 0;
        u.TunnelSince = Tick;
        if (Unit(u.Tunnel) is { } at)
        {
            u.Pos = at.Pos;
        }
        if (u.TunnelTo != 0 && u.TunnelTo != u.Tunnel)
        {
            NextLeg(u);
        }
    }

    // ---- Going in and coming out ----

    /// <summary>Whether u may go into entrance e (now or once it has room): e is its side's, finished and standing, and big enough.</summary>
    public static bool CanUseTunnel(Unit u, Unit e) =>
        e.Def.Archetype.Tunnel is { } t && e.IsBuilt && e.Alive && !e.Rubble && e.Owner == u.Owner && u.Alive &&
        u.Def.Archetype.TunnelSize > 0 && u.Def.Archetype.TunnelSize <= t.Size && u.Inside == 0 && u.Garrison == 0;

    // TryEnter takes u into e when it stands at e's door, the door is free and the network has room.
    private void TryEnter(Unit u, Unit e)
    {
        if (!CanUseTunnel(u, e) || !u.CanAct)
        {
            return; // the order system idles it
        }
        var t = e.Def.Archetype.Tunnel!;
        if (e.Gate > 0 || !Within(u.Pos, TunnelDoor(e), EnterReach + u.Def.Archetype.Radius))
        {
            return;
        }
        var (used, cap) = TunnelLoad(e);
        if (used + TunnelSlotsOf(u) > cap)
        {
            return; // full: it waits at the door
        }
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.AimAt = 0;
        u.AimLeft = 0;
        u.Vel = default;
        u.DriveSpeed = default;
        u.Inside = Underground;
        u.Tunnel = e.Id;
        u.TunnelNext = 0;
        u.TunnelLeft = 0;
        u.TunnelLeg = 0;
        u.TunnelTo = 0;
        u.TunnelOut = false;
        u.TunnelSince = Tick;
        u.EmergeFrom = 0;
        u.EmergeLeft = 0;
        u.Pos = e.Pos;
        e.Gate = t.Throughput;
        e.ExposeLeft = t.ExposeTicks;
    }

    // TryEmerge brings up the unit that has waited longest to come out at e, if the door is clear.
    private void TryEmerge(Unit e)
    {
        Unit? next = null;
        foreach (var u in _units)
        {
            if (u.InTunnel && u.Alive && u.Tunnel == e.Id && u.TunnelNext == 0 && u.TunnelOut && u.TunnelTo == e.Id &&
                TunnelExitable(e, u.Def.Archetype.TunnelSize) &&
                (next is null || u.TunnelSince < next.TunnelSince))
            {
                next = u;
            }
        }
        if (next is null)
        {
            return;
        }
        var door = TunnelDoor(e);
        var clear = true;
        foreach (var o in _units)
        {
            if (!o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Def.IsStructure || o.Def.Layer != Layers.Ground ||
                o.Def.Archetype.Locomotor == Locomotor.None ||
                !Within(door, o.Pos, next.Def.Archetype.Radius + o.Def.Archetype.Radius + Fixed.FromRaw(Fixed.OneRaw / 2)))
            {
                continue;
            }
            clear = false;
            if (o.Owner == e.Owner && o.Order.Kind == OrderKind.Idle && o.CanAct && o.Def.Speed.Raw > 0)
            {
                StepAside(o, e); // a friend idling in the door is told to make way
            }
        }
        if (!clear)
        {
            return; // blocked: it waits
        }
        var t = e.Def.Archetype.Tunnel!;
        next.Inside = 0;
        next.Tunnel = 0;
        next.TunnelTo = 0;
        next.TunnelOut = false;
        next.TunnelSince = 0;
        next.Pos = door;
        next.Facing = FixedPoint.Facing.Wrap(e.Facing + FixedPoint.Facing.South);
        next.TurretFacing = next.Facing;
        next.Heading = FixedPoint.Facing.Dir(next.Facing);
        next.EmergeFrom = e.Id;
        next.EmergeLeft = t.Throughput;
        foreach (var r in _units)
        {
            if (r.Inside == next.Id)
            {
                r.Pos = door; // its driver and riders come up with it
            }
        }
        e.Gate = t.Throughput;
        e.ExposeLeft = t.ExposeTicks;
        StepAside(next, e);
    }

    // StepAside sends u a few cells out in front of e's door, out of the way.
    private void StepAside(Unit u, Unit e)
    {
        var t = e.Def.Archetype.Tunnel!;
        var away = Cell.Of(TunnelLocal(e, (long)e.Def.Archetype.FootprintW * Fixed.OneRaw / 2,
            (long)(e.Def.Archetype.FootprintW + t.Apron + 2) * Fixed.OneRaw));
        if (!Grid.TrySpiral(away, (int)(u.Id % 4), out var cell))
        {
            return;
        }
        u.Order = new Order(OrderKind.Move, cell, 0, 0);
        u.ClearPath();
        RequestRoute(u, cell);
    }

    // ---- Survival ----

    // RemoveNode takes destroyed rubble x out of the network: units at it move to the nearest
    // entrance linked to it that they may pass, or die (vehicles with nowhere to drive, anyone with
    // nowhere at all); units on a leg to it turn back; units heading for it wait where they get to.
    // In unit ID order. Its ground starts returning.
    private void RemoveNode(Unit x)
    {
        foreach (var u in _units)
        {
            if (!u.InTunnel || !u.Alive)
            {
                continue;
            }
            if (u.TunnelNext == x.Id)
            {
                if (u.Tunnel != x.Id && Unit(u.Tunnel) is { Alive: true })
                {
                    u.TunnelNext = u.Tunnel; // turn back
                    u.TunnelLeft = Math.Max(1, u.TunnelLeg - u.TunnelLeft);
                }
                else
                {
                    Reseat(u, x);
                }
            }
            else if (u.Tunnel == x.Id)
            {
                if (u.TunnelNext != 0)
                {
                    u.Tunnel = u.TunnelNext; // already on its way out of it
                }
                else
                {
                    Reseat(u, x);
                }
            }
        }
        foreach (var u in _units)
        {
            if (u.InTunnel && u.TunnelTo == x.Id)
            {
                StopTravelPlan(u);
            }
        }
        StartRestore(x.Id);
    }

    // Reseat moves u, underground at removed entrance x, to the nearest entrance linked to x that
    // it may pass (ties to the lower ID), or destroys it (its driver and riders, infantry, then
    // reseat the same way).
    private void Reseat(Unit u, Unit x)
    {
        var size = u.Def.Archetype.TunnelSize;
        Unit? best = null;
        long bestD = 0;
        foreach (var n in _units)
        {
            if (n.Def.Archetype.Tunnel is null || !n.IsBuilt || !n.Alive || n == x || !TunnelLinked(x, n) ||
                !TunnelPassable(n, size) || !TunnelPassable(x, size))
            {
                continue;
            }
            var d = (n.Pos - x.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (n, d);
            }
        }
        if (best is not null)
        {
            u.Tunnel = best.Id;
            u.TunnelNext = 0;
            u.TunnelLeft = 0;
            u.TunnelLeg = 0;
            u.TunnelSince = Tick;
            u.Pos = best.Pos;
            return;
        }
        foreach (var o in _units)
        {
            if (o.Inside == u.Id && o.Alive)
            {
                // Its crew are infantry underground now, with the infantry's chances.
                o.Inside = Underground;
                o.Tunnel = x.Id;
                o.TunnelNext = 0;
                o.TunnelTo = 0;
                o.TunnelOut = false;
                o.TunnelSince = Tick;
                Reseat(o, x);
            }
        }
        u.Driver = 0;
        u.HP = 0;
    }

    // ---- Geometry: footprint-local points ----

    // TunnelLocal maps a point given in raw units from entrance e's footprint frame (x across its
    // front, y from its back toward its mouth) to the map, by its facing (0: mouth south).
    private static Vec TunnelLocal(Unit e, long lx, long ly)
    {
        var o = FootprintOrigin(e);
        return LocalToMap(o, e.Def.Archetype.FootprintW, e.Facing, lx, ly);
    }

    private static Vec LocalToMap(Cell o, int size, int facing, long lx, long ly)
    {
        long ox = (long)o.X * Fixed.OneRaw, oy = (long)o.Y * Fixed.OneRaw, s = (long)size * Fixed.OneRaw;
        var (x, y) = ((FixedPoint.Facing.Wrap(facing) / 8) & 3) switch
        {
            0 => (ox + lx, oy + ly),
            1 => (ox + s - ly, oy + lx),
            2 => (ox + s - lx, oy + s - ly),
            _ => (ox + ly, oy + s - lx),
        };
        return new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
    }

    // LocalCorner maps a footprint-local corner (i across, j from the back) to a map corner.
    private static (int X, int Y) LocalCorner(Cell o, int size, int facing, int i, int j) =>
        ((FixedPoint.Facing.Wrap(facing) / 8) & 3) switch
        {
            0 => (o.X + i, o.Y + j),
            1 => (o.X + size - j, o.Y + i),
            2 => (o.X + size - i, o.Y + size - j),
            _ => (o.X + j, o.Y + size - i),
        };

    /// <summary>The point in front of entrance e's mouth where units go in and come out.</summary>
    public static Vec TunnelDoor(Unit e)
    {
        var s = (long)e.Def.Archetype.FootprintW * Fixed.OneRaw;
        return TunnelLocal(e, s / 2, s + Fixed.OneRaw);
    }

    /// <summary>The middle of entrance e's portal (the dark opening at the end of its ramp), at ground level.</summary>
    public static Vec TunnelPortal(Unit e)
    {
        var s = (long)e.Def.Archetype.FootprintW * Fixed.OneRaw;
        var depth = e.Def.Archetype.Tunnel!.Depth;
        return TunnelLocal(e, s / 2, depth > 0 ? (long)depth * Fixed.OneRaw : s / 2);
    }

    /// <summary>The direction entrance e's mouth faces (a unit facing, 0 north).</summary>
    public static int TunnelMouthFacing(Unit e) => FixedPoint.Facing.Wrap(e.Facing + FixedPoint.Facing.South);
}

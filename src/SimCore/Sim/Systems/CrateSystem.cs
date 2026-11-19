using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Supply crates (CRATE-ENGINE, 2026-11-19; docs/design.md, "Supply crates"; decisions 1099 to 1105). A supply crate (content
// supply_crate, a supply block with crate: true) is a small depot that is no structure: no footprint, never built, it blocks no
// path and no build site (a static unit: no body in movement, no cells in routing). It rearms its side's ground units in its
// radius as a depot does, depot-reloaded launchers too (SupplySystem.cs: IsDepot), never aircraft or structures; cargo trucks
// unload into it as into a depot (IsCargoDepot), topping it up; it is no cargo source. It burns (fire.json's damage tags).
//  - Drop crate (command 57): each listed cargo truck with supply aboard takes a spot at the point (several: spots on a lattice
//    CrateSpacing cells apart round it, nearest first, none closer than 2 cells to another's), each the nearest cell the truck can
//    reach and stand on within CrateDropSearch cells (CrateSpot: routing's passable cells for its class, in its region: between
//    trees in a wood, never on a trunk, a building, water or a cliff), and drives there (OrderKind.DropCrate). Stopped there (its
//    route done), it stands its cargo's unloadTicks (UnloadLeft) and drops its whole load as a crate at its tail; the truck is
//    then empty and idle. Moving restarts the count; a new order cancels it and the truck keeps its load. Refusals: an empty
//    truck (NoCargo), no cell (NoDropPoint), in World.OrderRefusals.
//  - An airdropped pallet that lands outside every drop zone of its side and not in water becomes a crate of its side with the
//    pallet's supply, on the nearest standable cell (AirdropSystem.cs: AirdropLand).
//  - Crates are added after the cargo system (RunCrates: never while a system walks the units); a crate that runs empty raises
//    CrateEmpty and goes CrateEmptyTicks later (its Lifetime; topped up meanwhile, it stays). Events: World.LogisticsEvents
//    (CrateDropped, CrateEmpty). State: the crate's Stock, Lifetime and HP, the truck's Order and UnloadLeft, all hashed already.
public sealed partial class World
{
    /// <summary>The supply crate's archetype ID.</summary>
    public const string CrateArchetype = "supply_crate";

    /// <summary>How far (cells) a crate's spot may lie from the point asked for: the nearest standable cell within this radius.</summary>
    public const int CrateDropSearch = 10;

    /// <summary>The spacing (cells) of the spots several trucks' crates take round one point.</summary>
    public const int CrateSpacing = 3;

    /// <summary>The ticks an empty crate stays before it goes (10 s).</summary>
    public const int CrateEmptyTicks = 10 * TicksPerSecond;

    // The spots round a drop point, in lattice steps of CrateSpacing, nearest first: the point, then rings out to 2 steps.
    private static readonly (int X, int Y)[] CrateLattice =
    [
        (0, 0), (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1),
        (2, 0), (0, 2), (-2, 0), (0, -2), (2, 1), (1, 2), (-2, 1), (-1, 2), (2, -1), (1, -2), (-2, -1), (-1, -2),
        (2, 2), (-2, 2), (2, -2), (-2, -2),
    ];

    // Crates to add once the cargo system is done (a truck's drop, a landed pallet): never while a system walks the units.
    private readonly List<(byte Owner, uint From, Vec At, int Facing, int Amount, uint Pallet)> _crateDrops = [];

    /// <summary>
    /// Where cargo truck <paramref name="truck"/> would drop a crate for a Drop crate order at <paramref name="point"/>: the point's
    /// cell if the truck can reach it and stand there, else the nearest such cell within <see cref="CrateDropSearch"/> cells (by
    /// distance, then row, then column), or null (no such cell, or not a cargo truck). A read-only query for the client's cursor
    /// ghost: it uses routing as the last tick left it and changes nothing. It checks at most 21 x 21 cells: cheap, but call it
    /// when the cursor's cell changes, not every frame. A group's later trucks take spots spread round it (<see cref="CrateSpacing"/>).
    /// </summary>
    public Cell? CrateDropPoint(Unit truck, Cell point) => truck.Def.Cargo is null ? null : CrateSpot(Routing, truck, point);

    // CrateSpot is the nearest cell to c that truck t can reach and stand on (CrateDropPoint), with routing r.
    private Cell? CrateSpot(Router r, Unit t, Cell c)
    {
        var k = r.ClassOf(t.Def);
        if (k < 0)
        {
            return null;
        }
        var from = r.NearestPassable(k, Cell.Of(t.Pos), 4);
        return CrateSpot(r, k, from < 0 ? -1 : r.Component(k, from), c);
    }

    // CrateSpot is the nearest cell to c within CrateDropSearch cells that class k stands on (passable in routing) in region
    // comp (-1: any), by distance, then row, then column; or null.
    private Cell? CrateSpot(Router r, int k, int comp, Cell c)
    {
        Cell? best = null;
        var bestD2 = int.MaxValue;
        const int reach = CrateDropSearch;
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var d2 = dx * dx + dy * dy;
                int x = c.X + dx, y = c.Y + dy;
                if (d2 >= bestD2 || d2 > reach * reach || x < 0 || y < 0 || x >= Grid.Width || y >= Grid.Height)
                {
                    continue;
                }
                var i = r.Index(new Cell(x, y));
                if (r.Passable(k, i) && (comp < 0 || r.Component(k, i) == comp) && Ground?.HasTrunk(new Cell(x, y)) != true) // a pusher too: not on a tree
                {
                    (best, bestD2) = (new Cell(x, y), d2);
                }
            }
        }
        return best;
    }

    // DropCrateCommand runs a Drop crate command (see CommandKind.DropCrate): each listed cargo truck of the player with supply aboard
    // takes the next free spot round the point, in the order listed, and drives there.
    private void DropCrateCommand(Command c)
    {
        var r = SyncRouting();
        var point = new Cell(Math.Clamp(c.Cell.X, 0, Grid.Width - 1), Math.Clamp(c.Cell.Y, 0, Grid.Height - 1));
        List<Cell>? taken = null;
        var next = 0;
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Inside != 0 || u.Def.Cargo is null || u.Def.Speed.Raw == 0)
            {
                continue; // only cargo trucks
            }
            if (u.Cargo <= 0)
            {
                Refuse(u, OrderRefusalReason.NoCargo, 0, point);
                continue;
            }
            Cell? spot = null;
            while (spot is null && next < CrateLattice.Length)
            {
                var (lx, ly) = CrateLattice[next++];
                var want = new Cell(Math.Clamp(point.X + lx * CrateSpacing, 0, Grid.Width - 1), Math.Clamp(point.Y + ly * CrateSpacing, 0, Grid.Height - 1));
                if (CrateSpot(r, u, want) is { } s && !(taken?.Exists(o => Math.Max(Math.Abs(o.X - s.X), Math.Abs(o.Y - s.Y)) < 2) ?? false))
                {
                    spot = s; // not on top of another truck's spot
                }
            }
            if (spot is not { } at)
            {
                Refuse(u, OrderRefusalReason.NoDropPoint, 0, point);
                continue;
            }
            (taken ??= []).Add(at);
            Assign(u, new Order(OrderKind.DropCrate, at, 0, 0));
            RequestRoute(u, at);
        }
    }

    // DropCrateTick runs a tick of truck t's Drop crate order (from RunCargo): stopped at its spot (its route done), it counts its
    // cargo's unload ticks, then drops its whole load as a crate at its tail and goes idle. Moving restarts the count.
    private void DropCrateTick(Unit t, CargoEffect cargo)
    {
        t.UnloadDepot = 0;
        if (t.Cargo <= 0 || !t.Alive || t.Inside != 0)
        {
            t.UnloadLeft = 0;
            if (t.Alive && t.Inside == 0)
            {
                t.Idle();
            }
            return;
        }
        if (t.Moving || t.PathPending || t.Mode == RouteMode.Field || t.RouteIdx < t.Route.Length)
        {
            t.UnloadLeft = 0; // on its way
            return;
        }
        if (t.UnloadLeft == 0)
        {
            t.UnloadLeft = cargo.UnloadTicks;
        }
        if (--t.UnloadLeft > 0)
        {
            return;
        }
        _crateDrops.Add((t.Owner, t.Id, CrateTail(t), t.Facing, t.Cargo, 0));
        t.Cargo = 0;
        t.Idle();
    }

    // CrateTail is where truck t's crate lands: behind it, half its length and half the crate's out from its centre, on the map; its
    // own spot when that point is not ground its class stands on.
    private Vec CrateTail(Unit t)
    {
        var half = ((t.Def.Archetype.Size?.Length ?? 20) + 15) * Fixed.OneRaw / 20;
        var dir = Facing.Dir(t.Facing);
        var x = Math.Clamp(t.Pos.X.Raw - dir.X.Raw * half / Fixed.OneRaw, 0, (long)Grid.Width * Fixed.OneRaw - 1);
        var y = Math.Clamp(t.Pos.Y.Raw - dir.Y.Raw * half / Fixed.OneRaw, 0, (long)Grid.Height * Fixed.OneRaw - 1);
        var tail = new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
        var r = Routing;
        var k = r.ClassOf(t.Def);
        return k < 0 || r.Passable(k, r.Index(Cell.Of(tail))) ? tail : t.Pos;
    }

    // CrateDef is the supply crate's definition for player p, or null (content without one).
    private UnitDef? CrateDef(byte p) => Player(p)?.Setup.Find(Content, CrateArchetype);

    // PalletCrate turns pallet p (amount supply), landed at `at` outside every drop zone of its side and not in water, into a crate
    // of its side on the nearest cell the side's cargo truck stands on (any region), its own point when it can. False when there is
    // no such cell or no crate (the pallet is lost).
    private bool PalletCrate(Projectile p, Vec at, int amount)
    {
        if (CrateDef(p.Owner) is null || Player(p.Owner) is not { } pl)
        {
            return false;
        }
        var r = SyncRouting();
        var k = -1;
        foreach (var d in pl.Setup.Defaults)
        {
            if (d is { Cargo: not null } && d.Speed.Raw > 0 && (k = r.ClassOf(d)) >= 0)
            {
                break; // its side's first cargo truck: where a truck could come to top it up
            }
        }
        var c = Cell.Of(at);
        var spot = k < 0 ? (Grid.TrySpiral(c, 0, out var g) ? g : (Cell?)null) : CrateSpot(r, k, -1, c);
        if (spot is not { } s)
        {
            return false;
        }
        _crateDrops.Add((p.Owner, p.Shooter, s == c ? at : s.Center(), 0, amount, p.Id));
        return true;
    }

    // RunCrates (after the cargo system) adds the crates dropped this tick, then starts the countdown of each crate that ran empty
    // (CrateEmpty) and stops it for one topped up meanwhile.
    private void RunCrates()
    {
        foreach (var (owner, from, at, facing, amount, pallet) in _crateDrops)
        {
            if (CrateDef(owner) is not { } def)
            {
                continue;
            }
            var crate = Add(def, at, facing);
            crate.Stock = amount;
            _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.CrateDropped, from, crate.Id, at, amount, 0, pallet));
        }
        _crateDrops.Clear();
        foreach (var s in _supplySources)
        {
            if (s.Def.Supply is not { Crate: true } || !s.Alive)
            {
                continue;
            }
            if (s.Stock <= 0 && s.Lifetime == 0)
            {
                s.Lifetime = CrateEmptyTicks; // it goes in 10 s (LifetimeSystem.cs)
                _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.CrateEmpty, 0, s.Id, s.Pos, 0, 0, 0));
            }
            else if (s.Stock > 0 && s.Lifetime > 0)
            {
                s.Lifetime = 0; // topped up: it stays
            }
        }
    }
}

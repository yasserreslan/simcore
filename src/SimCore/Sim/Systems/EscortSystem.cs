using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Escort (ESCORT, 2026-11-08; docs/design.md, "Escort", decisions 1046 to 1057; the game repo's docs/plans/economy-spec.md). The
// owner: "a unit can escort another unit, and it will guard and defend it along its path, its following it and protecting it".
// Built on the Guard order on a unit (UnitAiSystem.cs):
//   * the order (CommandSystem): ground units that move escort one of their side's own units on the ground; never an aircraft (not
//     escorts for now), itself, or a unit that escorts it, itself or through others (no loops);
//   * the spots (RunEscorts, before the path system): the group round each escorted unit, in ID order, gets a layout by its size,
//     turned with the unit's heading: front, flanks, rear, then a second ring of eight and rings of sixteen; infantry 1.5 cells
//     clear of it (and 1.5 between rings), vehicles 3. The spots are given out again (nearest escort and spot first, so nobody
//     crosses over) when the group changes or the unit turned 45 degrees since; otherwise each keeps its spot as the unit turns;
//   * the pace (SteerEscort, in the path system): straight to its spot at the unit's speed of the last tick and an eighth of the
//     gap a tick (catching up), at most its own speed; along a route when the way isn't clear. Stopped, they form up round it;
//   * threats (EscortOrder, in the order system): an enemy that hurt the unit in the last 2 s comes first (before the escort's own
//     targets and chases); otherwise every scan an enemy spotted within its vision x 1.8 of the unit. The chase's leash (vision x
//     2.2) counts from the unit, and the walk back is the formation: it goes back to its spot;
//   * inside (a tunnel, a garage, a shelter, a building): they wait round where it was last seen outside and pick it up when it
//     comes out (a vehicle it rides in: they escort the vehicle); it fell: a Guard order on that spot. Any other order ends it.
// Everything is integer, in ID order, with no random draws. The numbers are code constants (as FollowRadius in UnitAiSystem.cs), so
// the content hash and every recorded replay stay as they were (decision 1056).
public sealed partial class World
{
    private static readonly Fixed EscortFootGap = Fixed.FromRaw(Fixed.OneRaw * 3 / 2); // infantry: 1.5 cells clear of the unit, rings 1.5 apart
    private static readonly Fixed EscortVehicleGap = Fixed.FromInt(3); // vehicles: 3 cells, rings 3 apart
    private static readonly Fixed EscortSettle = Fixed.FromRaw(Fixed.OneRaw / 2); // on its spot within half a cell (the unit stopped)
    private const int EscortTurn = 4; // a turn of 4 facings (45 degrees) since the spots were given out gives them out again
    private const int EscortCatchUp = 8; // closing an eighth of the gap to its spot a tick on top of the unit's pace
    private const uint EscortThreatTicks = 2 * TicksPerSecond; // a hit on the unit this recent sends its escorts after the shooter
    private const int EscortGreedy = 32; // the nearest-pair hand-out covers the first 32 of a group (by ID); the rest take spots in order

    private readonly List<(uint Ward, Unit U)> _escorts = [];
    private readonly List<long> _escortGaps = [];

    /// <summary>
    /// Whether u can escort at all: a ground unit that moves (infantry and vehicles); aircraft are not escorts for now, nor
    /// buildings, fixed pieces or launched drones.
    /// </summary>
    public static bool CanEscort(Unit u) =>
        u.Def.Layer == Layers.Ground && u.Def.Speed.Raw > 0 && u.Def.Strike is null && u.Def.Archetype.Flight is null;

    /// <summary>Whether u can be escorted: a unit that moves on the ground (not a building, an aircraft or a drone in flight).</summary>
    public static bool Escortable(Unit u) => CanEscort(u);

    /// <summary>Why u may not escort ward now, or null: it may (the order's checks; the client's pointer label).</summary>
    public EscortRefusal? EscortRefusalOf(Unit u, Unit ward)
    {
        if (!CanEscort(u))
        {
            return u.Def.Layer == Layers.Air ? EscortRefusal.Aircraft : EscortRefusal.CannotMove;
        }
        if (!ward.Alive)
        {
            return EscortRefusal.Gone;
        }
        if (ward.Owner != u.Owner)
        {
            return EscortRefusal.NotOwn;
        }
        if (ward == u)
        {
            return EscortRefusal.Self;
        }
        if (!Escortable(ward))
        {
            return EscortRefusal.NotEscortable;
        }
        if (u.Garrison != 0 || u.Inside != 0)
        {
            return EscortRefusal.Inside;
        }
        return EscortsThrough(ward, u) ? EscortRefusal.Loop : null;
    }

    /// <summary>How many of its side's units escort unit id now.</summary>
    public int EscortsOf(uint id)
    {
        var n = 0;
        foreach (var u in _units)
        {
            if (u.Alive && u.Order.Kind == OrderKind.Escort && u.Order.Target == id)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Spot i of a group of n escorts: its turn from the escorted unit's heading in facings (0 ahead, 8 its right, -8 its left, 16
    /// behind) and its ring (1 the nearest). One: ahead; two: ahead and behind; three: ahead and both flanks; four: and behind;
    /// then a second ring of eight (the diagonals first) and rings of sixteen.
    /// </summary>
    public static (int Turn, int Ring) EscortSpot(int n, int i)
    {
        if (n <= 3)
        {
            return n == 2 ? (i == 0 ? 0 : 16, 1) : (i switch { 0 => 0, 1 => -8, _ => 8 }, 1);
        }
        if (i < 4)
        {
            return (i switch { 0 => 0, 1 => -8, 2 => 8, _ => 16 }, 1);
        }
        if (i < 12)
        {
            return (new[] { -4, 4, -12, 12, 0, -8, 8, 16 }[i - 4], 2);
        }
        var k = (i - 12) % 16;
        var turn = 2 * ((k + 1) / 2) * (k % 2 == 1 ? -1 : 1);
        return (turn == -16 ? 16 : turn, 3 + (i - 12) / 16);
    }

    // EscortsThrough reports whether ward escorts u, itself or through a chain of escorts.
    private bool EscortsThrough(Unit ward, Unit u)
    {
        var x = ward;
        for (var i = 0; i <= _units.Count && x.Order.Kind == OrderKind.Escort; i++)
        {
            if (Unit(x.Order.Target) is not { } next)
            {
                return false;
            }
            if (next == u)
            {
                return true;
            }
            x = next;
        }
        return false;
    }

    // EscortCommand gives the command's units that may escort the player's unit Target an Escort order.
    private void EscortCommand(Command c)
    {
        if (Unit(c.Target) is not { Alive: true } ward || ward.Owner != c.Player)
        {
            return; // only the player's own units
        }
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || EscortRefusalOf(u!, ward) is not null)
            {
                continue;
            }
            u!.ResetAi();
            Assign(u, new Order(OrderKind.Escort, Cell.Of(ward.Pos), ward.Id, 0));
            u.Escort = new EscortState { Ward = ward.Id, Last = ward.Pos, LastHeading = ward.Heading, LastFacing = ward.Facing };
        }
    }

    // EscortAnchor: the unit the escorts form up round: the escorted unit out on the map, the vehicle it rides in; null while it
    // is off the map (a tunnel, a garage, a shelter, a building: they wait where it was last seen outside).
    private Unit? EscortAnchor(Unit ward)
    {
        if (ward.Garrison != 0)
        {
            return null;
        }
        if (ward.Inside == 0)
        {
            return ward;
        }
        return ward.Inside is not (Underground or InGarage or InShelter) && Unit(ward.Inside) is { Alive: true, Inside: 0 } carrier ? carrier : null;
    }

    // EscortOrder runs an Escort order (the order system): it notes where the unit is, guards the spot once it fell, sends the
    // escort after what threatens it, and runs the chase; otherwise the spots and the path system bring it to its spot.
    private void EscortOrder(Unit u)
    {
        var e = u.Escort;
        if (e is null || e.Ward != u.Order.Target)
        {
            e = u.Escort = new EscortState { Ward = u.Order.Target, Last = u.Order.Cell.Center(), LastFacing = u.Facing };
        }
        if (Unit(u.Order.Target) is not { Alive: true } ward || ward.Owner != u.Owner)
        {
            u.Order = new Order(OrderKind.Guard, Cell.Of(e.Last), 0, 0); // it fell: guard the spot (Guard area)
            u.Escort = null;
            if (u.Ai == AiState.None)
            {
                u.ClearPath();
            }
            GuardPost(u);
            return;
        }
        if (EscortAnchor(ward) is { } anchor)
        {
            (e.Last, e.LastHeading, e.LastFacing) = (anchor.Pos, anchor.Heading, anchor.Facing);
            u.Order = u.Order with { Cell = Cell.Of(anchor.Pos) };
        }
        if (!HoldsFire(u.Stance) && CanChase(u))
        {
            if (WardThreat(u, ward, e) is { } shooter && u.AiTarget != shooter.Id && !(u.Ai == AiState.Engage && e.Threat != 0 && u.AiTarget == e.Threat))
            {
                Engage(u, shooter, ward.LastHitFrom); // it hurt the unit: before the escort's own targets
                e.Threat = shooter.Id;
            }
            else if (u.Ai == AiState.None && (Tick + u.Id) % (uint)Content.UnitAi.ScanTicks == 0 && GuardScan(u, e.Last, InnerGuard(u)) is { } t)
            {
                Engage(u, t, t.Pos); // spotted near the unit
                e.Threat = 0;
            }
        }
        if (u.Ai == AiState.None)
        {
            return;
        }
        RunAi(u); // the chase, on the leash from the unit (HomeOf)
        if (u.Ai == AiState.Return)
        {
            u.ResetAi(); // the walk back is the formation: back to its spot
            u.ClearPath();
        }
        if (u.Ai == AiState.None)
        {
            e.Threat = 0;
        }
    }

    // WardThreat: the enemy that hurt ward in the last 2 s, if u can go after it (on the ground, its shot from within u's leash of
    // the unit, one of u's weapons can hit it; one firing from a building: the building), or null.
    private Unit? WardThreat(Unit u, Unit ward, EscortState e)
    {
        if (ward.LastHitBy == 0 || Tick - ward.LastHitTick > EscortThreatTicks || Unit(ward.LastHitBy) is not { Alive: true } by ||
            by.Owner == u.Owner || by.Owner == 0 || by.Def.Layer == Layers.Air || by.Def.Strike is not null || by.Def.Archetype.Flight is not null)
        {
            return null;
        }
        if (by.Garrison != 0 && Unit(by.Garrison) is { } building)
        {
            by = building;
        }
        return Within(e.Last, ward.LastHitFrom, Leash(u)) && PickWeapon(u, by, false) >= 0 ? by : null;
    }

    // RunEscorts (before the path system) drops the state of units no longer escorting, then gives each group its spots and each
    // escort this tick's spot point.
    private void RunEscorts()
    {
        _escorts.Clear();
        foreach (var u in _units)
        {
            if (u.Escort is null)
            {
                continue;
            }
            if (!u.Alive || u.Order.Kind != OrderKind.Escort)
            {
                u.Escort = null; // another order ended it
                continue;
            }
            _escorts.Add((u.Order.Target, u));
        }
        if (_escorts.Count == 0)
        {
            return;
        }
        _escorts.Sort((a, b) => a.Ward != b.Ward ? a.Ward.CompareTo(b.Ward) : a.U.Id.CompareTo(b.U.Id));
        for (var i = 0; i < _escorts.Count;)
        {
            var j = i;
            while (j < _escorts.Count && _escorts[j].Ward == _escorts[i].Ward)
            {
                j++;
            }
            PlaceEscorts(i, j);
            i = j;
        }
    }

    // PlaceEscorts: the spots of the group _escorts[from..to) (one escorted unit, in ID order).
    private void PlaceEscorts(int from, int to)
    {
        var n = to - from;
        var first = _escorts[from].U.Escort!;
        var ward = Unit(_escorts[from].Ward);
        var (at, heading, facing) = ward is { Alive: true } && EscortAnchor(ward) is { } a ? (a.Pos, a.Heading, a.Facing) : (first.Last, first.LastHeading, first.LastFacing);
        var redo = Math.Abs(Facing.Diff(first.Facing, facing)) >= EscortTurn;
        for (var k = from; k < to && !redo; k++)
        {
            var e = _escorts[k].U.Escort!;
            redo = e.Count != n || e.Spot < 0 || e.Spot >= n;
        }
        if (redo)
        {
            HandOutSpots(from, to, ward, at, heading, facing);
        }
        for (var k = from; k < to; k++)
        {
            var u = _escorts[k].U;
            u.Escort!.Point = SpotPoint(u, ward, at, heading, facing, n, u.Escort.Spot);
        }
    }

    // HandOutSpots gives the group's spots out again: the nearest escort and spot first (ties: the lower ID, then the lower spot),
    // so escorts take the spots on their own side and nobody crosses over; past the first 32, the rest in ID order.
    private void HandOutSpots(int from, int to, Unit? ward, Vec at, Vec heading, int facing)
    {
        var n = to - from;
        var g = Math.Min(n, EscortGreedy);
        _escortGaps.Clear();
        for (var k = 0; k < g; k++)
        {
            var u = _escorts[from + k].U;
            for (var s = 0; s < g; s++)
            {
                _escortGaps.Add((SpotPoint(u, ward, at, heading, facing, n, s) - u.Pos).LenSq());
            }
        }
        Span<bool> unitDone = stackalloc bool[g], spotTaken = stackalloc bool[g];
        for (var round = 0; round < g; round++)
        {
            int bk = -1, bs = -1;
            var best = long.MaxValue;
            for (var k = 0; k < g; k++)
            {
                if (unitDone[k])
                {
                    continue;
                }
                for (var s = 0; s < g; s++)
                {
                    if (!spotTaken[s] && _escortGaps[k * g + s] < best)
                    {
                        (best, bk, bs) = (_escortGaps[k * g + s], k, s);
                    }
                }
            }
            unitDone[bk] = spotTaken[bs] = true;
            _escorts[from + bk].U.Escort!.Spot = bs;
        }
        for (var k = 0; k < n; k++)
        {
            var e = _escorts[from + k].U.Escort!;
            if (k >= g)
            {
                e.Spot = k;
            }
            (e.Count, e.Facing) = (n, facing);
        }
    }

    // SpotPoint: where escort u's spot s of n is round the unit at at, heading heading (zero: its facing): turned from the heading
    // by the layout, the unit's and the escort's radius and the escort's gap out (a ring further for each ring past the first).
    private static Vec SpotPoint(Unit u, Unit? ward, Vec at, Vec heading, int facing, int n, int s)
    {
        var (turn, ring) = EscortSpot(n, s);
        return RingPoint(u, ward, at, heading, facing, turn, ring);
    }

    // RingPoint: the point turn facings from the heading on escort u's ring round the unit at at.
    private static Vec RingPoint(Unit u, Unit? ward, Vec at, Vec heading, int facing, int turn, int ring)
    {
        var gap = u.Def.Archetype.Locomotor == Locomotor.Foot ? EscortFootGap : EscortVehicleGap;
        var dist = (ward?.Def.Archetype.Radius ?? Fixed.Zero).Raw + u.Def.Archetype.Radius.Raw + gap.Raw * ring;
        var h = heading.LenSq() > 0 ? heading.WithLen(Fixed.One) : Facing.Dir(facing);
        var d = Facing.Dir(turn);
        // h turned clockwise by d's angle (both as (sin, -cos) of their angle from north, in 1024ths).
        long hx = h.X.Raw, hy = h.Y.Raw, dx = d.X.Raw, dy = d.Y.Raw;
        var rx = (-hx * dy - hy * dx) >> Fixed.Shift;
        var ry = (hx * dx - hy * dy) >> Fixed.Shift;
        return new Vec(Fixed.FromRaw(at.X.Raw + rx * dist / Fixed.OneRaw), Fixed.FromRaw(at.Y.Raw + ry * dist / Fixed.OneRaw));
    }

    // InTheWay reports whether the unit at a stands in the straight way from escort u to point p: the line passes within both radii
    // and a cell of it, between the two ends.
    private static bool InTheWay(Unit a, Unit u, Vec p)
    {
        long dx = p.X.Raw - u.Pos.X.Raw, dy = p.Y.Raw - u.Pos.Y.Raw, ax = a.Pos.X.Raw - u.Pos.X.Raw, ay = a.Pos.Y.Raw - u.Pos.Y.Raw;
        var len2 = dx * dx + dy * dy;
        var along = dx * ax + dy * ay;
        if (len2 == 0 || along <= 0 || along >= len2)
        {
            return false;
        }
        var clear = a.Def.Archetype.Radius.Raw + u.Def.Archetype.Radius.Raw + Fixed.OneRaw;
        var cross = dx * ay - dy * ax; // the distance times the line's length
        return cross / Math.Max(1, Fixed.Sqrt(len2)) is var d && d * d < clear * clear;
    }

    // RoundTheFlank: the way point round the unit at p to spot s for an escort at u: square to the spot's side of the unit on the
    // escort's side while the escort is well round the far side (over 120 degrees from the spot), then the diagonal between that
    // flank and the spot (so an escort near the flank never turns back to it).
    private static Vec RoundTheFlank(Vec p, Vec u, Vec s)
    {
        long sx = s.X.Raw - p.X.Raw, sy = s.Y.Raw - p.Y.Raw, vx = u.X.Raw - p.X.Raw, vy = u.Y.Raw - p.Y.Raw;
        var right = sx * vy - sy * vx >= 0;
        long px = right ? -sy : sy, py = right ? sx : -sx; // the spot's direction turned a quarter toward the escort
        var behind = 2 * (sx * vx + sy * vy) < -Fixed.Sqrt(sx * sx + sy * sy) * Fixed.Sqrt(vx * vx + vy * vy); // more than 120 degrees round
        long wx = behind ? px : (sx + px) * 724 / 1024, wy = behind ? py : (sy + py) * 724 / 1024;
        return new Vec(Fixed.FromRaw(p.X.Raw + wx), Fixed.FromRaw(p.Y.Raw + wy));
    }

    // EscortHold: how near the unit an escort with no room for its spot holds: its first ring's distance and a cell.
    private Fixed EscortHold(Unit u) =>
        Fixed.FromRaw((Unit(u.Order.Target)?.Def.Archetype.Radius.Raw ?? 0) + u.Def.Archetype.Radius.Raw +
            (u.Def.Archetype.Locomotor == Locomotor.Foot ? EscortFootGap : EscortVehicleGap).Raw + Fixed.OneRaw);

    // SteerEscort writes an escort's intent toward its spot (the path system's loop): straight there when its cell fits the escort's
    // class and the way is clear, at the escorted unit's pace of the last tick and an eighth of the gap a tick, at most its own
    // speed (still within half a cell of it while the unit stands); otherwise along a route of its own (re-planned at most once a
    // second), to the spot or, when the spot's cell doesn't fit, halfway in, or it holds near the unit (or heads for it).
    private void SteerEscort(Router r, Unit u, EscortState e)
    {
        var spot = e.Point;
        var k = r.ClassOf(u.Def);
        bool Fits(Vec p) => k >= 0 && Grid.Contains(Cell.Of(p)) && r.Passable(k, r.Index(Cell.Of(p)));
        var real = spot; // the speed goes by the gap to the spot itself
        if (Unit(e.Ward) is { Alive: true } w0 && EscortAnchor(w0) is { } a0 && InTheWay(a0, u, spot))
        {
            spot = RoundTheFlank(a0.Pos, u.Pos, spot); // the unit is between it and its spot: round its flank on the escort's side
        }
        var fits = Fits(spot);
        if (!fits && new Vec(Fixed.FromRaw((spot.X.Raw + e.Last.X.Raw) / 2), Fixed.FromRaw((spot.Y.Raw + e.Last.Y.Raw) / 2)) is var half && Fits(half))
        {
            (spot, fits) = (half, true); // its spot is on a building, a mine or the water: halfway in
        }
        var cell = Cell.Of(spot);
        if (!fits && Within(u.Pos, e.Last, EscortHold(u)))
        {
            if (u.Mode != RouteMode.None || u.Path.Count > 0)
            {
                u.ClearPath();
            }
            u.Intent = u.Intent with { Desired = default }; // no room for its spot: it holds near the unit
            return;
        }
        if (fits && r.Clear(k, u.Pos, spot))
        {
            if (u.Mode != RouteMode.None || u.Path.Count > 0)
            {
                u.ClearPath();
            }
            var pace = Unit(e.Ward) is { Alive: true } ward && EscortAnchor(ward) is { } anchor ? Fixed.Sqrt(anchor.Vel.LenSq()) : 0;
            var d = spot - u.Pos;
            var gap = Fixed.Sqrt(Math.Max(d.LenSq(), (real - u.Pos).LenSq()));
            if (pace == 0 && gap <= EscortSettle.Raw)
            {
                u.Intent = u.Intent with { Desired = default }; // formed up round it
                return;
            }
            var speed = Math.Min(u.Def.Speed.Raw, pace + gap / EscortCatchUp);
            var step = gap <= speed ? d : d.WithLen(Fixed.FromRaw(Math.Max(1, speed)));
            u.Intent = spot == u.Intent.Goal ? u.Intent with { Desired = step, Slot = default } : new MoveIntent(spot, step, default, false);
            return;
        }
        if (!u.Moving && !u.PathPending && Tick >= u.RepathAt)
        {
            RequestRoute(u, fits ? cell : Cell.Of(e.Last));
            u.RepathAt = Tick + RepathTicks + u.Id % RepathTicks;
        }
        Steer(r, u);
    }
}

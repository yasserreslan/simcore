using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Elements (E13): fixed wings given the same order fly together. When one command gives several fixed wings
// of one kind a new order or task (Move, Attack, Attack-ground, an AirTask), they form elements of at most one
// more than the kind's slots (archetype element: 3 slots, so 4), sizes balanced (5 make 3 and 2), the lowest ID
// leading each; the others are its wingmen (Unit.Wingman: lead, slot, the lead's recent path). All integer;
// units by ID (a lead has the lowest ID of its element, so it has moved this tick before its wingmen read it).
//
//  - The slot: a wingman flies the lead's own path (its positions a delay ago, offset aside: never a point
//    fixed to the lead's nose, which would swing it across the lead's turns). The delay is the slot's back
//    distance at cruise speed; it steers at the point of that offset path a second ahead of where it is on it
//    and trims its speed (the along-track error over two seconds, between halfway to its corner speed and its
//    dash) to hold the slot; within 3 cells of its lead it turns away and slows (separation, the backup).
//  - Valid only while both fly free (not at the airfield, not homing) on the same order and task; otherwise
//    the wingman flies its own order until they match again (its path record restarts then). A lead lost:
//    the lowest ID of its wingmen leads.
//  - On Guard the wingman takes its lead's pick (it never hunts on its own while it has a lead to follow).
//  - On a strike (both on the same target) the delay is the element's stagger times the slot: the wingman
//    flies the lead's path that many seconds behind, offset aside, begins its own align where its lead began
//    its own (the stagger later, on a line through where it is: an offset line), flies its own run and release,
//    breaks the lead's way and takes its slot again: staggered runs, a common break and rejoin.
//  - An air fight (its own air target) flies itself; the slot resumes after.
public sealed partial class World
{
    // The slot's carrot: this many ticks ahead of the wingman's place on the lead's path (1 s).
    private const int SlotLeadTicks = TicksPerSecond;

    // Separation, the backup: within this of its lead (raw: 3 cells) a wingman turns away and slows.
    private const long ElementSeparationRaw = 3 * Fixed.OneRaw;

    // Scratch: each commanded unit's order, task and homing before the command (who got a new order).
    private readonly List<(Order, AirMission?, bool)> _elementBefore = [];
    private readonly List<Unit> _elementNew = [];

    // ElementSlots is u's element spec: its archetype's, or the default slots (1.5 turn radii at cruise back,
    // half a radius each side; the third in trail at three radii) and a 3 s stagger.
    private static ElementSpec ElementOf(Unit u)
    {
        if (u.Def.Archetype.Element is { } e)
        {
            return e;
        }
        var f = u.Def.Archetype.Flight!;
        var r = f.Wing is { } g ? g.Radius(g.Cruise) : ScoutFlight.TurnRadiusRaw(u.Def.Speed.Raw, f.TurnPermille);
        return new ElementSpec([new(r * 3 / 2, r / 2), new(r * 3 / 2, -r / 2), new(r * 3, 0)], 3 * TicksPerSecond);
    }

    // ElementBefore records the command's units' state before it runs (FormElements tells who changed).
    private void ElementBefore(Command c)
    {
        _elementBefore.Clear();
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            _elementBefore.Add(u is { Def.Archetype.Flight.Wing: not null } ? (u.Order, u.AirMission, u.Homing) : default);
        }
    }

    // FormElements: the fixed wings this command gave a new order or task leave their old elements and form
    // new ones by kind, in ID order (E13).
    private void FormElements(Command c)
    {
        _elementNew.Clear();
        for (var i = 0; i < c.Units.Count && i < _elementBefore.Count; i++)
        {
            if (Unit(c.Units[i]) is not { Alive: true, Def.Archetype.Flight.Wing: not null } u || u.Owner != c.Player)
            {
                continue;
            }
            var (o, m, h) = _elementBefore[i];
            if ((o, m, h) == (u.Order, u.AirMission, u.Homing) && !(c.Kind == CommandKind.Move && u.Order.Kind == OrderKind.Move))
            {
                continue; // nothing new for it (a Shift-appended waypoint keeps its element)
            }
            u.Wingman = null;
            if (!_elementNew.Contains(u))
            {
                _elementNew.Add(u);
            }
        }
        if (_elementNew.Count < 2)
        {
            return;
        }
        _elementNew.Sort((a, b) => a.Def.Archetype.Index != b.Def.Archetype.Index ? a.Def.Archetype.Index.CompareTo(b.Def.Archetype.Index) : a.Id.CompareTo(b.Id));
        var start = 0;
        while (start < _elementNew.Count)
        {
            var kind = _elementNew[start].Def.Archetype.Index;
            var end = start;
            while (end < _elementNew.Count && _elementNew[end].Def.Archetype.Index == kind)
            {
                end++;
            }
            var n = end - start;
            var spec = ElementOf(_elementNew[start]);
            var max = spec.Slots.Count + 1;
            var groups = (n + max - 1) / max;
            var at = start;
            for (var gi = 0; gi < groups; gi++)
            {
                var size = n / groups + (gi < n % groups ? 1 : 0);
                var lead = _elementNew[at];
                for (var k = 1; k < size; k++)
                {
                    var wing = _elementNew[at + k];
                    wing.Wingman = new Wingman(lead.Id, k, WingmanCapacity(wing, k));
                }
                at += size;
            }
            start = end;
        }
    }

    // WingmanCapacity is how many ticks of its lead's path wingman u keeps for slot k: the longer of its
    // formation delay and its strike stagger, plus the carrot's lead and a spare.
    private static int WingmanCapacity(Unit u, int k)
    {
        var spec = ElementOf(u);
        return Math.Max(FormationDelay(u, spec, k), spec.Stagger * k) + SlotLeadTicks + 2;
    }

    // FormationDelay is slot k's delay along the lead's path (ticks): its back distance at cruise speed.
    private static int FormationDelay(Unit u, ElementSpec spec, int k)
    {
        var cruise = Math.Max(1, u.Def.Archetype.Flight!.Wing is { } g ? g.Cruise / TicksPerSecond : u.Def.Speed.Raw);
        return (int)Math.Clamp(spec.Slots[Math.Clamp(k - 1, 0, spec.Slots.Count - 1)].BackRaw / cruise, 1, 4000);
    }

    // WingmanLead is wingman u's lead while the element holds (both flying free, the same order and task; with
    // sameOrder false the order may differ: Guard's pick), else null. A lead lost hands the element to the lowest
    // ID of its wingmen.
    private Unit? WingmanLead(Unit u, bool sameOrder = true)
    {
        if (u.Wingman is not { } e)
        {
            return null;
        }
        var lead = Unit(e.Lead);
        if (lead is not { Alive: true })
        {
            Promote(e.Lead);
            if (u.Wingman is not { } e2 || Unit(e2.Lead) is not { Alive: true } nl)
            {
                return null;
            }
            (e, lead) = (e2, nl);
        }
        if (lead.Wingman is not null || lead.Owner != u.Owner || u.AirOps is not null || u.Homing || lead.AirOps is not null || lead.Homing ||
            lead.Wing is null || u.Altitude.Raw == 0)
        {
            return null;
        }
        if ((u.AirMission?.Kind ?? AirTaskKind.Orbit) != (lead.AirMission?.Kind ?? AirTaskKind.Orbit) || (u.AirMission is null) != (lead.AirMission is null))
        {
            return null;
        }
        if (!sameOrder)
        {
            return lead;
        }
        if (u.Order.Kind != lead.Order.Kind || u.Order.Target != lead.Order.Target ||
            (u.Order.Kind == OrderKind.AttackGround && u.Order.Cell != lead.Order.Cell) ||
            (u.AirMission is null && u.Order.Kind == OrderKind.Idle && u.OrbitAt != lead.OrbitAt))
        {
            return null;
        }
        return lead;
    }

    // Promote: lead `dead` is gone; the lowest ID of its wingmen leads the rest (slots renumbered in ID order).
    private void Promote(uint dead)
    {
        Unit? top = null;
        var slot = 0;
        foreach (var o in _units)
        {
            if (o.Wingman is not { } e || e.Lead != dead || !o.Alive)
            {
                continue;
            }
            if (top is null)
            {
                top = o;
                o.Wingman = null;
                continue;
            }
            slot++;
            o.Wingman = new Wingman(top.Id, slot, WingmanCapacity(o, slot));
        }
        foreach (var o in _units)
        {
            if (o.Wingman is { } e && e.Lead == dead)
            {
                o.Wingman = null; // a dead wingman's record
            }
        }
    }

    // WingmanStrikeLead is wingman u's lead when both fly the same strike (an air-to-ground attack), else null.
    private Unit? WingmanStrikeLead(Unit u) => WingmanLead(u) is { Strike: not null } lead && u.Strike is not null ? lead : null;

    // RecordLeadPath adds its lead's state this tick to wingman u's record of its path (called first each tick
    // it flies free); a broken element clears the record, so it restarts (back-filled) when the element holds again.
    private void RecordLeadPath(Unit u)
    {
        if (u.Wingman is not { } e)
        {
            return;
        }
        if (WingmanLead(u, false) is not { Wing: { } lw } lead)
        {
            (e.Count, e.Following) = (0, false);
            return;
        }
        var ph = lead.Strike is { } ls ? (byte)((byte)ls.Phase + 1 | (ls.Side > 0 ? 0x80 : 0)) : (byte)0;
        if (e.Count == 0)
        {
            // Restart: the lead's path back-filled straight behind it (its velocity now).
            for (var i = e.Capacity - 1; i >= 1; i--)
            {
                var k = e.Capacity - 1 - i;
                (e.X[k], e.Y[k], e.VX[k], e.VY[k], e.Phase[k]) = (lead.Pos.X.Raw - lw.VelX * i / TicksPerSecond, lead.Pos.Y.Raw - lw.VelY * i / TicksPerSecond, lw.VelX, lw.VelY, ph);
            }
            e.Head = e.Capacity - 2;
            e.Count = e.Capacity - 1;
        }
        e.Head = (e.Head + 1) % e.Capacity;
        (e.X[e.Head], e.Y[e.Head], e.VX[e.Head], e.VY[e.Head], e.Phase[e.Head]) = (lead.Pos.X.Raw, lead.Pos.Y.Raw, lw.VelX, lw.VelY, ph);
        e.Count = Math.Min(e.Count + 1, e.Capacity);
    }

    // PathAt is the lead's state `age` ticks ago from wingman e's record (age 0: this tick; older than the
    // record: extrapolated back; negative: extrapolated ahead): position, velocity (raw a second), strike phase byte.
    private static (long X, long Y, long VX, long VY, byte Ph) PathAt(Wingman e, int age)
    {
        var a = Math.Clamp(age, 0, e.Count - 1);
        var k = (e.Head - a + e.Capacity) % e.Capacity;
        var extra = age - a; // ticks beyond the record (positive: older; negative: ahead)
        return (e.X[k] - e.VX[k] * extra / TicksPerSecond, e.Y[k] - e.VY[k] * extra / TicksPerSecond, e.VX[k], e.VY[k], e.Phase[k]);
    }

    // SlotDelay is wingman u's delay along its lead's path now: the stagger times its slot on a common strike,
    // else its formation slot's.
    private int SlotDelay(Unit u, Wingman e, Unit lead)
    {
        var spec = ElementOf(u);
        return lead.Strike is not null && u.Strike is not null ? spec.Stagger * e.Slot : FormationDelay(u, spec, e.Slot);
    }

    // SlotPoint is the slot of wingman u at `age` ticks along its lead's path: that point offset aside (right positive).
    private static (long X, long Y, long VX, long VY, byte Ph) SlotPoint(Unit u, Wingman e, int age)
    {
        var p = PathAt(e, age);
        var spec = ElementOf(u);
        var aside = spec.Slots[Math.Clamp(e.Slot - 1, 0, spec.Slots.Count - 1)].AsideRaw;
        var l = Math.Max(1, Len3(p.VX, p.VY, 0));
        return (p.X - p.VY * aside / l, p.Y + p.VX * aside / l, p.VX, p.VY, p.Ph);
    }

    // FollowLeadPhase: wingman u on its lead's strike begins its own align where its lead began its own (the
    // stagger later: the phase on its record), and otherwise wears its lead's phase from there (it flies its slot).
    private void FollowLeadPhase(Unit u, StrikeRun run, Unit lead)
    {
        var e = u.Wingman!;
        if (run.Phase is StrikePhase.Align or StrikePhase.Run || e.Count == 0)
        {
            return; // its own run
        }
        var ph = PathAt(e, SlotDelay(u, e, lead)).Ph;
        if (ph == 0)
        {
            return;
        }
        var lp = (StrikePhase)((ph & 0x7F) - 1);
        if (lp is StrikePhase.Align or StrikePhase.Run)
        {
            if (run.Phase is StrikePhase.Ingress or StrikePhase.Extend or StrikePhase.Reposition)
            {
                LockAlign(u, run, false); // where its lead began its align, the stagger later: an offset line
            }
            return;
        }
        if (run.Phase == StrikePhase.Break && Tick - run.Since < EgressTicks)
        {
            return; // its own break first
        }
        if (lp != run.Phase)
        {
            SetPhase(u, run, lp == StrikePhase.Egress ? StrikePhase.Extend : lp);
        }
    }

    // ElementAim is where wingman u steers to fly its slot (and the speed to hold it, raw a second, in trim),
    // or null when it flies itself (no element, the element broken, its own run, an air fight).
    private Vec? ElementAim(Unit u, out long trim)
    {
        trim = 0;
        if (u.Wingman is not { } e)
        {
            return null;
        }
        e.Following = false;
        if (WingmanLead(u) is not { Wing: { } lw } lead || e.Count == 0 || u.Strike is { Phase: StrikePhase.Align or StrikePhase.Run } ||
            AirEngagement(u) is not null || u.Def.Archetype.Flight!.Wing is not { } g)
        {
            return null;
        }
        var delay = SlotDelay(u, e, lead);
        var slot = SlotPoint(u, e, delay);
        var sl = Math.Max(1, Len3(slot.VX, slot.VY, 0));
        long ex = slot.X - u.Pos.X.Raw, ey = slot.Y - u.Pos.Y.Raw;
        var along = (ex * slot.VX + ey * slot.VY) / sl; // positive: the slot is ahead of it
        e.SlotError = Len3(ex, ey, 0);
        e.Following = true;
        // The carrot: a second ahead of its own place on the offset path (its along-track error read as ticks).
        var perTick = Math.Max(1, sl / TicksPerSecond);
        var age = delay - SlotLeadTicks - (int)Math.Clamp(-along / perTick, -4000, 4000);
        var c = SlotPoint(u, e, age);
        trim = Math.Clamp(sl + along / 2, g.Min + (g.Corner - g.Min) / 2, g.Dash);
        long dx = u.Pos.X.Raw - lead.Pos.X.Raw, dy = u.Pos.Y.Raw - lead.Pos.Y.Raw;
        var dl = Len3(dx, dy, 0);
        if (dl < ElementSeparationRaw && dl > 0)
        {
            // Too near its lead: turn away from it and slow below its speed (the backup to the slot).
            trim = Math.Max(g.Min, Math.Min(trim, Len3(lw.VelX, lw.VelY, 0) - 2 * Fixed.OneRaw));
            return P(u.Pos.X.Raw + dx * 10, u.Pos.Y.Raw + dy * 10);
        }
        return P(c.X, c.Y);
    }

    // GuardFollow: a wingman on Guard takes its lead's pick (it never hunts on its own while its element holds).
    // True when it is handled (its lead's pick taken, or nothing to take).
    private bool GuardFollow(Unit u, AirMission m, WeaponDef? w)
    {
        if (WingmanLead(u, false) is not { AirMission: { } lm } lead || w is null)
        {
            return false;
        }
        if (u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround)
        {
            return false; // its strike runs (Guard's own leash check applies)
        }
        if (lm.Hunt != 0 && lead.Order.Kind == OrderKind.Attack && lead.Order.Target == lm.Hunt && Unit(lm.Hunt) is { } t && GuardStrikes(u, w, t) &&
            HasAmmo(u, w) && !u.Homing)
        {
            u.Order = new Order(OrderKind.Attack, Cell.Of(t.Pos), t.Id, 0);
            m.Hunt = t.Id;
        }
        return true;
    }
}

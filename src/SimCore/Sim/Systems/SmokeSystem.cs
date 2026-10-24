using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>A smoke screen on the map (<see cref="World.Smokes"/>): popped by a unit's smoke launchers, it stands where it was popped until it expires.</summary>
public sealed class SmokeCloud
{
    internal SmokeCloud(uint id, uint source, byte owner, Vec at, Fixed radius, uint since, uint until)
    {
        (Id, Source, Owner, At, Radius, Since, Until) = (id, source, owner, at, radius, since, until);
    }

    /// <summary>Its number, from 1 in the order screens were popped.</summary>
    public uint Id { get; }

    /// <summary>The unit that popped it (it may be gone).</summary>
    public uint Source { get; }

    /// <summary>The source's owner (a screen blocks everyone's sight alike).</summary>
    public byte Owner { get; }

    /// <summary>Its centre: where the source stood when it popped it.</summary>
    public Vec At { get; }

    /// <summary>Its radius.</summary>
    public Fixed Radius { get; }

    /// <summary>The tick it was popped.</summary>
    public uint Since { get; }

    /// <summary>The tick it is gone (it blocks sight on the ticks before).</summary>
    public uint Until { get; }
}

/// <summary>What happened to a smoke screen (<see cref="SmokeEvent"/>).</summary>
public enum SmokeEventKind : byte
{
    /// <summary>A unit popped a screen (<see cref="SmokeEvent.Unit"/> the source).</summary>
    Popped,

    /// <summary>A screen is gone (it lasted its duration).</summary>
    Expired,

    /// <summary>A guided missile lost guidance to a screen (<see cref="SmokeEvent.Unit"/> its target, <see cref="SmokeEvent.Projectile"/> the missile): it flies on unguided and drops.</summary>
    LockBroken,
}

/// <summary>A smoke event of the last tick (<see cref="World.SmokeEvents"/>), for renderers and the HUD.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Cloud">The screen's <see cref="SmokeCloud.Id"/>.</param>
/// <param name="Unit">The unit that popped it (Popped, Expired) or the missile's target (LockBroken; 0 for a shot at a point).</param>
/// <param name="Projectile">The missile that lost guidance (LockBroken), else 0.</param>
/// <param name="At">The screen's centre.</param>
/// <param name="Radius">The screen's radius.</param>
public readonly record struct SmokeEvent(SmokeEventKind Kind, uint Cloud, uint Unit, uint Projectile, Vec At, Fixed Radius);

/// <summary>A unit's smoke launchers (<see cref="World.SmokeOf"/>): pops left, the most it carries, and ticks until it may pop again.</summary>
/// <param name="Charges">Pops left.</param>
/// <param name="Max">Pops when full.</param>
/// <param name="ReadyIn">Ticks until the launchers have reloaded (0: ready).</param>
public readonly record struct SmokeStatus(int Charges, int Max, int ReadyIn);

// The smoke system (the Humvee's smoke launchers; generic for any unit with a smoke effect, docs/design.md, "Humvee"). A Smoke
// command pops a screen of the effect's radius around the unit where it stands (a charge; the next after its cooldown); the screen
// stays there for its duration. Sight: a line from a viewer to a ground unit that crosses a screen is blocked, unless both ends
// stand in that screen (an air viewer is never in one), so enemies outside can't see units inside or behind it and units inside
// see nothing outside; the vision system drops what its side spotted only through smoke (ScreenSmoke), ground radars do not see
// into a screen (they see past one), and flying units are never hidden by one. Guided missiles: one steering at a unit inside a
// screen, or whose operator's line to its target crosses one, loses guidance (StepGuided: it flies on and drops). Charges
// refill where weapons rearm. Screens are kept in pop order (their IDs) and hashed; nothing here uses floats or hash collections.
public sealed partial class World
{
    private readonly List<SmokeCloud> _smokes = [];
    private readonly List<SmokeEvent> _smokeEvents = [];
    private readonly List<Unit> _nearSmoke = []; // scratch: viewers around a target
    private uint _nextSmoke;
    private bool _smokeSeenCleared; // without fog: some SeenBy bit was cleared last tick (restored before screening again)

    /// <summary>The smoke screens on the map, in the order they were popped.</summary>
    public IReadOnlyList<SmokeCloud> Smokes => _smokes;

    /// <summary>The smoke events of the last tick (popped, expired, lock broken), in the order they happened.</summary>
    public IReadOnlyList<SmokeEvent> SmokeEvents => _smokeEvents;

    /// <summary>A unit's smoke launchers, or null when it has none.</summary>
    public SmokeStatus? SmokeOf(Unit u) =>
        u.Def.Smoke is null || u.Def.SmokeStock < 0 ? null
        : new SmokeStatus(u.Charges[u.Def.SmokeStock], u.StockMax(u.Def.SmokeStock), u.SmokeReady > Tick ? (int)(u.SmokeReady - Tick) : 0);

    /// <summary>The first smoke screen (in pop order) that covers point p, or null.</summary>
    public SmokeCloud? SmokeAt(Vec p)
    {
        foreach (var s in _smokes)
        {
            if (Within(s.At, p, s.Radius))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>Whether ground unit u stands in a smoke screen (a flying unit never does).</summary>
    public bool InSmoke(Unit u) => _smokes.Count > 0 && u.Inside == 0 && !InAir(u) && SmokeAt(u.Pos) is not null;

    // PopSmoke runs a Smoke command: each listed unit of the player's with smoke launchers, a driver and a charge, whose
    // launchers have reloaded, pops a screen around itself.
    private void PopSmoke(Command c)
    {
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Smoke is not { } sm || u.Def.SmokeStock < 0 || !u.Alive || InAir(u) ||
                u.Charges[u.Def.SmokeStock] == 0 || u.SmokeReady > Tick)
            {
                continue;
            }
            u.MutableCharges[u.Def.SmokeStock]--;
            u.SmokeReady = Tick + (uint)sm.Cooldown;
            var cloud = new SmokeCloud(++_nextSmoke, u.Id, u.Owner, u.Pos, sm.Radius, Tick, Tick + (uint)sm.DurationTicks);
            _smokes.Add(cloud);
            _smokeEvents.Add(new SmokeEvent(SmokeEventKind.Popped, cloud.Id, u.Id, 0, cloud.At, cloud.Radius));
        }
    }

    // ExpireSmoke removes the screens whose time is up (at the start of the vision system).
    private void ExpireSmoke()
    {
        for (var i = 0; i < _smokes.Count; i++)
        {
            var s = _smokes[i];
            if (s.Until <= Tick)
            {
                _smokeEvents.Add(new SmokeEvent(SmokeEventKind.Expired, s.Id, s.Source, 0, s.At, s.Radius));
            }
        }
        _smokes.RemoveAll(s => s.Until <= Tick);
    }

    // ScreenSmoke runs in the vision system after the sight passes (fog and distance), before the radars: every ground unit
    // (not a structure) spotted by an enemy side near a screen keeps that side's bit only if one of that side's viewers sees it
    // with a clear line (within its sight range, the line crossing no screen but one holding both ends). Terrain is not
    // checked again (the fog already showed the cell). Without fog SeenBy is cleared with it (an ordered attack needs it), and
    // restored next tick: without fog the map shows everything, so only a unit standing in smoke drops off it.
    private void ScreenSmoke(bool fog)
    {
        if (!fog && _smokeSeenCleared)
        {
            foreach (var u in _units)
            {
                u.SeenBy = uint.MaxValue;
            }
            _smokeSeenCleared = false;
        }
        if (_smokes.Count == 0)
        {
            return;
        }
        // The farthest any viewer's sight reaches from its position (its footprint or window counted).
        long reach = 0;
        foreach (var o in _units)
        {
            if (o.Inside == 0 && o.Def.Vision.Raw > 0)
            {
                reach = Math.Max(reach, SightRange(o).Raw + FromExtent(o));
            }
        }
        var all = (Bit((byte)_players.Count) << 1) - 2; // players 1..n
        foreach (var t in _units)
        {
            if (t.Inside != 0 || t.Garrison != 0 || !t.Alive || t.Def.IsStructure || InAir(t))
            {
                continue;
            }
            var enemies = t.SpottedBy & ~Bit(t.Owner);
            if (!fog && SmokeAt(t.Pos) is not null)
            {
                enemies |= all & ~Bit(t.Owner); // without fog every side sees it on the map until it stands in smoke
            }
            if (enemies == 0 || !NearSmoke(t.Pos, reach))
            {
                continue;
            }
            uint clear = 0;
            foreach (var o in AroundOthers(t, reach, t.Owner, true, _nearSmoke))
            {
                var bit = Bit(o.Owner);
                if ((enemies & bit) == 0 || (clear & bit) != 0 || o.Owner == t.Owner || o.Inside != 0 || o.Def.Vision.Raw == 0 ||
                    !o.IsBuilt || !o.Alive || (o.Rubble && o.Def.Archetype.Tunnel is null))
                {
                    continue;
                }
                var from = From(o, t.Pos);
                if (Within(from, t.Pos, SightRange(o)) && !SmokeBlocks(from, InAir(o), t.Pos))
                {
                    clear |= bit;
                }
            }
            var hidden = enemies & ~clear;
            if (hidden != 0)
            {
                t.SpottedBy &= ~hidden;
                if (!fog && SmokeAt(t.Pos) is not null)
                {
                    t.SeenBy &= ~hidden; // without fog the map shows everything but what stands in smoke (SpottedBy aside)
                    _smokeSeenCleared = true;
                }
            }
        }
    }

    // FromExtent: how far From(u, ...) may lie from u's position (raw): a structure's half footprint, a garrisoned soldier's window.
    private static long FromExtent(Unit u)
    {
        long e = 0;
        if (u.Garrison != 0)
        {
            e = (long)Math.Max(u.Cover.W, u.Cover.H) * Fixed.OneRaw;
        }
        if (u.Def.IsStructure)
        {
            e = Math.Max(e, (long)Math.Max(FootW(u), FootH(u)) * Fixed.OneRaw);
        }
        return e;
    }

    // NearSmoke: whether p lies within reach of a screen's edge (a sight line ending at p might cross it).
    private bool NearSmoke(Vec p, long reach)
    {
        foreach (var s in _smokes)
        {
            if (Within(s.At, p, Fixed.FromRaw(s.Radius.Raw + reach)))
            {
                return true;
            }
        }
        return false;
    }

    // SmokeBlocks: whether the sight line from a (a viewer; air: never inside a screen) to b crosses a screen that does not hold
    // both ends.
    private bool SmokeBlocks(Vec a, bool aAir, Vec b)
    {
        foreach (var s in _smokes)
        {
            var inA = !aAir && Within(s.At, a, s.Radius);
            var inB = Within(s.At, b, s.Radius);
            if (inA && inB)
            {
                continue;
            }
            if (inA || inB || SegmentMeets(s.At, s.Radius.Raw, a, b))
            {
                return true;
            }
        }
        return false;
    }

    // SegmentMeets: whether segment a-b passes within r (raw) of c. Exact in 128-bit integers: the squared distance from c to
    // the segment's nearest point, times the squared length, against r squared times the squared length.
    internal static bool SegmentMeets(Vec c, long r, Vec a, Vec b)
    {
        long dx = b.X.Raw - a.X.Raw, dy = b.Y.Raw - a.Y.Raw;
        long fx = c.X.Raw - a.X.Raw, fy = c.Y.Raw - a.Y.Raw;
        var len = (Int128)dx * dx + (Int128)dy * dy;
        var dot = (Int128)fx * dx + (Int128)fy * dy;
        var r2 = (Int128)r * r;
        if (len == 0 || dot <= 0)
        {
            return (Int128)fx * fx + (Int128)fy * fy <= r2; // a is the nearest point
        }
        if (dot >= len)
        {
            long gx = c.X.Raw - b.X.Raw, gy = c.Y.Raw - b.Y.Raw;
            return (Int128)gx * gx + (Int128)gy * gy <= r2; // b is
        }
        var f2 = (Int128)fx * fx + (Int128)fy * fy;
        return f2 * len - dot * dot <= r2 * len;
    }

    // SmokeBreaksLock: whether a guided missile steering at target loses guidance to a screen: the target (a ground unit)
    // stands in one, or (an operator-guided missile) the line from its operator to the target crosses one. The screen is
    // reported as a LockBroken event.
    private bool SmokeBreaksLock(Projectile p, Guidance g, Unit? target)
    {
        if (_smokes.Count == 0 || target is null || target.Def.IsStructure || InAir(target))
        {
            return false;
        }
        SmokeCloud? hit = SmokeAt(target.Pos);
        if (hit is null && !g.FireAndForget && Unit(p.Shooter) is { } op)
        {
            foreach (var s in _smokes)
            {
                if (SegmentMeets(s.At, s.Radius.Raw, op.Pos, target.Pos))
                {
                    hit = s;
                    break;
                }
            }
        }
        if (hit is null)
        {
            return false;
        }
        _smokeEvents.Add(new SmokeEvent(SmokeEventKind.LockBroken, hit.Id, target.Id, p.Id, hit.At, hit.Radius));
        return true;
    }

    // ClearSmokeEvents starts a tick's smoke reports.
    private void ClearSmokeEvents() => _smokeEvents.Clear();

    // HashSmoke mixes in the screens and their counter (only once one was ever popped: games without smoke hash as before).
    private void HashSmoke(ref WordHash h)
    {
        if (_nextSmoke == 0)
        {
            return;
        }
        h.Put(0x5307E);
        h.Put(_nextSmoke);
        h.PutSigned(_smokes.Count);
        foreach (var s in _smokes)
        {
            h.Put(s.Id);
            h.Put(s.Source);
            h.Put(s.Owner);
            h.PutSigned(s.At.X.Raw);
            h.PutSigned(s.At.Y.Raw);
            h.PutSigned(s.Radius.Raw);
            h.Put(s.Since);
            h.Put(s.Until);
        }
    }
}

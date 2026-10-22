using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The penetrating bomb (the stealth bomber's bunker buster; content weapon.penetrator): where it goes off, after its blast
// (BombBlast: the splash, the runway effect), within its radius:
//  1. The tunnels: every tunnel entrance (finished, standing, any owner's) whose footprint the radius reaches is hit: every
//     unit underground at it dies (the only weapon in the game that kills units in a tunnel), and so does every unit on a leg
//     whose place along the passage is within the radius; the standing entrance collapses (destroyed: the tunnel system turns
//     it into rubble, a junction nobody goes in or comes out of). Rubble already there stays rubble.
//  2. Everything else on the ground within the radius (a structure by its footprint) is destroyed whatever its armour:
//     structures, vehicles, soldiers, parked aircraft, and whoever is inside them (a garrison, a crew, riders). Flying aircraft
//     and the airfields' invulnerable pavement are untouched.
//  3. A deep crater (CraterKind.Deep) stays on the map for the penetrator's craterTicks (World.Craters; hashed).
// Events for renderers: World.TunnelKills (each unit killed underground), World.TunnelCollapses (each entrance collapsed),
// BombImpact.Crater. Everything visits units in ID order; integer distances.
public sealed partial class World
{
    private readonly List<GroundCrater> _craters = [];
    private readonly List<TunnelKill> _tunnelKills = [];
    private readonly List<TunnelCollapse> _tunnelCollapses = [];
    private readonly List<Unit> _penScratch = [];

    /// <summary>
    /// The deep craters on the map (a bunker buster's, <see cref="CraterKind.Deep"/>), oldest first; each stays until its
    /// <see cref="GroundCrater.Until"/> tick. Draw a deep penetration crater there. Hashed while not empty.
    /// </summary>
    public IReadOnlyList<GroundCrater> Craters => _craters;

    /// <summary>The units killed underground in the last tick (a bunker buster's hit on their tunnel), in unit ID order per bomb.</summary>
    public IReadOnlyList<TunnelKill> TunnelKills => _tunnelKills;

    /// <summary>The tunnel entrances a bunker buster collapsed in the last tick (they turn to rubble).</summary>
    public IReadOnlyList<TunnelCollapse> TunnelCollapses => _tunnelCollapses;

    // ClearPenetratorEvents starts a tick's tunnel reports and lets the old deep craters go (with ClearStrikeEvents).
    private void ClearPenetratorEvents()
    {
        _tunnelKills.Clear();
        _tunnelCollapses.Clear();
        _craters.RemoveAll(c => c.Until <= Tick);
    }

    // PenetratorBlast runs penetrator pen of bomb p where it went off (at): the tunnels, everything on the ground, the crater.
    private void PenetratorBlast(Projectile p, Vec at, Penetrator pen)
    {
        var r = pen.RadiusRaw;
        var by = Unit(p.Shooter);
        // 1. The tunnels: the entrances hit (in ID order), the units underground there or passing under the impact.
        _penScratch.Clear();
        foreach (var e in _units)
        {
            if (e.Def.Archetype.Tunnel is not null && e.IsBuilt && e.Alive && Len3(AimPoint(at, e).X.Raw - at.X.Raw, AimPoint(at, e).Y.Raw - at.Y.Raw, 0) <= r)
            {
                _penScratch.Add(e);
            }
        }
        foreach (var u in _units)
        {
            if (!u.InTunnel || !u.Alive)
            {
                continue;
            }
            var node = UndergroundHit(u, at, r);
            if (node == 0)
            {
                continue;
            }
            Obliterate(u, by);
            _tunnelKills.Add(new TunnelKill(p.Id, u.Id, node, u.Owner));
        }
        foreach (var e in _penScratch)
        {
            if (!e.Rubble)
            {
                Obliterate(e, by); // the tunnel system turns it into rubble
                _tunnelCollapses.Add(new TunnelCollapse(p.Id, e.Id, e.Owner, e.Pos));
            }
        }
        // 2. Everything else on the ground within the radius.
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Def.Archetype.Invulnerable || u.Def.Archetype.Tunnel is not null ||
                (u.Def.Archetype.Flight is not null && !Grounded(u)) || (u.Def.Archetype.AirBody is not null && !Grounded(u)))
            {
                continue;
            }
            var q = AimPoint(at, u);
            if (Len3(q.X.Raw - at.X.Raw, q.Y.Raw - at.Y.Raw, 0) <= r)
            {
                Obliterate(u, by);
            }
        }
        // 3. The deep crater.
        if (pen.CraterTicks > 0)
        {
            _craters.Add(new GroundCrater(p.Id, at, pen.CraterRadiusRaw, CraterKind.Deep, Tick, Tick + (uint)pen.CraterTicks, p.Owner));
        }
    }

    // UndergroundHit is the entrance whose node a bunker buster at `at` (radius r) hits underground unit u at: the entrance it
    // waits at, when one of the entrances hit (_penScratch); or, on a leg between two, the nearer end when its place along the
    // passage is within r of the impact; 0: not hit.
    private uint UndergroundHit(Unit u, Vec at, long r)
    {
        if (u.TunnelNext == 0)
        {
            return _penScratch.Exists(e => e.Id == u.Tunnel) ? u.Tunnel : 0;
        }
        if (Unit(u.Tunnel) is not { } a || Unit(u.TunnelNext) is not { } b)
        {
            return 0;
        }
        long leg = Math.Max(1, u.TunnelLeg), done = Math.Clamp(leg - u.TunnelLeft, 0, leg);
        long x = a.Pos.X.Raw + (b.Pos.X.Raw - a.Pos.X.Raw) * done / leg, y = a.Pos.Y.Raw + (b.Pos.Y.Raw - a.Pos.Y.Raw) * done / leg;
        if (Len3(x - at.X.Raw, y - at.Y.Raw, 0) > r)
        {
            return 0;
        }
        return done * 2 <= leg ? a.Id : b.Id;
    }

    // Obliterate destroys u whatever its armour (a penetrator), and everyone inside it: its garrison, its crew and riders (they die
    // in it, not thrown out). An enemy's bomb counts as their hit (Return fire, the kill's credit).
    private void Obliterate(Unit u, Unit? by)
    {
        if (by is not null && by.Owner != u.Owner && by.Owner != 0)
        {
            RecordHit(u, by);
        }
        u.HP = 0;
        if (u.Def.Archetype.Flight is not null && u.KilledBy == KillCause.None)
        {
            u.KilledBy = KillCause.Blast;
        }
        foreach (var o in _units)
        {
            if (o.Alive && (o.Garrison == u.Id || o.Inside == u.Id))
            {
                o.HP = 0;
            }
        }
    }
}

/// <summary>
/// A crater on the open ground that stays for a while (<see cref="World.Craters"/>; hashed): a bunker buster's deep penetration
/// crater. Visual only: it does not block movement (the entrance it collapsed is rubble).
/// </summary>
/// <param name="Bomb">The bomb's projectile ID (its <see cref="BombImpact"/> went off at the same tick).</param>
/// <param name="At">Its centre.</param>
/// <param name="RadiusRaw">Its radius (raw), for renderers.</param>
/// <param name="Kind">Its kind (<see cref="CraterKind.Deep"/>).</param>
/// <param name="Since">The tick it was made.</param>
/// <param name="Until">The tick it is gone (the penetrator's craterTicks later).</param>
/// <param name="Owner">The bomber's player.</param>
public readonly record struct GroundCrater(uint Bomb, Vec At, long RadiusRaw, CraterKind Kind, uint Since, uint Until, byte Owner);

/// <summary>A unit killed underground this tick by a bunker buster on its tunnel (<see cref="World.TunnelKills"/>).</summary>
/// <param name="Bomb">The bomb's projectile ID.</param>
/// <param name="Unit">The unit killed (its vehicle's crew and riders die with it and are listed too when underground themselves).</param>
/// <param name="Entrance">The tunnel entrance (node) it was at, or the nearer end of the passage it was in.</param>
/// <param name="Owner">Its player.</param>
public readonly record struct TunnelKill(uint Bomb, uint Unit, uint Entrance, byte Owner);

/// <summary>A tunnel entrance a bunker buster collapsed this tick (<see cref="World.TunnelCollapses"/>): it becomes rubble.</summary>
/// <param name="Bomb">The bomb's projectile ID.</param>
/// <param name="Entrance">The entrance.</param>
/// <param name="Owner">Its player.</param>
/// <param name="At">Its position.</param>
public readonly record struct TunnelCollapse(uint Bomb, uint Entrance, byte Owner, Vec At);

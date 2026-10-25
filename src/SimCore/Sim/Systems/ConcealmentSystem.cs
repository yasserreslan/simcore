using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// What enemy aircraft cameras make of a unit under trees now (<see cref="World.AirCoverOf"/>; the client's leaf marker).
/// </summary>
/// <param name="Hidden">
/// Under canopy that hides it (grove or dense forest by default) and not revealed by its own fire: an enemy camera above sees it
/// only within <paramref name="CameraReach"/>. False in the open or scrub, while revealed, without concealment (no fog, no
/// terrain, no content block), and for structures, traps, aircraft and units inside a vehicle, building or tunnel. Thermal and
/// ground radar may still see it (a vehicle that moved lately, <see cref="Unit.MovedAt"/>; a moving vehicle).
/// </param>
/// <param name="CameraReach">While hidden: within how far (level) a plain camera sees it (a low helicopter's a bit more); zero: never.</param>
/// <param name="RevealedUntil">Under canopy but revealed by a shot: the first tick it is hidden again (if it fires no more and stays); else 0.</param>
public readonly record struct AirCover(bool Hidden, Fixed CameraReach, uint RevealedUntil);

// Concealment from the air under trees (content: the concealment block, Model.ConcealmentRules; docs/design.md, "Concealment
// from the air"). With fog of war on a map with terrain, a ground unit that moves (infantry or a vehicle) standing in forest
// whose density is cover (grove and dense by default) is spotted on the fog grid only by what is not an air viewer (Fog.cs
// counts those stamps apart); each air unit decides about it in the vision system's distance pass, with its sensors:
//  - camera: within the table's reach for the unit's kind (infantry, vehicle), state (moved last tick or not) and density
//    (seen as in the open, within a few cells, or never), plus a low helicopter's bonus;
//  - thermal: a vehicle that moved within EngineTicks, moving infantry where the table allows, as in the open;
//  - ground radar: a moving vehicle, as in the open;
//  - any sensor: a unit that fired within FireRevealTicks, as in the open.
// "As in the open" is within the air unit's vision (a level distance). Ground units see into forest as before. Only bits are ORed
// (Unit.SpotUntil, as the distance pass does for air targets), so the candidates' order does not matter. State: Unit.MovedAt
// (hashed when not 0); everything else is read from the units (Vel, LastShot: hashed) and the terrain.
public sealed partial class World
{
    // The content's concealment rules once they apply (fog of war and a map with terrain: set by InitConcealment), else null; and
    // their tables flattened for the per-unit lookups: the camera reach by [vehicle * 8 + moving * 4 + density] (raw; Seen; 0),
    // the densities that are cover and those where thermal sees moving infantry, as bit masks. Derived from the content and the
    // setup, not state: not hashed.
    private ConcealmentRules? _concealing;
    private readonly long[] _cameraReach = new long[16];
    private int _coverDensities, _thermalInfantryDensities;

    private ConcealmentRules? Concealing => _concealing;

    // InitConcealment (AttachTerrain, before the first tick): with fog and the content's block concealment applies; every fog
    // grid counts its stamps that are not air viewers' from now on.
    private void InitConcealment()
    {
        if (_fog is not { } fog || Content.Concealment is not { } cr)
        {
            return;
        }
        _concealing = cr;
        for (var d = 0; d < 4; d++)
        {
            _cameraReach[d] = cr.InfantryStill[d];
            _cameraReach[4 + d] = cr.InfantryMoving[d];
            _cameraReach[8 + d] = cr.VehicleStill[d];
            _cameraReach[12 + d] = cr.VehicleMoving[d];
            _coverDensities |= cr.Covers(d) ? 1 << d : 0;
            _thermalInfantryDensities |= cr.ThermalMovingInfantry[d] ? 1 << d : 0;
        }
        for (var p = 1; p < fog.Length; p++)
        {
            fog[p].TrackGround();
        }
    }

    // Concealable reports whether canopy can hide t from the air: a ground unit that moves (infantry or a vehicle; not a
    // structure, trap or station), out on the map (not inside anything, not in a building).
    private static bool Concealable(Unit t) =>
        t.Def.Layer == Layers.Ground && t.Def.Archetype.Locomotor is Locomotor.Foot or Locomotor.Wheeled or Locomotor.Tracked &&
        !t.Def.IsStructure && t.Inside == 0 && t.Garrison == 0;

    // CanopyCell is the index of t's cell when canopy there hides t from the air (t concealable, its cell's forest density cover),
    // else -1. Concealment applies (Concealing is not null).
    private int CanopyCell(Unit t)
    {
        if (!Concealable(t))
        {
            return -1;
        }
        var c = Cell.Of(t.Pos);
        var i = Math.Clamp(c.Y, 0, Grid.Height - 1) * Grid.Width + Math.Clamp(c.X, 0, Grid.Width - 1);
        return (_coverDensities >> (Ground!.FlagBytes[i] & 0b11) & 1) != 0 ? i : -1; // (the forest density: TerrainMap.Forest)
    }

    // CameraReach is a camera's reach under the canopy of cell i for t (raw; ConcealmentRules.Seen; 0: never).
    private long CameraReach(Unit t, int i) =>
        _cameraReach[(t.Def.Archetype.Locomotor != Locomotor.Foot ? 8 : 0) + (MovedLastTick(t) ? 4 : 0) + (Ground!.FlagBytes[i] & 0b11)];

    // FireRevealed reports whether t fired within the rules' reveal (the next FireRevealTicks vision passes after its shot).
    private bool FireRevealed(ConcealmentRules cr, Unit t) => t.LastShot != 0 && Tick - t.LastShot <= (uint)cr.FireRevealTicks;

    private static bool MovedLastTick(Unit t) => (t.Vel.X.Raw | t.Vel.Y.Raw) != 0;

    /// <summary>
    /// The sensors air unit u looks under trees with now: its loadout mode's (<see cref="Loadout.Sensors"/>) when the mode has its
    /// own, else its archetype's (<see cref="Archetype.Sensors"/>, a camera by default).
    /// </summary>
    public AirSensors SensorsOf(Unit u)
    {
        var a = u.Def.Archetype;
        return a.Loadouts.Count > 0 && u.Loadout < a.Loadouts.Count && a.Loadouts[u.Loadout].Sensors != AirSensors.None
            ? a.Loadouts[u.Loadout].Sensors
            : a.Sensors;
    }

    // CameraBonus is what air viewer o's camera adds under trees (raw): a helicopter flying at most HeliLowRaw over the ground
    // under it gets HeliBonusRaw; everyone else nothing.
    private long CameraBonus(ConcealmentRules cr, Unit o) =>
        o.Def.Archetype.Flight?.Rotor is not null && o.Altitude.Raw - GroundRaw(o.Pos) <= cr.HeliLowRaw ? cr.HeliBonusRaw : 0;

    // CanopySees reports whether an air viewer with these sensors and camera bonus, d2 (level distance squared, raw) from ground
    // unit t under canopy of cell i, sees it within sight (raw).
    private bool CanopySees(ConcealmentRules cr, Unit t, int i, AirSensors sensors, long bonus, long d2, long sight)
    {
        long reach = 0;
        if (FireRevealed(cr, t))
        {
            reach = sight;
        }
        else
        {
            var vehicle = t.Def.Archetype.Locomotor != Locomotor.Foot;
            var moving = MovedLastTick(t);
            if ((sensors & AirSensors.Camera) != 0)
            {
                var cam = CameraReach(t, i);
                reach = cam == ConcealmentRules.Seen ? sight : cam == 0 ? 0 : cam + bonus;
            }
            if ((sensors & AirSensors.Thermal) != 0 && (vehicle ? t.MovedAt != 0 && Tick - t.MovedAt <= (uint)cr.EngineTicks
                : moving && (_thermalInfantryDensities >> (Ground!.FlagBytes[i] & 0b11) & 1) != 0))
            {
                reach = sight;
            }
            if ((sensors & AirSensors.GroundRadar) != 0 && vehicle && moving)
            {
                reach = sight;
            }
        }
        reach = Math.Min(reach, sight);
        return reach > 0 && d2 <= reach * reach;
    }

    /// <summary>
    /// Concealment from the air: what enemy aircraft cameras make of unit u where it stands now (the client's leaf marker on own
    /// units; see <see cref="AirCover"/>). Reads the world only.
    /// </summary>
    public AirCover AirCoverOf(Unit u)
    {
        if (Concealing is not { } cr || CanopyCell(u) is var i && i < 0)
        {
            return default;
        }
        if (FireRevealed(cr, u))
        {
            return new AirCover(false, Fixed.Zero, u.LastShot + (uint)cr.FireRevealTicks + 1);
        }
        var reach = CameraReach(u, i);
        return reach == ConcealmentRules.Seen ? default : new AirCover(true, Fixed.FromRaw(reach), 0);
    }
}

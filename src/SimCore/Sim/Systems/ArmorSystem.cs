using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>A hit on a unit with directional armour (<see cref="World.ArmorHits"/>), for renderers and the selection card.</summary>
/// <param name="Unit">The unit hit.</param>
/// <param name="Facing">Where it landed: front, side, rear or top.</param>
/// <param name="Percent">The percent of the damage the armour let through there.</param>
/// <param name="Damage">The HP it took.</param>
/// <param name="By">The unit whose shot it was (0: unknown, a mine, a falling wreck).</param>
public readonly record struct ArmorHit(uint Unit, HitFacing Facing, int Percent, int Damage, uint By);

// Directional armour (the tank's; archetype directionalArmor): Hurt scales every hit on such a unit by where it lands. A hit from
// above (top attack) is Top: a lobbed shot (artillery, mortar, launcher salvos, one-way drones), a flying shooter's (aircraft,
// helicopters, the Watcher's and Kaman's missiles, bombs, strafing), a drone striking (FPV), a falling wreck. Otherwise the
// direction it comes from: a splash from where it went off (when that is off the unit's centre), a projectile's direct hit from
// where it was launched (a straight shot's line), an instant shot from the shooter; compared with the hull's facing it is front
// within FrontHalf facings of the nose, rear within RearHalf of the tail, else side. A hit whose direction is unknown (a mine
// under it) counts as side. The turret is not modelled apart: the hull's facing decides. All integer, nothing hashed (scratch).
public sealed partial class World
{
    // The current hit's context (scratch, set by the systems dealing damage around their Hurt and Explode calls): from above,
    // and where it comes from (a projectile's launch point).
    private bool _hitTop;
    private bool _hitHasFrom;
    private Vec _hitFrom;

    // Set by Explode while it deals its splash: where the blast went off.
    private bool _blastHas;
    private Vec _blastAt;

    private readonly List<ArmorHit> _armorHits = [];

    /// <summary>The hits on units with directional armour in the last tick, in the order they landed (for renderers; not state).</summary>
    public IReadOnlyList<ArmorHit> ArmorHits => _armorHits;

    // SetHit sets the context of the hits about to be dealt: from above, and where they come from (null: unknown).
    private void SetHit(bool top, Vec? from)
    {
        _hitTop = top;
        _hitHasFrom = from is not null;
        _hitFrom = from ?? default;
    }

    // ClearHit ends a hit's context.
    private void ClearHit() => SetHit(false, null);

    /// <summary>
    /// Where a hit coming from point <paramref name="from"/> (on the ground) lands on unit u with directional armour: front within
    /// its FrontHalf facings of the hull's nose, rear within RearHalf of its tail, else side (also when from is u's own position).
    /// </summary>
    public static HitFacing FacingFrom(Unit u, DirectionalArmor da, Vec from)
    {
        if (!FixedPoint.Facing.TryFromDirection(from - u.Pos, out var f))
        {
            return HitFacing.Side;
        }
        var diff = Math.Abs(FixedPoint.Facing.Diff(u.Facing, f));
        return diff <= da.FrontHalf ? HitFacing.Front
            : diff >= FixedPoint.Facing.Count / 2 - da.RearHalf ? HitFacing.Rear
            : HitFacing.Side;
    }

    /// <summary>The percent of a hit's damage unit u takes when hit from point <paramref name="from"/> (100 without directional armour).</summary>
    public static int ArmorPercentFrom(Unit u, Vec from) =>
        u.Def.Archetype.DirectionalArmor is { } da ? da.PercentOf(FacingFrom(u, da, from)) : 100;

    // HitFacingOf is where the current hit lands on t (see the file comment); by is the shooter (before Hurt drops a friend's).
    private HitFacing HitFacingOf(Unit t, DirectionalArmor da, Unit? by)
    {
        if (_hitTop || by?.Def.Archetype.Flight is not null)
        {
            return HitFacing.Top;
        }
        var half = t.Def.Archetype.Radius.Raw / 2;
        if (_blastHas && (t.Pos - _blastAt).LenSq() > half * half)
        {
            return FacingFrom(t, da, _blastAt); // a splash: from where it went off
        }
        if (_hitHasFrom)
        {
            return FacingFrom(t, da, _hitFrom);
        }
        return by is not null ? FacingFrom(t, da, by.Pos) : HitFacing.Side;
    }

    // ClearArmorHits starts a tick's report of hits on directional armour.
    private void ClearArmorHits() => _armorHits.Clear();
}

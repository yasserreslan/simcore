using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A soldier's fieldcraft between trees now (<see cref="World.FieldcraftOf"/>; the client's concealed flag on its own sniper's
/// card or plate).
/// </summary>
/// <param name="Concealed">
/// Concealed now: enemy ground observers spot him only within <paramref name="Reach"/> (detectors, the scout drone among them,
/// keep today's rules), and still in scrub or a grove the air table takes him as in dense forest.
/// </param>
/// <param name="Reach">While concealed: within how far (level) enemy ground observers spot him (doubled while he crawls); else zero.</param>
/// <param name="ConcealedAt">Settling (still, or after a shot): the tick he is concealed if he stays still; else 0.</param>
/// <param name="RevealedUntil">Revealed by a shot: the first tick his concealment holds again (if he fires no more); else 0.</param>
public readonly record struct FieldCover(bool Concealed, Fixed Reach, uint ConcealedAt, uint RevealedUntil);

// Fieldcraft between trees (content: an archetype's fieldcraft block, Model.FieldcraftSpec, the sniper's; docs/design.md,
// "Fieldcraft"; the infantry brief 8.6, owner decision 28). Only where spotting applies (fog of war on a terrain map with the
// spotting block): elsewhere Unit.ConcealedAt stays 0 and nothing changes.
//  - Each vision pass (RunFieldcraft, first in RunVision) a soldier with the block in a forest cell whose ground reach is not -1
//    settles: still, he is concealed SettleTicks later; crawling (moving at most half his speed: there is no posture yet) keeps
//    a concealment he has; walking breaks it; a shot puts it off to RehideTicks after the shot (while still).
//  - Concealed, enemy ground observers (soldiers, vehicles, structures, garrisons) spot him only within the block's reach for his
//    cell's forest density (times CrawlMultiplier while moving) and within today's spotting range (SpotSees); a detector
//    (detect > 0: the sniper, the observation post, the scout drone) keeps today's rules.
//  - For FireRevealTicks after a shot today's rules alone decide (the shot's spotting state: about anyone with a line of sight).
//  - From the air, concealed and still in scrub or a grove he counts as in dense forest for the air table (CanopyCell,
//    CameraReach); thermal and the air's fire reveal stay as they are.
// State: Unit.ConcealedAt (hashed when not 0); the rest is read from hashed state (Pos, Vel, LastShot) and the terrain.
public sealed partial class World
{
    // RunFieldcraft (RunVision, before anyone looks): each soldier with fieldcraft settles, keeps or loses his concealment.
    private void RunFieldcraft()
    {
        if (_spotting is null)
        {
            return;
        }
        foreach (var u in _units)
        {
            if (u.Def.Archetype.Fieldcraft is { } fc)
            {
                u.ConcealedAt = NextConcealedAt(fc, u);
            }
        }
    }

    // NextConcealedAt is u's Unit.ConcealedAt after this tick's look at it (see RunFieldcraft).
    private uint NextConcealedAt(FieldcraftSpec fc, Unit u)
    {
        if (!u.Alive || !Concealable(u) || fc.GroundReach[Ground!.FlagBytes[CellIndex(u.Pos)] & 0b11] == FieldcraftSpec.Today)
        {
            return 0;
        }
        var at = u.ConcealedAt;
        var still = !MovedLastTick(u);
        if (!still)
        {
            if (!Crawling(u) || at == 0 || Tick < at)
            {
                at = 0; // walking breaks it; crawling keeps it only once he is concealed
            }
        }
        else if (at == 0)
        {
            at = Tick + (uint)fc.SettleTicks;
        }
        var rehide = u.LastShot + (uint)fc.RehideTicks;
        if (at != 0 && u.LastShot != 0 && rehide > at)
        {
            at = still ? rehide : 0; // a shot: concealed again once still that long after it
        }
        return at;
    }

    // Crawling reports whether u moved last tick at most half its speed (the stand-in for crawling until there is a posture).
    private static bool Crawling(Unit u) => u.Vel.LenSq() * 4 <= u.Def.Speed.Raw * u.Def.Speed.Raw;

    // FieldRevealed reports whether t's last shot still reveals it (within its fieldcraft's FireRevealTicks).
    private bool FieldRevealed(FieldcraftSpec fc, Unit t) => t.LastShot != 0 && Tick - t.LastShot <= (uint)fc.FireRevealTicks;

    // FieldConcealed reports whether t is concealed by its fieldcraft now (settled and not revealed by a shot).
    private bool FieldConcealed(Unit t) =>
        t.ConcealedAt != 0 && Tick >= t.ConcealedAt && t.Def.Archetype.Fieldcraft is { } fc && !FieldRevealed(fc, t);

    // FieldReach is within how far (raw) enemy ground observer o spots t, standing in cell i, by t's fieldcraft, or -1: today's
    // rules alone (t not concealed, o a detector, or no reach there).
    private long FieldReach(Unit o, Unit t, int i)
    {
        if (o.Def.Detect.Raw != 0 || !FieldConcealed(t))
        {
            return -1;
        }
        var fc = t.Def.Archetype.Fieldcraft!;
        var r = fc.GroundReach[Ground!.FlagBytes[i] & 0b11];
        return r < 0 ? -1 : MovedLastTick(t) ? r * fc.CrawlMultiplier : r;
    }

    // AirAsDense reports whether the air table takes t, in a cell of forest density d, as in dense forest: its fieldcraft says so
    // and it is concealed and still in scrub or a grove.
    private bool AirAsDense(Unit t, int d) =>
        d is 1 or 2 && !MovedLastTick(t) && t.Def.Archetype.Fieldcraft is { AirAsDense: true } && FieldConcealed(t);

    /// <summary>
    /// Fieldcraft between trees: what soldier u's concealment looks like to its owner now (see <see cref="FieldCover"/>); default
    /// without fieldcraft, without spotting, or out of such a cell. Reads the world only.
    /// </summary>
    public FieldCover FieldcraftOf(Unit u)
    {
        if (u.Def.Archetype.Fieldcraft is not { } fc || u.ConcealedAt == 0 || Ground is null)
        {
            return default;
        }
        var settling = Tick < u.ConcealedAt ? u.ConcealedAt : 0;
        var revealed = FieldRevealed(fc, u) ? u.LastShot + (uint)fc.FireRevealTicks + 1 : 0;
        if (settling != 0 || revealed != 0)
        {
            return new FieldCover(false, Fixed.Zero, settling, revealed);
        }
        var r = fc.GroundReach[Ground.FlagBytes[CellIndex(u.Pos)] & 0b11];
        return new FieldCover(true, Fixed.FromRaw(Math.Max(MovedLastTick(u) ? r * fc.CrawlMultiplier : r, 0)), 0, 0);
    }
}

using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>
/// A soldier's job in an infantry squad (docs/design.md, "Infantry squads"): the same habits in every mode, the mode only
/// changes the shape around them. Values are stable (content and the client name them): new roles are appended.
/// </summary>
public enum SquadRole : byte
{
    /// <summary>The rifleman and the engineer: forms the file and the line.</summary>
    Line,

    /// <summary>The medic: the middle of the file, behind the line.</summary>
    Medic,

    /// <summary>The sniper: ahead of the file, behind the line to a side.</summary>
    Recon,

    /// <summary>Anti-armour (RPG): behind the leader in the file, the ends of the line.</summary>
    Flank,

    /// <summary>The Kornet team: the rear of the file, behind the line.</summary>
    LongFlank,

    /// <summary>Air defence (Igla): the rear of the file, spread behind the line.</summary>
    AirGuard,

    /// <summary>The mortar: the rear, packed.</summary>
    Support,
}

/// <summary>
/// One role's place in Assault (the line facing the move): how far behind the line it stands and how far apart soldiers of the
/// role stand across it. Raw fixed-point distances.
/// </summary>
/// <param name="Back">How far behind the line (0: in it).</param>
/// <param name="Gap">How far apart across the move.</param>
public readonly record struct SquadPlace(Fixed Back, Fixed Gap);

/// <summary>
/// The infantry squad rules (content: the optional top-level <c>squad</c> block; docs/design.md, "Infantry squads"). Without the
/// block squads can't be formed. Distances are compiled from tenths of a cell (raw fixed point), speeds are percents of the slowest
/// member's, times are ticks.
/// </summary>
public sealed class SquadRules
{
    /// <summary>The fewest members a squad has: with fewer it disbands (2).</summary>
    public required int MinSize { get; init; }

    /// <summary>The most members a squad has (10).</summary>
    public required int MaxSize { get; init; }

    /// <summary>A soldier must have every one of these tags to join (infantry).</summary>
    public required TagSet Need { get; init; }

    /// <summary>A soldier with any of these tags never joins (workers, pilots).</summary>
    public required TagSet Never { get; init; }

    /// <summary>The role of each class, by tag: the first entry whose tag the archetype has; none matching: <see cref="SquadRole.Line"/>.</summary>
    public required IReadOnlyList<(TagSet Tag, SquadRole Role)> Roles { get; init; }

    /// <summary>The roles that never lead (the medic, the sniper, the mortar), a bit per <see cref="SquadRole"/> value.</summary>
    public required int NoLeadMask { get; init; }

    /// <summary>March: how far apart soldiers walk in the file.</summary>
    public required Fixed MarchSpacing { get; init; }

    /// <summary>March: the squad's speed in percent of its slowest member's.</summary>
    public required int MarchSpeedPercent { get; init; }

    /// <summary>March: how far ahead of the leader recon walks along the path.</summary>
    public required Fixed MarchAhead { get; init; }

    /// <summary>March: how far apart air guards walk one behind the other at the rear.</summary>
    public required Fixed MarchAirGuardGap { get; init; }

    /// <summary>March: how far apart support soldiers walk one behind the other at the rear (packed).</summary>
    public required Fixed MarchSupportGap { get; init; }

    /// <summary>Assault: how far apart soldiers stand in the line.</summary>
    public required Fixed AssaultSpacing { get; init; }

    /// <summary>Assault: the squad's speed in percent of its slowest member's.</summary>
    public required int AssaultSpeedPercent { get; init; }

    /// <summary>Assault: each role's place, indexed by <see cref="SquadRole"/> (Line and Flank are in the line: Back 0).</summary>
    public required IReadOnlyList<SquadPlace> AssaultPlaces { get; init; }

    /// <summary>Assault: how far beyond the end of the line recon stands, on the right.</summary>
    public required Fixed AssaultReconSide { get; init; }

    /// <summary>A member farther than this from his slot makes the squad wait for him (at most <see cref="RegroupTicks"/>).</summary>
    public required Fixed Regroup { get; init; }

    /// <summary>How long a moving squad waits at most for members that fell behind before it walks on anyway.</summary>
    public required int RegroupTicks { get; init; }

    /// <summary>How far apart the squads of one move order stand at its goal, side by side across the move.</summary>
    public required Fixed GroupGap { get; init; }

    /// <summary>Hold and its position picking (the optional <c>hold</c> part of the block, phase S2); null: squads can't hold.</summary>
    public SquadHoldRules? Hold { get; init; }

    /// <summary>Returns the role of a soldier of these tags.</summary>
    public SquadRole RoleOf(TagSet tags)
    {
        foreach (var (tag, role) in Roles)
        {
            if (tags.HasAll(tag))
            {
                return role;
            }
        }
        return SquadRole.Line;
    }

    /// <summary>Whether a soldier of this role may lead.</summary>
    public bool Leads(SquadRole role) => (NoLeadMask & (1 << (int)role)) == 0;

    /// <summary>Whether a soldier of these tags may join a squad (the tags only: the owner, the layer and so on are checked apart).</summary>
    public bool Joins(TagSet tags) => tags.HasAll(Need) && (tags.Bits & Never.Bits) == 0;

    internal void Digest(Action<long> put)
    {
        put(MinSize);
        put(MaxSize);
        put((long)Need.Bits);
        put((long)Never.Bits);
        put(Roles.Count);
        foreach (var (tag, role) in Roles)
        {
            put((long)tag.Bits);
            put((long)role);
        }
        put(NoLeadMask);
        put(MarchSpacing.Raw);
        put(MarchSpeedPercent);
        put(MarchAhead.Raw);
        put(MarchAirGuardGap.Raw);
        put(MarchSupportGap.Raw);
        put(AssaultSpacing.Raw);
        put(AssaultSpeedPercent);
        foreach (var p in AssaultPlaces)
        {
            put(p.Back.Raw);
            put(p.Gap.Raw);
        }
        put(AssaultReconSide.Raw);
        put(Regroup.Raw);
        put(RegroupTicks);
        put(GroupGap.Raw);
        if (Hold is { } h)
        {
            put(0x401D); // only content with Hold: an S1 block hashes as before
            h.Digest(put);
        }
    }
}

/// <summary>
/// One role's weights in Hold's position picking (docs/design.md, "Squads: Hold and position picking"): each factor of a cell
/// scores 0 to 100 and the cell's score is the sum of factor times weight. Integers.
/// </summary>
/// <param name="Cover">Trees, a wall, rubble, a building or a crest between the cell and the threat.</param>
/// <param name="Trees">Under canopy that hides from the air.</param>
/// <param name="Sight">How far the cell's sight lanes toward the threat reach (out to the soldier's own reach).</param>
/// <param name="Open">How much of those lanes is open ground or road.</param>
/// <param name="Spacing">How far from the teammates already placed (full at the role's spread).</param>
/// <param name="Safety">How far back from the threat side (along the facing).</param>
/// <param name="Range">The threat within weapon reach, best at three quarters of it.</param>
/// <param name="Near">How near the spot.</param>
public readonly record struct SquadHoldWeights(int Cover, int Trees, int Sight, int Open, int Spacing, int Safety, int Range, int Near);

/// <summary>One role's habits in Hold: its weights and its hard limits. Distances raw fixed point.</summary>
/// <param name="Weights">The factors' weights.</param>
/// <param name="Back">The least depth along the facing behind the line soldiers placed before him (their mean; before any, behind the spot; negative: so far ahead is allowed).</param>
/// <param name="Apart">The least distance from any teammate placed before him.</param>
/// <param name="ApartSame">The least distance from a teammate of the same role placed before him (air guards: 8 cells).</param>
/// <param name="Spread">The distance from the nearest teammate at which spacing scores in full.</param>
/// <param name="Garrison">Whether a garrisonable building counts as a position for him (his weapon helps from inside).</param>
public readonly record struct SquadHoldRole(SquadHoldWeights Weights, Fixed Back, Fixed Apart, Fixed ApartSame, Fixed Spread, bool Garrison);

/// <summary>
/// Hold's rules (the <c>hold</c> part of the <c>squad</c> block; docs/design.md, "Squads: Hold and position picking"). Distances raw
/// fixed point (compiled from tenths of a cell); cover points 0 to 100.
/// </summary>
public sealed class SquadHoldRules
{
    /// <summary>How far from the spot cells are scored (12 cells).</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The most cells scored a squad (about 600): the nearest the spot first.</summary>
    public required int Cells { get; init; }

    /// <summary>Without a dragged facing: how far ahead of the spot along the facing the threat is.</summary>
    public required Fixed Threat { get; init; }

    /// <summary>With a dragged facing: the threat is where the drag ends, kept this near the spot at least ...</summary>
    public required Fixed ThreatMin { get; init; }

    /// <summary>... and this far at most.</summary>
    public required Fixed ThreatMax { get; init; }

    /// <summary>How far sight lanes are walked at most.</summary>
    public required Fixed LaneMax { get; init; }

    /// <summary>The two side lanes aim this far either side of the threat, across the facing.</summary>
    public required Fixed LaneSpread { get; init; }

    /// <summary>Cover points: a wall (rock, a building or its rubble) next to the cell on the threat's side.</summary>
    public required int CoverWall { get; init; }

    /// <summary>Cover points: a tree trunk (standing or fallen) next to the cell on the threat's side.</summary>
    public required int CoverTrunk { get; init; }

    /// <summary>Cover points: the cell in a grove.</summary>
    public required int CoverGrove { get; init; }

    /// <summary>Cover points: the cell in dense forest.</summary>
    public required int CoverDense { get; init; }

    /// <summary>Cover points: a crest hides the cell from the threat.</summary>
    public required int CoverCrest { get; init; }

    /// <summary>The order roles pick in (each role's members in ID order; each takes the best cell left).</summary>
    public required IReadOnlyList<SquadRole> Order { get; init; }

    /// <summary>Each role's habits, indexed by <see cref="SquadRole"/>.</summary>
    public required IReadOnlyList<SquadHoldRole> Roles { get; init; }

    internal void Digest(Action<long> put)
    {
        put(Radius.Raw);
        put(Cells);
        put(Threat.Raw);
        put(ThreatMin.Raw);
        put(ThreatMax.Raw);
        put(LaneMax.Raw);
        put(LaneSpread.Raw);
        put(CoverWall);
        put(CoverTrunk);
        put(CoverGrove);
        put(CoverDense);
        put(CoverCrest);
        put(Order.Count);
        foreach (var r in Order)
        {
            put((long)r);
        }
        put(Roles.Count);
        foreach (var r in Roles)
        {
            var w = r.Weights;
            put(w.Cover);
            put(w.Trees);
            put(w.Sight);
            put(w.Open);
            put(w.Spacing);
            put(w.Safety);
            put(w.Range);
            put(w.Near);
            put(r.Back.Raw);
            put(r.Apart.Raw);
            put(r.ApartSame.Raw);
            put(r.Spread.Raw);
            put(r.Garrison ? 1 : 0);
        }
    }
}

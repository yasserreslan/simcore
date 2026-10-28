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

    /// <summary>The ambush (the optional <c>ambush</c> part of the block, phase S3; it needs <see cref="Hold"/>); null: squads can't ambush.</summary>
    public SquadAmbushRules? Ambush { get; init; }

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
        if (Ambush is { } a)
        {
            put(0xA3B5); // only content with an ambush: an S2 block hashes as before
            a.Digest(put);
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

/// <summary>Which end of the kill zone a role's <see cref="SquadAmbushWeights.End"/> factor draws him to.</summary>
public enum AmbushEnd : byte
{
    /// <summary>Neither: the factor scores nothing.</summary>
    None,

    /// <summary>The zone's start, where the enemy comes in (the sniper, watching the approach).</summary>
    Near,

    /// <summary>The far end, where the column is stopped (the Kornet's lane down the road).</summary>
    Far,

    /// <summary>The two ends in turn, by ID: the first to the far end, the second to the start, ... (the RPGs: lead and tail).</summary>
    Both,
}

/// <summary>
/// One role's weights in the ambush's position picking (docs/design.md, "Squads: Ambush"): Hold's eight factors, with the kill zone
/// as the threat, and four of the ambush's own. Each factor scores 0 to 100; a cell's score is the sum of factor times weight.
/// </summary>
/// <param name="Hold">Hold's factors: cover from the zone, trees, sight of the zone (its start, middle and far end), open ground, spacing,
/// safety (depth back from the zone), range (the zone within weapon reach), near (near the zone).</param>
/// <param name="End">Near his end of the zone (see <see cref="AmbushEnd"/>).</param>
/// <param name="Lane">How much of the lane from the cell down the road to the approach it sees (out to his own reach).</param>
/// <param name="Approach">Whether it sees the approach point itself (within his reach).</param>
/// <param name="Hidden">How little of the zone it sees: 100 when none of its three aims.</param>
public readonly record struct SquadAmbushWeights(SquadHoldWeights Hold, int End, int Lane, int Approach, int Hidden);

/// <summary>One role's habits in the ambush: its weights and hard limits. Distances raw fixed point.</summary>
/// <param name="Weights">The factors' weights.</param>
/// <param name="LateralMin">The least distance from the zone's line (0: none).</param>
/// <param name="LateralMax">The most distance from the zone's line (0: none).</param>
/// <param name="Back">The least depth back from the zone behind the line soldiers placed before him (their mean; as in Hold).</param>
/// <param name="Apart">The least distance from any teammate placed before him.</param>
/// <param name="ApartSame">The least distance from a teammate of the same role placed before him.</param>
/// <param name="Spread">The distance from the nearest teammate at which spacing scores in full.</param>
/// <param name="Garrison">Whether a garrisonable building counts as a position for him.</param>
/// <param name="End">Which end of the zone draws him.</param>
/// <param name="Hide">Whether he must stand out of sight of the zone (the medic).</param>
public readonly record struct SquadAmbushRole(
    SquadAmbushWeights Weights, Fixed LateralMin, Fixed LateralMax, Fixed Back, Fixed Apart, Fixed ApartSame, Fixed Spread, bool Garrison,
    AmbushEnd End, bool Hide);

/// <summary>
/// The ambush's rules (the <c>ambush</c> part of the <c>squad</c> block; docs/design.md, "Squads: Ambush"). Distances raw fixed point
/// (compiled from tenths of a cell), times in ticks. Cover points are Hold's.
/// </summary>
public sealed class SquadAmbushRules
{
    /// <summary>The shortest kill zone (6 cells): shorter orders are refused.</summary>
    public required Fixed MinLength { get; init; }

    /// <summary>The longest kill zone (30 cells): longer orders are refused.</summary>
    public required Fixed MaxLength { get; init; }

    /// <summary>How far from the zone cells are scored (12 cells), on one side.</summary>
    public required Fixed Radius { get; init; }

    /// <summary>The most cells scored for one side (about 600): the nearest the zone first.</summary>
    public required int Cells { get; init; }

    /// <summary>The zone reaches this far either side of its line (triggers, mines; 2 cells).</summary>
    public required Fixed Width { get; init; }

    /// <summary>The side's spot: this far out from the zone's middle (where a squad far away marches to first; 5 cells).</summary>
    public required Fixed Spot { get; init; }

    /// <summary>The approach point: this far before the zone's start, along it (10 cells).</summary>
    public required Fixed Approach { get; init; }

    /// <summary>An enemy vehicle within this of the far end (along the zone) springs it (3 cells).</summary>
    public required Fixed FarEnd { get; init; }

    /// <summary>This many enemies in the zone spring it (3).</summary>
    public required int Crowd { get; init; }

    /// <summary>What counts as a vehicle for the far-end trigger and the RPGs' and the Kornet's first targets.</summary>
    public required TagSet Vehicles { get; init; }

    /// <summary>Sprung, this long without a fight: Ready again (10 s).</summary>
    public required int RearmTicks { get; init; }

    /// <summary>Setting, this long at most: Ready then whatever (a member who can't reach his position; 60 s).</summary>
    public required int SetTicks { get; init; }

    /// <summary>The roles that lay mines (the line: assault soldiers and engineers), a bit per <see cref="SquadRole"/> value.</summary>
    public required int LayerMask { get; init; }

    /// <summary>The anti-tank mine's archetype (laid where vehicles drive, the far end first).</summary>
    public required int AtMine { get; init; }

    /// <summary>The anti-personnel mine's archetype (laid where infantry would run for cover).</summary>
    public required int ApMine { get; init; }

    /// <summary>The booby trap's archetype (laid as the anti-personnel mines are).</summary>
    public required int Trap { get; init; }

    /// <summary>Booby traps a soldier lays at most (1: the engineer's one).</summary>
    public required int TrapsEach { get; init; }

    /// <summary>Anti-tank mines are laid this far apart along the zone (2 cells).</summary>
    public required Fixed AtGap { get; init; }

    /// <summary>Anti-personnel mines and booby traps are laid this far apart (3 cells).</summary>
    public required Fixed ApGap { get; init; }

    /// <summary>Anti-personnel mines lie this far from the zone's line at least, on the far side ...</summary>
    public required Fixed ApNear { get; init; }

    /// <summary>... and this far at most.</summary>
    public required Fixed ApFar { get; init; }

    /// <summary>Each role's habits, indexed by <see cref="SquadRole"/>.</summary>
    public required IReadOnlyList<SquadAmbushRole> Roles { get; init; }

    /// <summary>Whether soldiers of this role lay mines.</summary>
    public bool Lays(SquadRole role) => (LayerMask & (1 << (int)role)) != 0;

    internal void Digest(Action<long> put)
    {
        put(MinLength.Raw);
        put(MaxLength.Raw);
        put(Radius.Raw);
        put(Cells);
        put(Width.Raw);
        put(Spot.Raw);
        put(Approach.Raw);
        put(FarEnd.Raw);
        put(Crowd);
        put((long)Vehicles.Bits);
        put(RearmTicks);
        put(SetTicks);
        put(LayerMask);
        put(AtMine);
        put(ApMine);
        put(Trap);
        put(TrapsEach);
        put(AtGap.Raw);
        put(ApGap.Raw);
        put(ApNear.Raw);
        put(ApFar.Raw);
        put(Roles.Count);
        foreach (var r in Roles)
        {
            var w = r.Weights.Hold;
            put(w.Cover);
            put(w.Trees);
            put(w.Sight);
            put(w.Open);
            put(w.Spacing);
            put(w.Safety);
            put(w.Range);
            put(w.Near);
            put(r.Weights.End);
            put(r.Weights.Lane);
            put(r.Weights.Approach);
            put(r.Weights.Hidden);
            put(r.LateralMin.Raw);
            put(r.LateralMax.Raw);
            put(r.Back.Raw);
            put(r.Apart.Raw);
            put(r.ApartSame.Raw);
            put(r.Spread.Raw);
            put(r.Garrison ? 1 : 0);
            put((long)r.End);
            put(r.Hide ? 1 : 0);
        }
    }
}

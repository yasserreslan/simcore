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
    }
}

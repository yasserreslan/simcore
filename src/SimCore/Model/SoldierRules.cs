namespace SimCore.Model;

/// <summary>
/// The numbers of the content's <c>soldiers</c> block (INF-E2a; docs/design.md, "Wounds, rescue, bodies, ranks and Retreat"), by
/// name: <see cref="SoldierRules.Names"/> gives each its content key (group.key). Percents 0 to 100 (changes may be negative),
/// distances in tenths of a cell, times in ticks. Values are stable: new ones are appended.
/// </summary>
public enum SoldierValue
{
    /// <summary>wounds.wounded: at or under this percent of max HP he is wounded.</summary>
    WoundedPercent,
    /// <summary>wounds.down: under this percent of max HP (and above 0) he is down.</summary>
    DownPercent,
    /// <summary>wounds.speed: a wounded man's speed change, percent (-40).</summary>
    WoundedSpeed,
    /// <summary>wounds.aim: a wounded man's aim change, percent (-25).</summary>
    WoundedAim,
    /// <summary>wounds.posture: a wounded man's posture-change speed, percent (-50; a hook for INF-E1b's postures).</summary>
    WoundedPosture,
    /// <summary>wounds.suppression: a wounded man's suppression gains, percent (+25; a hook for INF-E1b's suppression).</summary>
    WoundedSuppression,
    /// <summary>wounds.bleedTicks: a downed man bleeds out after this long.</summary>
    BleedTicks,
    /// <summary>wounds.crawlSpeed: his crawl, tenths of a cell a second.</summary>
    CrawlSpeed,
    /// <summary>wounds.crawlReach: he crawls to cover or a squadmate within this.</summary>
    CrawlReach,
    /// <summary>rescue.reach: the nearest free squadmate within this of the downed man carries him.</summary>
    RescueReach,
    /// <summary>rescue.cover: to the nearest hard cover within this of the downed man (or toward the medic).</summary>
    RescueCover,
    /// <summary>rescue.speed: the carrier's speed, percent of his own.</summary>
    RescueSpeed,
    /// <summary>rescue.pauseTicks: put down at cover, the bleeding stops this long.</summary>
    RescuePauseTicks,
    /// <summary>rescue.calmTicks: "safe" (the suppression hook): no enemy shot at the carrier for this long.</summary>
    RescueCalmTicks,
    /// <summary>rescue.enemyRange: no enemy his side sees within this percent of its weapon's range of the downed man.</summary>
    RescueEnemyRange,
    /// <summary>rescue.hardCover: a cell is hard cover with at least these cover points (Hold's cover rules).</summary>
    HardCover,
    /// <summary>medic.treatTicks: treating a downed man, kneeling beside him.</summary>
    TreatTicks,
    /// <summary>medic.treatHp: treated, he is back to wounded at this percent of max HP.</summary>
    TreatHpPercent,
    /// <summary>medic.search: an idle medic goes to the downed, then the wounded, within this.</summary>
    MedicSearch,
    /// <summary>medic.reach: he treats within this of the downed man.</summary>
    MedicReach,
    /// <summary>medic.healReach: he walks to within this of a wounded man (his aura heals him there).</summary>
    MedicHealReach,
    /// <summary>bodies.ticks: a body lies this long.</summary>
    BodyTicks,
    /// <summary>bodies.max: at most this many bodies on the map; the oldest goes first.</summary>
    BodyMax,
    /// <summary>scavenge.ticks: kneeling at a body.</summary>
    ScavengeTicks,
    /// <summary>scavenge.low: on his own only under this percent of his main arm's load.</summary>
    ScavengeLow,
    /// <summary>scavenge.reach: on his own only to a body within this.</summary>
    ScavengeReach,
    /// <summary>scavenge.calmTicks: on his own only with no enemy shot at him for this long (the suppression hook).</summary>
    ScavengeCalmTicks,
    /// <summary>scavenge.at: he kneels within this of the body.</summary>
    ScavengeAt,
    /// <summary>scavenge.search: an ordered Scavenge with no body named takes the nearest useful one within this.</summary>
    ScavengeSearch,
    /// <summary>ranks.regular: points for Regular.</summary>
    RankRegular,
    /// <summary>ranks.veteran: points for Veteran.</summary>
    RankVeteran,
    /// <summary>ranks.elite: points for Elite.</summary>
    RankElite,
    /// <summary>ranks.aimRegular: Regular's aim, percent.</summary>
    AimRegular,
    /// <summary>ranks.aimVeteran.</summary>
    AimVeteran,
    /// <summary>ranks.aimElite.</summary>
    AimElite,
    /// <summary>ranks.reloadRegular: Regular's reloads (and posture changes, a hook) faster, percent.</summary>
    ReloadRegular,
    /// <summary>ranks.reloadVeteran.</summary>
    ReloadVeteran,
    /// <summary>ranks.reloadElite.</summary>
    ReloadElite,
    /// <summary>ranks.suppressRegular: Regular's suppression gains lower, percent (a hook for INF-E1b).</summary>
    SuppressRegular,
    /// <summary>ranks.suppressVeteran.</summary>
    SuppressVeteran,
    /// <summary>ranks.suppressElite.</summary>
    SuppressElite,
    /// <summary>ranks.kill: points for killing an enemy soldier.</summary>
    XpKill,
    /// <summary>ranks.killHigher: points for one who outranked him.</summary>
    XpKillHigher,
    /// <summary>ranks.share: points for a share in a kill.</summary>
    XpShare,
    /// <summary>ranks.shareTicks: damage within this before the kill earns a share.</summary>
    XpShareTicks,
    /// <summary>ranks.survive: points for surviving a battle.</summary>
    XpSurvive,
    /// <summary>ranks.surviveHurt: more if he was wounded or down during it.</summary>
    XpSurviveHurt,
    /// <summary>ranks.battleCalmTicks: a battle ends after this long with no shots near him.</summary>
    BattleCalmTicks,
    /// <summary>ranks.battleReach: shots within this of him keep the battle on.</summary>
    BattleReach,
    /// <summary>ranks.rescue: points for carrying a downed man to cover.</summary>
    XpRescue,
    /// <summary>ranks.treat: points for a medic treating a downed man.</summary>
    XpTreat,
    /// <summary>retreat.min: the squad falls back at least this far from the threat.</summary>
    RetreatMin,
    /// <summary>retreat.max: and at most this far.</summary>
    RetreatMax,
    /// <summary>retreat.boundMin: a pair's shortest bound.</summary>
    BoundMin,
    /// <summary>retreat.boundMax: a pair's longest bound.</summary>
    BoundMax,
    /// <summary>retreat.boundTicks: a pair's bound ends after this long at most.</summary>
    BoundTicks,
    /// <summary>retreat.threatSearch: the threat it knows: an enemy its side sees within this of the squad.</summary>
    ThreatSearch,
    /// <summary>retreat.arrive: a member is there within this of his place.</summary>
    RetreatArrive,
}

/// <summary>
/// The content's <c>soldiers</c> block (INF-E2a): wounds and the down state, rescue, the medic's treatment, bodies and
/// scavenging, ranks and the Retreat order, for the units with every tag of <see cref="Need"/>.
/// </summary>
public sealed class SoldierRules
{
    /// <summary>The content key of each <see cref="SoldierValue"/>, in order.</summary>
    public static readonly string[] Names =
    [
        "wounds.wounded", "wounds.down", "wounds.speed", "wounds.aim", "wounds.posture", "wounds.suppression", "wounds.bleedTicks",
        "wounds.crawlSpeed", "wounds.crawlReach",
        "rescue.reach", "rescue.cover", "rescue.speed", "rescue.pauseTicks", "rescue.calmTicks", "rescue.enemyRange", "rescue.hardCover",
        "medic.treatTicks", "medic.treatHp", "medic.search", "medic.reach", "medic.healReach",
        "bodies.ticks", "bodies.max",
        "scavenge.ticks", "scavenge.low", "scavenge.reach", "scavenge.calmTicks", "scavenge.at", "scavenge.search",
        "ranks.regular", "ranks.veteran", "ranks.elite", "ranks.aimRegular", "ranks.aimVeteran", "ranks.aimElite",
        "ranks.reloadRegular", "ranks.reloadVeteran", "ranks.reloadElite", "ranks.suppressRegular", "ranks.suppressVeteran",
        "ranks.suppressElite", "ranks.kill", "ranks.killHigher", "ranks.share", "ranks.shareTicks", "ranks.survive",
        "ranks.surviveHurt", "ranks.battleCalmTicks", "ranks.battleReach", "ranks.rescue", "ranks.treat",
        "retreat.min", "retreat.max", "retreat.boundMin", "retreat.boundMax", "retreat.boundTicks", "retreat.threatSearch",
        "retreat.arrive",
    ];

    /// <summary>Who has wounds, bodies and ranks: units with every one of these tags.</summary>
    public required TagSet Need { get; init; }

    /// <summary>Medics: units with any of these tags treat the downed and go to the wounded.</summary>
    public required TagSet Medics { get; init; }

    /// <summary>Never scavenge on their own (medics, non-combatants): units with any of these tags.</summary>
    public required TagSet NoScavenge { get; init; }

    /// <summary>Points for killing a vehicle or aircraft: the first entry whose tag the victim has.</summary>
    public required IReadOnlyList<(TagSet Tag, int Points)> VehicleXp { get; init; }

    /// <summary>The numbers, one per <see cref="SoldierValue"/> (read them through the indexer).</summary>
    public required IReadOnlyList<int> Values { get; init; }

    /// <summary>One of the block's numbers.</summary>
    public int this[SoldierValue v] => Values[(int)v];

    /// <summary>Whether a unit with these tags has wounds, a body and a rank.</summary>
    public bool Applies(TagSet tags) => Need.Bits != 0 && tags.HasAll(Need);

    /// <summary>Whether a unit with these tags is a medic.</summary>
    public bool IsMedic(TagSet tags) => (tags.Bits & Medics.Bits) != 0;

    /// <summary>The points for killing a unit with these tags that has no rank (a vehicle or aircraft): 0 for none.</summary>
    public int VehiclePoints(TagSet tags)
    {
        foreach (var (tag, points) in VehicleXp)
        {
            if ((tags.Bits & tag.Bits) != 0)
            {
                return points;
            }
        }
        return 0;
    }

    internal void Digest(Action<long> put)
    {
        put((long)Need.Bits);
        put((long)Medics.Bits);
        put((long)NoScavenge.Bits);
        put(VehicleXp.Count);
        foreach (var (tag, points) in VehicleXp)
        {
            put((long)tag.Bits);
            put(points);
        }
        put(Values.Count);
        foreach (var v in Values)
        {
            put(v);
        }
    }
}

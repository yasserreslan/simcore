namespace SimCore.Model;

// Generated with the content block's keys (one list): docs/design.md, "Infantry combat: the ground and the senses".
/// <summary>The numbers of the infantryCombat block, in the order of <see cref="CombatNames.Names"/>.</summary>
public enum CombatValue
{
    /// <summary>infantryCombat.perception.reactMin</summary>
    ReactMin,
    /// <summary>infantryCombat.perception.reactMax</summary>
    ReactMax,
    /// <summary>infantryCombat.perception.surpriseMin</summary>
    SurpriseMin,
    /// <summary>infantryCombat.perception.surpriseMax</summary>
    SurpriseMax,
    /// <summary>infantryCombat.perception.shareReact</summary>
    ShareReact,
    /// <summary>infantryCombat.perception.laterContact</summary>
    LaterContact,
    /// <summary>infantryCombat.perception.rankRegular</summary>
    ReactRegular,
    /// <summary>infantryCombat.perception.rankVeteran</summary>
    ReactVeteran,
    /// <summary>infantryCombat.perception.rankElite</summary>
    ReactElite,
    /// <summary>infantryCombat.perception.reactFloor</summary>
    ReactFloor,
    /// <summary>infantryCombat.perception.seenMemory</summary>
    SeenMemory,
    /// <summary>infantryCombat.perception.heardMemory</summary>
    HeardMemory,
    /// <summary>infantryCombat.perception.reportedMemory</summary>
    ReportedMemory,
    /// <summary>infantryCombat.perception.squadShareTicks</summary>
    SquadShareTicks,
    /// <summary>infantryCombat.perception.nearShareTicks</summary>
    NearShareTicks,
    /// <summary>infantryCombat.perception.allyShareTicks</summary>
    AllyShareTicks,
    /// <summary>infantryCombat.perception.shareRadius</summary>
    ShareRadius,
    /// <summary>infantryCombat.perception.contactsPerSoldier</summary>
    ContactsPerSoldier,
    /// <summary>infantryCombat.perception.contactsPerSquad</summary>
    ContactsPerSquad,
    /// <summary>infantryCombat.perception.alertRange</summary>
    AlertRange,
    /// <summary>infantryCombat.perception.reachPercent</summary>
    ReachPercent,
    /// <summary>infantryCombat.perception.contactRangePercent</summary>
    ContactRangePercent,
    /// <summary>infantryCombat.perception.shotAtMemoryTicks</summary>
    ShotAtMemoryTicks,
    /// <summary>infantryCombat.perception.heardPerThink</summary>
    HeardPerThink,
    /// <summary>infantryCombat.perception.areaHalf</summary>
    AreaHalf,
    /// <summary>infantryCombat.perception.areaOffset</summary>
    AreaOffset,
    /// <summary>infantryCombat.perception.areaMerge</summary>
    AreaMerge,
    /// <summary>infantryCombat.perception.areaTicks</summary>
    AreaTicks,
    /// <summary>infantryCombat.perception.fireRevealTicks</summary>
    FireRevealTicks,
    /// <summary>infantryCombat.perception.fireRevealExtra</summary>
    FireRevealExtra,
    /// <summary>infantryCombat.perception.effectiveShots</summary>
    EffectiveShots,
    /// <summary>infantryCombat.perception.effectiveTicks</summary>
    EffectiveTicks,
    /// <summary>infantryCombat.perception.effectiveHitTicks</summary>
    EffectiveHitTicks,
    /// <summary>infantryCombat.perception.nearShot</summary>
    NearShot,
    /// <summary>infantryCombat.perception.axisSteps</summary>
    AxisSteps,
    /// <summary>infantryCombat.perception.axisTicks</summary>
    AxisTicks,
    /// <summary>infantryCombat.perception.twoFrontSteps</summary>
    TwoFrontSteps,
    /// <summary>infantryCombat.perception.twoFrontDanger</summary>
    TwoFrontDanger,
    /// <summary>infantryCombat.danger.rifle</summary>
    DangerRifle,
    /// <summary>infantryCombat.danger.carbine</summary>
    DangerCarbine,
    /// <summary>infantryCombat.danger.smg</summary>
    DangerSmg,
    /// <summary>infantryCombat.danger.pistol</summary>
    DangerPistol,
    /// <summary>infantryCombat.danger.lmg</summary>
    DangerLmg,
    /// <summary>infantryCombat.danger.lmgDeployed</summary>
    DangerLmgDeployed,
    /// <summary>infantryCombat.danger.marksman</summary>
    DangerMarksman,
    /// <summary>infantryCombat.danger.marksmanOthers</summary>
    DangerMarksmanOthers,
    /// <summary>infantryCombat.danger.launcher</summary>
    DangerLauncher,
    /// <summary>infantryCombat.danger.launcherReach</summary>
    LauncherReach,
    /// <summary>infantryCombat.danger.crew</summary>
    DangerCrew,
    /// <summary>infantryCombat.danger.crewReach</summary>
    CrewReach,
    /// <summary>infantryCombat.danger.unknown</summary>
    DangerUnknown,
    /// <summary>infantryCombat.danger.lightVehicle</summary>
    DangerLightVehicle,
    /// <summary>infantryCombat.danger.armour</summary>
    DangerArmour,
    /// <summary>infantryCombat.danger.helicopter</summary>
    DangerHelicopter,
    /// <summary>infantryCombat.danger.area</summary>
    DangerArea,
    /// <summary>infantryCombat.danger.areaMax</summary>
    DangerAreaMax,
    /// <summary>infantryCombat.danger.areaReach</summary>
    AreaReach,
    /// <summary>infantryCombat.exposure.stand</summary>
    HitStand,
    /// <summary>infantryCombat.exposure.crouch</summary>
    HitCrouch,
    /// <summary>infantryCombat.exposure.kneel</summary>
    HitKneel,
    /// <summary>infantryCombat.exposure.prone</summary>
    HitProne,
    /// <summary>infantryCombat.exposure.concealed</summary>
    ConcealFactor,
    /// <summary>infantryCombat.exposure.concealSeenTicks</summary>
    ConcealSeenTicks,
    /// <summary>infantryCombat.exposure.nearRange</summary>
    NearRangePercent,
    /// <summary>infantryCombat.exposure.atRange</summary>
    AtRangeFactor,
    /// <summary>infantryCombat.exposure.inReach</summary>
    InReachFactor,
    /// <summary>infantryCombat.exposure.lowStanding</summary>
    LowStanding,
    /// <summary>infantryCombat.cover.lowFiring</summary>
    LowFiring,
    /// <summary>infantryCombat.cover.lowDucked</summary>
    LowDucked,
    /// <summary>infantryCombat.cover.highFiring</summary>
    HighFiring,
    /// <summary>infantryCombat.cover.highDucked</summary>
    HighDucked,
    /// <summary>infantryCombat.cover.softFiring</summary>
    SoftFiring,
    /// <summary>infantryCombat.cover.softDucked</summary>
    SoftDucked,
    /// <summary>infantryCombat.cover.crestReach</summary>
    CrestReach,
    /// <summary>infantryCombat.cover.rebuildCells</summary>
    RebuildCells,
    /// <summary>infantryCombat.cover.rebuildMargin</summary>
    RebuildMargin,
    /// <summary>infantryCombat.cover.structureTicks</summary>
    StructureTicks,
    /// <summary>infantryCombat.cover.tallHalfMetres</summary>
    TallHalfMetres,
    /// <summary>infantryCombat.cover.concealDensity</summary>
    ConcealDensity,
    /// <summary>infantryCombat.posture.standCrouch</summary>
    StandCrouch,
    /// <summary>infantryCombat.posture.standKneel</summary>
    StandKneel,
    /// <summary>infantryCombat.posture.crouchKneel</summary>
    CrouchKneel,
    /// <summary>infantryCombat.posture.downProne</summary>
    DownProne,
    /// <summary>infantryCombat.posture.proneUp</summary>
    ProneUp,
    /// <summary>infantryCombat.posture.proneCrouch</summary>
    ProneCrouch,
    /// <summary>infantryCombat.posture.postureHoldTicks</summary>
    PostureHoldTicks,
    /// <summary>infantryCombat.posture.proneFireRangePercent</summary>
    ProneFireRangePercent,
    /// <summary>infantryCombat.posture.crouchSpeed</summary>
    CrouchSpeed,
    /// <summary>infantryCombat.posture.crawlSpeed</summary>
    CrawlSpeed,
    /// <summary>infantryCombat.posture.pinnedSpeed</summary>
    PinnedSpeed,
    /// <summary>infantryCombat.posture.aimStand</summary>
    AimStand,
    /// <summary>infantryCombat.posture.aimCrouch</summary>
    AimCrouch,
    /// <summary>infantryCombat.posture.aimKneel</summary>
    AimKneel,
    /// <summary>infantryCombat.posture.aimProne</summary>
    AimProne,
    /// <summary>infantryCombat.posture.pinnedCover</summary>
    PinnedCover,
    /// <summary>infantryCombat.posture.proneCover</summary>
    ProneCover,
    /// <summary>infantryCombat.suppression.hit</summary>
    SuppressHit,
    /// <summary>infantryCombat.suppression.mateKilled</summary>
    MateKilled,
    /// <summary>infantryCombat.suppression.mateReach</summary>
    MateReach,
    /// <summary>infantryCombat.suppression.blast</summary>
    SuppressBlast,
    /// <summary>infantryCombat.suppression.blastReachPercent</summary>
    BlastReachPercent,
    /// <summary>infantryCombat.suppression.hardCover</summary>
    SuppressHardCover,
    /// <summary>infantryCombat.suppression.softCover</summary>
    SuppressSoftCover,
    /// <summary>infantryCombat.suppression.prone</summary>
    SuppressProne,
    /// <summary>infantryCombat.suppression.holdTicks</summary>
    SuppressHoldTicks,
    /// <summary>infantryCombat.suppression.decay</summary>
    SuppressDecay,
    /// <summary>infantryCombat.suppression.shaken</summary>
    Shaken,
    /// <summary>infantryCombat.suppression.pinned</summary>
    Pinned,
    /// <summary>infantryCombat.suppression.pinnedHard</summary>
    PinnedHard,
    /// <summary>infantryCombat.suppression.aimShaken</summary>
    AimShaken,
    /// <summary>infantryCombat.suppression.aimPinned</summary>
    AimPinned,
    /// <summary>infantryCombat.suppression.medicBleedTicks</summary>
    MedicBleedTicks,
    /// <summary>infantryCombat.zones.impact</summary>
    ZoneImpact,
    /// <summary>infantryCombat.zones.impactReach</summary>
    ZoneImpactReach,
    /// <summary>infantryCombat.zones.impactOuter</summary>
    ZoneImpactOuter,
    /// <summary>infantryCombat.zones.impactOuterReach</summary>
    ZoneImpactOuterReach,
    /// <summary>infantryCombat.zones.impactTicks</summary>
    ZoneImpactTicks,
    /// <summary>infantryCombat.zones.minSplash</summary>
    ZoneMinSplash,
    /// <summary>infantryCombat.zones.fire</summary>
    ZoneFire,
    /// <summary>infantryCombat.zones.max</summary>
    ZoneMax,
    /// <summary>infantryCombat.think.soldierEvery</summary>
    SoldierEvery,
    /// <summary>infantryCombat.think.soldierCalmEvery</summary>
    SoldierCalmEvery,
    /// <summary>infantryCombat.think.squadEvery</summary>
    SquadEvery,
    /// <summary>infantryCombat.think.urgentThinks</summary>
    UrgentThinks,
}

/// <summary>The infantryCombat block's keys.</summary>
public static class CombatNames
{
    /// <summary>The content key of each <see cref="CombatValue"/>, in order.</summary>
    public static readonly string[] Names =
    [
        "perception.reactMin",
        "perception.reactMax",
        "perception.surpriseMin",
        "perception.surpriseMax",
        "perception.shareReact",
        "perception.laterContact",
        "perception.rankRegular",
        "perception.rankVeteran",
        "perception.rankElite",
        "perception.reactFloor",
        "perception.seenMemory",
        "perception.heardMemory",
        "perception.reportedMemory",
        "perception.squadShareTicks",
        "perception.nearShareTicks",
        "perception.allyShareTicks",
        "perception.shareRadius",
        "perception.contactsPerSoldier",
        "perception.contactsPerSquad",
        "perception.alertRange",
        "perception.reachPercent",
        "perception.contactRangePercent",
        "perception.shotAtMemoryTicks",
        "perception.heardPerThink",
        "perception.areaHalf",
        "perception.areaOffset",
        "perception.areaMerge",
        "perception.areaTicks",
        "perception.fireRevealTicks",
        "perception.fireRevealExtra",
        "perception.effectiveShots",
        "perception.effectiveTicks",
        "perception.effectiveHitTicks",
        "perception.nearShot",
        "perception.axisSteps",
        "perception.axisTicks",
        "perception.twoFrontSteps",
        "perception.twoFrontDanger",
        "danger.rifle",
        "danger.carbine",
        "danger.smg",
        "danger.pistol",
        "danger.lmg",
        "danger.lmgDeployed",
        "danger.marksman",
        "danger.marksmanOthers",
        "danger.launcher",
        "danger.launcherReach",
        "danger.crew",
        "danger.crewReach",
        "danger.unknown",
        "danger.lightVehicle",
        "danger.armour",
        "danger.helicopter",
        "danger.area",
        "danger.areaMax",
        "danger.areaReach",
        "exposure.stand",
        "exposure.crouch",
        "exposure.kneel",
        "exposure.prone",
        "exposure.concealed",
        "exposure.concealSeenTicks",
        "exposure.nearRange",
        "exposure.atRange",
        "exposure.inReach",
        "exposure.lowStanding",
        "cover.lowFiring",
        "cover.lowDucked",
        "cover.highFiring",
        "cover.highDucked",
        "cover.softFiring",
        "cover.softDucked",
        "cover.crestReach",
        "cover.rebuildCells",
        "cover.rebuildMargin",
        "cover.structureTicks",
        "cover.tallHalfMetres",
        "cover.concealDensity",
        "posture.standCrouch",
        "posture.standKneel",
        "posture.crouchKneel",
        "posture.downProne",
        "posture.proneUp",
        "posture.proneCrouch",
        "posture.postureHoldTicks",
        "posture.proneFireRangePercent",
        "posture.crouchSpeed",
        "posture.crawlSpeed",
        "posture.pinnedSpeed",
        "posture.aimStand",
        "posture.aimCrouch",
        "posture.aimKneel",
        "posture.aimProne",
        "posture.pinnedCover",
        "posture.proneCover",
        "suppression.hit",
        "suppression.mateKilled",
        "suppression.mateReach",
        "suppression.blast",
        "suppression.blastReachPercent",
        "suppression.hardCover",
        "suppression.softCover",
        "suppression.prone",
        "suppression.holdTicks",
        "suppression.decay",
        "suppression.shaken",
        "suppression.pinned",
        "suppression.pinnedHard",
        "suppression.aimShaken",
        "suppression.aimPinned",
        "suppression.medicBleedTicks",
        "zones.impact",
        "zones.impactReach",
        "zones.impactOuter",
        "zones.impactOuterReach",
        "zones.impactTicks",
        "zones.minSplash",
        "zones.fire",
        "zones.max",
        "think.soldierEvery",
        "think.soldierCalmEvery",
        "think.squadEvery",
        "think.urgentThinks",
    ];

    /// <summary>The groups of the block, in order.</summary>
    public static readonly string[] Groups = ["perception", "danger", "exposure", "cover", "posture", "suppression", "zones", "think"];
}

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
    /// <summary>infantryCombat.gaits.walk</summary>
    GaitWalk,
    /// <summary>infantryCombat.gaits.strafe</summary>
    GaitStrafe,
    /// <summary>infantryCombat.gaits.backpedal</summary>
    GaitBackpedal,
    /// <summary>infantryCombat.gaits.crouchGait</summary>
    GaitCrouchGait,
    /// <summary>infantryCombat.gaits.crouchRun</summary>
    GaitCrouchRun,
    /// <summary>infantryCombat.gaits.run</summary>
    GaitRun,
    /// <summary>infantryCombat.gaits.sprint</summary>
    GaitSprint,
    /// <summary>infantryCombat.gaits.walkAim</summary>
    GaitWalkAim,
    /// <summary>infantryCombat.gaits.strafeAim</summary>
    GaitStrafeAim,
    /// <summary>infantryCombat.gaits.backAim</summary>
    GaitBackAim,
    /// <summary>infantryCombat.gaits.crouchAimPlus</summary>
    GaitCrouchAimPlus,
    /// <summary>infantryCombat.gaits.crouchHit</summary>
    GaitCrouchHit,
    /// <summary>infantryCombat.gaits.runHit</summary>
    GaitRunHit,
    /// <summary>infantryCombat.gaits.crawlHit</summary>
    GaitCrawlHit,
    /// <summary>infantryCombat.gaits.sprintTicks</summary>
    GaitSprintTicks,
    /// <summary>infantryCombat.gaits.sprintRest</summary>
    GaitSprintRest,
    /// <summary>infantryCombat.gaits.lmgSpeed</summary>
    GaitLmgSpeed,
    /// <summary>infantryCombat.gaits.lmgHipAim</summary>
    GaitLmgHipAim,
    /// <summary>infantryCombat.gaits.reloadMove</summary>
    GaitReloadMove,
    /// <summary>infantryCombat.facing.advanceSteps</summary>
    FaceAdvanceSteps,
    /// <summary>infantryCombat.facing.backSteps</summary>
    FaceBackSteps,
    /// <summary>infantryCombat.facing.runSteps</summary>
    FaceRunSteps,
    /// <summary>infantryCombat.facing.bigTurn</summary>
    FaceBigTurn,
    /// <summary>infantryCombat.facing.bigTurnTicks</summary>
    FaceBigTurnTicks,
    /// <summary>infantryCombat.facing.faceDanger</summary>
    FaceFaceDanger,
    /// <summary>infantryCombat.facing.outOfReachPercent</summary>
    FaceOutOfReachPercent,
    /// <summary>infantryCombat.facing.unspottedTicks</summary>
    FaceUnspottedTicks,
    /// <summary>infantryCombat.position.gainNeeded</summary>
    PosGainNeeded,
    /// <summary>infantryCombat.position.dashFire</summary>
    PosDashFire,
    /// <summary>infantryCombat.position.dashContact</summary>
    PosDashContact,
    /// <summary>infantryCombat.position.dashCalm</summary>
    PosDashCalm,
    /// <summary>infantryCombat.position.hFight</summary>
    PosHFight,
    /// <summary>infantryCombat.position.hAdvance</summary>
    PosHAdvance,
    /// <summary>infantryCombat.position.hWithdraw</summary>
    PosHWithdraw,
    /// <summary>infantryCombat.position.hLoose</summary>
    PosHLoose,
    /// <summary>infantryCombat.position.hAlert</summary>
    PosHAlert,
    /// <summary>infantryCombat.position.hFlank</summary>
    PosHFlank,
    /// <summary>infantryCombat.position.shakenH</summary>
    PosShakenH,
    /// <summary>infantryCombat.position.woundedH</summary>
    PosWoundedH,
    /// <summary>infantryCombat.position.radiusFire</summary>
    PosRadiusFire,
    /// <summary>infantryCombat.position.radiusContact</summary>
    PosRadiusContact,
    /// <summary>infantryCombat.position.radiusCalm</summary>
    PosRadiusCalm,
    /// <summary>infantryCombat.position.maxCandidates</summary>
    PosMaxCandidates,
    /// <summary>infantryCombat.position.floodCells</summary>
    PosFloodCells,
    /// <summary>infantryCombat.position.coverSteps</summary>
    PosCoverSteps,
    /// <summary>infantryCombat.position.minSpacing</summary>
    PosMinSpacing,
    /// <summary>infantryCombat.position.coverSpacing</summary>
    PosCoverSpacing,
    /// <summary>infantryCombat.position.fireLineClear</summary>
    PosFireLineClear,
    /// <summary>infantryCombat.position.noFireLine</summary>
    PosNoFireLine,
    /// <summary>infantryCombat.position.noFireLineHeavy</summary>
    PosNoFireLineHeavy,
    /// <summary>infantryCombat.position.bandTerm</summary>
    PosBandTerm,
    /// <summary>infantryCombat.position.spreadTerm</summary>
    PosSpreadTerm,
    /// <summary>infantryCombat.position.spreadReach</summary>
    PosSpreadReach,
    /// <summary>infantryCombat.position.searchTicks</summary>
    PosSearchTicks,
    /// <summary>infantryCombat.position.goodTicks</summary>
    PosGoodTicks,
    /// <summary>infantryCombat.position.risePercent</summary>
    PosRisePercent,
    /// <summary>infantryCombat.position.stayTicks</summary>
    PosStayTicks,
    /// <summary>infantryCombat.position.doublePercent</summary>
    PosDoublePercent,
    /// <summary>infantryCombat.position.unreachableTicks</summary>
    PosUnreachableTicks,
    /// <summary>infantryCombat.position.pathSample</summary>
    PosPathSample,
    /// <summary>infantryCombat.position.searchesPerTick</summary>
    PosSearchesPerTick,
    /// <summary>infantryCombat.position.enterTicks</summary>
    PosEnterTicks,
    /// <summary>infantryCombat.position.insideDucked</summary>
    PosInsideDucked,
    /// <summary>infantryCombat.position.insideFiring</summary>
    PosInsideFiring,
    /// <summary>infantryCombat.position.lmgLow</summary>
    PosLmgLow,
    /// <summary>infantryCombat.position.lmgHigh</summary>
    PosLmgHigh,
    /// <summary>infantryCombat.position.sniperConceal</summary>
    PosSniperConceal,
    /// <summary>infantryCombat.targeting.downThreat</summary>
    TargetDownThreat,
    /// <summary>infantryCombat.targeting.sectorIn</summary>
    TargetSectorIn,
    /// <summary>infantryCombat.targeting.sectorNext</summary>
    TargetSectorNext,
    /// <summary>infantryCombat.targeting.sectorOut</summary>
    TargetSectorOut,
    /// <summary>infantryCombat.targeting.focus</summary>
    TargetFocus,
    /// <summary>infantryCombat.targeting.stick</summary>
    TargetStick,
    /// <summary>infantryCombat.targeting.overkill</summary>
    TargetOverkill,
    /// <summary>infantryCombat.targeting.shareMin</summary>
    TargetShareMin,
    /// <summary>infantryCombat.targeting.minScore</summary>
    TargetMinScore,
    /// <summary>infantryCombat.targeting.retargetMin</summary>
    TargetRetargetMin,
    /// <summary>infantryCombat.targeting.retargetMax</summary>
    TargetRetargetMax,
    /// <summary>infantryCombat.targeting.heavyThreat</summary>
    TargetHeavyThreat,
    /// <summary>infantryCombat.targeting.focusLostTicks</summary>
    TargetFocusLostTicks,
    /// <summary>infantryCombat.targeting.marksmanTicks</summary>
    TargetMarksmanTicks,
    /// <summary>infantryCombat.targeting.burstMin</summary>
    TargetBurstMin,
    /// <summary>infantryCombat.targeting.burstMax</summary>
    TargetBurstMax,
    /// <summary>infantryCombat.targeting.burstSmg</summary>
    TargetBurstSmg,
    /// <summary>infantryCombat.targeting.burstLmgMin</summary>
    TargetBurstLmgMin,
    /// <summary>infantryCombat.targeting.burstLmgMax</summary>
    TargetBurstLmgMax,
    /// <summary>infantryCombat.targeting.burstPistolMin</summary>
    TargetBurstPistolMin,
    /// <summary>infantryCombat.targeting.burstPistolMax</summary>
    TargetBurstPistolMax,
    /// <summary>infantryCombat.targeting.waitMin</summary>
    TargetWaitMin,
    /// <summary>infantryCombat.targeting.waitMax</summary>
    TargetWaitMax,
    /// <summary>infantryCombat.targeting.moveWaitMin</summary>
    TargetMoveWaitMin,
    /// <summary>infantryCombat.targeting.moveWaitMax</summary>
    TargetMoveWaitMax,
    /// <summary>infantryCombat.targeting.reloadShare</summary>
    TargetReloadShare,
    /// <summary>infantryCombat.targeting.keepFiring</summary>
    TargetKeepFiring,
    /// <summary>infantryCombat.targeting.tacticalReload</summary>
    TargetTacticalReload,
    /// <summary>infantryCombat.targeting.noTargetTicks</summary>
    TargetNoTargetTicks,
    /// <summary>infantryCombat.targeting.openFireChance</summary>
    TargetOpenFireChance,
    /// <summary>infantryCombat.grenades.minRange</summary>
    GrenadeMinRange,
    /// <summary>infantryCombat.grenades.maxRange</summary>
    GrenadeMaxRange,
    /// <summary>infantryCombat.grenades.proneRange</summary>
    GrenadeProneRange,
    /// <summary>infantryCombat.grenades.seenTicks</summary>
    GrenadeSeenTicks,
    /// <summary>infantryCombat.grenades.hitBelow</summary>
    GrenadeHitBelow,
    /// <summary>infantryCombat.grenades.friendClear</summary>
    GrenadeFriendClear,
    /// <summary>infantryCombat.grenades.squadCooldown</summary>
    GrenadeSquadCooldown,
    /// <summary>infantryCombat.grenades.throwTicks</summary>
    GrenadeThrowTicks,
    /// <summary>infantryCombat.grenades.flightTicks</summary>
    GrenadeFlightTicks,
    /// <summary>infantryCombat.grenades.fuseTicks</summary>
    GrenadeFuseTicks,
    /// <summary>infantryCombat.grenades.scatter</summary>
    GrenadeScatter,
    /// <summary>infantryCombat.grenades.scatterRecruit</summary>
    GrenadeScatterRecruit,
    /// <summary>infantryCombat.grenades.scatterElite</summary>
    GrenadeScatterElite,
    /// <summary>infantryCombat.grenades.damage</summary>
    GrenadeDamage,
    /// <summary>infantryCombat.grenades.radius</summary>
    GrenadeRadius,
    /// <summary>infantryCombat.grenades.zone</summary>
    GrenadeZone,
    /// <summary>infantryCombat.grenades.zoneRadius</summary>
    GrenadeZoneRadius,
    /// <summary>infantryCombat.grenades.reflexMin</summary>
    GrenadeReflexMin,
    /// <summary>infantryCombat.grenades.reflexMax</summary>
    GrenadeReflexMax,
    /// <summary>infantryCombat.grenades.escape</summary>
    GrenadeEscape,
    /// <summary>infantryCombat.grenades.dash</summary>
    GrenadeDash,
    /// <summary>infantryCombat.grenades.proneDamage</summary>
    GrenadeProneDamage,
    /// <summary>infantryCombat.grenades.hearReach</summary>
    GrenadeHearReach,
    /// <summary>infantryCombat.grenades.lmgCarry</summary>
    GrenadeLmgCarry,
    /// <summary>infantryCombat.vehicleCover.speedPercent</summary>
    VehicleCoverSpeedPercent,
    /// <summary>infantryCombat.vehicleCover.side</summary>
    VehicleCoverSide,
    /// <summary>infantryCombat.vehicleCover.scan</summary>
    VehicleCoverScan,
    /// <summary>infantryCombat.vehicleCover.followReach</summary>
    VehicleCoverFollowReach,
    /// <summary>infantryCombat.vehicleCover.goalReach</summary>
    VehicleCoverGoalReach,
    /// <summary>infantryCombat.vehicleCover.slotsHigh</summary>
    VehicleCoverSlotsHigh,
    /// <summary>infantryCombat.vehicleCover.slotsLow</summary>
    VehicleCoverSlotsLow,
    /// <summary>infantryCombat.vehicleCover.slotOffset</summary>
    VehicleCoverSlotOffset,
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
        "gaits.walk",
        "gaits.strafe",
        "gaits.backpedal",
        "gaits.crouchGait",
        "gaits.crouchRun",
        "gaits.run",
        "gaits.sprint",
        "gaits.walkAim",
        "gaits.strafeAim",
        "gaits.backAim",
        "gaits.crouchAimPlus",
        "gaits.crouchHit",
        "gaits.runHit",
        "gaits.crawlHit",
        "gaits.sprintTicks",
        "gaits.sprintRest",
        "gaits.lmgSpeed",
        "gaits.lmgHipAim",
        "gaits.reloadMove",
        "facing.advanceSteps",
        "facing.backSteps",
        "facing.runSteps",
        "facing.bigTurn",
        "facing.bigTurnTicks",
        "facing.faceDanger",
        "facing.outOfReachPercent",
        "facing.unspottedTicks",
        "position.gainNeeded",
        "position.dashFire",
        "position.dashContact",
        "position.dashCalm",
        "position.hFight",
        "position.hAdvance",
        "position.hWithdraw",
        "position.hLoose",
        "position.hAlert",
        "position.hFlank",
        "position.shakenH",
        "position.woundedH",
        "position.radiusFire",
        "position.radiusContact",
        "position.radiusCalm",
        "position.maxCandidates",
        "position.floodCells",
        "position.coverSteps",
        "position.minSpacing",
        "position.coverSpacing",
        "position.fireLineClear",
        "position.noFireLine",
        "position.noFireLineHeavy",
        "position.bandTerm",
        "position.spreadTerm",
        "position.spreadReach",
        "position.searchTicks",
        "position.goodTicks",
        "position.risePercent",
        "position.stayTicks",
        "position.doublePercent",
        "position.unreachableTicks",
        "position.pathSample",
        "position.searchesPerTick",
        "position.enterTicks",
        "position.insideDucked",
        "position.insideFiring",
        "position.lmgLow",
        "position.lmgHigh",
        "position.sniperConceal",
        "targeting.downThreat",
        "targeting.sectorIn",
        "targeting.sectorNext",
        "targeting.sectorOut",
        "targeting.focus",
        "targeting.stick",
        "targeting.overkill",
        "targeting.shareMin",
        "targeting.minScore",
        "targeting.retargetMin",
        "targeting.retargetMax",
        "targeting.heavyThreat",
        "targeting.focusLostTicks",
        "targeting.marksmanTicks",
        "targeting.burstMin",
        "targeting.burstMax",
        "targeting.burstSmg",
        "targeting.burstLmgMin",
        "targeting.burstLmgMax",
        "targeting.burstPistolMin",
        "targeting.burstPistolMax",
        "targeting.waitMin",
        "targeting.waitMax",
        "targeting.moveWaitMin",
        "targeting.moveWaitMax",
        "targeting.reloadShare",
        "targeting.keepFiring",
        "targeting.tacticalReload",
        "targeting.noTargetTicks",
        "targeting.openFireChance",
        "grenades.minRange",
        "grenades.maxRange",
        "grenades.proneRange",
        "grenades.seenTicks",
        "grenades.hitBelow",
        "grenades.friendClear",
        "grenades.squadCooldown",
        "grenades.throwTicks",
        "grenades.flightTicks",
        "grenades.fuseTicks",
        "grenades.scatter",
        "grenades.scatterRecruit",
        "grenades.scatterElite",
        "grenades.damage",
        "grenades.radius",
        "grenades.zone",
        "grenades.zoneRadius",
        "grenades.reflexMin",
        "grenades.reflexMax",
        "grenades.escape",
        "grenades.dash",
        "grenades.proneDamage",
        "grenades.hearReach",
        "grenades.lmgCarry",
        "vehicleCover.speedPercent",
        "vehicleCover.side",
        "vehicleCover.scan",
        "vehicleCover.followReach",
        "vehicleCover.goalReach",
        "vehicleCover.slotsHigh",
        "vehicleCover.slotsLow",
        "vehicleCover.slotOffset",
    ];

    /// <summary>The groups of the block, in order.</summary>
    public static readonly string[] Groups = ["perception", "danger", "exposure", "cover", "posture", "suppression", "zones", "think", "gaits", "facing", "position", "targeting", "grenades", "vehicleCover"];
}

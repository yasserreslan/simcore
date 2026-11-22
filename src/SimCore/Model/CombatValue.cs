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
    /// <summary>infantryCombat.squad.urgentThinks</summary>
    SqUrgentThinks,
    /// <summary>infantryCombat.squad.calmTicks</summary>
    SqCalmTicks,
    /// <summary>infantryCombat.squad.groupRadius</summary>
    SqGroupRadius,
    /// <summary>infantryCombat.squad.mainKeep</summary>
    SqMainKeep,
    /// <summary>infantryCombat.squad.rifle</summary>
    SqRifle,
    /// <summary>infantryCombat.squad.rifleOut</summary>
    SqRifleOut,
    /// <summary>infantryCombat.squad.carbine</summary>
    SqCarbine,
    /// <summary>infantryCombat.squad.carbineOut</summary>
    SqCarbineOut,
    /// <summary>infantryCombat.squad.smg</summary>
    SqSmg,
    /// <summary>infantryCombat.squad.smgOut</summary>
    SqSmgOut,
    /// <summary>infantryCombat.squad.lmg</summary>
    SqLmg,
    /// <summary>infantryCombat.squad.lmgDeployed</summary>
    SqLmgDeployed,
    /// <summary>infantryCombat.squad.lmgOut</summary>
    SqLmgOut,
    /// <summary>infantryCombat.squad.marksman</summary>
    SqMarksman,
    /// <summary>infantryCombat.squad.marksmanOut</summary>
    SqMarksmanOut,
    /// <summary>infantryCombat.squad.launcher</summary>
    SqLauncher,
    /// <summary>infantryCombat.squad.launcherVehicle</summary>
    SqLauncherVehicle,
    /// <summary>infantryCombat.squad.pistol</summary>
    SqPistol,
    /// <summary>infantryCombat.squad.kornet</summary>
    SqKornet,
    /// <summary>infantryCombat.squad.kornetVehicle</summary>
    SqKornetVehicle,
    /// <summary>infantryCombat.squad.vehicleReach</summary>
    SqVehicleReach,
    /// <summary>infantryCombat.squad.wounded</summary>
    SqWounded,
    /// <summary>infantryCombat.squad.suppressed</summary>
    SqSuppressed,
    /// <summary>infantryCombat.squad.pinnedHard</summary>
    SqPinnedHard,
    /// <summary>infantryCombat.squad.lowAmmo</summary>
    SqLowAmmo,
    /// <summary>infantryCombat.squad.empty</summary>
    SqEmpty,
    /// <summary>infantryCombat.squad.rankRegular</summary>
    SqRankRegular,
    /// <summary>infantryCombat.squad.rankVeteran</summary>
    SqRankVeteran,
    /// <summary>infantryCombat.squad.rankElite</summary>
    SqRankElite,
    /// <summary>infantryCombat.squad.seenBefore</summary>
    SqSeenBefore,
    /// <summary>infantryCombat.squad.heard</summary>
    SqHeard,
    /// <summary>infantryCombat.squad.area</summary>
    SqArea,
    /// <summary>infantryCombat.squad.ratioMin</summary>
    SqRatioMin,
    /// <summary>infantryCombat.squad.ratioMax</summary>
    SqRatioMax,
    /// <summary>infantryCombat.squad.ratioStep</summary>
    SqRatioStep,
    /// <summary>infantryCombat.squad.choiceHoldTicks</summary>
    SqChoiceHoldTicks,
    /// <summary>infantryCombat.squad.choiceHysteresis</summary>
    SqChoiceHysteresis,
    /// <summary>infantryCombat.squad.advanceToPercent</summary>
    SqAdvanceToPercent,
    /// <summary>infantryCombat.squad.advanceR</summary>
    SqAdvanceR,
    /// <summary>infantryCombat.squad.idleAdvanceR</summary>
    SqIdleAdvanceR,
    /// <summary>infantryCombat.squad.guardAdvanceR</summary>
    SqGuardAdvanceR,
    /// <summary>infantryCombat.squad.autoWithdrawBelow</summary>
    SqAutoWithdrawBelow,
    /// <summary>infantryCombat.squad.postLeash</summary>
    SqPostLeash,
    /// <summary>infantryCombat.squad.contactSpacing</summary>
    SqContactSpacing,
    /// <summary>infantryCombat.squad.minSpacing</summary>
    SqMinSpacing,
    /// <summary>infantryCombat.squad.manyOnLine</summary>
    SqManyOnLine,
    /// <summary>infantryCombat.squad.boxAlong</summary>
    SqBoxAlong,
    /// <summary>infantryCombat.squad.boxBehind</summary>
    SqBoxBehind,
    /// <summary>infantryCombat.squad.boxAhead</summary>
    SqBoxAhead,
    /// <summary>infantryCombat.squad.rebuildMoved</summary>
    SqRebuildMoved,
    /// <summary>infantryCombat.squad.urgentSteps</summary>
    SqUrgentSteps,
    /// <summary>infantryCombat.squad.medicBehind</summary>
    SqMedicBehind,
    /// <summary>infantryCombat.squad.flankBehind</summary>
    SqFlankBehind,
    /// <summary>infantryCombat.squad.supportBehind</summary>
    SqSupportBehind,
    /// <summary>infantryCombat.squad.airGuardBehind</summary>
    SqAirGuardBehind,
    /// <summary>infantryCombat.squad.readyTicks</summary>
    SqReadyTicks,
    /// <summary>infantryCombat.squad.boundMin</summary>
    SqBoundMin,
    /// <summary>infantryCombat.squad.boundMax</summary>
    SqBoundMax,
    /// <summary>infantryCombat.squad.leapMin</summary>
    SqLeapMin,
    /// <summary>infantryCombat.squad.leapMax</summary>
    SqLeapMax,
    /// <summary>infantryCombat.squad.leapR</summary>
    SqLeapR,
    /// <summary>infantryCombat.squad.boundAcross</summary>
    SqBoundAcross,
    /// <summary>infantryCombat.squad.boundMaxTicks</summary>
    SqBoundMaxTicks,
    /// <summary>infantryCombat.squad.marchR</summary>
    SqMarchR,
    /// <summary>infantryCombat.squad.marchQuietTicks</summary>
    SqMarchQuietTicks,
    /// <summary>infantryCombat.squad.marchStep</summary>
    SqMarchStep,
    /// <summary>infantryCombat.squad.suppressHitBelow</summary>
    SqSuppressHitBelow,
    /// <summary>infantryCombat.squad.suppressAmmoPercent</summary>
    SqSuppressAmmoPercent,
    /// <summary>infantryCombat.squad.lmgSuppressAmmo</summary>
    SqLmgSuppressAmmo,
    /// <summary>infantryCombat.squad.suppressSpread</summary>
    SqSuppressSpread,
    /// <summary>infantryCombat.squad.suppressHitPercent</summary>
    SqSuppressHitPercent,
    /// <summary>infantryCombat.squad.suppressHitNear</summary>
    SqSuppressHitNear,
    /// <summary>infantryCombat.squad.suppressLiftTicks</summary>
    SqSuppressLiftTicks,
    /// <summary>infantryCombat.squad.suppressAimClear</summary>
    SqSuppressAimClear,
    /// <summary>infantryCombat.squad.lmgWait</summary>
    SqLmgWait,
    /// <summary>infantryCombat.squad.lmgDeployTicks</summary>
    SqLmgDeployTicks,
    /// <summary>infantryCombat.squad.lmgStopTicks</summary>
    SqLmgStopTicks,
    /// <summary>infantryCombat.squad.lmgArcSteps</summary>
    SqLmgArcSteps,
    /// <summary>infantryCombat.squad.lmgOutTicks</summary>
    SqLmgOutTicks,
    /// <summary>infantryCombat.squad.lmgRedeployTicks</summary>
    SqLmgRedeployTicks,
    /// <summary>infantryCombat.squad.lmgDeployedSuppress</summary>
    SqLmgDeployedSuppress,
    /// <summary>infantryCombat.squad.flankFipTicks</summary>
    SqFlankFipTicks,
    /// <summary>infantryCombat.squad.flankWindowTicks</summary>
    SqFlankWindowTicks,
    /// <summary>infantryCombat.squad.flankHitsBelow</summary>
    SqFlankHitsBelow,
    /// <summary>infantryCombat.squad.flankCoverAvg</summary>
    SqFlankCoverAvg,
    /// <summary>infantryCombat.squad.flankMinAble</summary>
    SqFlankMinAble,
    /// <summary>infantryCombat.squad.flankMinR</summary>
    SqFlankMinR,
    /// <summary>infantryCombat.squad.autoFlank</summary>
    SqAutoFlank,
    /// <summary>infantryCombat.squad.flankBigSquad</summary>
    SqFlankBigSquad,
    /// <summary>infantryCombat.squad.flankDistancePercent</summary>
    SqFlankDistancePercent,
    /// <summary>infantryCombat.squad.flankMaxPath</summary>
    SqFlankMaxPath,
    /// <summary>infantryCombat.squad.flankExposed</summary>
    SqFlankExposed,
    /// <summary>infantryCombat.squad.flankMaxExposed</summary>
    SqFlankMaxExposed,
    /// <summary>infantryCombat.squad.flankSuppressTicks</summary>
    SqFlankSuppressTicks,
    /// <summary>infantryCombat.squad.flankCloseFire</summary>
    SqFlankCloseFire,
    /// <summary>infantryCombat.squad.flankMoved</summary>
    SqFlankMoved,
    /// <summary>infantryCombat.squad.flankRetryTicks</summary>
    SqFlankRetryTicks,
    /// <summary>infantryCombat.squad.flankRejectTicks</summary>
    SqFlankRejectTicks,
    /// <summary>infantryCombat.squad.flankMaxTicks</summary>
    SqFlankMaxTicks,
    /// <summary>infantryCombat.squad.assaultRange</summary>
    SqAssaultRange,
    /// <summary>infantryCombat.squad.assaultR</summary>
    SqAssaultR,
    /// <summary>infantryCombat.squad.assaultMinAble</summary>
    SqAssaultMinAble,
    /// <summary>infantryCombat.squad.assaultShare</summary>
    SqAssaultShare,
    /// <summary>infantryCombat.squad.assaultGrenades</summary>
    SqAssaultGrenades,
    /// <summary>infantryCombat.squad.assaultGrenadeWait</summary>
    SqAssaultGrenadeWait,
    /// <summary>infantryCombat.squad.assaultShiftRange</summary>
    SqAssaultShiftRange,
    /// <summary>infantryCombat.squad.assaultEndRange</summary>
    SqAssaultEndRange,
    /// <summary>infantryCombat.squad.assaultFailR</summary>
    SqAssaultFailR,
    /// <summary>infantryCombat.squad.assaultMaxTicks</summary>
    SqAssaultMaxTicks,
    /// <summary>infantryCombat.squad.assaultRetryTicks</summary>
    SqAssaultRetryTicks,
    /// <summary>infantryCombat.gaits.riseMove</summary>
    GaitRiseMove,
    /// <summary>infantryCombat.squad.withdrawStepMin</summary>
    SqWithdrawStepMin,
    /// <summary>infantryCombat.squad.withdrawStepMax</summary>
    SqWithdrawStepMax,
    /// <summary>infantryCombat.squad.withdrawAcross</summary>
    SqWithdrawAcross,
    /// <summary>infantryCombat.squad.withdrawStepTicks</summary>
    SqWithdrawStepTicks,
    /// <summary>infantryCombat.squad.withdrawReach</summary>
    SqWithdrawReach,
    /// <summary>infantryCombat.squad.withdrawWidth</summary>
    SqWithdrawWidth,
    /// <summary>infantryCombat.squad.withdrawGainPercent</summary>
    SqWithdrawGainPercent,
    /// <summary>infantryCombat.squad.withdrawRefusals</summary>
    SqWithdrawRefusals,
    /// <summary>infantryCombat.squad.withdrawRetryTicks</summary>
    SqWithdrawRetryTicks,
    /// <summary>infantryCombat.squad.consolidateTicks</summary>
    SqConsolidateTicks,
    /// <summary>infantryCombat.squad.consolidateOrderTicks</summary>
    SqConsolidateOrderTicks,
    /// <summary>infantryCombat.squad.withdrawRescueWaitTicks</summary>
    SqWithdrawRescueWaitTicks,
    /// <summary>infantryCombat.squad.moveBehindSteps</summary>
    SqMoveBehindSteps,
    /// <summary>infantryCombat.squad.longFlankBehind</summary>
    SqLongFlankBehind,
    /// <summary>infantryCombat.morale.start</summary>
    MoStart,
    /// <summary>infantryCombat.morale.killed</summary>
    MoKilled,
    /// <summary>infantryCombat.morale.down</summary>
    MoDown,
    /// <summary>infantryCombat.morale.wounded</summary>
    MoWounded,
    /// <summary>infantryCombat.morale.leader</summary>
    MoLeader,
    /// <summary>infantryCombat.morale.leaderPauseTicks</summary>
    MoLeaderPauseTicks,
    /// <summary>infantryCombat.morale.pinnedTicks</summary>
    MoPinnedTicks,
    /// <summary>infantryCombat.morale.pinnedRate</summary>
    MoPinnedRate,
    /// <summary>infantryCombat.morale.flanked</summary>
    MoFlanked,
    /// <summary>infantryCombat.morale.flankedTicks</summary>
    MoFlankedTicks,
    /// <summary>infantryCombat.morale.vehicleReach</summary>
    MoVehicleReach,
    /// <summary>infantryCombat.morale.vehicleRate</summary>
    MoVehicleRate,
    /// <summary>infantryCombat.morale.enemyKilled</summary>
    MoEnemyKilled,
    /// <summary>infantryCombat.morale.quietTicks</summary>
    MoQuietTicks,
    /// <summary>infantryCombat.morale.quietRate</summary>
    MoQuietRate,
    /// <summary>infantryCombat.morale.aidReach</summary>
    MoAidReach,
    /// <summary>infantryCombat.morale.aidRate</summary>
    MoAidRate,
    /// <summary>infantryCombat.morale.garrisonRate</summary>
    MoGarrisonRate,
    /// <summary>infantryCombat.morale.steadyAt</summary>
    MoSteadyAt,
    /// <summary>infantryCombat.morale.brokenBelow</summary>
    MoBrokenBelow,
    /// <summary>infantryCombat.morale.rally</summary>
    MoRally,
    /// <summary>infantryCombat.morale.rallyLed</summary>
    MoRallyLed,
    /// <summary>infantryCombat.morale.routBelow</summary>
    MoRoutBelow,
    /// <summary>infantryCombat.morale.routEnd</summary>
    MoRoutEnd,
    /// <summary>infantryCombat.morale.shakenAim</summary>
    MoShakenAim,
    /// <summary>infantryCombat.morale.brokenAim</summary>
    MoBrokenAim,
    /// <summary>infantryCombat.morale.shakenSuppress</summary>
    MoShakenSuppress,
    /// <summary>infantryCombat.morale.brokenSuppress</summary>
    MoBrokenSuppress,
    /// <summary>infantryCombat.morale.shakenReact</summary>
    MoShakenReact,
    /// <summary>infantryCombat.morale.brokenReact</summary>
    MoBrokenReact,
    /// <summary>infantryCombat.morale.shakenH</summary>
    MoShakenH,
    /// <summary>infantryCombat.morale.shakenForce</summary>
    MoShakenForce,
    /// <summary>infantryCombat.morale.brokenForce</summary>
    MoBrokenForce,
    /// <summary>infantryCombat.morale.brokenFireRange</summary>
    MoBrokenFireRange,
    /// <summary>infantryCombat.morale.firedOnTicks</summary>
    MoFiredOnTicks,
    /// <summary>infantryCombat.morale.safeReach</summary>
    MoSafeReach,
    /// <summary>infantryCombat.morale.lossSpan</summary>
    MoLossSpan,
    /// <summary>infantryCombat.calls.uContact</summary>
    CaUContact,
    /// <summary>infantryCombat.calls.uRatio</summary>
    CaURatio,
    /// <summary>infantryCombat.calls.uRatioLow</summary>
    CaURatioLow,
    /// <summary>infantryCombat.calls.uPinned</summary>
    CaUPinned,
    /// <summary>infantryCombat.calls.uLoss</summary>
    CaULoss,
    /// <summary>infantryCombat.calls.uLossMax</summary>
    CaULossMax,
    /// <summary>infantryCombat.calls.lossTicks</summary>
    CaLossTicks,
    /// <summary>infantryCombat.calls.uArmour</summary>
    CaUArmour,
    /// <summary>infantryCombat.calls.uFlankRefused</summary>
    CaUFlankRefused,
    /// <summary>infantryCombat.calls.uGarrison</summary>
    CaUGarrison,
    /// <summary>infantryCombat.calls.withdrawTicks</summary>
    CaWithdrawTicks,
    /// <summary>infantryCombat.calls.matchEvery</summary>
    CaMatchEvery,
    /// <summary>infantryCombat.calls.needPercent</summary>
    CaNeedPercent,
    /// <summary>infantryCombat.calls.callRadius</summary>
    CaCallRadius,
    /// <summary>infantryCombat.calls.maxHelpers</summary>
    CaMaxHelpers,
    /// <summary>infantryCombat.calls.releaseTicks</summary>
    CaReleaseTicks,
    /// <summary>infantryCombat.calls.supportNear</summary>
    CaSupportNear,
    /// <summary>infantryCombat.calls.otherFight</summary>
    CaOtherFight,
    /// <summary>infantryCombat.calls.minAmmo</summary>
    CaMinAmmo,
    /// <summary>infantryCombat.calls.minAble</summary>
    CaMinAble,
    /// <summary>infantryCombat.calls.supportSteps</summary>
    CaSupportSteps,
    /// <summary>infantryCombat.calls.approachPercent</summary>
    CaApproachPercent,
    /// <summary>infantryCombat.calls.helpArrived</summary>
    CaHelpArrived,
    /// <summary>infantryCombat.calls.arrivedReach</summary>
    CaArrivedReach,
    /// <summary>infantryCombat.calls.standDownTicks</summary>
    CaStandDownTicks,
    /// <summary>infantryCombat.calls.autoSupport</summary>
    CaAutoSupport,
    /// <summary>infantryCombat.calls.lineSideMin</summary>
    CaLineSideMin,
    /// <summary>infantryCombat.calls.lineSideMax</summary>
    CaLineSideMax,
    /// <summary>infantryCombat.reactions.impactNear</summary>
    ReImpactNear,
    /// <summary>infantryCombat.reactions.fireSpacing</summary>
    ReFireSpacing,
    /// <summary>infantryCombat.reactions.airNear</summary>
    ReAirNear,
    /// <summary>infantryCombat.reactions.airTicks</summary>
    ReAirTicks,
    /// <summary>infantryCombat.reactions.cornerReach</summary>
    ReCornerReach,
    /// <summary>infantryCombat.reactions.cornerPinned</summary>
    ReCornerPinned,
    /// <summary>infantryCombat.reactions.looseCorner</summary>
    ReLooseCorner,
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
        "squad.urgentThinks",
        "squad.calmTicks",
        "squad.groupRadius",
        "squad.mainKeep",
        "squad.rifle",
        "squad.rifleOut",
        "squad.carbine",
        "squad.carbineOut",
        "squad.smg",
        "squad.smgOut",
        "squad.lmg",
        "squad.lmgDeployed",
        "squad.lmgOut",
        "squad.marksman",
        "squad.marksmanOut",
        "squad.launcher",
        "squad.launcherVehicle",
        "squad.pistol",
        "squad.kornet",
        "squad.kornetVehicle",
        "squad.vehicleReach",
        "squad.wounded",
        "squad.suppressed",
        "squad.pinnedHard",
        "squad.lowAmmo",
        "squad.empty",
        "squad.rankRegular",
        "squad.rankVeteran",
        "squad.rankElite",
        "squad.seenBefore",
        "squad.heard",
        "squad.area",
        "squad.ratioMin",
        "squad.ratioMax",
        "squad.ratioStep",
        "squad.choiceHoldTicks",
        "squad.choiceHysteresis",
        "squad.advanceToPercent",
        "squad.advanceR",
        "squad.idleAdvanceR",
        "squad.guardAdvanceR",
        "squad.autoWithdrawBelow",
        "squad.postLeash",
        "squad.contactSpacing",
        "squad.minSpacing",
        "squad.manyOnLine",
        "squad.boxAlong",
        "squad.boxBehind",
        "squad.boxAhead",
        "squad.rebuildMoved",
        "squad.urgentSteps",
        "squad.medicBehind",
        "squad.flankBehind",
        "squad.supportBehind",
        "squad.airGuardBehind",
        "squad.readyTicks",
        "squad.boundMin",
        "squad.boundMax",
        "squad.leapMin",
        "squad.leapMax",
        "squad.leapR",
        "squad.boundAcross",
        "squad.boundMaxTicks",
        "squad.marchR",
        "squad.marchQuietTicks",
        "squad.marchStep",
        "squad.suppressHitBelow",
        "squad.suppressAmmoPercent",
        "squad.lmgSuppressAmmo",
        "squad.suppressSpread",
        "squad.suppressHitPercent",
        "squad.suppressHitNear",
        "squad.suppressLiftTicks",
        "squad.suppressAimClear",
        "squad.lmgWait",
        "squad.lmgDeployTicks",
        "squad.lmgStopTicks",
        "squad.lmgArcSteps",
        "squad.lmgOutTicks",
        "squad.lmgRedeployTicks",
        "squad.lmgDeployedSuppress",
        "squad.flankFipTicks",
        "squad.flankWindowTicks",
        "squad.flankHitsBelow",
        "squad.flankCoverAvg",
        "squad.flankMinAble",
        "squad.flankMinR",
        "squad.autoFlank",
        "squad.flankBigSquad",
        "squad.flankDistancePercent",
        "squad.flankMaxPath",
        "squad.flankExposed",
        "squad.flankMaxExposed",
        "squad.flankSuppressTicks",
        "squad.flankCloseFire",
        "squad.flankMoved",
        "squad.flankRetryTicks",
        "squad.flankRejectTicks",
        "squad.flankMaxTicks",
        "squad.assaultRange",
        "squad.assaultR",
        "squad.assaultMinAble",
        "squad.assaultShare",
        "squad.assaultGrenades",
        "squad.assaultGrenadeWait",
        "squad.assaultShiftRange",
        "squad.assaultEndRange",
        "squad.assaultFailR",
        "squad.assaultMaxTicks",
        "squad.assaultRetryTicks",
        "gaits.riseMove",
        "squad.withdrawStepMin",
        "squad.withdrawStepMax",
        "squad.withdrawAcross",
        "squad.withdrawStepTicks",
        "squad.withdrawReach",
        "squad.withdrawWidth",
        "squad.withdrawGainPercent",
        "squad.withdrawRefusals",
        "squad.withdrawRetryTicks",
        "squad.consolidateTicks",
        "squad.consolidateOrderTicks",
        "squad.withdrawRescueWaitTicks",
        "squad.moveBehindSteps",
        "squad.longFlankBehind",
        "morale.start",
        "morale.killed",
        "morale.down",
        "morale.wounded",
        "morale.leader",
        "morale.leaderPauseTicks",
        "morale.pinnedTicks",
        "morale.pinnedRate",
        "morale.flanked",
        "morale.flankedTicks",
        "morale.vehicleReach",
        "morale.vehicleRate",
        "morale.enemyKilled",
        "morale.quietTicks",
        "morale.quietRate",
        "morale.aidReach",
        "morale.aidRate",
        "morale.garrisonRate",
        "morale.steadyAt",
        "morale.brokenBelow",
        "morale.rally",
        "morale.rallyLed",
        "morale.routBelow",
        "morale.routEnd",
        "morale.shakenAim",
        "morale.brokenAim",
        "morale.shakenSuppress",
        "morale.brokenSuppress",
        "morale.shakenReact",
        "morale.brokenReact",
        "morale.shakenH",
        "morale.shakenForce",
        "morale.brokenForce",
        "morale.brokenFireRange",
        "morale.firedOnTicks",
        "morale.safeReach",
        "morale.lossSpan",
        "calls.uContact",
        "calls.uRatio",
        "calls.uRatioLow",
        "calls.uPinned",
        "calls.uLoss",
        "calls.uLossMax",
        "calls.lossTicks",
        "calls.uArmour",
        "calls.uFlankRefused",
        "calls.uGarrison",
        "calls.withdrawTicks",
        "calls.matchEvery",
        "calls.needPercent",
        "calls.callRadius",
        "calls.maxHelpers",
        "calls.releaseTicks",
        "calls.supportNear",
        "calls.otherFight",
        "calls.minAmmo",
        "calls.minAble",
        "calls.supportSteps",
        "calls.approachPercent",
        "calls.helpArrived",
        "calls.arrivedReach",
        "calls.standDownTicks",
        "calls.autoSupport",
        "calls.lineSideMin",
        "calls.lineSideMax",
        "reactions.impactNear",
        "reactions.fireSpacing",
        "reactions.airNear",
        "reactions.airTicks",
        "reactions.cornerReach",
        "reactions.cornerPinned",
        "reactions.looseCorner",
    ];

    /// <summary>The groups of the block, in order.</summary>
    public static readonly string[] Groups = ["perception", "danger", "exposure", "cover", "posture", "suppression", "zones", "think", "gaits", "facing", "position", "targeting", "grenades", "vehicleCover", "squad", "morale", "calls", "reactions"];
}

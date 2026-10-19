using System.Security.Cryptography;
using System.Text;

namespace SimCore.Model;

/// <summary>
/// Serializes a compiled <see cref="ContentSet"/> canonically and hashes it.
/// The serialization walks the sorted collections field by field, so two
/// content directories that compile to the same definitions hash the same no
/// matter how their files are split, ordered or formatted.
/// </summary>
/// <remarks>
/// Integers are 8 little-endian bytes (signed values sign-extended), strings
/// are a length and their UTF-8 bytes, lists are a count and their items.
/// Changing this layout changes every content hash; bump <see cref="Format"/>.
/// </remarks>
public static class ContentHasher
{
    /// <summary>The serialization format tag, hashed first.</summary>
    public const string Format = "simcore-content/3";

    /// <summary>Returns the SHA-256 of set's canonical serialization as lowercase hex.</summary>
    public static string Compute(ContentSet set) =>
        Convert.ToHexString(SHA256.HashData(Serialize(set))).ToLowerInvariant();

    /// <summary>Returns the canonical serialization of set.</summary>
    public static byte[] Serialize(ContentSet set)
    {
        var w = new Writer();
        w.Str(Format);
        w.List(set.Tags, w.Str);
        w.List(set.ArmorClasses, w.Str);
        w.List(set.Warheads, h =>
        {
            w.Str(h.Id);
            w.List(h.Versus, v => w.Int(v));
            w.List(h.MaxHpPercent, v => w.Int(v));
        });
        w.List(set.Archetypes, a =>
        {
            w.Str(a.Id);
            w.Str(a.Name);
            w.Int(a.Glyph);
            w.UInt(a.Tags.Bits);
            w.Int((int)a.Locomotor);
            w.Int(a.Armor);
            w.Int(a.MaxHp);
            w.Int(a.Speed.Raw);
            w.Int(a.TurnRate);
            w.Bool(a.Turret);
            w.Int((int)a.WeaponMode);
            w.Int(a.Radius.Raw);
            w.Int(a.DeployTicks);
            w.Int(a.StowTicks);
            w.Bool(a.AutoFireDeployed);
            w.Int(a.Crusher);
            w.Int(a.Crushable);
            w.Int(a.AccelTicks);
            w.Int(a.ExpireWhenEmpty);
            w.Int((int)a.HeightRaw);
            w.Bool(a.Hidden);
            w.Bool(a.Locked);
            w.Int(a.Cost);
            w.Int(a.Vision.Raw);
            w.Int(a.Detect.Raw);
            w.Int(a.AirVision.Raw);
            w.Int(a.Stance);
            w.Bool(a.Pursue);
            w.Int(a.Leash.Raw);
            w.Int(a.RetaliateMax.Raw);
            w.Int(a.Lifetime);
            w.List(a.SlotChoices, choices => w.List(choices, v => w.Int(v)));
            w.List(a.Components, v => w.Int(v));
            w.Bool(a.Crew is not null);
            if (a.Crew is { } crew)
            {
                w.UInt(crew.Tags.Bits);
                w.Int(crew.EnterRange.Raw);
                w.Int(crew.Survival);
                w.Int(crew.ExitDamage);
                w.Int(crew.Passengers);
                w.UInt(crew.PassengerTags.Bits);
            }
            w.Bool(a.Garrison is not null);
            if (a.Garrison is { } g)
            {
                w.Int(g.Capacity);
                w.UInt(g.Tags.Bits);
                w.Int(g.EnterRange.Raw);
                w.Int(g.RangeBonus.Raw);
                w.Int(g.VisionBonus.Raw);
                w.Int(g.OccupantDamage);
                w.List(g.OccupantWarheads, v => w.Int(v));
                w.List(g.Stages, v => w.Int(v));
                w.List(g.StageCapacity, v => w.Int(v));
                w.Int(g.EjectDamage);
                w.Int(g.CollapseSurvival);
                w.Int(g.CollapseHp);
                w.List(g.Doors, p => { w.Int(p.X); w.Int(p.Y); });
                w.Bool(g.Owned);
                w.UInt(g.LauncherTags.Bits);
                if (!g.DeployTags.IsEmpty)
                {
                    w.UInt(g.DeployTags.Bits); // only when set: content without it hashes as before
                }
            }
            w.Bool(a.HighGround is not null);
            if (a.HighGround is { } hg)
            {
                w.Int(hg.PercentPerMetre);
                w.Int(hg.MaxPercent);
                w.Int(hg.RadiusCells);
            }
            w.Int(a.FootprintW);
            w.Int(a.FootprintH);
            w.Int(a.BuildTime);
            w.Bool(a.Production is not null);
            if (a.Production is { } pr)
            {
                w.UInt(pr.Tags.Bits);
                w.Int(pr.Bays);
                w.Int(pr.QueueMax);
                w.Int(pr.DoorTicks);
                w.Int(pr.PushAfter);
                w.Str(pr.Crew);
                w.List(pr.Exits, e => { w.Int(e.X); w.Int(e.Y); });
                w.List(pr.BayPoints, e => { w.Int(e.X); w.Int(e.Y); });
                w.Int(pr.Rally?.X ?? -1);
                w.Int(pr.Rally?.Y ?? -1);
                w.Int(pr.MinHpPercent);
                if (pr.IntoStation)
                {
                    w.Str("intoStation"); // only an airfield facility's (E8)
                }
            }
            w.Bool(a.Construction is not null);
            if (a.Construction is { } cs)
            {
                w.Int(cs.StartHp);
                w.List(cs.Stages, v => w.Int(v));
                w.Int(cs.Refund);
                w.List(cs.Helpers, v => w.Int(v));
                w.Int(cs.PadBlend);
            }
            if (a.Tunnel is { } tn)
            {
                // Only tunnel entrances: content without them hashes as before.
                w.Str("tunnel");
                w.Int(tn.Size);
                w.Int(tn.Capacity);
                w.Int(tn.Throughput);
                w.Int(tn.RubbleHp);
                w.Int((int)tn.LinkRange.Raw);
                w.Int((int)tn.Spacing.Raw);
                w.Int((int)tn.Speed.Raw);
                w.Int(tn.ExposeTicks);
                w.Int(tn.FlatGrade);
                w.Int(tn.HillGrade);
                w.Int(tn.Depth);
                w.Bool(tn.Walls);
                w.Int(tn.Mound);
                w.Int(tn.HillMound);
                w.Int(tn.Apron);
                w.Int(tn.Blend);
                w.Int(tn.EmergeDamage);
                w.Int(tn.RestoreTicks);
            }
            if (a.TunnelSize != 0)
            {
                w.Str("tunnelUnit"); // only units that go underground
                w.Int(a.TunnelSize);
                w.Int(a.TunnelSlots);
            }
            if (a.Flight is { } fl)
            {
                // Only scout drones: content without them hashes as before.
                w.Str("flight");
                w.Int((int)fl.HeightRaw);
                w.Int((int)fl.ClimbPerTick);
                w.Int((int)fl.LaunchHeightRaw);
                w.Int(fl.OrbitRadius.Raw);
                w.Int(fl.TurnPermille);
                w.Int((int)fl.Class);
                w.Int(fl.ReserveTicks);
                w.Int(fl.RecoverRange.Raw);
                w.Int(fl.TurnaroundTicks);
                if (fl.HomeWhenEmpty || fl.Relaunch)
                {
                    w.Bool(fl.HomeWhenEmpty); // only strike drones: scouts hash as before
                    w.Bool(fl.Relaunch);
                }
                w.Str("edgeMargin");
                w.Int((int)fl.EdgeMarginRaw);
                if (fl.Wing is { } wg)
                {
                    // Only fixed-wing flights (E4): drone content hashes as before.
                    w.Str("fixedWing");
                    foreach (var v in (ReadOnlySpan<long>)[wg.Cruise, wg.Dash, wg.Military, wg.Min, wg.Corner, wg.TurnAccel, wg.Drag, wg.Bleed, wg.Gravity,
                        wg.ThrustIdle, wg.ThrustCruise, wg.ThrustMilitary, wg.ThrustAfterburner, wg.DivePerTick, wg.FloorRaw, wg.IngressRaw,
                        wg.LookAheadTicks, wg.SeparationRaw, wg.BurnIdle, wg.BurnCruise, wg.BurnMilitary, wg.BurnAfterburner])
                    {
                        w.Int(v);
                    }
                }
            }
            if (a.Mission is { } ms)
            {
                w.Str("mission"); // only aircraft with mission numbers
                w.Int(ms.HuntLeash.Raw);
                w.Int((int)ms.DefaultGuard);
            }
            if (a.Signature != Signature.Normal)
            {
                w.Str("signature"); // only targets with a signature of their own (E5)
                w.Int(a.Signature.Detect);
                w.Int(a.Signature.Lock);
                w.Int(a.Signature.Kill);
            }
            if (a.Countermeasures.Count > 0)
            {
                w.Str("countermeasures"); // only aircraft with dispensers (E5)
                w.List(a.Countermeasures, cm =>
                {
                    w.Int((int)cm.Kind);
                    w.Int(cm.Salvos);
                    w.Int((int)cm.Vs);
                    w.Int(cm.Pk);
                    w.Int(cm.Window);
                    w.Int(cm.PerMissile);
                    w.Int(cm.RearmTicks);
                });
            }
            if (a.Evade is { } ev)
            {
                w.Str("evade"); // only aircraft that break (E6)
                w.Int(ev.BreakG);
                w.Int(ev.Warn);
                w.Int(ev.MaxTicks);
            }
            if (a.AirBody is { } ab)
            {
                w.Str("airBody"); // only air targets without a flight (E6: the FPV drone)
                w.Int((int)ab.Class);
                w.Int(ab.HeightRaw);
            }
            WriteAirfield(w, a);
        });
        w.List(set.Components, c =>
        {
            w.Str(c.Id);
            w.Int(c.Cost);
            w.UInt(c.Requires.Bits);
            w.Bool(c.Placeholder);
            w.List(c.Effects, e => WriteEffect(w, e));
        });
        w.List(set.Factions, f =>
        {
            w.Str(f.Id);
            w.Str(f.Name);
            w.List(f.Archetypes, w.Bool);
            w.List(f.Components, w.Bool);
            w.List(f.Modifiers, m =>
            {
                w.UInt(m.Tags.Bits);
                WriteModifier(w, m.Modifier);
            });
            w.Int(f.MaxPresetsPerArchetype);
            w.UInt(f.DefeatedWithout.Bits);
        });
        var ai = set.UnitAi;
        w.Str("unitAi");
        w.Int(ai.GuardInnerPercent);
        w.Int(ai.GuardOuterPercent);
        w.Int(ai.AggressivePercent);
        w.Int(ai.ChaseTicks);
        w.Int(ai.ScanTicks);
        w.Int(ai.ReturnScanTicks);
        w.Int(ai.RetaliateMax.Raw);
        w.Int(ai.RetaliateFriends.Raw);
        w.Int(ai.RetaliateCooldownTicks);
        if (set.AirfieldTypes.Count > 0)
        {
            w.Str("airfieldTypes"); // only content with airfields: content without them hashes as before
            w.List(set.AirfieldTypes, t => WriteAirfieldType(w, t));
        }
        if (set.AirClasses.Count > 0)
        {
            w.Str("airClasses"); // only content with air classes (E5): content without them hashes as before
            w.List(set.AirClasses, c =>
            {
                w.Str(c.Id);
                w.Int((int)c.Kind);
            });
        }
        return w.ToArray();
    }

    // WriteRunway writes a weapon's or strike's runway effect, only when it has one: content
    // without them hashes as before.
    private static void WriteRunway(Writer w, RunwayEffect? r)
    {
        if (r is not null)
        {
            w.Str("runway");
            w.Int((int)r.Crater);
            w.Int(r.Wear);
        }
    }

    // WriteAirfield writes an archetype's aircraft block, airfield role and structure flags, each
    // only when present: content without them hashes as before.
    private static void WriteAirfield(Writer w, Archetype a)
    {
        if (a.Aircraft is { } ac)
        {
            w.Str("aircraft");
            w.Int((int)ac.SizeClass);
            w.Int(ac.Span.Raw);
            w.Int(ac.Length.Raw);
            w.Int((int)ac.Landing);
            w.Int(ac.GroundArmor);
            w.Int(ac.MinLandRun.Raw);
            w.Int(ac.MinTakeoffRun.Raw);
            w.Int(ac.FixDistance.Raw);
            w.Int(ac.FixHeightRaw);
            w.Int(ac.Touchdown.Raw);
            w.Int(ac.Flare.Raw);
            w.Int(ac.RolloutDecel.Raw);
            w.Int(ac.Brake.Raw);
            w.Int(ac.ApproachSpeed.Raw);
            w.Int(ac.TouchdownSpeed.Raw);
            w.Int(ac.TaxiSpeed.Raw);
            w.Int(ac.TurnSpeed.Raw);
            w.Int(ac.PushbackSpeed.Raw);
            w.Int(ac.TakeoffSpeed.Raw);
            w.Int(ac.TakeoffAccel.Raw);
            w.Int(ac.RefuelTicks);
            w.Int(ac.RearmTicksPerRound);
            w.Int(ac.RepairHpPerSecond);
            w.Int(ac.GroundVision.Raw);
            w.Int(ac.GroundEyeRaw);
            w.Int(ac.HoldReserveTicks);
            w.Bool(ac.Sortie);
        }
        if (a.Airfield is { } ap)
        {
            w.Str("airfieldPiece");
            w.Int((int)ap.Role);
            w.Int(ap.Type);
            w.Int((int)ap.Kind);
            w.Bool(ap.NeedsFacility);
            w.Int(ap.RearmPercent);
            w.Int(ap.RepairPercent);
            w.Int(ap.RefuelPercent);
            w.Int(ap.MaxSpan.Raw);
        }
        if (a.Pavement || a.Untargetable || a.Invulnerable || a.RubbleHpPercent != 0 || a.RepairPercentOfBuild != 0)
        {
            w.Str("pieceFlags");
            w.Bool(a.Pavement);
            w.Bool(a.Untargetable);
            w.Bool(a.Invulnerable);
            w.Int(a.RubbleHpPercent);
            w.Int(a.RepairPercentOfBuild);
        }
    }

    // WriteAirfieldType writes one airfield type.
    private static void WriteAirfieldType(Writer w, AirfieldType t)
    {
        w.Str(t.Id);
        w.Str(t.Name);
        w.Int((int)t.MaxClass);
        w.Int(t.Facility);
        w.Int(t.Row);
        w.Int(t.Segment);
        w.Int(t.Loading);
        w.Int(t.Normal);
        w.Int(t.StationCells);
        w.Int(t.LaneCells);
        w.Int(t.SegmentCells);
        w.Int(t.RunwayCells);
        w.Int(t.GrassCells);
        w.Int(t.TaxiwayCells);
        w.Int(t.Connector.Raw);
        w.Int(t.MinSegments);
        w.Int(t.CorridorCells);
        w.Int(t.FacilityGapCells);
        w.Int(t.MaxReliefRaw);
        w.List(t.RowSlots, s =>
        {
            w.Int((int)s.Kind);
            w.Int(s.X);
            w.Int(s.Heading);
            w.Bool(s.Pushback);
            w.List(s.ParkIn, p => { w.Int(p.X); w.Int(p.Y); });
            w.List(s.ParkOut, p => { w.Int(p.X); w.Int(p.Y); });
        });
        w.Int(t.HoldDistance.Raw);
        w.Int(t.HoldRadius.Raw);
        w.Int(t.HoldHeightRaw);
        w.Int(t.HoldStepRaw);
        w.Int(t.HoldPlaces);
        w.Int(t.LockReleaseTicks);
        w.Int(t.TaxiWaitTicks);
        w.Int(t.CollapseDamage);
        w.Int(t.CraterHitDamage);
        w.Int(t.SmallCrater.Radius.Raw);
        w.Int(t.SmallCrater.RepairTicks);
        w.Int(t.LargeCrater.Radius.Raw);
        w.Int(t.LargeCrater.RepairTicks);
        w.Int(t.CraterMargin.Raw);
        w.Int(t.MaxCraters);
        w.Int(t.WearPatchCells);
        w.Int(t.WearToCrater);
        w.Int(t.WearNeighbourPercent);
        w.Int(t.WearRepairPerSecond);
    }

    private static void WriteEffect(Writer w, Effect e)
    {
        switch (e)
        {
            case WeaponEffect x:
                w.Str("weapon");
                w.Int(x.Damage);
                w.Int(x.Range.Raw);
                w.Int(x.Reload);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                w.Int(x.Scatter.Raw);
                w.Int((int)x.Targets);
                w.Int(x.ProjectileSpeed.Raw);
                w.Int(x.SalvoSize);
                w.Int(x.SalvoInterval);
                w.Int(x.Ammo);
                w.Int(x.MinRange.Raw);
                w.Bool(x.AmmoPerRocket);
                w.Int(x.SplashCenter);
                w.Int(x.SplashEdge);
                w.Int(x.DepotReload?.EveryTicks ?? 0);
                w.Int(x.DepotReload?.AfterFireTicks ?? -1);
                w.Int(x.DepotReload?.Cost ?? 0);
                w.Int(x.DepotReload?.Stowed == true ? 1 : 0);
                w.Int(x.SplashInner.Raw);
                w.Int(x.SplashOuterPercent);
                w.List(x.AutoRange, r => w.Int(r.Raw));
                w.Bool(x.AutoSkipRetreating);
                w.Int(x.MaxMisses);
                w.Int(x.AimTicks);
                w.Int(x.Guided?.MaxFlight.Raw ?? 0);
                w.Int(x.Guided?.TurnPermille ?? 0);
                w.Bool(x.Guided?.RequiresOperatorStill ?? false);
                w.Int(x.Guided?.DropDistance.Raw ?? 0);
                w.List(x.Priority, p => w.Int(p));
                w.Int(x.ArcPercent);
                w.Int((int)x.Intercept);
                w.Bool(x.Drone is not null);
                if (x.Drone is { } dr)
                {
                    w.Int((int)dr.CruiseAboveMax);
                    w.Int((int)dr.ClimbPerTick);
                    w.Int(dr.BoosterTicks);
                    w.Int(dr.DivePermille);
                    w.Int((int)dr.LaunchHeight);
                    w.Int(dr.RoundOffTicks);
                    w.Int(dr.PitchOverTicks);
                }
                w.Bool(x.NoResupply);
                if (x.ScatterNear.Raw > 0)
                {
                    w.Int(x.ScatterNear.Raw); // only when set: content without it hashes as before
                }
                w.Bool(x.Interceptor is not null);
                if (x.Interceptor is { } ic)
                {
                    w.Int(ic.Classes);
                    w.Int(ic.LockTicks);
                    w.Int(ic.Fuse.Raw);
                    w.Int(ic.KillTail);
                    w.Int(ic.KillHead);
                    w.Int(ic.CrossHalf);
                    w.Int(ic.SpeedRef.Raw);
                    w.Int(ic.RangeFar);
                    w.Int(ic.TrackBonus);
                    w.Int(ic.TrackFull);
                    w.Int(ic.KillMin);
                    w.Int(ic.KillMax);
                    w.Int(ic.LeadTicks);
                    if (ic.LaunchHeight != 0)
                    {
                        w.Int((int)ic.LaunchHeight); // only when set: content without it hashes as before
                    }
                    if (ic.Rules.Count > 0)
                    {
                        w.Str("classRules"); // only when set (E5: killBest, speedRefs, hitDamage per class)
                        w.List(ic.Rules, r =>
                        {
                            w.Int((int)r.Class);
                            w.Int(r.Best);
                            w.Int(r.SpeedRef.Raw);
                            w.Int(r.HitDamage);
                        });
                    }
                    if (ic.Seeker != Seeker.None || ic.Boresight != 0)
                    {
                        w.Str("seeker"); // only when set (E5)
                        w.Int((int)ic.Seeker);
                        w.Int(ic.Boresight);
                    }
                    if (ic.Hits is { Count: > 0 } hits)
                    {
                        w.Str("hits"); // only when set: content without them hashes as before
                        w.Int(hits.Count);
                        foreach (var hit in hits)
                        {
                            w.Int((int)hit.Class);
                            w.Int(hit.Chance);
                            w.Int(hit.Damage);
                        }
                    }
                }
                if (x.AirStrike is { } st)
                {
                    w.Str("airStrike"); // only strike missiles: content without them hashes as before
                    w.Int(st.HitIdle);
                    w.Int(st.HitMoving);
                    w.UInt(st.Kills.Bits);
                    w.UInt(st.HeavyTags.Bits);
                    w.Int(st.HeavyKill);
                    w.Int(st.HeavyHpLeft);
                    w.Int(st.MissNear.Raw);
                    w.Int(st.MissFar.Raw);
                }
                if (x.AirGun is { } ag)
                {
                    w.Str("airGun"); // only aircraft guns (E8)
                    w.Int(ag.Cone);
                    w.List(ag.HitBase, h =>
                    {
                        w.Int((int)h.Class);
                        w.Int(h.Chance);
                    });
                    w.Int(ag.CrossHalf);
                    w.Int(ag.RangeFar);
                    w.Int(ag.CeilingRaw);
                }
                if (x.Delivery is { } dl)
                {
                    w.Str("delivery"); // only bombs (E8)
                    w.Int((int)dl.Profile);
                    w.Int(dl.ReleaseHeightRaw);
                    w.Int(dl.DiveAngle);
                    w.Int(dl.TossAngle);
                    w.Int(dl.IpDistance.Raw);
                    w.Int(dl.ScatterLevel.Raw);
                    w.Int(dl.ScatterDive.Raw);
                    w.Int(dl.ScatterToss.Raw);
                    w.Bool(dl.Guided is not null);
                    if (dl.Guided is { } gb)
                    {
                        w.Int(gb.HitIdle);
                        w.Int(gb.HitMoving);
                        w.Int(gb.MissNear.Raw);
                        w.Int(gb.MissFar.Raw);
                    }
                    w.Int(dl.Stick);
                    w.Int(dl.StickSpacing.Raw);
                    if (dl.GravityRaw != 0)
                    {
                        w.Int(dl.GravityRaw); // free-fall bombs (E7)
                    }
                }
                WriteRunway(w, x.Runway);
                break;
            case AuraEffect x:
                w.Str("aura");
                w.Int(x.Radius.Raw);
                w.Int(x.EveryTicks);
                w.Int(x.Heal);
                w.List(x.Modifiers, m => WriteModifier(w, m));
                w.Int((int)x.Affects);
                w.UInt(x.AffectsTags.Bits);
                break;
            case PlacerEffect x:
                w.Str("placer");
                w.Int(x.Archetype);
                w.Int(x.Charges);
                w.Int(x.Cooldown);
                w.Int(x.Range.Raw);
                w.Int(x.MaxActive);
                w.Int(x.PlaceTicks);
                w.Int(x.DepotReload?.EveryTicks ?? 0);
                w.Int(x.DepotReload?.AfterFireTicks ?? -1);
                w.Int(x.DepotReload?.Cost ?? 0);
                break;
            case LaunchEffect x:
                w.Str("launch");
                w.Int(x.Archetype);
                w.Int(x.Charges);
                w.Int(x.Cooldown);
                w.Int(x.Range.Raw);
                if (x.MaxActive != 0 || x.DepotReload is not null || x.NoResupply)
                {
                    // Only scout launchers: content without them hashes as before.
                    w.Int(x.MaxActive);
                    w.Int(x.DepotReload?.EveryTicks ?? 0);
                    w.Int(x.DepotReload?.AfterFireTicks ?? -1);
                    w.Int(x.DepotReload?.Cost ?? 0);
                    w.Bool(x.NoResupply);
                }
                break;
            case StrikeEffect x:
                w.Str("strike");
                w.Int(x.Contact.Raw);
                w.Int(x.Damage);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                WriteRunway(w, x.Runway);
                break;
            case SupplyEffect x:
                w.Str("supply");
                w.Int(x.Radius.Raw);
                w.Int(x.EveryTicks);
                w.Int(x.Amount);
                w.Int(x.Stock);
                w.Int(x.Restock);
                w.Int(x.ReloadBonus);
                w.Int(x.ConstructedStock);
                break;
            case CargoEffect x:
                w.Str("cargo");
                w.Int(x.Amount);
                w.Int(x.UnloadTicks);
                break;
            case TriggerEffect x:
                w.Str("trigger");
                w.Int(x.Radius.Raw);
                w.Int(x.Damage);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                w.UInt(x.Affects.Bits);
                w.Int(x.Edge);
                w.UInt(x.Kills.Bits);
                w.Int(x.ChainTicks);
                break;
            case BuilderEffect x:
                w.Str("builder");
                w.Int(x.Rate);
                w.Int(x.Range.Raw);
                break;
            case BuildAssistEffect x:
                w.Str("buildAssist");
                w.Int(x.Bonus);
                w.Int(x.Radius.Raw);
                w.List(x.Unlocks, v => w.Int(v));
                break;
            case RepairEffect x:
                w.Str("repair");
                w.Int(x.Rate);
                w.Int(x.EveryTicks);
                w.Int(x.Radius.Raw);
                w.UInt(x.Affects.Bits);
                break;
            case ModifierEffect x:
                w.Str("modifier");
                WriteModifier(w, x.Modifier);
                break;
            default:
                throw new ArgumentException($"content: effect type {e.GetType().Name} has no canonical form");
        }
    }

    private static void WriteModifier(Writer w, Modifier m)
    {
        w.Int((int)m.Stat);
        w.Int(m.Percent);
        w.Int(m.Flat);
    }

    private sealed class Writer
    {
        private readonly MemoryStream _buf = new();

        public void UInt(ulong v)
        {
            for (var i = 0; i < 8; i++)
            {
                _buf.WriteByte((byte)(v >> (8 * i)));
            }
        }

        public void Int(long v) => UInt(unchecked((ulong)v));

        public void Bool(bool v) => Int(v ? 1 : 0);

        public void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Int(b.Length);
            _buf.Write(b);
        }

        public void List<T>(IReadOnlyList<T> items, Action<T> item)
        {
            Int(items.Count);
            foreach (var x in items)
            {
                item(x);
            }
        }

        public byte[] ToArray() => _buf.ToArray();
    }
}

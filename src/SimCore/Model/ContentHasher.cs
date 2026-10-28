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
            if (h.MaxHpBySplash)
            {
                w.Str("maxHpBySplash"); // only the incendiary bomb's warhead: the others hash as before
            }
            if (h.Fire is { } fire)
            {
                w.Str("fire"); // only a warhead that sets forest on fire: the others hash as before
                fire.Digest(v => w.Int(v));
            }
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
                if (crew.UnloadInterval != 0)
                {
                    w.Str("unloadInterval"); // only a staged unload (the Humvee's fast dismount)
                    w.Int(crew.UnloadInterval);
                }
                if (crew.OccupantDamage != 0)
                {
                    w.Str("crewExposed"); // only an open emplacement's crewman (the howitzer's)
                    w.Int(crew.OccupantDamage);
                    w.List(crew.OccupantWarheads, v => w.Int(v));
                }
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
                if (!pr.ExcludeTags.IsEmpty)
                {
                    w.Str("excludeTags"); // only the vehicle garage's (no tanks)
                    w.UInt(pr.ExcludeTags.Bits);
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
                w.Str("flyZoneMargin"); // E12 (the retired edgeMargin hashed here before)
                w.Int((int)fl.FlyZoneMarginRaw);
                if (fl.ByMap is { } hm)
                {
                    w.Str("heightByMap"); // only flights whose height grows with the map (E13)
                    w.Int(hm.FromCells);
                    w.Int(hm.ToCells);
                    w.Int(hm.HeightRaw);
                    w.Int(hm.MaxRaw);
                }
                if (fl.Endurance is { } en)
                {
                    w.Str("enduranceByMap"); // only flights whose tank grows with the map (E13)
                    w.Int(en.FromCells);
                    w.Int(en.ToCells);
                    w.Int(en.Percent);
                }
                if (fl.Speeds is { } sp)
                {
                    w.Str("speedByMap"); // only flights whose speeds grow with the map (E13)
                    w.Int(sp.FromCells);
                    w.Int(sp.ToCells);
                    w.Int(sp.Percent);
                    w.Int(sp.DashPercent);
                }
                if (fl.Rotor is { } ro)
                {
                    // Only helicopters: content without them hashes as before.
                    w.Str("rotary");
                    foreach (var v in (ReadOnlySpan<long>)[ro.MaxSpeed, ro.SideSpeed, ro.AccelPerTick, ro.YawRate, ro.YawStep.Cos, ro.YawStep.Sin,
                        ro.DescentPerTick, ro.AttackHeightRaw, ro.HideHeightRaw, ro.PopMaxRaw, ro.StandoffPercent, ro.CoverSearchRaw, ro.GunPercent,
                        ro.LeashRaw, ro.SpoolTicks, ro.OutOfControl.Ticks, ro.OutOfControl.HpPercent, ro.OutOfControl.SpeedPercent,
                        ro.OutOfControl.SpinRate, ro.OutOfControl.EndHeightRaw, ro.Crew.Count, ro.Crew.Survive, ro.Crew.Unit])
                    {
                        w.Int(v);
                    }
                    if (ro.TempoLeave != 100 || ro.TempoReturn != 100)
                    {
                        w.Str("tempo"); // 2026-10-24: written only when used
                        w.Int(ro.TempoLeave);
                        w.Int(ro.TempoReturn);
                    }
                    w.Bool(ro.Riders is not null);
                    if (ro.Riders is { } rd)
                    {
                        w.Int(rd.Seats);
                        w.UInt(rd.Tags.Bits);
                        w.Int(rd.BoardRange.Raw);
                        w.Int(rd.DropHeightRaw);
                        w.Int(rd.DropTicks);
                        w.Int(rd.Survive);
                    }
                }
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
            if (a.Loadouts.Count > 0)
            {
                w.Str("loadouts"); // only aircraft with loadout modes (the stealth multirole)
                w.Int(a.LoadoutSwitchTicks);
                w.List(a.Loadouts, l =>
                {
                    w.Str(l.Id);
                    w.List(l.Ammo, x => { w.Int(x.Component); w.Int(x.Ammo); });
                    w.Int(l.SpeedPercent);
                    w.Int(l.Signature.Detect);
                    w.Int(l.Signature.Lock);
                    w.Int(l.Signature.Kill);
                    if (l.Sensors != AirSensors.None)
                    {
                        w.Str("sensors"); // only a mode with sensors of its own (the stealth multirole's heavy mode)
                        w.Int((int)l.Sensors);
                    }
                });
            }
            if (a.Sensors != AirSensors.Camera)
            {
                w.Str("sensors"); // only air units with more than a camera (concealment from the air)
                w.Int((int)a.Sensors);
            }
            if (a.RadarSpotting is { } rsp)
            {
                w.Str("radarSpotting"); // only aircraft that spot radars (the stealth multirole)
                w.UInt(rsp.Tags.Bits);
                w.Int(rsp.LingerTicks);
                w.Int(rsp.Radius.Raw);
            }
            if (a.DeploySlope != 0)
            {
                w.Str("deploySlope"); // only a launcher with a slope rule (the cruise missile truck)
                w.Int(a.DeploySlope);
            }
            if (a.CounterStealth != 0)
            {
                w.Str("counterStealth"); // only a counter-stealth radar (the S-400 type)
                w.Int(a.CounterStealth);
            }
            if (a.RadarHorizon is { } rh)
            {
                w.Str("radarHorizon"); // only a radar with a horizon (the S-400 type)
                w.Int(rh.BeyondRaw);
                w.Int(rh.BelowRaw);
            }
            if (a.Emitter is { } em)
            {
                w.Str("emitter"); // only an emitter (the S-400 type radar)
                w.Int(em.RangeRaw);
                w.Int(em.Radius.Raw);
                w.Int(em.LingerTicks);
                w.UInt(em.ExcludeTags.Bits);
            }
            if (a.Exclusive >= 0)
            {
                w.Str("exclusive"); // only archetypes in an exclusive group
                w.Int(a.Exclusive);
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
                    if (cm.BreakPk >= 0)
                    {
                        w.Str("breakPk"); // only dispensers with a break rule (E13)
                        w.Int(cm.BreakPk);
                    }
                });
            }
            if (a.AttackRun is { } ar)
            {
                w.Str("attackRun"); // only aircraft with attack-run numbers (E13)
                w.Int(ar.AlignRaw);
                w.Int(ar.BreakTurn);
                w.Int(ar.BreakRaw);
                w.Int(ar.ExtendRaw);
                w.Int(ar.WindowRaw);
                w.Int(ar.Offset);
            }
            if (a.Element is { } el)
            {
                w.Str("element"); // only aircraft with element numbers (E13)
                w.List(el.Slots, sl =>
                {
                    w.Int(sl.BackRaw);
                    w.Int(sl.AsideRaw);
                });
                w.Int(el.Stagger);
            }
            if (a.Pursuit is { } pu)
            {
                w.Str("pursuit"); // only aircraft with pursuit numbers (E13)
                w.Int(pu.SeparationRaw);
                w.Int(pu.BreakOffTicks);
                w.Int(pu.ReattackRaw);
                w.Int(pu.ClimbRaw);
                w.Int(pu.MaxTicks);
            }
            if (a.Gunship is { } gs)
            {
                w.Str("gunship"); // only gunships (H3)
                w.Int(gs.RadiusRaw);
                w.Int(gs.AreaRaw);
                w.Int(gs.OnCircleRaw);
                w.Int(gs.Bearing);
            }
            if (a.Evade is { } ev)
            {
                w.Str("evade"); // only aircraft that break (E6)
                w.Int(ev.BreakG);
                w.Int(ev.Warn);
                w.Int(ev.MaxTicks);
            }
            if (a.Loss is { } loss)
            {
                w.Str("loss"); // only aircraft that crash or eject (E10)
                if (loss.Crash is { } cr)
                {
                    w.Str("crash");
                    w.Int(cr.Warhead);
                    w.Int(cr.Damage);
                    w.Int(cr.Splash.Raw);
                    w.Int(cr.GravityRaw);
                    w.Int(cr.Drag);
                    w.Int(cr.BreakUp);
                    w.Int(cr.BreakUpDrag);
                    WriteRunway(w, cr.Runway);
                }
                if (loss.Eject is { } ej)
                {
                    w.Str("eject");
                    w.Int(ej.Unit);
                    w.Int(ej.SeatRiseRaw);
                    w.Int(ej.RiseTicks);
                    w.Int(ej.ChuteTicks);
                    w.Int(ej.ChuteRateRaw);
                    w.Int(ej.Drift.Raw);
                    w.Int(ej.DriftTicks);
                }
            }
            if (a.Kaman is { } km)
            {
                w.Str("kaman"); // only a Kaman drone: content without one hashes as before
                w.Int(km.Damage);
                w.Int(km.Warhead);
                w.Int(km.Splash.Raw);
                w.Int(km.SplashCenter);
                w.Int(km.SplashEdge);
                w.Int(km.DivePermille);
                w.Int(km.PitchOverTicks);
                WriteRunway(w, km.Runway);
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
        w.Int(ai.DetourBudget);
        w.Int(ai.DetourRadiusCells);
        w.Int(ai.DetourCost);
        w.Int(ai.DetourRetryTicks);
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
        if (set.ExclusiveGroups.Count > 0)
        {
            w.Str("exclusiveGroups"); // only content with exclusive groups (the heavy bombers' rule)
            w.List(set.ExclusiveGroups, g =>
            {
                w.Str(g.Id);
                w.Int(g.Max);
                w.Bool(g.LockType);
            });
        }
        if (set.Concealment is { } cr)
        {
            w.Str("concealment"); // only content with concealment from the air: content without it hashes as before
            cr.Digest(v => w.Int(v));
        }
        if (set.Fire is { } fr)
        {
            w.Str("fire"); // only content with forest fire: content without it hashes as before
            fr.Digest(v => w.Int(v));
        }
        if (set.Squad is { } sq)
        {
            w.Str("squad"); // only content with infantry squads: content without them hashes as before
            sq.Digest(v => w.Int(v));
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
            if (ac.Pattern != ApproachPattern.StraightIn || ac.AerobrakeSpeed.Raw != 0 || ac.MilitaryAccel.Raw != 0 || ac.CorridorNeed.Raw != 0)
            {
                // E9's pattern, takeoff and corridor fields: only when set (aircraft without them hash as before).
                w.Str("aircraftE9");
                w.Int((int)ac.Pattern);
                w.Int(ac.InitialDistance.Raw);
                w.Int(ac.PatternHeightRaw);
                w.Int(ac.BreakSpeed.Raw);
                w.Int(ac.AerobrakeSpeed.Raw);
                w.Int(ac.MilitaryAccel.Raw);
                w.Int(ac.CorridorNeed.Raw);
                w.Int(ac.PatternRadius.Raw);
                w.Int(ac.JoinRadius.Raw);
            }
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
            if (ap.MinClass != AircraftClass.S || ap.MaxClass != AircraftClass.XL)
            {
                w.Str("stationClasses"); // H1
                w.Int((int)ap.MinClass);
                w.Int((int)ap.MaxClass);
            }
        }
        if (a.GroundRadar.Raw != 0)
        {
            w.Str("groundRadar"); // H1
            w.Int(a.GroundRadar.Raw);
            if (a.GroundRadarVehicles)
            {
                w.Str("vehiclesOnly"); // only a helicopter's mast radar
            }
        }
        if (a.DirectionalArmor is { } da)
        {
            w.Str("directionalArmor"); // only the tanks
            w.Int(da.Front);
            w.Int(da.Side);
            w.Int(da.Rear);
            w.Int(da.Top);
            w.Int(da.FrontHalf);
            w.Int(da.RearHalf);
        }
        if (a.Airlift is { } al)
        {
            w.Str("airlift"); // only the airlifter
            w.Int(al.Seats);
            w.UInt(al.Tags.Bits);
            w.Int(al.Ramp.Raw);
            w.Int(al.BoardRange.Raw);
            w.Int(al.BoardTicks);
            w.Int(al.UnloadTicks);
        }
        if (a.TraverseTicks != 0)
        {
            w.Str("traverse"); // only a slow turret (the howitzer)
            w.Int(a.TraverseTicks);
        }
        if (a.HullAims || a.VerticalLaunch)
        {
            w.Str("launcherAim"); // only the truck launchers
            w.Bool(a.HullAims);
            w.Int(a.HullArc);
            w.Bool(a.VerticalLaunch);
        }
        if (a.CueLock != 0 || a.SweepTicks != 0)
        {
            w.Str("airRadar"); // only the mobile radar
            w.Int(a.CueLock);
            w.Int(a.SweepTicks);
        }
        if (a.Helipad is { } hp)
        {
            w.Str("helipad"); // only helipads
            w.Int(hp.RefuelPercent);
            w.Int(hp.RearmPercent);
            w.Int(hp.RepairPercent);
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
        if (a.Neutral)
        {
            w.Str("neutral"); // only when set: content without it hashes as before
        }
        if (a.Bridge is { } br)
        {
            w.Str("bridge"); // only bridges: content without them hashes as before
            w.Int((int)br.Road);
            w.Int(br.Stages.Count);
            foreach (var v in br.Stages)
            {
                w.Int(v);
            }
            w.Int(br.ReopenPercent);
            w.Int(br.Helpers.Count);
            foreach (var v in br.Helpers)
            {
                w.Int(v);
            }
        }
        if (a.ReloadPad is { } pad)
        {
            w.Str("reloadPad"); // only when set: content without it hashes as before
            w.UInt(pad.Tags.Bits);
            w.Int(pad.ReloadTicks);
            w.Int(pad.AirfieldRange.Raw);
        }
        if (a.Service is { } sv)
        {
            w.Str("service"); // only when set: content without it hashes as before
            w.UInt(sv.Tags.Bits);
            w.Int(sv.ReloadTicks);
            w.Int(sv.RepairTicks);
            w.Int(sv.CargoTicks);
        }
        if (a.DropZone is { } dz)
        {
            w.Str("dropZone"); // only when set (logistics)
            w.Int(dz.OffsetX);
            w.Int(dz.OffsetY);
            w.Int(dz.Radius.Raw);
        }
        if (a.AwayFrom is { } aw)
        {
            w.Str("awayFrom"); // only when set (logistics)
            w.List(aw.Archetypes, i => w.Int(i));
            w.Int(aw.Cells);
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
        if (t.MinTaxi.Raw != 0 || t.MinTurn.Raw != 0 || t.BreakDistance.Raw != 0)
        {
            w.Str("landing"); // the landing fix: written only when used, so older content hashes as before
            w.Int(t.MinTaxi.Raw);
            w.Int(t.MinTurn.Raw);
            w.Int(t.BreakDistance.Raw);
        }
        if (t.TempoLeave != 100 || t.TempoReturn != 100 || t.HoldReach.Raw != 2 * t.HoldRadius.Raw)
        {
            w.Str("tempo"); // 2026-10-24: written only when used, so older content hashes as before
            w.Int(t.TempoLeave);
            w.Int(t.TempoReturn);
            w.Int(t.HoldReach.Raw);
        }
        if (t.HasCore || t.RowOrigin != (0, t.SegmentDepth) || t.MaxColumns != 0 || t.MaxRows != 0 || t.ConnectorEvery != 1 ||
            t.RefuelWithoutFacility || t.LinkCells != 0)
        {
            w.Str("mainAirBase"); // H1: written only when used, so the forward airfield hashes as before
            w.Int(t.Core);
            w.Int(t.Hangar);
            w.Int(t.Radar);
            w.Int(t.Heavy);
            w.Int(t.RowOrigin.X);
            w.Int(t.RowOrigin.Y);
            w.Int(t.MaxColumns);
            w.Int(t.MaxRows);
            w.Int(t.ConnectorEvery);
            w.Bool(t.RefuelWithoutFacility);
            w.Int(t.LinkCells);
            w.Bool(t.CoreLayout is not null);
            if (t.CoreLayout is { } core)
            {
                w.Int(core.Segments);
                w.Int(core.Facility.X);
                w.Int(core.Facility.Y);
                w.Int(core.Hangar.X);
                w.Int(core.Hangar.Y);
                w.Int(core.Radar.X);
                w.Int(core.Radar.Y);
                w.Int(core.Apron.Y0);
                w.Int(core.Apron.Y1);
                w.Int(core.Rows);
                w.List(core.Bays, b =>
                {
                    w.Int(b.X);
                    w.Int(b.W);
                    w.Int(b.Lane.X);
                    w.Int(b.Lane.Y);
                    w.Int((int)b.Slot.Kind);
                    w.Int(b.Slot.Heading);
                    w.Bool(b.Slot.Pushback);
                    w.List(b.Slot.ParkIn, p => { w.Int(p.X); w.Int(p.Y); });
                    w.List(b.Slot.ParkOut, p => { w.Int(p.X); w.Int(p.Y); });
                });
            }
            if (!t.ExtraRows)
            {
                w.Str("noExtraRows"); // logistics: the forward airbase (the main air base hashes as before)
            }
        }
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
                if (x.Guided is { FireAndForget: true })
                {
                    w.Str("fireAndForget"); // only a helicopter's missile: content without it hashes as before
                }
                if (x.Cone != 0)
                {
                    w.Str("cone"); // only a helicopter's mount
                    w.Int(x.Cone);
                }
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
                if (x.Launches >= 0)
                {
                    w.Str("launches"); // only a Kaman launcher: content without one hashes as before
                    w.Int(x.Launches);
                }
                if (x.ScatterNear.Raw > 0)
                {
                    w.Int(x.ScatterNear.Raw); // only when set: content without it hashes as before
                }
                if (x.TruckReload is not null || x.RevealTicks != 0 || x.GroupRadius.Raw != 0)
                {
                    w.Str("emplacement"); // only the howitzer's gun: content without it hashes as before
                    w.Int(x.TruckReload?.Cost ?? 0);
                    w.Int(x.TruckReload?.EveryTicks ?? 0);
                    w.Int(x.TruckReload?.Range.Raw ?? 0);
                    w.Int(x.RevealTicks);
                    w.Int(x.GroupRadius.Raw);
                    w.Int(x.StructureWeight);
                }
                if (x.Terminal is { } tf)
                {
                    w.Str("terminal"); // only the hypersonic missile: content without one hashes as before
                    w.Int(tf.ClimbTicks);
                    w.Int(tf.PeakPercent);
                    w.Int(tf.PeakMinRaw);
                    w.Int(tf.PeakMaxRaw);
                    w.Int(tf.PeakAtPercent);
                    w.Int(tf.DiveFrom.Raw);
                    w.Int(tf.DiveSlopePermille);
                    w.Int(tf.DiveSpeed.Raw);
                    w.Int((int)tf.WeaveRaw);
                    w.Int(tf.Weaves);
                    w.Int(tf.WarnTicks);
                }
                if (x.Cruise is { } cr)
                {
                    w.Str("cruise"); // only the cruise missile: content without one hashes as before
                    w.Int(cr.BoostTicks);
                    w.Int(cr.BoostRaw);
                    w.Int(cr.HeightRaw);
                    w.Int(cr.LookAhead.Raw);
                    w.Int(cr.ClimbRaw);
                    w.Int(cr.DescendRaw);
                    w.Int(cr.TurnRadius.Raw);
                    w.Int(cr.PopUpRaw);
                    w.Int(cr.DiveFrom.Raw);
                    w.Int(cr.RouteMin.Raw);
                    w.Int(cr.RouteMax.Raw);
                    w.Int(cr.MaxWaypoints);
                    w.Int(cr.WarnTicks);
                }
                if (x.LowSight is { } ls)
                {
                    w.Str("lowFlight"); // only a weapon with the low-flight sight rule
                    w.Int(ls.BelowRaw);
                    w.Int(ls.Sight.Raw);
                }
                if (x.Signature is { } sg)
                {
                    w.Str("projectileSignature"); // only projectiles with a signature
                    w.Int(sg.Detect);
                    w.Int(sg.Lock);
                    w.Int(sg.Kill);
                }
                if (x.Fire is { } wf)
                {
                    w.Str("weaponFire"); // only a weapon with its own fire block: content without one hashes as before
                    wf.Digest(v => w.Int(v));
                }
                if (x.PointsOnly || x.Upgrade is not null)
                {
                    w.Str("pointsUpgrade"); // only the incendiary bomb: content without one hashes as before
                    w.Bool(x.PointsOnly);
                    w.Str(x.Upgrade ?? "");
                }
                if (x.SharedAmmo || x.Coaxial || x.ApsThreat)
                {
                    w.Str("mount"); // only the tank's guns and the weapons an APS engages: content without them hashes as before
                    w.Bool(x.SharedAmmo);
                    w.Bool(x.Coaxial);
                    w.Bool(x.ApsThreat);
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
                    if (ic.Outright)
                    {
                        w.Str("outright"); // only when set (the Patriot type against helicopters)
                    }
                    if (ic.CeilingRaw != 0)
                    {
                        w.Str("ceiling"); // only when set (H3): content without it hashes as before
                        w.Int(ic.CeilingRaw);
                    }
                    if (ic.RadarLinked || ic.Salvo.Count > 0 || ic.DefendRadiusRaw != 0)
                    {
                        w.Str("radarLinked"); // only when set (the Patriot-type launcher): content without them hashes as before
                        w.Bool(ic.RadarLinked);
                        w.List(ic.Salvo, s =>
                        {
                            w.Int((int)s.Class);
                            w.Int(s.Count);
                        });
                        w.Int(ic.DefendRadiusRaw);
                    }
                    if (ic.Battery is { } battery)
                    {
                        w.Str("battery"); // only when set (the SA-6 type launcher): content without it hashes as before
                        w.Int(battery.LinkRadiusRaw);
                        w.UInt(battery.RadarTags.Bits);
                        w.Bool(battery.SemiActive);
                    }
                    if (ic.MinAltitudeRaw != 0 || ic.AltitudeFullRaw != 0)
                    {
                        w.Str("altitude"); // only when set (the high-altitude interceptor): content without it hashes as before
                        w.Int(ic.MinAltitudeRaw);
                        w.Int(ic.AltitudeFullRaw);
                        w.Int(ic.AltitudeLow);
                    }
                    if (ic.MaxPerTarget != 0)
                    {
                        w.Str("maxPerTarget"); // only when set (the high-altitude interceptor): content without it hashes as before
                        w.Int(ic.MaxPerTarget);
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
                    if (ag.StrafeRange.Raw != 0 || ag.StrafeEvery != 0)
                    {
                        w.Int(ag.StrafeRange.Raw); // strafing (E7)
                        w.Int(ag.StrafeEvery);
                    }
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
                    if (dl.FireModes.Count > 0 || dl.GroupRaw != 0 || dl.GroupAsideRaw != 0 || dl.Retard != 0)
                    {
                        w.Str("modes"); // fire modes, grouping, retarded bombs (H2)
                        w.Int(dl.FireModes.Count);
                        foreach (var m in dl.FireModes)
                        {
                            w.Str(m.Id);
                            w.Int(m.Salvo);
                        }
                        w.Int(dl.GroupRaw);
                        w.Int(dl.GroupAsideRaw);
                        w.Int(dl.Retard);
                    }
                    if (dl.Overfly || dl.HasSpread)
                    {
                        w.Str("carpet"); // H4: the overfly and spread (carpet) modes
                        w.Bool(dl.Overfly);
                        foreach (var m in dl.FireModes)
                        {
                            w.Bool(m.Spread);
                        }
                    }
                    if (dl.HasRows)
                    {
                        w.Str("rows"); // the supersonic bomber's rows across the path
                        foreach (var m in dl.FireModes)
                        {
                            w.Int(m.Across);
                            w.Int(m.AcrossSpacingRaw);
                        }
                    }
                    if (dl.Precision || dl.MaxAims != 0 || dl.Tier != 0)
                    {
                        w.Str("precision"); // the stealth bomber's precision bombs
                        w.Bool(dl.Precision);
                        w.Int(dl.MaxAims);
                        w.Int(dl.Tier);
                    }
                }
                if (x.Airdrop is { } ad)
                {
                    w.Str("airdrop"); // only a cargo pallet (the airlifter's)
                    w.Int(ad.Amount);
                    w.Int(ad.UnloadTicks);
                }
                if (x.Penetrator is { } pen)
                {
                    w.Str("penetrator"); // only a bunker buster
                    w.Int(pen.RadiusRaw);
                    w.Int(pen.CraterTicks);
                    w.Int(pen.CraterRadiusRaw);
                }
                if (x.SideFire is { } sf)
                {
                    w.Str("sideFire"); // only a gunship's side cannons (H3)
                    w.Int(sf.ScatterRaw);
                    w.Int(sf.Tier);
                    w.List(sf.Priority, r => w.Int(r));
                    w.Bool(sf.AreaFire);
                    w.Bool(sf.Biggest);
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
            case ApsEffect x:
                w.Str("aps");
                w.Int(x.Charges);
                w.Int(x.Range.Raw);
                w.Int(x.Cooldown);
                w.Int(x.Chance);
                w.Int(x.FastFrom.Raw);
                w.Int(x.FastStep.Raw);
                w.Int(x.FastPenalty);
                w.Int(x.CloseRange.Raw);
                w.Int(x.ClosePenalty);
                w.Int(x.MinChance);
                break;
            case SmokeEffect x:
                w.Str("smoke");
                w.Int(x.Charges);
                w.Int(x.Radius.Raw);
                w.Int(x.DurationTicks);
                w.Int(x.Cooldown);
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
                if (x.StartStock != x.Stock || x.Storage || x.CargoSource || x.GroundOnly || x.RoundCost != 1 || x.RepairCost != 0 || x.FuelCost != 0)
                {
                    w.Str("logistics"); // only the logistics stores (the depots hash as before)
                    w.Int(x.StartStock);
                    w.Bool(x.Storage);
                    w.Bool(x.CargoSource);
                    w.Bool(x.GroundOnly);
                    w.Int(x.RoundCost);
                    w.Int(x.RepairCost);
                    w.Int(x.FuelCost);
                }
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

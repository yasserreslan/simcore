namespace SimCore.Sim;

public sealed partial class World
{
    /// <summary>
    /// Original implementation retained as an equivalence oracle. Returns a digest including the
    /// match setup's digest. Two machines with equal hashes on a tick have
    /// identical worlds; a mismatch is a desync. Pending commands are
    /// excluded: peers may not have received the same future commands yet.
    /// </summary>
    /// <remarks>
    /// Every value is mixed in as one 64-bit word (signed values sign-extended
    /// first) by <see cref="WordHash"/>. The map's cells go in as one cached
    /// digest (<see cref="Grid.Digest"/>), recomputed only after a cell changes.
    /// </remarks>
    internal ulong HashReference()
    {
        var h = new WordHash();
        h.Put(Match.Digest);
        h.Put(Tick);
        h.Put(_rng.State);
        h.Put(_nextId);
        h.Put(Result.Over ? 1UL : 0);
        h.Put(Result.Winner);
        h.Put(Result.EndTick);
        foreach (var p in _players)
        {
            h.Put(p.Id);
            h.PutSigned(p.Credits);
            h.Put(p.Defeated ? 1UL : 0);
            if (p.HasExclusiveLock)
            {
                h.Put(0xE7C1); // only a player whose exclusive group is locked to a type (the heavy bombers)
                foreach (var l in p.ExclusiveLocks)
                {
                    h.PutSigned(l);
                }
            }
        }
        h.Put(Grid.Digest()); // every cell
        if (Ground is { } terrain)
        {
            h.Put(terrain.Digest()); // only maps with terrain: flat maps hash as before
        }
        foreach (var u in _units)
        {
            h.Put(u.Id);
            h.PutSigned(u.Def.Index);
            h.PutSigned(u.Pos.X.Raw);
            h.PutSigned(u.Pos.Y.Raw);
            h.PutSigned(u.Facing);
            h.PutSigned(u.TurretFacing);
            h.PutSigned(u.Vel.X.Raw);
            h.PutSigned(u.Vel.Y.Raw);
            h.PutSigned(u.Heading.X.Raw);
            h.PutSigned(u.Heading.Y.Raw);
            h.PutSigned(u.DriveSpeed.Raw);
            h.PutSigned(u.StallGoal.X.Raw);
            h.PutSigned(u.StallGoal.Y.Raw);
            h.PutSigned(u.StallBest.Raw);
            h.PutSigned(u.StallTicks);
            h.Put(u.DetourRetryAt);
            h.PutSigned(u.DetourEnd);
            h.PutSigned(u.HP);
            h.PutSigned(u.Reload);
            h.Put((ulong)u.Order.Kind);
            h.PutSigned(u.Order.Cell.X);
            h.PutSigned(u.Order.Cell.Y);
            h.Put(u.Order.Target);
            h.PutSigned(u.Order.Ability);
            h.Put(u.AutoTarget);
            h.PutByte((byte)u.WeaponMode);
            h.PutByte((byte)u.Deploy);
            h.PutSigned(u.DeployLeft);
            h.Put(u.LastShot);
            if (u.MovedAt != 0)
            {
                h.Put(0xC0E7); // only where concealment from the air applies (fog, terrain): other games hash as before
                h.Put(u.MovedAt);
            }
            h.PutSigned(u.DepotReloadLeft);
            h.Put(u.SalvoTarget);
            h.Put(u.MissTarget);
            h.PutSigned(u.MissStreak);
            h.Put(u.RepathAt);
            h.PutSigned(u.PathIdx);
            h.PutSigned(u.Path.Count);
            for (var i = 0; i < u.Path.Count; i++)
            {
                h.PutSigned(u.Path[i].X);
                h.PutSigned(u.Path[i].Y);
            }
            h.Put(u.Inside);
            h.Put(u.Driver);
            h.Put(u.Owner);
            h.Put(u.Garrison);
            h.PutSigned(u.RangeBonus.Raw);
            h.PutSigned(u.VisionBonus.Raw);
            h.PutSigned(u.Cover.W);
            h.PutSigned(u.Cover.H);
            h.PutSigned(u.Stage);
            h.PutSigned(u.Occupants);
            h.PutSigned(u.Charges.Count);
            for (var i = 0; i < u.Charges.Count; i++)
            {
                h.PutSigned(u.Charges[i]);
            }
            h.PutSigned(u.Cooldown);
            h.PutSigned(u.SalvoLeft);
            h.PutSigned(u.SalvoWeapon);
            h.PutSigned(u.SalvoAim.X.Raw);
            h.PutSigned(u.SalvoAim.Y.Raw);
            h.PutSigned(u.SalvoWait);
            h.Put(u.SpottedBy);
            for (var pi = 0; pi <= _players.Count; pi++)
            {
                h.Put(u.SpotUntil[pi]);
            }
            h.PutSigned(u.VisionCell.X);
            h.PutSigned(u.VisionCell.Y);
            h.Put(u.LookedAt);
            h.Put(u.RevealedTo);
            h.Put(u.Source);
            h.PutSigned(u.Lifetime);
            h.PutSigned(u.Stock);
            h.PutSigned(u.Cargo);
            h.PutSigned(u.UnloadLeft);
            h.Put(u.UnloadDepot);
            h.Put(u.DepotStarved ? 1UL : 0);
            if (u.Def.Smoke is not null || u.Def.Crew is { UnloadInterval: > 0 })
            {
                // Only units with smoke launchers or a one-by-one unload (the Humvee): games without them hash as before.
                h.Put(0x4D7EE);
                h.PutSigned(u.DismountWait);
                h.PutSigned(u.Dismounted);
                h.Put(u.SmokeReady);
            }
            if (u.FeedTo != 0 || u.FeedLeft != 0 || u.RevealUntil != 0 || u.GroundQueue.Count > 0)
            {
                // Only a truck handing a howitzer its shells, a unit whose shots flashed, a Shift queue of ground points: games
                // without them hash as before.
                h.Put(0x155);
                h.Put(u.FeedTo);
                h.PutSigned(u.FeedLeft);
                h.Put(u.RevealUntil);
                h.PutSigned(u.GroundQueue.Count);
                foreach (var q in u.GroundQueue)
                {
                    h.PutSigned(q.X);
                    h.PutSigned(q.Y);
                }
            }
            if (u.Def.Aps is not null || u.Def.HasCoaxial)
            {
                // Only units with an active protection system or a coaxial gun (the tank): games without them hash as before.
                h.Put(0xA95);
                h.Put(u.ApsReady);
                h.PutSigned(u.CoaxReload);
                h.Put(u.CoaxShot);
            }
            h.PutSigned(u.ReloadBonus);
            h.PutSigned(u.Intent.Goal.X.Raw);
            h.PutSigned(u.Intent.Goal.Y.Raw);
            h.PutSigned(u.Intent.Desired.X.Raw);
            h.PutSigned(u.Intent.Desired.Y.Raw);
            h.PutSigned(u.Intent.Slot.X.Raw);
            h.PutSigned(u.Intent.Slot.Y.Raw);
            h.Put(u.Intent.Arrived ? 1UL : 0);
            h.Put(u.Intent.Stuck ? 1UL : 0);
            h.Put(u.Group);
            h.PutSigned(u.Dest.X);
            h.PutSigned(u.Dest.Y);
            h.PutByte((byte)u.Mode);
            h.Put(u.PathPending ? 1UL : 0);
            h.PutSigned(u.SlotOffset.X.Raw);
            h.PutSigned(u.SlotOffset.Y.Raw);
            h.PutSigned(u.RouteIdx);
            h.PutSigned(u.Route.Length);
            foreach (var c in u.Route)
            {
                h.PutSigned(c);
            }
            if (u.DeployAfterMove)
            {
                h.Put(0xDE); // only on terrain (a Deploy command on a slope): flat games hash as before
            }
            if (u.Def.Archetype.Production is not null)
            {
                // Only factories: games without them hash as before.
                h.PutSigned(u.ProductionQueue.Count);
                foreach (var q in u.ProductionQueue)
                {
                    h.PutSigned(q);
                }
                var shared = u.Def.Archetype.Service is not null; // a garage that services too (shares its doors)
                foreach (var b in u.Bays)
                {
                    h.PutSigned(b.Def);
                    h.PutSigned(b.Left);
                    h.PutSigned(b.Wait);
                    if (shared)
                    {
                        h.PutSigned(b.Door); // only such garages: games without them hash as before
                    }
                }
                foreach (var d in u.Doors)
                {
                    h.Put((ulong)d.State);
                    h.PutSigned(d.Left);
                    h.PutSigned(d.Def);
                    h.Put(d.Vehicle);
                    h.PutSigned(d.Wait);
                    if (shared)
                    {
                        h.PutByte((byte)d.Use);
                        h.Put(d.Serviced);
                        h.PutByte((byte)d.Needs);
                        h.PutSigned(d.ReloadLeft);
                        h.PutSigned(d.RepairLeft);
                        h.PutSigned(d.CargoLeft);
                        h.PutSigned(d.RepairFrom);
                    }
                }
                h.PutSigned(u.LastDoor);
                h.PutSigned(u.DoorSerial);
                h.PutSigned(u.Rally.X);
                h.PutSigned(u.Rally.Y);
                h.Put(u.RallyTarget);
            }
            if (u.OutOf != 0)
            {
                // Only a vehicle driving out of its factory: as before otherwise.
                h.Put(u.OutOf);
                h.PutSigned(u.OutVia.X);
                h.PutSigned(u.OutVia.Y);
            }
            if (u.ServiceAt != 0)
            {
                // Only a vehicle sent to a garage for service: games without one hash as before.
                h.Put(0x5E7);
                h.Put(u.ServiceAt);
                h.PutByte((byte)u.ServiceStage);
                h.Put(u.ServiceSince);
                h.PutSigned(u.ServiceDoor);
            }
            if (u.LoadLeft != 0 || u.LoadFrom != 0 || u.AirdropAt != 0)
            {
                h.Put(0x10C); // logistics: only a truck loading at a store, an airlifter on an Airdrop order
                h.PutSigned(u.LoadLeft);
                h.Put(u.LoadFrom);
                h.Put(u.AirdropAt);
            }
            if (u.AirliftBoard != 0 || u.Lift != 0)
            {
                h.Put(0xA11F7); // the airlift: only a vehicle going up an airlifter's ramp, an airlifter carrying or taking vehicles
                h.PutSigned(u.AirliftBoard);
                h.PutSigned(u.Lift);
            }
            if (u.DepotReloadStock != 0)
            {
                h.Put(0xD5); // only a unit with several depot-refilled stocks (the engineer's two station kits)
                h.PutSigned(u.DepotReloadStock);
            }
            if (u.HasNextGround || u.PlaceLeft != 0)
            {
                // Only drone launchers with a second point and engineers placing: as before otherwise.
                h.Put(u.HasNextGround ? 1UL : 0);
                h.PutSigned(u.NextGround.X);
                h.PutSigned(u.NextGround.Y);
                h.PutSigned(u.PlaceLeft);
            }
            if (u.Construction != ConstructionState.Complete || u.Work != 0 || u.Building)
            {
                // Only sites and builders: games without construction hash as before.
                h.Put((ulong)u.Construction);
                h.PutSigned(u.Work);
                h.Put(u.Building ? 1UL : 0);
            }
            if (u.AimAt != 0 || u.AimLeft != 0 || u.Guiding != 0)
            {
                // Only units aiming or guiding: the hash of games without them is unchanged.
                h.Put(u.AimAt);
                h.PutSigned(u.AimLeft);
                h.Put(u.Guiding);
            }
            if (u.Stance != Stance.Guard || u.LastHitBy != 0)
            {
                // Only units out of Guard (the default) or hurt by an enemy.
                h.Put(0x57A7CE);
                h.PutByte((byte)u.Stance);
                h.Put(u.LastHitBy);
                h.Put(u.LastHitTick);
                h.PutSigned(u.LastHitFrom.X.Raw);
                h.PutSigned(u.LastHitFrom.Y.Raw);
            }
            if (u.Ai != AiState.None || u.RetaliateAt != 0 || u.MoveOn)
            {
                // Only units that chased, called for help or stopped on an attack-move (the unit AI).
                h.Put(0xA1E);
                h.Put(u.MoveOn ? 1UL : 0);
                h.PutByte((byte)u.Ai);
                h.Put(u.AiTarget);
                h.PutSigned(u.GuardPoint.X.Raw);
                h.PutSigned(u.GuardPoint.Y.Raw);
                h.Put(u.GiveUpTick);
                h.PutSigned(u.SeekPoint.X.Raw);
                h.PutSigned(u.SeekPoint.Y.Raw);
                h.Put(u.RetaliateAt);
            }
            if (u.AirTarget != 0 || u.TrackTicks != 0)
            {
                // Only air defence tracking a target.
                h.Put(0xA1A1);
                h.Put(u.AirTarget);
                h.PutSigned(u.TrackTicks);
            }
            if (u.Fuse != 0 || u.AutoOff || u.Detonated)
            {
                // Only traps lit, set to on-demand or gone off: games without them hash as before.
                h.Put(0x7A9);
                h.PutSigned(u.Fuse);
                h.Put(u.AutoOff ? 1UL : 0);
                h.Put(u.Detonated ? 1UL : 0);
            }
            if (u.Tunnel != 0 || u.TunnelTo != 0 || u.EmergeFrom != 0 || u.EmergeLeft != 0 || u.TunnelSince != 0)
            {
                // Only units in or coming out of a tunnel: games without tunnels hash as before.
                h.Put(0x7E1);
                h.Put(u.Tunnel);
                h.Put(u.TunnelNext);
                h.PutSigned(u.TunnelLeft);
                h.PutSigned(u.TunnelLeg);
                h.Put(u.TunnelTo);
                h.Put(u.TunnelOut ? 1UL : 0);
                h.Put(u.TunnelSince);
                h.Put(u.EmergeFrom);
                h.PutSigned(u.EmergeLeft);
            }
            if (u.Def.Archetype.Tunnel is not null)
            {
                // Only tunnel entrances.
                h.Put(0x7E2);
                h.Put(u.Rubble ? 1UL : 0);
                h.PutSigned(u.Variant);
                h.PutSigned(u.Gate);
                h.PutSigned(u.ExposeLeft);
            }
            if (u.Def.Archetype.Flight is not null)
            {
                // Only scout drones: games without them hash as before.
                h.Put(0x5C0);
                h.PutSigned(u.Altitude.Raw);
                h.PutSigned(u.VelZ.Raw);
                h.PutSigned(u.OrbitAt.X.Raw);
                h.PutSigned(u.OrbitAt.Y.Raw);
                h.Put(u.Homing ? 1UL : 0);
                h.Put(u.Recovered ? 1UL : 0);
                if (u.Homing)
                {
                    h.PutByte((byte)u.HomeReason); // why it is homing (refuel, rearm, recall)
                }
                u.AirMission?.HashInto(ref h); // only aircraft on a mission (E3): plain orbits hash as before
                u.Wing?.HashInto(ref h); // only fixed wings in free flight (E4): drones hash as before
                u.Strike?.HashInto(ref h); // only aircraft on an air-to-ground attack (E7)
                u.Gunship?.HashInto(ref h); // only a gunship on its orbit attack (H3)
                u.Heli?.HashInto(ref h); // only helicopters
                u.Kaman?.HashInto(ref h); // only Kaman drones
                if (u.FireMode != 0 || u.Aims.Count > 0)
                {
                    // Only aircraft with a fire mode set or a queue of aims (H2).
                    h.Put(0xA1A5);
                    h.PutSigned(u.FireMode);
                    h.PutSigned(u.Aims.Count);
                    foreach (var a in u.Aims)
                    {
                        h.Put(a.Target);
                        h.PutSigned(a.Cell.X);
                        h.PutSigned(a.Cell.Y);
                        h.PutSigned(a.End.X);
                        h.PutSigned(a.End.Y);
                        h.Put(a.Line ? 1UL : 0);
                    }
                }
                if (u.BombWeapon != 0 || u.OtherModes != 0)
                {
                    h.Put(0xB0B5); // only an aircraft with several bomb weapons, another selected or another's mode set (the stealth bomber)
                    h.PutSigned(u.BombWeapon);
                    h.PutSigned(u.OtherModes);
                }
                if (u.Loadout != 0 || u.LoadoutLeft != 0)
                {
                    h.Put(0x10AD0); // only an aircraft with loadout modes out of its first or changing mode (the stealth multirole)
                    h.PutSigned(u.Loadout);
                    h.PutSigned(u.LoadoutTo);
                    h.PutSigned(u.LoadoutLeft);
                }
                u.Wingman?.HashInto(ref h); // only wingmen in an element (E13)
                if (u.SeekTicks != 0)
                {
                    h.Put(0x5EE1);
                    h.PutSigned(u.SeekTicks); // only an aircraft seeking an unseen target (E13)
                }
                if (u.Salvos.Count > 0)
                {
                    h.Put(0xC0F);
                    foreach (var n in u.Salvos)
                    {
                        h.PutSigned(n); // only aircraft with dispensers (E6)
                    }
                }
                if (u.KilledBy != KillCause.None)
                {
                    h.PutByte((byte)u.KilledBy); // only an aircraft brought down this tick (E6)
                }
            }
            if (u.AirOps is { } ops)
            {
                // Only aircraft at an airfield (holding, landing, on the ground, climbing out).
                h.Put(0xA10);
                h.PutByte((byte)ops.Phase);
                h.Put(ops.Airfield);
                h.Put(ops.Station);
                h.PutSigned(ops.Speed);
                h.PutSigned(ops.Pace);
                h.PutSigned(ops.Leg);
                h.PutSigned(ops.Side);
                h.PutSigned(ops.Wait);
                h.Put(ops.Since);
                h.PutSigned(ops.ReturnCell.X);
                h.PutSigned(ops.ReturnCell.Y);
                h.Put(ops.Hold ? 1UL : 0);
                h.Put(ops.Launch ? 1UL : 0);
                h.PutSigned(ops.FuelAcc);
                h.PutSigned(ops.AmmoAcc);
                h.PutSigned(ops.HpAcc);
                h.PutSigned(ops.RwWest);
                h.PutSigned(ops.RwEast);
                h.Put(ops.Recall ? 1UL : 0);
                if (ops.Recall)
                {
                    h.PutByte((byte)ops.RecallReason);
                }
                if (ops.Afterburner)
                {
                    h.Put(0xAB); // only an afterburner takeoff (E9)
                }
                if (ops.RecallTo != 0 || ops.UnloadLeft != 0 || ops.FuelCostAcc != 0 || ops.HpCostAcc != 0)
                {
                    h.Put(0x10A); // logistics: only a recall to a given airfield, an airlifter unloading, a service paid from a stock
                    h.Put(ops.RecallTo);
                    h.PutSigned(ops.UnloadLeft);
                    h.PutSigned(ops.FuelCostAcc);
                    h.PutSigned(ops.HpCostAcc);
                }
                h.PutSigned(ops.PathIdx);
                h.PutSigned(ops.MarkA);
                h.PutSigned(ops.MarkB);
                h.PutSigned(ops.MarkMouth);
                h.PutSigned(ops.PathPts.Count);
                for (var pi = 0; pi < ops.PathPts.Count; pi++)
                {
                    h.PutSigned(ops.PathPts[pi].X.Raw);
                    h.PutSigned(ops.PathPts[pi].Y.Raw);
                    h.Put(ops.PathTurn[pi] ? 1UL : 0);
                }
            }
            if (u.Airfield != 0 || u.BuildTotal != 0)
            {
                // Only airfield pieces: games without airfields hash as before.
                h.Put(0xA1F);
                h.Put(u.Airfield);
                h.PutSigned(u.BuildTotal);
            }
            HashRepairState(ref h, u); // rubble and repair (only when set)
            u.Bridge?.HashInto(ref h); // only bridges: their span down, being repaired
            if (u.PushContact)
            {
                h.Put(0x9054); // only a pusher pressing on a tree trunk (TreeSystem.cs)
            }
            if (u.Turned)
            {
                h.Put(0x7D2); // only structures standing turned a quarter: games without them hash as before
            }
            if (u.Quarter != 0)
            {
                h.Put(0x7D3); // only structures whose layout stands turned (built at a facing other than 0, spawned turned): games without them hash as before
                h.PutSigned(u.Quarter);
            }
            if (u.PadUnit != 0)
            {
                h.Put(0x9A7); // only a reload pad reloading (the Patriot-type launcher's): games without one hash as before
                h.Put(u.PadUnit);
                h.PutSigned(u.PadLeft);
            }
            if (u.HealReady != 0)
            {
                h.Put(0x4EA); // only a unit an aura has healed (the assault medic's): games without auras hash as before
                h.Put(u.HealReady);
            }
            HashCapShots(ref h, u.CapShots); // only a flying unit a capped interceptor shot at (maxPerTarget)
            if (u.RelaunchPending)
            {
                // Only a launcher waiting to send its strike drone up again (the Watcher's station).
                h.Put(0x5C1);
                h.PutSigned(u.RelaunchCell.X);
                h.PutSigned(u.RelaunchCell.Y);
                h.PutSigned(u.RelaunchArchetype);
            }
            if (u.KeptMission is { } km)
            {
                // Only a ground control station holding its recovered drone's mission for the relaunch.
                h.Put(0x5C3);
                km.HashInto(ref h);
            }
            if (u.KeptStocks is { } kept)
            {
                // Only a ground control station holding a recovered drone's stocks and HP for its next launch.
                h.Put(0x5C2);
                h.PutSigned(u.KeptArchetype);
                h.PutSigned(u.KeptHp);
                h.PutSigned(kept.Length);
                foreach (var k in kept)
                {
                    h.PutSigned(k);
                }
            }
        }
        if (_tunnelGround.Count > 0)
        {
            // The ground tunnel entrances reshaped, live and returning (only games with them).
            h.Put(0x7E11);
            h.PutSigned(_tunnelGround.Count);
            foreach (var r in _tunnelGround)
            {
                h.Put(r.Entrance);
                h.PutSigned(r.X0);
                h.PutSigned(r.Y0);
                h.PutSigned(r.W);
                h.PutSigned(r.H);
                h.PutSigned(r.PadX0);
                h.PutSigned(r.PadY0);
                h.PutSigned(r.PadW);
                h.PutSigned(r.PadH);
                h.PutSigned(r.Step);
                h.PutSigned(r.Steps);
                h.PutSigned(r.Wait);
                foreach (var v in r.Orig)
                {
                    h.PutSigned(v);
                }
                foreach (var v in r.Cut)
                {
                    h.PutSigned(v);
                }
            }
        }
        if (_airfields.Count > 0)
        {
            // The airfields (only games with them): their frame, pieces and stations.
            h.Put(0xA1F1);
            h.PutSigned(_airfields.Count);
            foreach (var af in _airfields)
            {
                h.Put(af.Id);
                h.Put(af.Owner);
                h.PutSigned(af.Type.Index);
                h.PutSigned(af.Origin.X);
                h.PutSigned(af.Origin.Y);
                h.PutSigned(af.Facing);
                h.PutSigned(af.PadRaw);
                h.Put(af.Facility);
                if (af.Type.HasCore)
                {
                    h.Put(0xA1F5); // a main air base's core, hangar and radar (H1; only for its type)
                    h.Put(af.Core);
                    h.Put(af.Hangar);
                    h.Put(af.Radar);
                }
                h.PutSigned(af.West);
                h.PutSigned(af.East);
                h.Put(af.LeadRow);
                h.PutSigned(af.SegmentList.Count);
                foreach (var s in af.SegmentList)
                {
                    h.Put(s);
                }
                h.PutSigned(af.RowList.Count);
                foreach (var r in af.RowList)
                {
                    h.PutSigned(r.Col);
                    h.PutSigned(r.Row);
                    h.Put(r.Unit);
                    foreach (var st in r.Stations)
                    {
                        h.PutByte((byte)st.Kind);
                        h.Put(st.Unit);
                        h.Put(st.Occupant);
                        h.Put(st.ReservedBy);
                    }
                }
                HashRunwayDamage(ref h, af); // craters and wear (only when present)
                if (af.RunwayLock != 0 || af.LandingList.Count > 0 || af.TakeoffList.Count > 0 || af.ApproachList.Count > 0)
                {
                    // Its traffic (only while there is any): the runway lock and the queues.
                    h.Put(0xA1F2);
                    h.Put(af.RunwayLock);
                    h.PutSigned(af.LandingList.Count);
                    foreach (var (tick, id) in af.LandingList)
                    {
                        h.Put(tick);
                        h.Put(id);
                    }
                    h.PutSigned(af.TakeoffList.Count);
                    foreach (var (tick, id) in af.TakeoffList)
                    {
                        h.Put(tick);
                        h.Put(id);
                    }
                    h.PutSigned(af.ApproachList.Count); // the approach sequence (2026-10-24)
                    foreach (var id in af.ApproachList)
                    {
                        h.Put(id);
                    }
                }
            }
        }
        // Routing: the request queue and the group moves (their flows' planned state; the
        // portal costs and fields are derived from the map, the goal and the hashed members).
        h.PutSigned(_requests.Count);
        foreach (var q in _requests)
        {
            h.Put(q.Tick);
            h.Put(q.Unit);
            h.Put(q.Group);
        }
        h.Put(_nextGroup);
        foreach (var g in _groups)
        {
            h.Put(g.Id);
            h.PutSigned(g.Goal.X);
            h.PutSigned(g.Goal.Y);
            h.Put(g.Queued ? 1UL : 0);
            h.PutSigned(g.Members.Count);
            foreach (var m in g.Members)
            {
                h.Put(m);
            }
            foreach (var f in g.Flows)
            {
                h.PutSigned(f.Goal);
                h.Put(f.Version is { } v && v == _router?.Version ? 1UL : 0);
            }
        }
        foreach (var p in _projectiles)
        {
            h.Put(p.Id);
            h.Put(p.Owner);
            h.Put(p.Shooter);
            h.PutSigned(p.ShooterDef.Index);
            h.PutSigned(p.Weapon.Index);
            h.PutSigned(p.Pos.X.Raw);
            h.PutSigned(p.Pos.Y.Raw);
            h.PutSigned(p.Dest.X.Raw);
            h.PutSigned(p.Dest.Y.Raw);
            h.Put(p.Target);
            h.Put(p.Cover);
            if (p.Interceptible != Model.InterceptClass.None)
            {
                h.Put((ulong)p.Interceptible); // only marked projectiles: older games hash as before
            }
            HashCapShots(ref h, p.CapShots); // only a target a capped interceptor shot at (maxPerTarget)
            if ((p.Weapon.Effect.ApsThreat || !p.Weapon.Effect.Lobbed) && LaunchMatters())
            {
                // Only in a match with an APS or directional armour (the tank): where it was launched, and the APS's look.
                h.PutSigned(p.Launched.X.Raw);
                h.PutSigned(p.Launched.Y.Raw);
                h.Put(p.ApsSeen ? 1UL : 0);
            }
            if (p.Phase != DronePhase.None)
            {
                h.Put((ulong)p.Phase);
                h.PutSigned(p.Altitude.Raw);
                h.PutSigned(p.DiveFrom.Raw);
                h.PutSigned(p.DiveDist.Raw);
                h.PutSigned(p.Age);
                h.PutSigned(p.DiveSlope);
                h.PutSigned(p.Vel.X.Raw); // its last step
                h.PutSigned(p.Vel.Y.Raw);
                h.PutSigned(p.VelZ.Raw);
            }
            if (p.Arcs || p.LowFlight)
            {
                // Only on terrain: flat games hash as before.
                h.PutSigned(p.Origin.X.Raw);
                h.PutSigned(p.Origin.Y.Raw);
                h.PutSigned(p.LaunchHeight.Raw);
                h.PutSigned(p.LandHeight.Raw);
                h.PutSigned(p.ArcRise.Raw);
            }
            if (p.Weapon.Effect.Guided is not null)
            {
                h.PutSigned(p.Vel.X.Raw);
                h.PutSigned(p.Vel.Y.Raw);
                h.Put(p.Guided ? 1UL : 0);
                h.PutSigned(p.Flown.Raw);
                h.PutSigned(p.DropLeft.Raw);
            }
            if (p.Weapon.Effect.Interceptor is not null)
            {
                // Only air-defence missiles: their 3D flight and the engagement they carry.
                h.PutSigned(p.Altitude.Raw);
                h.PutSigned(p.VelZ.Raw);
                h.PutSigned(p.LaunchRange.Raw);
                h.PutSigned(p.LaunchCross);
                h.PutSigned(p.Tracked);
                if (p.Decoys != 0)
                {
                    h.PutSigned(p.Decoys); // only missiles decoyed (E6)
                    h.PutSigned(p.DecoyPercent);
                }
            }
            p.Bomb?.HashInto(ref h); // only bombs (E7): their fall
            if (p.Weapon.Effect.AirStrike is not null)
            {
                // Only air strike missiles: their 3D flight and the roll they carry.
                h.PutSigned(p.Altitude.Raw);
                h.PutSigned(p.Vel.X.Raw);
                h.PutSigned(p.Vel.Y.Raw);
                h.PutSigned(p.VelZ.Raw);
                h.PutSigned(p.StrikeAim.X.Raw);
                h.PutSigned(p.StrikeAim.Y.Raw);
                h.Put(p.StrikeHit ? 1UL : 0);
                h.Put(p.StrikeMoving ? 1UL : 0);
            }
        }
        if (_wrecks.Count > 0 || _chutes.Count > 0)
        {
            // Only while something falls (E10): games without losses hash as before.
            h.Put(0x10557);
            foreach (var k in _wrecks)
            {
                k.HashInto(ref h);
            }
            h.Put(0x0C47E);
            foreach (var c in _chutes)
            {
                c.HashInto(ref h);
            }
        }
        HashSmoke(ref h); // only once a smoke screen was popped
        HashTrees(ref h); // only once a tree fell or a push began (TreeSystem.cs)
        if (_craters.Count > 0)
        {
            // Only while a deep crater lies on the map (a bunker buster's): games without one hash as before.
            h.Put(0xC4A7E);
            foreach (var c in _craters)
            {
                h.Put(c.Bomb);
                h.PutSigned(c.At.X.Raw);
                h.PutSigned(c.At.Y.Raw);
                h.PutSigned(c.RadiusRaw);
                h.PutByte((byte)c.Kind);
                h.Put(c.Since);
                h.Put(c.Until);
                h.Put(c.Owner);
            }
        }
        if (_radarSpots.Count > 0)
        {
            // Only while a radar is revealed by radar spotting (the stealth multirole): games without one hash as before.
            h.Put(0x5907);
            foreach (var s in _radarSpots)
            {
                h.Put(s.Radar);
                h.PutByte(s.Player);
                h.Put(s.Spotter);
                h.Put(s.Since);
                h.Put(s.Until);
                h.Put(s.Inside ? 1UL : 0);
                h.PutSigned(s.Radius.Raw);
            }
        }
        return h.Sum;
    }
}

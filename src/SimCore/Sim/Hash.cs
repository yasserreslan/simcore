namespace SimCore.Sim;

public sealed partial class World
{
    /// <summary>
    /// Returns a 64-bit digest of the whole game state, including the
    /// match setup's digest. Two machines with equal hashes on a tick have
    /// identical worlds; a mismatch is a desync. Pending commands are
    /// excluded: peers may not have received the same future commands yet.
    /// </summary>
    /// <remarks>
    /// Every value is mixed in as one 64-bit word (signed values sign-extended
    /// first) by <see cref="WordHash"/>. The map's cells go in as one cached
    /// digest (<see cref="Grid.Digest"/>), recomputed only after a cell changes.
    /// Words are batched four at a time (<see cref="WordHash.Put4"/>) in the same order as one by one;
    /// <see cref="HashReference"/> keeps the one-by-one original for the equivalence tests, so a new
    /// hashed value goes into both.
    /// </remarks>
    public ulong Hash()
    {
        var h = new WordHash();
        h.Put4(
            unchecked((ulong)(Match.Digest)),
            unchecked((ulong)(Tick)),
            unchecked((ulong)(_rng.State)),
            unchecked((ulong)(_nextId)));
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
            if (p.Upgrades != 0)
            {
                h.Put(0x09A1); // only a player with upgrades (UpgradeSystem.cs)
                h.Put(p.Upgrades);
            }
        }
        h.Put(Grid.Digest()); // every cell
        if (Ground is { } terrain)
        {
            h.Put(terrain.Digest()); // only maps with terrain: flat maps hash as before
        }
        foreach (var u in _units)
        {
            h = HashUnitPrefix(h, u, _players.Count);
            h = HashUnitRest(h, u);
        }
        if (_tunnelGround.Count > 0)
        {
            // The ground tunnel entrances reshaped, live and returning (only games with them).
            h.Put(0x7E11);
            h.PutSigned(_tunnelGround.Count);
            foreach (var r in _tunnelGround)
            {
                h.Put4(
                    unchecked((ulong)(r.Entrance)),
                    unchecked((ulong)(r.X0)),
                    unchecked((ulong)(r.Y0)),
                    unchecked((ulong)(r.W)));
                h.Put4(
                    unchecked((ulong)(r.H)),
                    unchecked((ulong)(r.PadX0)),
                    unchecked((ulong)(r.PadY0)),
                    unchecked((ulong)(r.PadW)));
                h.Put4(
                    unchecked((ulong)(r.PadH)),
                    unchecked((ulong)(r.Step)),
                    unchecked((ulong)(r.Steps)),
                    unchecked((ulong)(r.Wait)));
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
                h.Put4(
                    unchecked((ulong)(af.Id)),
                    unchecked((ulong)(af.Owner)),
                    unchecked((ulong)(af.Type.Index)),
                    unchecked((ulong)(af.Origin.X)));
                h.Put4(
                    unchecked((ulong)(af.Origin.Y)),
                    unchecked((ulong)(af.Facing)),
                    unchecked((ulong)(af.PadRaw)),
                    unchecked((ulong)(af.Facility)));
                if (af.Type.HasCore)
                {
                    h.Put(0xA1F5); // a main air base's core, hangar and radar (H1; only for its type)
                    h.Put(af.Core);
                    h.Put(af.Hangar);
                    h.Put(af.Radar);
                }
                h.Put4(
                    unchecked((ulong)(af.West)),
                    unchecked((ulong)(af.East)),
                    unchecked((ulong)(af.LeadRow)),
                    unchecked((ulong)(af.SegmentList.Count)));
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
                        h.Put4(
                            unchecked((ulong)((byte)st.Kind)),
                            unchecked((ulong)(st.Unit)),
                            unchecked((ulong)(st.Occupant)),
                            unchecked((ulong)(st.ReservedBy)));
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
            h.Put4(
                unchecked((ulong)(g.Id)),
                unchecked((ulong)(g.Goal.X)),
                unchecked((ulong)(g.Goal.Y)),
                unchecked((ulong)(g.Queued ? 1UL : 0)));
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
            h.Put4(
                unchecked((ulong)(p.Id)),
                unchecked((ulong)(p.Owner)),
                unchecked((ulong)(p.Shooter)),
                unchecked((ulong)(p.ShooterDef.Index)));
            h.Put4(
                unchecked((ulong)(p.Weapon.Index)),
                unchecked((ulong)(p.Pos.X.Raw)),
                unchecked((ulong)(p.Pos.Y.Raw)),
                unchecked((ulong)(p.Dest.X.Raw)));
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
                h.Put4(
                    unchecked((ulong)((ulong)p.Phase)),
                    unchecked((ulong)(p.Altitude.Raw)),
                    unchecked((ulong)(p.DiveFrom.Raw)),
                    unchecked((ulong)(p.DiveDist.Raw)));
                h.PutSigned(p.Age);
                h.PutSigned(p.DiveSlope);
                h.PutSigned(p.Vel.X.Raw); // its last step
                h.PutSigned(p.Vel.Y.Raw);
                h.PutSigned(p.VelZ.Raw);
            }
            if (p.Arcs || p.LowFlight)
            {
                // Only on terrain: flat games hash as before.
                h.Put4(
                    unchecked((ulong)(p.Origin.X.Raw)),
                    unchecked((ulong)(p.Origin.Y.Raw)),
                    unchecked((ulong)(p.LaunchHeight.Raw)),
                    unchecked((ulong)(p.LandHeight.Raw)));
                h.PutSigned(p.ArcRise.Raw);
            }
            if (p.Weapon.Effect.Terminal is not null)
            {
                // Only a missile with a terminal profile (TerminalSystem.cs): its age is its whole flight.
                h.Put(0x7E5);
                h.PutSigned(p.Age);
                h.PutSigned(p.WeaveSeed);
                h.PutSigned(p.Altitude.Raw);
            }
            if (p.Cruise is { } cr)
            {
                // Only a cruise missile (CruiseSystem.cs): its age on its planned flight (the digest of its route and plan), its
                // height, who saw it and the warnings.
                h.Put(0xC2015E);
                h.PutSigned(p.Age);
                h.PutSigned(p.Altitude.Raw);
                h.Put(cr.Digest);
                h.Put(cr.Seen);
                h.Put(cr.SeenNow);
                h.PutSigned(cr.Warn.Count);
                foreach (var (pl, until) in cr.Warn)
                {
                    h.Put(pl);
                    h.Put(until);
                }
            }
            if (p.Weapon.Effect.Guided is not null)
            {
                h.Put4(
                    unchecked((ulong)(p.Vel.X.Raw)),
                    unchecked((ulong)(p.Vel.Y.Raw)),
                    unchecked((ulong)(p.Guided ? 1UL : 0)),
                    unchecked((ulong)(p.Flown.Raw)));
                h.PutSigned(p.DropLeft.Raw);
            }
            if (p.Weapon.Effect.Interceptor is not null)
            {
                // Only air-defence missiles: their 3D flight and the engagement they carry.
                h.Put4(
                    unchecked((ulong)(p.Altitude.Raw)),
                    unchecked((ulong)(p.VelZ.Raw)),
                    unchecked((ulong)(p.LaunchRange.Raw)),
                    unchecked((ulong)(p.LaunchCross)));
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
                h.Put4(
                    unchecked((ulong)(p.Altitude.Raw)),
                    unchecked((ulong)(p.Vel.X.Raw)),
                    unchecked((ulong)(p.Vel.Y.Raw)),
                    unchecked((ulong)(p.VelZ.Raw)));
                h.Put4(
                    unchecked((ulong)(p.StrikeAim.X.Raw)),
                    unchecked((ulong)(p.StrikeAim.Y.Raw)),
                    unchecked((ulong)(p.StrikeHit ? 1UL : 0)),
                    unchecked((ulong)(p.StrikeMoving ? 1UL : 0)));
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
        HashFire(ref h); // only once a cell burnt or a blast thinned one (FireSystem.cs)
        if (_craters.Count > 0)
        {
            // Only while a deep crater lies on the map (a bunker buster's): games without one hash as before.
            h.Put(0xC4A7E);
            foreach (var c in _craters)
            {
                h.Put4(
                    unchecked((ulong)(c.Bomb)),
                    unchecked((ulong)(c.At.X.Raw)),
                    unchecked((ulong)(c.At.Y.Raw)),
                    unchecked((ulong)(c.RadiusRaw)));
                h.Put4(
                    unchecked((ulong)((byte)c.Kind)),
                    unchecked((ulong)(c.Since)),
                    unchecked((ulong)(c.Until)),
                    unchecked((ulong)(c.Owner)));
            }
        }
        if (_radarSpots.Count > 0)
        {
            // Only while a radar is revealed by radar spotting (the stealth multirole): games without one hash as before.
            h.Put(0x5907);
            foreach (var s in _radarSpots)
            {
                h.Put4(
                    unchecked((ulong)(s.Radar)),
                    unchecked((ulong)(s.Player)),
                    unchecked((ulong)(s.Spotter)),
                    unchecked((ulong)(s.Since)));
                h.Put(s.Until);
                h.Put(s.Inside ? 1UL : 0);
                h.PutSigned(s.Radius.Raw);
            }
        }
        if (_nextSquad != 0)
        {
            // Only once a squad was formed (SquadSystem.cs): games without squads hash as before.
            h.Put(0x5A0D);
            h.Put(_nextSquad);
            h.PutSigned(_squads.Count);
            foreach (var s in _squads)
            {
                h.Put4(
                    unchecked((ulong)(s.Id)),
                    unchecked((ulong)(s.Owner)),
                    unchecked((ulong)(s.Leader)),
                    unchecked((ulong)((byte)s.Mode)));
                h.Put4(
                    unchecked((ulong)((byte)s.Stance)),
                    unchecked((ulong)((byte)s.Order)),
                    unchecked((ulong)(s.Goal.X)),
                    unchecked((ulong)(s.Goal.Y)));
                h.Put4(
                    unchecked((ulong)(s.Anchor.X.Raw)),
                    unchecked((ulong)(s.Anchor.Y.Raw)),
                    unchecked((ulong)(s.Facing.X.Raw)),
                    unchecked((ulong)(s.Facing.Y.Raw)));
                h.Put4(
                    unchecked((ulong)(s.PathIdx)),
                    unchecked((ulong)(s.RouteIdx)),
                    s.PathPending ? 1UL : 0,
                    unchecked((ulong)(s.Waited)));
                h.Put(s.Loose ? 1UL : 0);
                h.PutSigned(s.MemberList.Count);
                foreach (var m in s.MemberList)
                {
                    h.Put(m);
                }
                h.PutSigned(s.PathCells.Length);
                foreach (var c in s.PathCells)
                {
                    h.PutSigned(c.X);
                    h.PutSigned(c.Y);
                }
                h.PutSigned(s.Route.Length);
                foreach (var c in s.Route)
                {
                    h.PutSigned(c);
                }
                h.PutSigned(s.Trail.Count);
                foreach (var p in s.Trail)
                {
                    h.PutSigned(p.X.Raw);
                    h.PutSigned(p.Y.Raw);
                }
                // Hold (phase S2): the spot, the facing and the threat, whether placed or waiting, and the picks.
                h.Put((s.HoldPlaced ? 1UL : 0) | (s.HoldPending ? 2UL : 0) | (s.HoldFaced ? 4UL : 0));
                h.PutSigned(s.HoldSpot.X);
                h.PutSigned(s.HoldSpot.Y);
                h.PutSigned(s.HoldFacing.X.Raw);
                h.PutSigned(s.HoldFacing.Y.Raw);
                h.PutSigned(s.HoldThreat.X.Raw);
                h.PutSigned(s.HoldThreat.Y.Raw);
                h.PutSigned(s.PickList.Count);
                foreach (var k in s.PickList)
                {
                    h.Put(k.Unit);
                    h.PutSigned(k.Cell.X);
                    h.PutSigned(k.Cell.Y);
                    h.Put(k.Building);
                }
                if (s.Mode == SquadMode.Ambush || s.AmbushPhase != SquadState.Idle || s.TaskList.Count > 0 || s.MineList.Count > 0)
                {
                    // Ambush (phase S3), only once used: the zone, the side and its spot, the phase, the triggers, the times, the
                    // mine tasks and the mines laid.
                    h.Put(0xA3B5);
                    h.PutSigned(s.AmbushStart.X);
                    h.PutSigned(s.AmbushStart.Y);
                    h.PutSigned(s.AmbushEnd.X);
                    h.PutSigned(s.AmbushEnd.Y);
                    h.PutSigned(s.AmbushSide);
                    h.PutSigned(s.AmbushSpot.X);
                    h.PutSigned(s.AmbushSpot.Y);
                    h.Put((ulong)s.AmbushPhase | ((ulong)s.AmbushTrigger << 8) | ((ulong)s.SpringPending << 16));
                    h.Put(s.AmbushSince);
                    h.PutSigned(s.AmbushCalm);
                    h.PutSigned(s.TaskList.Count);
                    foreach (var t in s.TaskList)
                    {
                        h.Put(t.Unit);
                        h.PutSigned(t.Cell.X);
                        h.PutSigned(t.Cell.Y);
                        h.Put((ulong)t.Kind | ((ulong)t.State << 8));
                        h.PutSigned(t.Ability);
                    }
                    h.PutSigned(s.MineList.Count);
                    foreach (var m in s.MineList)
                    {
                        h.Put(m);
                    }
                    h.Put(s.AmbushRightDone ? 1UL : 0);
                    h.PutSigned(s.AmbushRightScore);
                    h.PutSigned(s.AltPicks.Count);
                    foreach (var k in s.AltPicks)
                    {
                        h.Put(k.Unit);
                        h.PutSigned(k.Cell.X);
                        h.PutSigned(k.Cell.Y);
                        h.Put(k.Building);
                    }
                    if (s.AmbushAir || s.VolleyTarget != 0 || s.VolleySince != 0)
                    {
                        // The air ambush (phase S4), only once used: the kind, the volley's target and wait.
                        h.Put(0xA1A5);
                        h.Put(s.AmbushAir ? 1UL : 0);
                        h.Put(s.VolleyTarget);
                        h.Put(s.VolleySince);
                    }
                }
            }
        }
        return h.Sum;
    }

    // A small hot loop gives the JIT room to inline the streaming hasher and property reads.
    // Pass the eight-byte accumulator by value so its serial dependency can stay in a register.
    private static WordHash HashUnitPrefix(WordHash h, Unit u, int players)
    {
        h.Put4(
            unchecked((ulong)(u.Id)),
            unchecked((ulong)(u.Def.Index)),
            unchecked((ulong)(u.Pos.X.Raw)),
            unchecked((ulong)(u.Pos.Y.Raw)));
        h.Put4(
            unchecked((ulong)(u.Facing)),
            unchecked((ulong)(u.TurretFacing)),
            unchecked((ulong)(u.Vel.X.Raw)),
            unchecked((ulong)(u.Vel.Y.Raw)));
        h.Put4(
            unchecked((ulong)(u.Heading.X.Raw)),
            unchecked((ulong)(u.Heading.Y.Raw)),
            unchecked((ulong)(u.DriveSpeed.Raw)),
            unchecked((ulong)(u.StallGoal.X.Raw)));
        h.Put4(
            unchecked((ulong)(u.StallGoal.Y.Raw)),
            unchecked((ulong)(u.StallBest.Raw)),
            unchecked((ulong)(u.StallTicks)),
            unchecked((ulong)(u.DetourRetryAt)));
        h.Put4(
            unchecked((ulong)(u.DetourEnd)),
            unchecked((ulong)(u.HP)),
            unchecked((ulong)(u.Reload)),
            unchecked((ulong)((ulong)u.Order.Kind)));
        h.Put4(
            unchecked((ulong)(u.Order.Cell.X)),
            unchecked((ulong)(u.Order.Cell.Y)),
            unchecked((ulong)(u.Order.Target)),
            unchecked((ulong)(u.Order.Ability)));
        h.Put4(
            unchecked((ulong)(u.AutoTarget)),
            unchecked((ulong)((byte)u.WeaponMode)),
            unchecked((ulong)((byte)u.Deploy)),
            unchecked((ulong)(u.DeployLeft)));
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
        var path = u.HashPath;
        h.PutSigned(path.Length);
        foreach (var cell in path)
        {
            h.PutSigned(cell.X);
            h.PutSigned(cell.Y);
        }
        h.Put4(
            unchecked((ulong)(u.Inside)),
            unchecked((ulong)(u.Driver)),
            unchecked((ulong)(u.Owner)),
            unchecked((ulong)(u.Garrison)));
        h.Put4(
            unchecked((ulong)(u.RangeBonus.Raw)),
            unchecked((ulong)(u.VisionBonus.Raw)),
            unchecked((ulong)(u.Cover.W)),
            unchecked((ulong)(u.Cover.H)));
        h.PutSigned(u.Stage);
        h.PutSigned(u.Occupants);
        var charges = u.MutableCharges;
        h.PutSigned(charges.Length);
        foreach (var charge in charges) { h.PutSigned(charge); }
        h.Put4(
            unchecked((ulong)(u.Cooldown)),
            unchecked((ulong)(u.SalvoLeft)),
            unchecked((ulong)(u.SalvoWeapon)),
            unchecked((ulong)(u.SalvoAim.X.Raw)));
        h.PutSigned(u.SalvoAim.Y.Raw);
        h.PutSigned(u.SalvoWait);
        h.Put(u.SpottedBy);
        for (var pi = 0; pi <= players; pi++)
        {
            h.Put(u.SpotUntil[pi]);
        }
        h.Put4(
            unchecked((ulong)(u.VisionCell.X)),
            unchecked((ulong)(u.VisionCell.Y)),
            unchecked((ulong)(u.LookedAt)),
            unchecked((ulong)(u.RevealedTo)));
        h.Put4(
            unchecked((ulong)(u.Source)),
            unchecked((ulong)(u.Lifetime)),
            unchecked((ulong)(u.Stock)),
            unchecked((ulong)(u.Cargo)));
        h.PutSigned(u.UnloadLeft);
        h.Put(u.UnloadDepot);
        h.Put(u.DepotStarved ? 1UL : 0);
        return h;
    }

    private static WordHash HashUnitRest(WordHash h, Unit u)
    {
        if (u.Def.Smoke is not null || u.Def.Crew is { UnloadInterval: > 0 })
        {
            // Only units with smoke launchers or a one-by-one unload (the Humvee): games without them hash as before.
            h.Put4(
                unchecked((ulong)(0x4D7EE)),
                unchecked((ulong)(u.DismountWait)),
                unchecked((ulong)(u.Dismounted)),
                unchecked((ulong)(u.SmokeReady)));
        }
        if (u.FeedTo != 0 || u.FeedLeft != 0 || u.RevealUntil != 0 || u.GroundQueue.Count > 0)
        {
            // Only a truck handing a howitzer its shells, a unit whose shots flashed, a Shift queue of ground points: games
            // without them hash as before.
            h.Put4(
                unchecked((ulong)(0x155)),
                unchecked((ulong)(u.FeedTo)),
                unchecked((ulong)(u.FeedLeft)),
                unchecked((ulong)(u.RevealUntil)));
            h.PutSigned(u.GroundQueue.Count);
            foreach (var q in u.GroundQueue)
            {
                h.PutSigned(q.X);
                h.PutSigned(q.Y);
            }
        }
        if (u.CruiseRoutes.Count > 0)
        {
            HashCruiseRoutes(ref h, u.CruiseRoutes); // only a cruise truck with routes to fly: games without one hash as before
        }
        if (u.Def.Aps is not null || u.Def.HasCoaxial)
        {
            // Only units with an active protection system or a coaxial gun (the tank): games without them hash as before.
            h.Put4(
                unchecked((ulong)(0xA95)),
                unchecked((ulong)(u.ApsReady)),
                unchecked((ulong)(u.CoaxReload)),
                unchecked((ulong)(u.CoaxShot)));
        }
        h.Put4(
            unchecked((ulong)(u.ReloadBonus)),
            unchecked((ulong)(u.Intent.Goal.X.Raw)),
            unchecked((ulong)(u.Intent.Goal.Y.Raw)),
            unchecked((ulong)(u.Intent.Desired.X.Raw)));
        h.Put4(
            unchecked((ulong)(u.Intent.Desired.Y.Raw)),
            unchecked((ulong)(u.Intent.Slot.X.Raw)),
            unchecked((ulong)(u.Intent.Slot.Y.Raw)),
            unchecked((ulong)(u.Intent.Arrived ? 1UL : 0)));
        h.Put4(
            unchecked((ulong)(u.Intent.Stuck ? 1UL : 0)),
            unchecked((ulong)(u.Group)),
            unchecked((ulong)(u.Dest.X)),
            unchecked((ulong)(u.Dest.Y)));
        h.Put4(
            unchecked((ulong)((byte)u.Mode)),
            unchecked((ulong)(u.PathPending ? 1UL : 0)),
            unchecked((ulong)(u.SlotOffset.X.Raw)),
            unchecked((ulong)(u.SlotOffset.Y.Raw)));
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
                h.Put4(
                    unchecked((ulong)((ulong)d.State)),
                    unchecked((ulong)(d.Left)),
                    unchecked((ulong)(d.Def)),
                    unchecked((ulong)(d.Vehicle)));
                h.PutSigned(d.Wait);
                if (shared)
                {
                    h.Put4(
                        unchecked((ulong)((byte)d.Use)),
                        unchecked((ulong)(d.Serviced)),
                        unchecked((ulong)((byte)d.Needs)),
                        unchecked((ulong)(d.ReloadLeft)));
                    h.PutSigned(d.RepairLeft);
                    h.PutSigned(d.CargoLeft);
                    h.PutSigned(d.RepairFrom);
                }
            }
            h.Put4(
                unchecked((ulong)(u.LastDoor)),
                unchecked((ulong)(u.DoorSerial)),
                unchecked((ulong)(u.Rally.X)),
                unchecked((ulong)(u.Rally.Y)));
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
            h.Put4(
                unchecked((ulong)(0x5E7)),
                unchecked((ulong)(u.ServiceAt)),
                unchecked((ulong)((byte)u.ServiceStage)),
                unchecked((ulong)(u.ServiceSince)));
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
            h.Put4(
                unchecked((ulong)(u.HasNextGround ? 1UL : 0)),
                unchecked((ulong)(u.NextGround.X)),
                unchecked((ulong)(u.NextGround.Y)),
                unchecked((ulong)(u.PlaceLeft)));
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
            h.Put4(
                unchecked((ulong)(0x57A7CE)),
                unchecked((ulong)((byte)u.Stance)),
                unchecked((ulong)(u.LastHitBy)),
                unchecked((ulong)(u.LastHitTick)));
            h.PutSigned(u.LastHitFrom.X.Raw);
            h.PutSigned(u.LastHitFrom.Y.Raw);
        }
        if (u.Ai != AiState.None || u.RetaliateAt != 0 || u.MoveOn)
        {
            // Only units that chased, called for help or stopped on an attack-move (the unit AI).
            h.Put4(
                unchecked((ulong)(0xA1E)),
                unchecked((ulong)(u.MoveOn ? 1UL : 0)),
                unchecked((ulong)((byte)u.Ai)),
                unchecked((ulong)(u.AiTarget)));
            h.Put4(
                unchecked((ulong)(u.GuardPoint.X.Raw)),
                unchecked((ulong)(u.GuardPoint.Y.Raw)),
                unchecked((ulong)(u.GiveUpTick)),
                unchecked((ulong)(u.SeekPoint.X.Raw)));
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
            h.Put4(
                unchecked((ulong)(0x7A9)),
                unchecked((ulong)(u.Fuse)),
                unchecked((ulong)(u.AutoOff ? 1UL : 0)),
                unchecked((ulong)(u.Detonated ? 1UL : 0)));
        }
        if (u.Tunnel != 0 || u.TunnelTo != 0 || u.EmergeFrom != 0 || u.EmergeLeft != 0 || u.TunnelSince != 0)
        {
            // Only units in or coming out of a tunnel: games without tunnels hash as before.
            h.Put4(
                unchecked((ulong)(0x7E1)),
                unchecked((ulong)(u.Tunnel)),
                unchecked((ulong)(u.TunnelNext)),
                unchecked((ulong)(u.TunnelLeft)));
            h.Put4(
                unchecked((ulong)(u.TunnelLeg)),
                unchecked((ulong)(u.TunnelTo)),
                unchecked((ulong)(u.TunnelOut ? 1UL : 0)),
                unchecked((ulong)(u.TunnelSince)));
            h.Put(u.EmergeFrom);
            h.PutSigned(u.EmergeLeft);
        }
        if (u.Def.Archetype.Tunnel is not null)
        {
            // Only tunnel entrances.
            h.Put4(
                unchecked((ulong)(0x7E2)),
                unchecked((ulong)(u.Rubble ? 1UL : 0)),
                unchecked((ulong)(u.Variant)),
                unchecked((ulong)(u.Gate)));
            h.PutSigned(u.ExposeLeft);
        }
        if (u.Def.Archetype.Flight is not null)
        {
            // Only scout drones: games without them hash as before.
            h.Put4(
                unchecked((ulong)(0x5C0)),
                unchecked((ulong)(u.Altitude.Raw)),
                unchecked((ulong)(u.VelZ.Raw)),
                unchecked((ulong)(u.OrbitAt.X.Raw)));
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
                    h.Put4(
                        unchecked((ulong)(a.Target)),
                        unchecked((ulong)(a.Cell.X)),
                        unchecked((ulong)(a.Cell.Y)),
                        unchecked((ulong)(a.End.X)));
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
            h.Put4(
                unchecked((ulong)(0xA10)),
                unchecked((ulong)((byte)ops.Phase)),
                unchecked((ulong)(ops.Airfield)),
                unchecked((ulong)(ops.Station)));
            h.Put4(
                unchecked((ulong)(ops.Speed)),
                unchecked((ulong)(ops.Pace)),
                unchecked((ulong)(ops.Leg)),
                unchecked((ulong)(ops.Side)));
            h.Put4(
                unchecked((ulong)(ops.Wait)),
                unchecked((ulong)(ops.Since)),
                unchecked((ulong)(ops.ReturnCell.X)),
                unchecked((ulong)(ops.ReturnCell.Y)));
            h.Put4(
                unchecked((ulong)(ops.Hold ? 1UL : 0)),
                unchecked((ulong)(ops.Launch ? 1UL : 0)),
                unchecked((ulong)(ops.FuelAcc)),
                unchecked((ulong)(ops.AmmoAcc)));
            h.Put4(
                unchecked((ulong)(ops.HpAcc)),
                unchecked((ulong)(ops.RwWest)),
                unchecked((ulong)(ops.RwEast)),
                unchecked((ulong)(ops.Recall ? 1UL : 0)));
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
                h.Put4(
                    unchecked((ulong)(ops.RecallTo)),
                    unchecked((ulong)(ops.UnloadLeft)),
                    unchecked((ulong)(ops.FuelCostAcc)),
                    unchecked((ulong)(ops.HpCostAcc)));
            }
            h.Put4(
                unchecked((ulong)(ops.PathIdx)),
                unchecked((ulong)(ops.MarkA)),
                unchecked((ulong)(ops.MarkB)),
                unchecked((ulong)(ops.MarkMouth)));
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
            h.Put4(
                unchecked((ulong)(0x5C1)),
                unchecked((ulong)(u.RelaunchCell.X)),
                unchecked((ulong)(u.RelaunchCell.Y)),
                unchecked((ulong)(u.RelaunchArchetype)));
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
            h.Put4(
                unchecked((ulong)(0x5C2)),
                unchecked((ulong)(u.KeptArchetype)),
                unchecked((ulong)(u.KeptHp)),
                unchecked((ulong)(kept.Length)));
            foreach (var k in kept)
            {
                h.PutSigned(k);
            }
        }
        if (u.Upgrades != 0)
        {
            h.Put(0x09A0); // only a unit with upgrades (UpgradeSystem.cs): games without hash as before
            h.Put(u.Upgrades);
        }
        return h;
    }
}

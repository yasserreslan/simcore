namespace SimCore.Sim;

public sealed partial class World
{
    /// <summary>
    /// Returns a 64-bit FNV-1a digest of the whole game state, including the
    /// match setup's digest. Two machines with equal hashes on a tick have
    /// identical worlds; a mismatch is a desync. Pending commands are
    /// excluded: peers may not have received the same future commands yet.
    /// </summary>
    /// <remarks>
    /// Every value is hashed as 8 little-endian bytes (signed values
    /// sign-extended to 64 bits first), except terrain, which is one byte per cell.
    /// </remarks>
    public ulong Hash()
    {
        var h = new Fnv64a();
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
        }
        foreach (var t in Grid.Cells)
        {
            h.PutByte((byte)t);
        }
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
            h.PutSigned(u.DepotReloadLeft);
            h.Put(u.SalvoTarget);
            h.Put(u.MissTarget);
            h.PutSigned(u.MissStreak);
            h.Put(u.RepathAt);
            h.PutSigned(u.PathIdx);
            h.PutSigned(u.Path.Count);
            foreach (var c in u.Path)
            {
                h.PutSigned(c.X);
                h.PutSigned(c.Y);
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
            foreach (var c in u.Charges)
            {
                h.PutSigned(c);
            }
            h.PutSigned(u.Cooldown);
            h.PutSigned(u.SalvoLeft);
            h.PutSigned(u.SalvoWeapon);
            h.PutSigned(u.SalvoAim.X.Raw);
            h.PutSigned(u.SalvoAim.Y.Raw);
            h.PutSigned(u.SalvoWait);
            h.Put(u.SpottedBy);
            h.Put(u.RevealedTo);
            h.Put(u.Source);
            h.PutSigned(u.Lifetime);
            h.PutSigned(u.Stock);
            h.PutSigned(u.Cargo);
            h.PutSigned(u.UnloadLeft);
            h.Put(u.UnloadDepot);
            h.Put(u.DepotStarved ? 1UL : 0);
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
                foreach (var b in u.Bays)
                {
                    h.PutSigned(b.Def);
                    h.PutSigned(b.Left);
                    h.PutSigned(b.Wait);
                }
                foreach (var d in u.Doors)
                {
                    h.Put((ulong)d.State);
                    h.PutSigned(d.Left);
                    h.PutSigned(d.Def);
                    h.Put(d.Vehicle);
                    h.PutSigned(d.Wait);
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
            if (u.Stance != Stance.Guard || u.HitBy != 0)
            {
                // Only units out of Guard (the default): games without stances hash as before.
                h.Put(0x57A7CE);
                h.PutByte((byte)u.Stance);
                h.Put(u.HitBy);
            }
            if (u.AirTarget != 0 || u.TrackTicks != 0)
            {
                // Only air defence tracking a target.
                h.Put(0xA1A1);
                h.Put(u.AirTarget);
                h.PutSigned(u.TrackTicks);
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
            }
        }
        return h.Sum;
    }

    // Fnv64a is a streaming FNV-1a 64-bit hash.
    private struct Fnv64a()
    {
        private const ulong Prime = 1099511628211;

        public ulong Sum { get; private set; } = 14695981039346656037;

        public void PutByte(byte b) => Sum = unchecked((Sum ^ b) * Prime);

        // Put hashes v as 8 little-endian bytes.
        public void Put(ulong v)
        {
            for (var i = 0; i < 8; i++)
            {
                PutByte((byte)(v >> (8 * i)));
            }
        }

        // PutSigned sign-extends v to 64 bits and hashes it like Put.
        public void PutSigned(long v) => Put(unchecked((ulong)v));
    }
}

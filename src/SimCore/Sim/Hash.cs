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
        foreach (var u in _units)
        {
            h.Put(u.Id);
            h.PutSigned(u.Def.Index);
            h.PutSigned(u.Pos.X.Raw);
            h.PutSigned(u.Pos.Y.Raw);
            h.PutSigned(u.Facing);
            h.PutSigned(u.TurretFacing);
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
            h.PutSigned(u.ReloadBonus);
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

namespace SimCore.Sim;

public sealed partial class World
{
    /// <summary>
    /// Returns a 64-bit FNV-1a digest of the whole game state. Two machines
    /// with equal hashes on a tick have identical worlds; a mismatch is a
    /// desync. Pending commands are excluded: peers may not have received the
    /// same future commands yet.
    /// </summary>
    /// <remarks>
    /// Every value is hashed as 8 little-endian bytes (signed values
    /// sign-extended to 64 bits first), except terrain, which is one byte per
    /// cell. The layout matches the original Go implementation byte for byte.
    /// </remarks>
    public ulong Hash()
    {
        var h = new Fnv64a();
        h.Put(Tick);
        h.Put(_rng.State);
        h.Put(_nextId);
        foreach (var p in _players)
        {
            h.Put(p.Id);
            h.PutSigned(p.Credits);
        }
        foreach (var t in Grid.Cells)
        {
            h.PutByte((byte)t);
        }
        foreach (var u in _units)
        {
            h.Put(u.Id);
            h.Put(u.Owner);
            h.Put((ulong)u.Type);
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
            h.Put(u.AutoTarget);
            h.Put(u.RepathAt);
            h.PutSigned(u.PathIdx);
            h.PutSigned(u.Path.Count);
            foreach (var c in u.Path)
            {
                h.PutSigned(c.X);
                h.PutSigned(c.Y);
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

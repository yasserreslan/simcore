using System.Numerics;
using System.Runtime.CompilerServices;

namespace SimCore.Sim;

/// <summary>
/// A streaming 64-bit hash that mixes one whole 64-bit word per step (an xxHash64-style
/// round: multiply the word, add, rotate, multiply) and finishes with a full avalanche.
/// It is integer-only and never looks at memory byte order, so every platform computes
/// the same digest. It feeds the game hash (<see cref="World.Hash"/>).
/// </summary>
internal struct WordHash()
{
    private const ulong P1 = 0x9E3779B185EBCA87;
    private const ulong P2 = 0xC2B2AE3D27D4EB4F;
    private const ulong P3 = 0x165667B19E3779F9;

    private ulong _acc = 0x27D4EB2F165667C5;

    /// <summary>The digest of every word put so far.</summary>
    public readonly ulong Sum
    {
        get
        {
            var h = _acc;
            h ^= h >> 33;
            h *= P2;
            h ^= h >> 29;
            h *= P3;
            h ^= h >> 32;
            return h;
        }
    }

    /// <summary>Mixes in one word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(ulong v) => _acc = BitOperations.RotateLeft(_acc + v * P2, 31) * P1;

    /// <summary>Four consecutive words, with independent input multiplications ready before the serial rounds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put4(ulong a, ulong b, ulong c, ulong d)
    {
        a *= P2;
        b *= P2;
        c *= P2;
        d *= P2;
        var acc = _acc;
        acc = BitOperations.RotateLeft(acc + a, 31) * P1;
        acc = BitOperations.RotateLeft(acc + b, 31) * P1;
        acc = BitOperations.RotateLeft(acc + c, 31) * P1;
        _acc = BitOperations.RotateLeft(acc + d, 31) * P1;
    }

    /// <summary>Sign-extends v to 64 bits and mixes it in like <see cref="Put"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutSigned(long v) => Put(unchecked((ulong)v));

    /// <summary>Mixes in a byte as one word (zero-extended).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutByte(byte b) => Put(b);
}

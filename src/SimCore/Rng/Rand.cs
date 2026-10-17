// The simulation's only source of randomness: a seeded SplitMix64 generator
// whose state is part of the world hash.
namespace SimCore.Rng;

/// <summary>
/// A deterministic SplitMix64 random number generator. It is a mutable struct:
/// keep it in a field and call it in place, never through a copy. The default
/// value is usable.
/// </summary>
/// <param name="seed">The starting state.</param>
public struct Rand(ulong seed)
{
    private ulong _state = seed;

    /// <summary>The internal state, for hashing.</summary>
    public readonly ulong State => _state;

    /// <summary>Returns the next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9e3779b97f4a7c15;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
            z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Returns a value in [0, n).</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is not positive.</exception>
    public int Intn(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        return (int)(NextUInt64() % (ulong)n);
    }

    /// <summary>Returns a value in [lo, hi], inclusive.</summary>
    public int Range(int lo, int hi) => lo + Intn(hi - lo + 1);

    /// <summary>
    /// Returns 64 random-looking bits that depend only on the four values: SplitMix64's
    /// finalizer chained over them. It reads and changes no generator state, so a roll made
    /// with it (an air-defence kill roll) never shifts the world's other random draws.
    /// </summary>
    public static ulong Hash(ulong a, ulong b, ulong c, ulong d) => Mix(Mix(Mix(Mix(a) ^ b) ^ c) ^ d);

    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9e3779b97f4a7c15;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
            z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
            return z ^ (z >> 31);
        }
    }
}

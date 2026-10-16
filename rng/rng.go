// Package rng is the simulation's only source of randomness: a seeded
// SplitMix64 generator whose state is part of the world hash.
package rng

// Rand is a deterministic random number generator. The zero value is usable.
type Rand struct {
	state uint64
}

// New returns a generator seeded with seed.
func New(seed uint64) Rand { return Rand{state: seed} }

// State returns the internal state, for hashing.
func (r *Rand) State() uint64 { return r.state }

// Uint64 returns the next 64 random bits.
func (r *Rand) Uint64() uint64 {
	r.state += 0x9e3779b97f4a7c15
	z := r.state
	z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9
	z = (z ^ (z >> 27)) * 0x94d049bb133111eb
	return z ^ (z >> 31)
}

// Intn returns a value in [0, n). It panics if n <= 0.
func (r *Rand) Intn(n int) int {
	if n <= 0 {
		panic("rng: Intn with n <= 0")
	}
	return int(r.Uint64() % uint64(n))
}

// Range returns a value in [lo, hi], inclusive.
func (r *Rand) Range(lo, hi int) int { return lo + r.Intn(hi-lo+1) }

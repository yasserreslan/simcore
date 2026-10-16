package rng

import "testing"

// TestKnownSequence pins the output so a change to the generator, which
// would silently break every saved replay, fails loudly.
func TestKnownSequence(t *testing.T) {
	r := New(42)
	want := []uint64{0xbdd732262feb6e95, 0x28efe333b266f103, 0x47526757130f9f52}
	for i, w := range want {
		if got := r.Uint64(); got != w {
			t.Fatalf("value %d = %#x, want %#x", i, got, w)
		}
	}
}

func TestRangeBounds(t *testing.T) {
	r := New(7)
	for range 10_000 {
		v := r.Range(-3, 3)
		if v < -3 || v > 3 {
			t.Fatalf("Range(-3, 3) = %d", v)
		}
	}
}

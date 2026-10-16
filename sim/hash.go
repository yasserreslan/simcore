package sim

import (
	"encoding/binary"
	"hash/fnv"
)

// Hash returns a 64-bit FNV-1a digest of the whole game state. Two machines
// with equal hashes on a tick have identical worlds; a mismatch is a desync.
// Pending commands are excluded: peers may not have received the same future
// commands yet.
func (w *World) Hash() uint64 {
	b := w.hashBuf[:0]
	put := func(v uint64) { b = binary.LittleEndian.AppendUint64(b, v) }
	puti := func(v int) { put(uint64(int64(v))) }

	put(uint64(w.Tick))
	put(w.rng.State())
	put(uint64(w.nextID))
	for _, p := range w.Players {
		put(uint64(p.ID))
		puti(p.Credits)
	}
	for _, t := range w.Grid.cells {
		b = append(b, byte(t))
	}
	for _, u := range w.Units {
		put(uint64(u.ID))
		put(uint64(u.Owner))
		put(uint64(u.Type))
		put(uint64(u.Pos.X))
		put(uint64(u.Pos.Y))
		puti(u.Facing)
		puti(u.TurretFacing)
		puti(u.HP)
		puti(u.Reload)
		put(uint64(u.Order.Kind))
		puti(u.Order.Cell.X)
		puti(u.Order.Cell.Y)
		put(uint64(u.Order.Target))
		put(uint64(u.AutoTarget))
		put(uint64(u.RepathAt))
		puti(u.PathIdx)
		puti(len(u.Path))
		for _, c := range u.Path {
			puti(c.X)
			puti(c.Y)
		}
	}
	w.hashBuf = b

	h := fnv.New64a()
	_, _ = h.Write(b) // hash.Hash never returns an error
	return h.Sum64()
}

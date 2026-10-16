// Package sim is the deterministic game simulation: the whole game minus
// graphics, sound and input devices.
//
// The world changes only through Commands applied by Step. Given the same
// starting world and the same commands, every machine computes a
// bit-identical world on every tick; Hash proves it. See README.md for the
// determinism rules this package follows.
package sim

import "github.com/yasserreslan/simcore/fixed"

// TicksPerSecond is the fixed simulation rate.
const TicksPerSecond = 20

// InputDelay is how many ticks ahead a player's command is scheduled, giving
// it time to reach every peer before it runs.
const InputDelay = 2

// PlayerID identifies a player. 0 is neutral; real players start at 1.
type PlayerID uint8

// EntityID identifies a unit. IDs are assigned in increasing order and never
// reused.
type EntityID uint32

// Cell is a map grid coordinate.
type Cell struct {
	X int `json:"x"`
	Y int `json:"y"`
}

// Center returns the world position of the cell's center.
func (c Cell) Center() fixed.Vec {
	return fixed.Vec{X: fixed.FromInt(c.X) + fixed.One/2, Y: fixed.FromInt(c.Y) + fixed.One/2}
}

// CellOf returns the cell containing world position p.
func CellOf(p fixed.Vec) Cell { return Cell{X: p.X.Floor(), Y: p.Y.Floor()} }

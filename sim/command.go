package sim

import (
	"cmp"
	"errors"
	"fmt"
	"slices"
)

// CommandKind is the type of a player command.
type CommandKind uint8

// Command kinds.
const (
	CmdMove CommandKind = iota + 1
	CmdAttack
	CmdStop
)

// Command is a player's order, scheduled for a tick. Commands are the only
// way to change the world, and the only thing peers exchange in multiplayer.
type Command struct {
	Tick   uint32      `json:"tick"`
	Player PlayerID    `json:"player"`
	Seq    uint32      `json:"seq"` // per-player order; assigned by Issue when 0
	Kind   CommandKind `json:"kind"`
	Units  []EntityID  `json:"units"`
	Cell   Cell        `json:"cell"`
	Target EntityID    `json:"target,omitempty"`
}

// Errors returned by Issue.
var (
	ErrLateCommand   = errors.New("sim: command scheduled for a past tick")
	ErrUnknownPlayer = errors.New("sim: unknown player")
)

// Issue schedules a command and returns it with its sequence number set.
// Commands for the same tick run ordered by player, then sequence, no matter
// what order they arrived in.
func (w *World) Issue(c Command) (Command, error) {
	if c.Tick < w.Tick {
		return c, fmt.Errorf("%w: tick %d, now %d", ErrLateCommand, c.Tick, w.Tick)
	}
	if c.Player == 0 || int(c.Player) >= len(w.nextSeq) {
		return c, fmt.Errorf("%w: %d", ErrUnknownPlayer, c.Player)
	}
	if c.Seq == 0 {
		w.nextSeq[c.Player]++
		c.Seq = w.nextSeq[c.Player]
	} else {
		w.nextSeq[c.Player] = max(w.nextSeq[c.Player], c.Seq)
	}
	c.Units = slices.Clone(c.Units)
	i, _ := slices.BinarySearchFunc(w.pending, c, compareCommands)
	w.pending = slices.Insert(w.pending, i, c)
	return c, nil
}

func compareCommands(a, b Command) int {
	return cmp.Or(
		cmp.Compare(a.Tick, b.Tick),
		cmp.Compare(a.Player, b.Player),
		cmp.Compare(a.Seq, b.Seq),
	)
}

func (w *World) applyCommands() {
	n := 0
	for n < len(w.pending) && w.pending[n].Tick == w.Tick {
		n++
	}
	for _, c := range w.pending[:n] {
		w.apply(c)
	}
	w.pending = slices.Delete(w.pending, 0, n)
}

// apply runs one command. Units the player doesn't own, dead units and
// invalid targets are skipped, the same way on every machine.
func (w *World) apply(c Command) {
	switch c.Kind {
	case CmdMove:
		dest := 0
		for _, id := range c.Units {
			u := w.Unit(id)
			if u == nil || u.Owner != c.Player || u.Def().Speed == 0 {
				continue
			}
			// Each unit in the group gets its own cell around the target.
			cell, ok := w.Grid.Spiral(c.Cell, dest)
			dest++
			if !ok {
				continue
			}
			u.Order = Order{Kind: OrderMove, Cell: cell}
			w.setPath(u, cell)
		}
	case CmdAttack:
		target := w.Unit(c.Target)
		if target == nil || target.Owner == c.Player {
			return
		}
		for _, id := range c.Units {
			u := w.Unit(id)
			if u == nil || u.Owner != c.Player || u.Def().Weapon == nil {
				continue
			}
			u.Order = Order{Kind: OrderAttack, Target: target.ID}
			u.clearPath()
			u.RepathAt = w.Tick
		}
	case CmdStop:
		for _, id := range c.Units {
			if u := w.Unit(id); u != nil && u.Owner == c.Player {
				u.Order = Order{}
				u.clearPath()
			}
		}
	}
}

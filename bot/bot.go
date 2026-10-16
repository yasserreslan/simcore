// Package bot is a scripted player that issues random orders. It stands in
// for real players when recording test matches. It reads the world the way a
// player would and only outputs commands, so a replay never needs it.
package bot

import (
	"github.com/yasserreslan/simcore/rng"
	"github.com/yasserreslan/simcore/sim"
)

// Bot issues orders for one player.
type Bot struct {
	Player sim.PlayerID
	Every  uint32 // ticks between decisions
	rng    rng.Rand
}

// New returns a bot that decides every two seconds.
func New(p sim.PlayerID, seed uint64) *Bot {
	return &Bot{Player: p, Every: 2 * sim.TicksPerSecond, rng: rng.New(seed)}
}

// Think returns the commands the bot issues this tick, scheduled
// sim.InputDelay ticks ahead like a real player's.
func (b *Bot) Think(w *sim.World) []sim.Command {
	if w.Tick%b.Every != 0 {
		return nil
	}
	var own, enemies []sim.EntityID
	for _, u := range w.Units {
		if u.Owner == b.Player {
			own = append(own, u.ID)
		} else if u.Owner != 0 {
			enemies = append(enemies, u.ID)
		}
	}
	if len(own) == 0 {
		return nil
	}

	// Pick 1–5 distinct units with a partial shuffle.
	n := 1 + b.rng.Intn(min(5, len(own)))
	for i := range n {
		j := i + b.rng.Intn(len(own)-i)
		own[i], own[j] = own[j], own[i]
	}
	c := sim.Command{Tick: w.Tick + sim.InputDelay, Player: b.Player, Units: own[:n]}

	switch roll := b.rng.Intn(10); {
	case roll < 5:
		c.Kind = sim.CmdMove
		c.Cell = sim.Cell{X: b.rng.Intn(w.Grid.W), Y: b.rng.Intn(w.Grid.H)}
	case roll < 9 && len(enemies) > 0:
		c.Kind = sim.CmdAttack
		c.Target = enemies[b.rng.Intn(len(enemies))]
	default:
		c.Kind = sim.CmdStop
	}
	return []sim.Command{c}
}

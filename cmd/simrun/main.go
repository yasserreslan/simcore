// Command simrun runs a match headless and prints the map as text.
//
//	go run ./cmd/simrun                         # bot vs bot, 10 minutes
//	go run ./cmd/simrun -every 20 -ticks 2400   # watch every second for 2 minutes
//	go run ./cmd/simrun -save match.json        # also save the replay
//	go run ./cmd/simrun -replay match.json      # play a saved replay
//
// Player 1 units are uppercase, player 2 lowercase: T tank, R rifleman,
// K rocket trooper, B buggy, A artillery. # is rock.
package main

import (
	"flag"
	"fmt"
	"os"
	"strings"
	"unicode"

	"github.com/yasserreslan/simcore/replay"
	"github.com/yasserreslan/simcore/sim"
)

func main() {
	seed := flag.Uint64("seed", 42, "match seed")
	ticks := flag.Uint("ticks", 10*60*sim.TicksPerSecond, "ticks to simulate")
	every := flag.Uint("every", 60*sim.TicksPerSecond, "print the map every N ticks (0 = final only)")
	save := flag.String("save", "", "write the recorded replay to this file")
	load := flag.String("replay", "", "play this replay file instead of recording bots")
	flag.Parse()

	if err := run(*seed, uint32(*ticks), uint32(*every), *save, *load); err != nil {
		fmt.Fprintln(os.Stderr, "simrun:", err)
		os.Exit(1)
	}
}

func run(seed uint64, ticks, every uint32, save, load string) error {
	var r replay.Replay
	if load != "" {
		var err error
		if r, err = replay.Load(load); err != nil {
			return err
		}
	} else {
		var err error
		if r, _, err = replay.Record("skirmish", seed, ticks); err != nil {
			return err
		}
	}
	if save != "" {
		if err := replay.Save(save, r); err != nil {
			return err
		}
	}

	// Play the replay tick by tick so we can print along the way.
	w, err := replay.NewWorld(r.Scenario, r.Seed)
	if err != nil {
		return err
	}
	for _, c := range r.Commands {
		if _, err := w.Issue(c); err != nil {
			return err
		}
	}
	for w.Tick < r.Ticks {
		w.Step()
		if every > 0 && w.Tick%every == 0 && w.Tick != r.Ticks {
			fmt.Print(render(w))
		}
	}
	fmt.Print(render(w))
	return nil
}

func render(w *sim.World) string {
	var b strings.Builder
	counts := map[sim.PlayerID]int{}
	glyphs := make([]rune, w.Grid.W*w.Grid.H)
	for _, u := range w.Units {
		counts[u.Owner]++
		c := sim.CellOf(u.Pos)
		if !w.Grid.In(c) {
			continue
		}
		g := u.Def().Glyph
		if u.Owner != 1 {
			g = unicode.ToLower(g)
		}
		if i := c.Y*w.Grid.W + c.X; glyphs[i] == 0 {
			glyphs[i] = g
		}
	}

	secs := w.Tick / sim.TicksPerSecond
	fmt.Fprintf(&b, "\ntick %d (%d:%02d)  P1 %d units  P2 %d units  hash %016x\n",
		w.Tick, secs/60, secs%60, counts[1], counts[2], w.Hash())
	for y := range w.Grid.H {
		for x := range w.Grid.W {
			switch c := (sim.Cell{X: x, Y: y}); {
			case glyphs[y*w.Grid.W+x] != 0:
				b.WriteRune(glyphs[y*w.Grid.W+x])
			case !w.Grid.Passable(c):
				b.WriteByte('#')
			default:
				b.WriteByte('.')
			}
		}
		b.WriteByte('\n')
	}
	return b.String()
}

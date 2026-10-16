// Package replay records matches as command logs and plays them back.
//
// A replay is just the starting scenario, its seed and every command. Because
// the sim is deterministic, playing it back recreates the match exactly, and
// the per-tick hashes must match the original run.
package replay

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"

	"github.com/yasserreslan/simcore/bot"
	"github.com/yasserreslan/simcore/scenario"
	"github.com/yasserreslan/simcore/sim"
)

// Version is the replay format version.
const Version = 1

// Replay is a recorded match.
type Replay struct {
	Version  int           `json:"version"`
	Scenario string        `json:"scenario"`
	Seed     uint64        `json:"seed"`
	Ticks    uint32        `json:"ticks"`
	Commands []sim.Command `json:"commands"`
}

// ErrUnknownScenario is returned for a scenario name not in the registry.
var ErrUnknownScenario = errors.New("replay: unknown scenario")

var scenarios = map[string]func(seed uint64) *sim.World{
	"skirmish": scenario.Skirmish,
}

// NewWorld builds the named scenario's starting world.
func NewWorld(name string, seed uint64) (*sim.World, error) {
	build, ok := scenarios[name]
	if !ok {
		return nil, fmt.Errorf("%w: %q", ErrUnknownScenario, name)
	}
	return build(seed), nil
}

// Record plays a bot-vs-bot match for ticks ticks and returns its replay and
// the world hash after every tick.
func Record(name string, seed uint64, ticks uint32) (Replay, []uint64, error) {
	w, err := NewWorld(name, seed)
	if err != nil {
		return Replay{}, nil, err
	}
	r := Replay{Version: Version, Scenario: name, Seed: seed, Ticks: ticks}
	bots := make([]*bot.Bot, 0, len(w.Players))
	for _, p := range w.Players {
		bots = append(bots, bot.New(p.ID, seed+uint64(p.ID)))
	}

	hashes := make([]uint64, 0, ticks)
	for w.Tick < ticks {
		for _, b := range bots {
			for _, c := range b.Think(w) {
				issued, err := w.Issue(c)
				if err != nil {
					return Replay{}, nil, fmt.Errorf("tick %d: %w", w.Tick, err)
				}
				r.Commands = append(r.Commands, issued)
			}
		}
		w.Step()
		hashes = append(hashes, w.Hash())
	}
	return r, hashes, nil
}

// Play runs a replay from its starting world and returns the world hash
// after every tick, plus the final world.
func Play(r Replay) ([]uint64, *sim.World, error) {
	if r.Version != Version {
		return nil, nil, fmt.Errorf("replay: version %d, want %d", r.Version, Version)
	}
	w, err := NewWorld(r.Scenario, r.Seed)
	if err != nil {
		return nil, nil, err
	}
	for _, c := range r.Commands {
		if _, err := w.Issue(c); err != nil {
			return nil, nil, err
		}
	}
	hashes := make([]uint64, 0, r.Ticks)
	for w.Tick < r.Ticks {
		w.Step()
		hashes = append(hashes, w.Hash())
	}
	return hashes, w, nil
}

// Encode writes r as JSON.
func Encode(wr io.Writer, r Replay) error {
	enc := json.NewEncoder(wr)
	enc.SetIndent("", " ")
	return enc.Encode(r)
}

// Decode reads a JSON replay.
func Decode(rd io.Reader) (Replay, error) {
	var r Replay
	err := json.NewDecoder(rd).Decode(&r)
	return r, err
}

// Save writes r to a file.
func Save(path string, r Replay) error {
	f, err := os.Create(path)
	if err != nil {
		return err
	}
	if err := Encode(f, r); err != nil {
		_ = f.Close()
		return err
	}
	return f.Close()
}

// Load reads a replay file.
func Load(path string) (Replay, error) {
	f, err := os.Open(path)
	if err != nil {
		return Replay{}, err
	}
	defer f.Close()
	return Decode(f)
}

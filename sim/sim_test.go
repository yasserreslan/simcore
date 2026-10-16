package sim

import (
	"errors"
	"testing"

	"github.com/yasserreslan/simcore/fixed"
)

func newTestWorld(w, h int) *World { return NewWorld(NewGrid(w, h), 1, 2) }

func run(w *World, ticks int) {
	for range ticks {
		w.Step()
	}
}

func TestTankKillsRiflemanInRange(t *testing.T) {
	w := newTestWorld(16, 16)
	tank := w.Spawn(BattleTank, 1, Cell{X: 5, Y: 5}, fixed.East)
	rifle := w.Spawn(Rifleman, 2, Cell{X: 8, Y: 5}, fixed.West)

	if _, err := w.Issue(Command{Tick: 0, Player: 1, Kind: CmdAttack, Units: []EntityID{tank}, Target: rifle}); err != nil {
		t.Fatal(err)
	}
	run(w, 200)

	if w.Unit(rifle) != nil {
		t.Fatal("rifleman still alive after 10 s")
	}
	if u := w.Unit(tank); u == nil || u.HP >= u.Def().MaxHP {
		t.Fatal("tank should be alive and have taken return fire")
	}
}

func TestMoveRoutesAroundWall(t *testing.T) {
	w := newTestWorld(10, 10)
	for y := range 9 {
		w.Grid.Set(Cell{X: 5, Y: y}, TerrainBlocked) // the only gap is at y = 9
	}
	id := w.Spawn(BattleTank, 1, Cell{X: 2, Y: 2}, fixed.East)
	dest := Cell{X: 8, Y: 2}
	if _, err := w.Issue(Command{Tick: 0, Player: 1, Kind: CmdMove, Units: []EntityID{id}, Cell: dest}); err != nil {
		t.Fatal(err)
	}
	w.Step()

	u := w.Unit(id)
	prev := Cell{X: 2, Y: 2}
	for _, c := range u.Path {
		if !w.Grid.Passable(c) {
			t.Fatalf("path enters blocked cell %v", c)
		}
		if abs(c.X-prev.X) > 1 || abs(c.Y-prev.Y) > 1 {
			t.Fatalf("path jumps from %v to %v", prev, c)
		}
		prev = c
	}
	if prev != dest {
		t.Fatalf("path ends at %v, want %v", prev, dest)
	}

	run(w, 40*TicksPerSecond)
	if got := CellOf(u.Pos); got != dest {
		t.Fatalf("tank at %v after 40 s, want %v", got, dest)
	}
	if u.Order.Kind != OrderIdle {
		t.Fatalf("order = %v after arriving, want idle", u.Order.Kind)
	}
}

func TestGroupMoveSpreadsDestinations(t *testing.T) {
	w := newTestWorld(20, 20)
	var ids []EntityID
	for i := range 5 {
		ids = append(ids, w.Spawn(Rifleman, 1, Cell{X: 2, Y: 2 + i}, fixed.East))
	}
	if _, err := w.Issue(Command{Tick: 0, Player: 1, Kind: CmdMove, Units: ids, Cell: Cell{X: 15, Y: 10}}); err != nil {
		t.Fatal(err)
	}
	w.Step()
	seen := map[Cell]bool{}
	for _, id := range ids {
		c := w.Unit(id).Order.Cell
		if seen[c] {
			t.Fatalf("two units sent to %v", c)
		}
		seen[c] = true
	}
}

func TestCommandsForOtherPlayersUnitsAreIgnored(t *testing.T) {
	w := newTestWorld(10, 10)
	enemy := w.Spawn(BattleTank, 2, Cell{X: 5, Y: 5}, fixed.West)
	if _, err := w.Issue(Command{Tick: 0, Player: 1, Kind: CmdMove, Units: []EntityID{enemy}, Cell: Cell{X: 0, Y: 0}}); err != nil {
		t.Fatal(err)
	}
	w.Step()
	if u := w.Unit(enemy); u.Order.Kind != OrderIdle || u.Moving() {
		t.Fatal("player 1 moved player 2's tank")
	}
}

func TestIssueRejectsLateCommands(t *testing.T) {
	w := newTestWorld(4, 4)
	run(w, 5)
	_, err := w.Issue(Command{Tick: 3, Player: 1, Kind: CmdStop})
	if !errors.Is(err, ErrLateCommand) {
		t.Fatalf("err = %v, want ErrLateCommand", err)
	}
	_, err = w.Issue(Command{Tick: 9, Player: 7, Kind: CmdStop})
	if !errors.Is(err, ErrUnknownPlayer) {
		t.Fatalf("err = %v, want ErrUnknownPlayer", err)
	}
}

// TestCommandOrderIndependentOfArrival checks that peers receiving the same
// commands in different orders still compute the same world.
func TestCommandOrderIndependentOfArrival(t *testing.T) {
	build := func() *World {
		w := newTestWorld(16, 16)
		w.Spawn(BattleTank, 1, Cell{X: 2, Y: 2}, fixed.East)
		w.Spawn(BattleTank, 2, Cell{X: 12, Y: 12}, fixed.West)
		return w
	}
	cmds := []Command{
		{Tick: 3, Player: 2, Seq: 1, Kind: CmdMove, Units: []EntityID{2}, Cell: Cell{X: 4, Y: 4}},
		{Tick: 3, Player: 1, Seq: 1, Kind: CmdMove, Units: []EntityID{1}, Cell: Cell{X: 10, Y: 2}},
		{Tick: 3, Player: 1, Seq: 2, Kind: CmdMove, Units: []EntityID{1}, Cell: Cell{X: 2, Y: 10}},
	}
	a, b := build(), build()
	for _, c := range cmds {
		if _, err := a.Issue(c); err != nil {
			t.Fatal(err)
		}
	}
	for i := len(cmds) - 1; i >= 0; i-- {
		if _, err := b.Issue(cmds[i]); err != nil {
			t.Fatal(err)
		}
	}
	run(a, 200)
	run(b, 200)
	if a.Hash() != b.Hash() {
		t.Fatal("worlds diverged when commands arrived in a different order")
	}
	if got := CellOf(a.Unit(1).Pos); got != (Cell{X: 2, Y: 10}) {
		t.Fatalf("later command should win; tank 1 at %v", got)
	}
}

func TestHashChangesWithState(t *testing.T) {
	w := newTestWorld(8, 8)
	id := w.Spawn(Rifleman, 1, Cell{X: 1, Y: 1}, 0)
	h := w.Hash()
	if w.Hash() != h {
		t.Fatal("hash is not stable")
	}
	w.Unit(id).HP--
	if w.Hash() == h {
		t.Fatal("hash ignores unit HP")
	}
}

func TestSplashHitsEveryoneInRadius(t *testing.T) {
	w := newTestWorld(4, 4)
	a := w.Spawn(Rifleman, 1, Cell{X: 1, Y: 1}, 0)
	b := w.Spawn(Rifleman, 2, Cell{X: 1, Y: 1}, 0)
	far := w.Spawn(Rifleman, 2, Cell{X: 3, Y: 3}, 0)
	w.applyHits([]hit{{weapon: &shell, target: b, at: Cell{X: 1, Y: 1}.Center()}})

	full := UnitTypes[Rifleman].MaxHP - damage(&shell, ArmorNone, 100)
	if w.Unit(a).HP != full || w.Unit(b).HP != full {
		t.Fatalf("units at the impact point should take full splash damage: %d, %d, want %d",
			w.Unit(a).HP, w.Unit(b).HP, full)
	}
	if w.Unit(far).HP != UnitTypes[Rifleman].MaxHP {
		t.Fatal("unit outside the radius was damaged")
	}
}

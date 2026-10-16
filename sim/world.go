package sim

import (
	"cmp"
	"slices"

	"github.com/yasserreslan/simcore/fixed"
	"github.com/yasserreslan/simcore/rng"
)

// OrderKind is what a unit is currently told to do.
type OrderKind uint8

// Orders.
const (
	OrderIdle OrderKind = iota
	OrderMove
	OrderAttack
)

// Order is a unit's current order.
type Order struct {
	Kind   OrderKind
	Cell   Cell     // OrderMove destination
	Target EntityID // OrderAttack target
}

// Unit is one unit on the map. Renderers read these fields; only the sim
// writes them.
type Unit struct {
	ID           EntityID
	Owner        PlayerID
	Type         TypeID
	Pos          fixed.Vec
	Facing       int // hull, 0..31
	TurretFacing int // 0..31; equals Facing for units without a turret
	HP           int
	Reload       int // ticks until the weapon can fire
	Order        Order
	AutoTarget   EntityID // enemy picked automatically when not on an attack order
	Path         []Cell   // cells still to visit, from Path[PathIdx]
	PathIdx      int
	RepathAt     uint32 // tick at which a chasing unit recomputes its path
}

// Def returns the unit's static definition.
func (u *Unit) Def() *UnitType { return &UnitTypes[u.Type] }

// Moving reports whether the unit still has path to follow.
func (u *Unit) Moving() bool { return u.PathIdx < len(u.Path) }

// Player is per-player state.
type Player struct {
	ID      PlayerID
	Credits int
}

// World is the complete game state. Everything that affects gameplay lives
// here, and Hash covers all of it.
type World struct {
	Tick    uint32
	Grid    *Grid
	Units   []*Unit // sorted by ID, ascending
	Players []Player

	rng     rng.Rand
	nextID  EntityID
	pending []Command // sorted by (Tick, Player, Seq); not hashed
	nextSeq []uint32  // per player
	pf      pathfinder
	hashBuf []byte
}

// NewWorld returns a world at tick 0 with players 1..players.
func NewWorld(grid *Grid, seed uint64, players int) *World {
	w := &World{
		Grid:    grid,
		rng:     rng.New(seed),
		nextSeq: make([]uint32, players+1),
	}
	for i := 1; i <= players; i++ {
		w.Players = append(w.Players, Player{ID: PlayerID(i)})
	}
	return w
}

// Spawn creates a unit at the center of cell c and returns its ID.
func (w *World) Spawn(t TypeID, owner PlayerID, c Cell, facing int) EntityID {
	w.nextID++
	facing = fixed.Wrap(facing)
	u := &Unit{
		ID:           w.nextID,
		Owner:        owner,
		Type:         t,
		Pos:          c.Center(),
		Facing:       facing,
		TurretFacing: facing,
		HP:           UnitTypes[t].MaxHP,
	}
	w.Units = append(w.Units, u) // IDs only grow, so the slice stays sorted
	return u.ID
}

// Unit returns the live unit with the given ID, or nil.
func (w *World) Unit(id EntityID) *Unit {
	i, ok := slices.BinarySearchFunc(w.Units, id, func(u *Unit, id EntityID) int {
		return cmp.Compare(u.ID, id)
	})
	if !ok {
		return nil
	}
	return w.Units[i]
}

// Step advances the world by one tick. Systems run in a fixed order, and
// each visits units in ID order.
func (w *World) Step() {
	w.applyCommands()
	for _, u := range w.Units {
		w.think(u)
	}
	for _, u := range w.Units {
		w.move(u)
	}
	w.applyHits(w.fire())
	w.Units = slices.DeleteFunc(w.Units, func(u *Unit) bool { return u.HP <= 0 })
	w.Tick++
}

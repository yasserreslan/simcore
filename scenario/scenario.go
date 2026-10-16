// Package scenario builds starting worlds.
package scenario

import (
	"github.com/yasserreslan/simcore/fixed"
	"github.com/yasserreslan/simcore/rng"
	"github.com/yasserreslan/simcore/sim"
)

// Size is the Skirmish map's width and height in cells.
const Size = 64

// army is each side's starting force, in spawn (and therefore ID) order.
var army = []sim.TypeID{
	sim.BattleTank, sim.BattleTank, sim.BattleTank, sim.BattleTank,
	sim.Rifleman, sim.Rifleman, sim.Rifleman, sim.Rifleman, sim.Rifleman, sim.Rifleman,
	sim.RocketTrooper, sim.RocketTrooper,
	sim.ScoutBuggy, sim.ScoutBuggy,
	sim.Artillery,
}

// ArmySize is how many units each player starts with.
var ArmySize = len(army)

// Skirmish returns a 64×64 two-player map. A ridge splits it down the
// middle with two 4-cell gaps, the chokepoints both armies must path
// through. Rock clusters are placed from the seed and mirrored, so the map
// is fair. Player 1 starts on the left, player 2 on the right.
func Skirmish(seed uint64) *sim.World {
	g := sim.NewGrid(Size, Size)
	for y := 8; y < Size-8; y++ {
		if (y >= 20 && y < 24) || (y >= 40 && y < 44) {
			continue
		}
		g.Set(sim.Cell{X: Size/2 - 1, Y: y}, sim.TerrainBlocked)
		g.Set(sim.Cell{X: Size / 2, Y: y}, sim.TerrainBlocked)
	}

	r := rng.New(seed)
	for range 12 {
		x, y := r.Range(14, 26), r.Range(2, Size-4)
		for dy := range 2 {
			for dx := range 2 {
				g.Set(sim.Cell{X: x + dx, Y: y + dy}, sim.TerrainBlocked)
				g.Set(sim.Cell{X: Size - 1 - x - dx, Y: y + dy}, sim.TerrainBlocked)
			}
		}
	}

	w := sim.NewWorld(g, seed, 2)
	spawnArmy(w, 1, sim.Cell{X: 7, Y: Size / 2}, fixed.East)
	spawnArmy(w, 2, sim.Cell{X: Size - 8, Y: Size / 2}, fixed.West)
	return w
}

func spawnArmy(w *sim.World, p sim.PlayerID, base sim.Cell, facing int) {
	for i, t := range army {
		c, _ := w.Grid.Spiral(base, i)
		w.Spawn(t, p, c, facing)
	}
}

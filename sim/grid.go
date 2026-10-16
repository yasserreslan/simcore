package sim

// Terrain is what occupies a map cell.
type Terrain uint8

// Terrain kinds.
const (
	TerrainClear Terrain = iota
	TerrainBlocked
)

// Grid is the map: W×H cells, stored row by row.
type Grid struct {
	W, H  int
	cells []Terrain
}

// NewGrid returns an all-clear map.
func NewGrid(w, h int) *Grid {
	return &Grid{W: w, H: h, cells: make([]Terrain, w*h)}
}

// In reports whether c is on the map.
func (g *Grid) In(c Cell) bool { return c.X >= 0 && c.Y >= 0 && c.X < g.W && c.Y < g.H }

// At returns the terrain at c. Off-map cells are blocked.
func (g *Grid) At(c Cell) Terrain {
	if !g.In(c) {
		return TerrainBlocked
	}
	return g.cells[g.index(c)]
}

// Set changes the terrain at c. Off-map cells are ignored.
func (g *Grid) Set(c Cell, t Terrain) {
	if g.In(c) {
		g.cells[g.index(c)] = t
	}
}

// Passable reports whether ground units can enter c.
func (g *Grid) Passable(c Cell) bool { return g.At(c) == TerrainClear }

// Spiral returns the n-th passable cell (0 = nearest) walking outward from
// c in rings. Group orders use it to give each unit its own destination.
func (g *Grid) Spiral(c Cell, n int) (Cell, bool) {
	for r := range max(g.W, g.H) + 1 {
		for _, p := range ring(c, r) {
			if !g.Passable(p) {
				continue
			}
			if n == 0 {
				return p, true
			}
			n--
		}
	}
	return Cell{}, false
}

// ring returns the cells at Chebyshev distance r from c, clockwise from the
// top-left corner.
func ring(c Cell, r int) []Cell {
	if r == 0 {
		return []Cell{c}
	}
	out := make([]Cell, 0, 8*r)
	for x := c.X - r; x <= c.X+r; x++ {
		out = append(out, Cell{X: x, Y: c.Y - r})
	}
	for y := c.Y - r + 1; y <= c.Y+r; y++ {
		out = append(out, Cell{X: c.X + r, Y: y})
	}
	for x := c.X + r - 1; x >= c.X-r; x-- {
		out = append(out, Cell{X: x, Y: c.Y + r})
	}
	for y := c.Y + r - 1; y > c.Y-r; y-- {
		out = append(out, Cell{X: c.X - r, Y: y})
	}
	return out
}

func (g *Grid) index(c Cell) int32 { return int32(c.Y*g.W + c.X) }

func (g *Grid) cellAt(i int32) Cell { return Cell{X: int(i) % g.W, Y: int(i) / g.W} }

package sim

import (
	"container/heap"
	"slices"
)

// Step costs: 10 straight, 14 diagonal (≈10√2).
const (
	costStraight = 10
	costDiagonal = 14
)

// neighbors are visited in this fixed order so ties always break the same way.
var neighbors = [8]struct {
	dx, dy int
	cost   int32
}{
	{0, -1, costStraight}, {1, -1, costDiagonal}, {1, 0, costStraight}, {1, 1, costDiagonal},
	{0, 1, costStraight}, {-1, 1, costDiagonal}, {-1, 0, costStraight}, {-1, -1, costDiagonal},
}

// pathfinder holds reusable A* buffers. A generation counter marks which
// entries belong to the current search, so nothing is cleared between
// searches. It is scratch space, not game state.
type pathfinder struct {
	g      []int32
	parent []int32
	seen   []uint32
	closed []uint32
	gen    uint32
	open   openHeap
}

func (pf *pathfinder) reset(n int) {
	if len(pf.g) != n {
		pf.g = make([]int32, n)
		pf.parent = make([]int32, n)
		pf.seen = make([]uint32, n)
		pf.closed = make([]uint32, n)
		pf.gen = 0
	}
	pf.gen++
	if pf.gen == 0 { // wrapped: old marks could look current
		clear(pf.seen)
		clear(pf.closed)
		pf.gen = 1
	}
	pf.open = pf.open[:0]
}

type openNode struct {
	f, h int32
	idx  int32
}

// openHeap orders by f, then h, then cell index: a total order, so the
// search is deterministic.
type openHeap []openNode

func (h openHeap) Len() int { return len(h) }
func (h openHeap) Less(i, j int) bool {
	a, b := h[i], h[j]
	if a.f != b.f {
		return a.f < b.f
	}
	if a.h != b.h {
		return a.h < b.h
	}
	return a.idx < b.idx
}
func (h openHeap) Swap(i, j int) { h[i], h[j] = h[j], h[i] }
func (h *openHeap) Push(x any)   { *h = append(*h, x.(openNode)) }
func (h *openHeap) Pop() any {
	old := *h
	n := old[len(old)-1]
	*h = old[:len(old)-1]
	return n
}

func octile(a, b Cell) int32 {
	dx, dy := abs(a.X-b.X), abs(a.Y-b.Y)
	return int32(costStraight*(dx+dy) + (costDiagonal-2*costStraight)*min(dx, dy))
}

// findPath returns the cells from (not including) from to to, using 8-way A*
// without cutting corners. A blocked destination is replaced by the nearest
// passable cell; an unreachable one by the closest cell the search reached.
func (w *World) findPath(from, to Cell) []Cell {
	g := w.Grid
	if !g.Passable(to) {
		c, ok := g.Spiral(to, 0)
		if !ok {
			return nil
		}
		to = c
	}
	if from == to || !g.In(from) {
		return nil
	}

	pf := &w.pf
	pf.reset(len(g.cells))
	start, goal := g.index(from), g.index(to)
	pf.g[start], pf.parent[start], pf.seen[start] = 0, -1, pf.gen
	h0 := octile(from, to)
	heap.Push(&pf.open, openNode{f: h0, h: h0, idx: start})

	best, bestH := start, h0
	for pf.open.Len() > 0 {
		cur := heap.Pop(&pf.open).(openNode)
		if pf.closed[cur.idx] == pf.gen {
			continue
		}
		pf.closed[cur.idx] = pf.gen
		if cur.idx == goal {
			best = goal
			break
		}
		if cur.h < bestH {
			best, bestH = cur.idx, cur.h
		}
		cc := g.cellAt(cur.idx)
		for _, nb := range neighbors {
			n := Cell{X: cc.X + nb.dx, Y: cc.Y + nb.dy}
			if !g.Passable(n) {
				continue
			}
			if nb.dx != 0 && nb.dy != 0 &&
				(!g.Passable(Cell{X: cc.X + nb.dx, Y: cc.Y}) || !g.Passable(Cell{X: cc.X, Y: cc.Y + nb.dy})) {
				continue
			}
			ni := g.index(n)
			if pf.closed[ni] == pf.gen {
				continue
			}
			ng := pf.g[cur.idx] + nb.cost
			if pf.seen[ni] == pf.gen && ng >= pf.g[ni] {
				continue
			}
			pf.g[ni], pf.parent[ni], pf.seen[ni] = ng, cur.idx, pf.gen
			h := octile(n, to)
			heap.Push(&pf.open, openNode{f: ng + h, h: h, idx: ni})
		}
	}

	var path []Cell
	for i := best; i != start; i = pf.parent[i] {
		path = append(path, g.cellAt(i))
	}
	slices.Reverse(path)
	return path
}

func (w *World) setPath(u *Unit, dest Cell) {
	u.Path = w.findPath(CellOf(u.Pos), dest)
	u.PathIdx = 0
}

func (u *Unit) clearPath() {
	u.Path = nil
	u.PathIdx = 0
}

func abs(x int) int {
	if x < 0 {
		return -x
	}
	return x
}

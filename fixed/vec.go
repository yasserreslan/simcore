package fixed

// Vec is a 2D vector in fixed-point. X grows right, Y grows down (screen space).
type Vec struct {
	X Fixed `json:"x"`
	Y Fixed `json:"y"`
}

// Add returns a+b.
func (a Vec) Add(b Vec) Vec { return Vec{X: a.X + b.X, Y: a.Y + b.Y} }

// Sub returns a-b.
func (a Vec) Sub(b Vec) Vec { return Vec{X: a.X - b.X, Y: a.Y - b.Y} }

// LenSq returns the squared length in raw units.
func (a Vec) LenSq() int64 { return int64(a.X)*int64(a.X) + int64(a.Y)*int64(a.Y) }

// Len returns the length, rounded down.
func (a Vec) Len() Fixed { return Fixed(Sqrt(a.LenSq())) }

// WithLen returns a scaled to length l. The zero vector stays zero.
func (a Vec) WithLen(l Fixed) Vec {
	n := a.Len()
	if n == 0 {
		return Vec{}
	}
	return Vec{X: a.X * l / n, Y: a.Y * l / n}
}

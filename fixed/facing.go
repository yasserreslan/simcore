package fixed

// Facings is the number of directions a unit can face. Facing 0 is north
// (up the screen) and facings increase clockwise, 11.25° apart. This matches
// the 32-direction sprite sheets.
const Facings = 32

// Common facings.
const (
	North = 0
	East  = 8
	South = 16
	West  = 24
)

// quarter holds sin(k × 11.25°) × 1024 for k = 0..8, rounded. The other
// quadrants are built from it by symmetry, so no trig runs at startup.
var quarter = [9]Fixed{0, 200, 392, 569, 724, 851, 946, 1004, 1024}

var dirs = buildDirs()

func buildDirs() [Facings]Vec {
	var d [Facings]Vec
	for i := range Facings {
		r := i % 8
		s, c := quarter[r], quarter[8-r]
		var sin, cos Fixed
		switch i / 8 {
		case 0:
			sin, cos = s, c
		case 1:
			sin, cos = c, -s
		case 2:
			sin, cos = -s, -c
		default:
			sin, cos = -c, s
		}
		d[i] = Vec{X: sin, Y: -cos}
	}
	return d
}

// Wrap maps any integer to a facing in [0, Facings).
func Wrap(f int) int { return ((f % Facings) + Facings) % Facings }

// Dir returns the unit vector for facing f.
func Dir(f int) Vec { return dirs[Wrap(f)] }

// FacingTo returns the facing closest to direction d. It returns false for
// the zero vector.
func FacingTo(d Vec) (int, bool) {
	if d.X == 0 && d.Y == 0 {
		return 0, false
	}
	best := 0
	var bestDot int64
	for i := range Facings {
		dot := int64(d.X)*int64(dirs[i].X) + int64(d.Y)*int64(dirs[i].Y)
		if i == 0 || dot > bestDot {
			best, bestDot = i, dot
		}
	}
	return best, true
}

// Diff returns the signed shortest turn from cur to want, in [-16, 15].
func Diff(cur, want int) int {
	d := Wrap(want - cur)
	if d >= Facings/2 {
		d -= Facings
	}
	return d
}

// TurnToward rotates cur toward want by at most rate facings, the short way.
func TurnToward(cur, want, rate int) int {
	d := Diff(cur, want)
	d = max(-rate, min(rate, d))
	return Wrap(cur + d)
}

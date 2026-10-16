// Package fixed provides deterministic fixed-point math for the simulation.
//
// The sim never uses floats: float results can differ between CPU
// architectures (Mac ARM vs Windows x86), and any difference desyncs a
// lockstep match. Integer math is bit-identical everywhere.
package fixed

// Shift is the number of fractional bits.
const Shift = 10

// One is 1.0. One map cell is One raw units wide.
const One Fixed = 1 << Shift

// Fixed is a signed fixed-point number with Shift fractional bits.
type Fixed int64

// FromInt converts an integer to Fixed.
func FromInt(i int) Fixed { return Fixed(i) << Shift }

// Frac returns num/den as Fixed, truncated toward zero.
func Frac(num, den int) Fixed { return Fixed((int64(num) << Shift) / int64(den)) }

// Mul returns a*b.
func (a Fixed) Mul(b Fixed) Fixed { return (a * b) >> Shift }

// Div returns a/b, truncated toward zero.
func (a Fixed) Div(b Fixed) Fixed { return (a << Shift) / b }

// Floor returns the integer part, rounded toward negative infinity.
func (a Fixed) Floor() int { return int(a >> Shift) }

// Abs returns |a|.
func (a Fixed) Abs() Fixed {
	if a < 0 {
		return -a
	}
	return a
}

// Sqrt returns floor(sqrt(n)). It panics if n is negative.
func Sqrt(n int64) int64 {
	if n < 0 {
		panic("fixed: Sqrt of negative number")
	}
	if n < 2 {
		return n
	}
	x := n
	y := (x + 1) / 2
	for y < x {
		x = y
		y = (x + n/x) / 2
	}
	return x
}

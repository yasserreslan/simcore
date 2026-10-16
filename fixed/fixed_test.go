package fixed

import "testing"

func TestSqrt(t *testing.T) {
	cases := []struct{ in, want int64 }{
		{0, 0}, {1, 1}, {2, 1}, {3, 1}, {4, 2}, {15, 3}, {16, 4},
		{1 << 40, 1 << 20}, {(1 << 40) - 1, (1 << 20) - 1},
	}
	for _, c := range cases {
		if got := Sqrt(c.in); got != c.want {
			t.Errorf("Sqrt(%d) = %d, want %d", c.in, got, c.want)
		}
	}
}

func TestMulDiv(t *testing.T) {
	a, b := FromInt(3), Frac(1, 2)
	if got := a.Mul(b); got != Frac(3, 2) {
		t.Errorf("3 × 0.5 = %d, want %d", got, Frac(3, 2))
	}
	if got := a.Div(b); got != FromInt(6) {
		t.Errorf("3 / 0.5 = %d, want %d", got, FromInt(6))
	}
	if got := Frac(-1, 2).Floor(); got != -1 {
		t.Errorf("Floor(-0.5) = %d, want -1", got)
	}
}

func TestDirsAreUnitLength(t *testing.T) {
	for i := range Facings {
		l := Dir(i).Len()
		if l < One-2 || l > One+2 {
			t.Errorf("facing %d has length %d, want ~%d", i, l, One)
		}
	}
}

func TestFacingTo(t *testing.T) {
	cases := []struct {
		d    Vec
		want int
	}{
		{Vec{X: 0, Y: -One}, North},
		{Vec{X: One, Y: 0}, East},
		{Vec{X: 0, Y: One}, South},
		{Vec{X: -One, Y: 0}, West},
		{Vec{X: One, Y: -One}, 4},
		{Vec{X: -One, Y: One}, 20},
	}
	for _, c := range cases {
		got, ok := FacingTo(c.d)
		if !ok || got != c.want {
			t.Errorf("FacingTo(%v) = %d, want %d", c.d, got, c.want)
		}
	}
	for i := range Facings {
		if got, _ := FacingTo(Dir(i)); got != i {
			t.Errorf("FacingTo(Dir(%d)) = %d", i, got)
		}
	}
}

func TestTurnToward(t *testing.T) {
	cases := []struct{ cur, want, rate, out int }{
		{0, 8, 1, 1},
		{0, 24, 1, 31},
		{30, 2, 2, 0},
		{5, 6, 4, 6},
		{0, 16, 32, 16},
	}
	for _, c := range cases {
		if got := TurnToward(c.cur, c.want, c.rate); got != c.out {
			t.Errorf("TurnToward(%d, %d, %d) = %d, want %d", c.cur, c.want, c.rate, got, c.out)
		}
	}
}

package sim

import "github.com/yasserreslan/simcore/fixed"

const (
	repathTicks       = TicksPerSecond // chasing units re-plan once a second
	turretTurnRate    = 2              // facings per tick
	moveAlignFacings  = 2              // hull must be this close to the path direction to drive
	fireAlignFacings  = 1              // weapon must be this close to the target to fire
	splashEdgePercent = 50             // damage at the edge of a splash, in percent
)

// think updates a unit's order and target.
func (w *World) think(u *Unit) {
	def := u.Def()
	switch u.Order.Kind {
	case OrderMove:
		if !u.Moving() {
			u.Order = Order{}
			u.clearPath()
		}
	case OrderAttack:
		target := w.Unit(u.Order.Target)
		if target == nil {
			u.Order = Order{}
			u.clearPath()
			break
		}
		if w.inRange(u, target) {
			u.clearPath()
			break
		}
		if def.Speed > 0 && (!u.Moving() || w.Tick >= u.RepathAt) {
			w.setPath(u, CellOf(target.Pos))
			u.RepathAt = w.Tick + repathTicks
		}
	case OrderIdle:
	}

	if u.Order.Kind == OrderAttack || def.Weapon == nil {
		u.AutoTarget = 0
		return
	}
	// Keep the current auto target while it stays in range, so fire doesn't
	// flicker between enemies.
	if t := w.Unit(u.AutoTarget); t != nil && w.inRange(u, t) {
		return
	}
	u.AutoTarget = w.nearestEnemyInRange(u)
}

// nearestEnemyInRange returns the closest enemy in weapon range, ties going
// to the lowest ID, or 0.
func (w *World) nearestEnemyInRange(u *Unit) EntityID {
	r := int64(u.Def().Weapon.Range)
	var best EntityID
	var bestD int64
	for _, o := range w.Units {
		if o.Owner == u.Owner || o.Owner == 0 {
			continue
		}
		d := o.Pos.Sub(u.Pos).LenSq()
		if d > r*r {
			continue
		}
		if best == 0 || d < bestD {
			best, bestD = o.ID, d
		}
	}
	return best
}

func (w *World) inRange(u, target *Unit) bool {
	r := int64(u.Def().Weapon.Range)
	return target.Pos.Sub(u.Pos).LenSq() <= r*r
}

// move drives a unit along its path. Vehicles turn in place until roughly
// facing the next cell.
func (w *World) move(u *Unit) {
	def := u.Def()
	if def.Speed == 0 || !u.Moving() {
		return
	}
	wp := u.Path[u.PathIdx].Center()
	d := wp.Sub(u.Pos)
	want, ok := fixed.FacingTo(d)
	if !ok {
		u.PathIdx++
		return
	}
	u.Facing = fixed.TurnToward(u.Facing, want, def.TurnRate)
	if !def.Turret {
		u.TurretFacing = u.Facing
	}
	if abs(fixed.Diff(u.Facing, want)) > moveAlignFacings {
		return
	}
	if d.Len() <= def.Speed {
		u.Pos = wp
		u.PathIdx++
	} else {
		u.Pos = u.Pos.Add(d.WithLen(def.Speed))
	}
}

type hit struct {
	weapon *Weapon
	target EntityID
	at     fixed.Vec
}

// fire aims weapons and returns the shots fired this tick. Damage is applied
// afterwards, so units that shoot each other on the same tick both fire.
func (w *World) fire() []hit {
	var hits []hit
	for _, u := range w.Units {
		def := u.Def()
		wpn := def.Weapon
		if wpn == nil {
			continue
		}
		if u.Reload > 0 {
			u.Reload--
		}
		id := u.AutoTarget
		if u.Order.Kind == OrderAttack {
			id = u.Order.Target
		}
		target := w.Unit(id)
		if target == nil || !w.inRange(u, target) {
			if def.Turret {
				u.TurretFacing = fixed.TurnToward(u.TurretFacing, u.Facing, turretTurnRate)
			}
			continue
		}

		want, _ := fixed.FacingTo(target.Pos.Sub(u.Pos))
		var aim int
		if def.Turret {
			u.TurretFacing = fixed.TurnToward(u.TurretFacing, want, turretTurnRate)
			aim = u.TurretFacing
		} else {
			if u.Moving() {
				continue // fixed weapons can't fire on the move
			}
			u.Facing = fixed.TurnToward(u.Facing, want, def.TurnRate)
			u.TurretFacing = u.Facing
			aim = u.Facing
		}
		if u.Reload > 0 || abs(fixed.Diff(aim, want)) > fireAlignFacings {
			continue
		}

		u.Reload = wpn.ReloadTicks
		at := target.Pos
		if wpn.Scatter > 0 {
			s := int(wpn.Scatter)
			at = at.Add(fixed.Vec{X: fixed.Fixed(w.rng.Range(-s, s)), Y: fixed.Fixed(w.rng.Range(-s, s))})
		}
		hits = append(hits, hit{weapon: wpn, target: target.ID, at: at})
	}
	return hits
}

// applyHits deals damage. Splash hits everyone in the radius, friends
// included, falling off to splashEdgePercent at the edge.
func (w *World) applyHits(hits []hit) {
	for _, h := range hits {
		if h.weapon.Splash == 0 {
			if t := w.Unit(h.target); t != nil {
				t.HP -= damage(h.weapon, t.Def().Armor, 100)
			}
			continue
		}
		r := int64(h.weapon.Splash)
		for _, u := range w.Units {
			d := int64(u.Pos.Sub(h.at).Len())
			if d > r {
				continue
			}
			pct := 100 - (100-splashEdgePercent)*d/r
			u.HP -= damage(h.weapon, u.Def().Armor, pct)
		}
	}
}

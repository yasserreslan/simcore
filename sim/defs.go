package sim

import "github.com/yasserreslan/simcore/fixed"

// Armor is a unit's armor class.
type Armor uint8

// Armor classes.
const (
	ArmorNone Armor = iota
	ArmorLight
	ArmorHeavy
	ArmorBuilding
)

// Warhead is the damage type a weapon deals.
type Warhead uint8

// Warheads.
const (
	WarheadBullet Warhead = iota
	WarheadAP
	WarheadRocket
	WarheadHE
)

// damageTable[warhead][armor] is the percent of weapon damage dealt.
var damageTable = [4][4]int64{
	WarheadBullet: {100, 60, 20, 15},
	WarheadAP:     {30, 75, 100, 50},
	WarheadRocket: {25, 90, 100, 60},
	WarheadHE:     {90, 70, 40, 100},
}

// Weapon is a unit's weapon. Shots hit instantly; Scatter offsets where a
// splash shell lands.
type Weapon struct {
	Name        string
	Damage      int
	Range       fixed.Fixed
	ReloadTicks int
	Warhead     Warhead
	Splash      fixed.Fixed // radius; 0 = single target
	Scatter     fixed.Fixed // max landing offset per axis
}

// TypeID identifies a unit type.
type TypeID uint8

// Unit types.
const (
	Rifleman TypeID = iota
	RocketTrooper
	ScoutBuggy
	BattleTank
	Artillery
	Harvester
	ConstructionVehicle
)

// UnitType is the static definition of a kind of unit.
type UnitType struct {
	Key      string
	Name     string
	Glyph    rune // debug-view letter
	Cost     int
	MaxHP    int
	Speed    fixed.Fixed // per tick; 0 = cannot move
	TurnRate int         // facings per tick
	Armor    Armor
	Turret   bool // weapon aims independently of the hull
	Weapon   *Weapon
}

// cells converts tenths of a cell to Fixed.
func cells(tenths int) fixed.Fixed { return fixed.Frac(tenths, 10) }

// cellsPerSecond converts a speed in tenths of a cell per second to Fixed
// per tick.
func cellsPerSecond(tenths int) fixed.Fixed { return fixed.Frac(tenths, 10*TicksPerSecond) }

var (
	rifle  = Weapon{Name: "Rifle", Damage: 15, Range: cells(40), ReloadTicks: 20, Warhead: WarheadBullet}
	rocket = Weapon{Name: "Rocket", Damage: 60, Range: cells(50), ReloadTicks: 60, Warhead: WarheadRocket}
	mg     = Weapon{Name: "MG", Damage: 10, Range: cells(40), ReloadTicks: 10, Warhead: WarheadBullet}
	cannon = Weapon{Name: "Cannon", Damage: 60, Range: cells(45), ReloadTicks: 40, Warhead: WarheadAP}
	shell  = Weapon{
		Name: "Shell", Damage: 80, Range: cells(90), ReloadTicks: 100, Warhead: WarheadHE,
		Splash: cells(10), Scatter: cells(5),
	}
)

// UnitTypes holds every unit definition, indexed by TypeID. Values are the
// starting stats from the design doc, for tuning.
var UnitTypes = [...]UnitType{
	Rifleman: {
		Key: "rifleman", Name: "Rifleman", Glyph: 'R', Cost: 100, MaxHP: 50,
		Speed: cellsPerSecond(10), TurnRate: 8, Armor: ArmorNone, Weapon: &rifle,
	},
	RocketTrooper: {
		Key: "rocket_trooper", Name: "Rocket trooper", Glyph: 'K', Cost: 300, MaxHP: 45,
		Speed: cellsPerSecond(9), TurnRate: 8, Armor: ArmorNone, Weapon: &rocket,
	},
	ScoutBuggy: {
		Key: "scout_buggy", Name: "Scout buggy", Glyph: 'B', Cost: 400, MaxHP: 150,
		Speed: cellsPerSecond(30), TurnRate: 2, Armor: ArmorLight, Turret: true, Weapon: &mg,
	},
	BattleTank: {
		Key: "battle_tank", Name: "Battle tank", Glyph: 'T', Cost: 800, MaxHP: 400,
		Speed: cellsPerSecond(16), TurnRate: 1, Armor: ArmorHeavy, Turret: true, Weapon: &cannon,
	},
	Artillery: {
		Key: "artillery", Name: "Artillery", Glyph: 'A', Cost: 900, MaxHP: 150,
		Speed: cellsPerSecond(12), TurnRate: 1, Armor: ArmorLight, Weapon: &shell,
	},
	Harvester: {
		Key: "harvester", Name: "Harvester", Glyph: 'H', Cost: 1400, MaxHP: 600,
		Speed: cellsPerSecond(12), TurnRate: 1, Armor: ArmorHeavy,
	},
	ConstructionVehicle: {
		Key: "construction_vehicle", Name: "Construction vehicle", Glyph: 'M', Cost: 2500, MaxHP: 800,
		Speed: cellsPerSecond(8), TurnRate: 1, Armor: ArmorHeavy,
	},
}

// damage returns the damage weapon w deals to armor a, scaled by pct
// (100 = full hit, less at the edge of a splash). It is at least 1.
func damage(w *Weapon, a Armor, pct int64) int {
	d := int64(w.Damage) * damageTable[w.Warhead][a] / 100 * pct / 100
	return int(max(d, 1))
}

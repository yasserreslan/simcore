using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The garrison system: soldiers on an Enter order for a garrisonable building
// walk to its nearest door and go inside (see RunBoarding). A building
// belongs to whoever is inside and is neutral (owner 0) while empty; nobody
// may enter a building held by an enemy. An owned post (the observation post)
// stays its builder's for good: only its side enters, once it is finished. Occupants fire from the building's
// position with extra range and vision, can't be hit directly, and take a
// share of explosive hits on the building. A building's damage stage follows
// its HP; each stage holds fewer soldiers, and the excess is thrown out hurt.
// When it collapses, each occupant survives (hurt) with the spec's chance,
// rolled on the world's generator in occupant ID order. The footprint stays
// blocked as rubble. Buildings don't block line of sight (no LOS yet).
public sealed partial class World
{
    // StageOf returns building b's damage stage from its HP: 0 while above the
    // first threshold, one more per threshold passed; the rubble stage (one past
    // the last) once destroyed.
    internal static int StageOf(Unit b, GarrisonSpec g)
    {
        if (!b.Alive)
        {
            return g.Stages.Count + 1;
        }
        var pct = (long)b.HP * 100;
        for (var i = 0; i < g.Stages.Count; i++)
        {
            if (pct > (long)g.Stages[i] * b.Def.MaxHp)
            {
                return i;
            }
        }
        return g.Stages.Count;
    }

    // CapacityOf returns how many soldiers b holds in its current stage.
    internal static int CapacityOf(Unit b, GarrisonSpec g)
    {
        var s = StageOf(b, g);
        return s < g.StageCapacity.Count ? g.Capacity * g.StageCapacity[s] / 100 : 0;
    }

    // CountOccupants returns how many live soldiers are inside b.
    private int CountOccupants(Unit b)
    {
        var n = 0;
        foreach (var u in _units)
        {
            if (u.Garrison == b.Id && u.Alive)
            {
                n++;
            }
        }
        return n;
    }

    // CanGarrison reports whether u may go into building b now: b is finished, neutral
    // (not an owned post) or u's side holds it, it has room in its stage, and u is a soldier it accepts.
    internal bool CanGarrison(Unit u, Unit b) =>
        b.Def.Archetype.Garrison is { } g && b.Alive && b.IsBuilt && u.Alive && u.Inside == 0 && u.Garrison == 0 &&
        u.Def.Crew is null && !u.Def.IsStructure && u.Def.Layer == Layers.Ground && u.Def.Tags.Matches(g.Tags) &&
        ((b.Owner == 0 && !g.Owned) || b.Owner == u.Owner) && CountOccupants(b) < CapacityOf(b, g);

    /// <summary>
    /// The occupant that makes building post a launch point now (<see cref="GarrisonSpec.LauncherTags"/>:
    /// the observation post's sniper), the first in ID order; null when none is inside or the
    /// building has no such rule.
    /// </summary>
    public Unit? PostLauncher(Unit post)
    {
        if (post.Def.Archetype.Garrison is not { LauncherTags.IsEmpty: false } g || !post.Alive || !post.IsBuilt)
        {
            return null;
        }
        foreach (var u in _units)
        {
            if (u.Garrison == post.Id && u.Alive && u.Def.Tags.Matches(g.LauncherTags))
            {
                return u;
            }
        }
        return null;
    }

    /// <summary>
    /// The map cell of garrisonable building b's door i: content gives it from the unturned top-left cell of the footprint
    /// (just outside it, on the front), and it turns with the building (<see cref="Unit.Quarter"/>, <see cref="LayoutCell(Unit, int, int)"/>).
    /// </summary>
    public static Cell DoorCell(Unit b, int i)
    {
        var d = b.Def.Archetype.Garrison!.Doors[i];
        return LayoutCell(b, d.X, d.Y);
    }

    /// <summary>The index of building b's door closest to p (its cell's centre), ties going to the lower index.</summary>
    public static int NearestDoor(Unit b, Vec p)
    {
        var doors = b.Def.Archetype.Garrison!.Doors;
        var best = 0;
        long bestD = 0;
        for (var i = 0; i < doors.Count; i++)
        {
            var d = (DoorCell(b, i).Center() - p).LenSq();
            if (i == 0 || d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        return best;
    }

    // EnterBuilding puts u inside b at once, wherever u is; scenarios use it to spawn garrisons.
    internal static void EnterBuilding(Unit u, Unit b)
    {
        var g = b.Def.Archetype.Garrison!;
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Garrison = b.Id;
        u.Pos = b.Pos;
        u.RangeBonus = g.RangeBonus;
        u.VisionBonus = g.VisionBonus;
        u.Cover = (FootW(b), FootH(b));
        b.Owner = u.Owner;
    }

    // LeaveBuilding puts u out of b on the n-th free cell around b's door. A
    // building left empty turns neutral at the end of the tick.
    private void LeaveBuilding(Unit u, Unit b, int door, int n)
    {
        u.Garrison = 0;
        u.RangeBonus = default;
        u.VisionBonus = default;
        u.Cover = default;
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Pos = Grid.TrySpiral(DoorCell(b, door), n, out var c) ? c.Center() : DoorCell(b, door).Center();
    }

    // Evacuate puts every soldier in b out, in ID order: at the door nearest
    // toward (when given), otherwise spread over the doors in turn.
    private void Evacuate(Unit b, Vec? toward)
    {
        var doors = b.Def.Archetype.Garrison!.Doors.Count;
        var k = 0;
        foreach (var u in _units)
        {
            if (u.Garrison != b.Id)
            {
                continue;
            }
            if (toward is { } p)
            {
                LeaveBuilding(u, b, NearestDoor(b, p), k);
            }
            else
            {
                LeaveBuilding(u, b, k % doors, k / doors);
            }
            k++;
        }
    }

    // Hurt deals a warhead's damage at pct percent to t. A garrisoned building
    // hit by one of its occupant warheads (explosives) passes the spec's share
    // of the hit on to every soldier inside, as if each were hit at pct.
    internal void Hurt(Unit t, Warhead w, long damage, long pct)
    {
        if (t.Def.Archetype.Invulnerable)
        {
            return; // never damaged (an airfield's pavement and open stands)
        }
        var by = _hurtBy;
        // Directional armour (the tank's): the hit scaled by where it lands (ArmorSystem.cs); 100 for everything else.
        var da = t.Def.Archetype.DirectionalArmor;
        var facing = da is null ? HitFacing.Side : HitFacingOf(t, da, by);
        var facingPct = da is null ? 100 : da.PercentOf(facing);
        if (by is not null && (by.Owner == t.Owner || by.Owner == 0))
        {
            by = null; // only an enemy's shot counts
        }
        if (by is not null)
        {
            RecordHit(t, by); // for Return fire and retaliation (the unit AI)
        }
        // A max-HP rule for its armor (a drone's charge on a building) takes that share whatever the hit.
        var armor = ArmorOf(t); // an aircraft on the ground has its ground armor (M3b)
        var share = w.MaxHpPercentOf(armor);
        var hp = t.HP;
        if (share > 0 && w.MaxHpBySplash)
        {
            share = (int)(share * pct / 100); // the incendiary: the share falls off with the splash (the outer ring its percent)
            facingPct = 100; // and fire wraps a hull: directional armour does not change it
        }
        t.HP -= share > 0 ? Math.Max(1, (int)((long)t.Def.MaxHp * share * facingPct / 10000))
            : w.MaxHpBySplash && w.MaxHpPercentOf(armor) > 0 ? 1 : w.Damage(damage * facingPct / 100, armor, pct);
        if (t.Squad != 0 && t.HP < hp)
        {
            AmbushMemberHurt(t); // a member of a set ambush hurt: it springs (SquadAmbushSystem.cs)
        }
        if (hp > 0 && t.HP <= 0 && t.Def.Crew is { PassengerSurvive.Count: > 0 })
        {
            // The killing hit on a carrier with survival bands (the Merkava type): its damage before armour decides the passengers'
            // band (RunEjection); an incendiary's burn counts as fire (0).
            t.KillHit = share > 0 && w.MaxHpBySplash ? 0 : (int)Math.Clamp(damage * pct / 100, 0, int.MaxValue);
        }
        if (da is not null)
        {
            _armorHits.Add(new ArmorHit(t.Id, facing, facingPct, hp - t.HP, _hurtBy?.Id ?? 0));
        }
        if (by is not null && !t.Def.IsStructure)
        {
            Retaliate(t, by); // its idle friends nearby fight back, even if the hit killed it
        }
        if (t.Def.Crew is { OccupantDamage: > 0 } crew && t.Driver != 0 && crew.OccupantWarheads.Contains(w.Index) &&
            Unit(t.Driver) is { Alive: true } driver && w.CanHit(driver.Def.Armor))
        {
            // An open emplacement's crewman (the howitzer's) takes his share of an explosive hit on it, as if hit himself; killed,
            // he leaves the seat empty (RunEjection).
            driver.HP -= (int)((long)w.Damage(damage, driver.Def.Armor, pct) * crew.OccupantDamage / 100);
            if (by is not null)
            {
                RecordHit(driver, by);
            }
        }
        if (t.Def.Archetype.Garrison is not { OccupantDamage: > 0 } g || !g.OccupantWarheads.Contains(w.Index))
        {
            return;
        }
        foreach (var o in _units)
        {
            if (o.Garrison == t.Id && o.Alive && w.CanHit(o.Def.Armor))
            {
                o.HP -= (int)((long)w.Damage(damage, o.Def.Armor, pct) * g.OccupantDamage / 100);
                if (by is not null)
                {
                    RecordHit(o, by);
                }
            }
        }
    }

    // RunCollapse runs just before cleanup: the occupants of each building
    // destroyed this tick get out, each surviving with the collapse chance at
    // most the collapse HP, or dying in the ruins.
    private void RunCollapse()
    {
        foreach (var b in _units)
        {
            if (b.Alive || b.Def.Archetype.Garrison is not { } g)
            {
                continue;
            }
            var doors = g.Doors.Count;
            var k = 0;
            foreach (var u in _units)
            {
                if (u.Garrison != b.Id)
                {
                    continue;
                }
                var alive = u.Alive;
                LeaveBuilding(u, b, k % doors, k / doors);
                k++;
                if (!alive)
                {
                    continue;
                }
                u.HP = _rng.Intn(100) < g.CollapseSurvival ? Math.Max(1, Math.Min(u.HP, u.Def.MaxHp * g.CollapseHp / 100)) : 0;
            }
            b.Occupants = 0;
            b.Stage = g.Stages.Count + 1;
        }
    }

    // The buildings of the live occupants (one entry each), sorted: RunGarrisons's scratch list, not state (it is rebuilt
    // from the units every time).
    private readonly List<uint> _occupiedBuildings = [];

    internal bool FullGarrisonScan { get; set; } // test/benchmark oracle: each building's occupants counted by a scan of every unit

    // RunGarrisons runs after cleanup: each building's stage follows its HP,
    // soldiers beyond the stage's capacity are thrown out hurt (highest ID
    // first), and an empty building turns neutral.
    //
    // The occupants are counted in one pass over the units first (each live soldier inside a building files that
    // building's ID; the IDs sorted, then walked beside the buildings in ID order), not by a scan of every unit for every
    // building: a town of a thousand buildings made that a million visits a tick. The counts are the same as counting
    // each building's in turn: ejecting building b's excess changes only b's own occupants (their Garrison goes to 0 and
    // they stay alive, HP at least 1), never a later building's.
    private void RunGarrisons()
    {
        var occupied = _occupiedBuildings;
        occupied.Clear();
        foreach (var u in _units)
        {
            if (u.Garrison != 0 && u.Alive)
            {
                occupied.Add(u.Garrison);
            }
        }
        occupied.Sort();
        var next = 0;
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Garrison is not { } g)
            {
                continue;
            }
            var inside = 0;
            while (next < occupied.Count && occupied[next] < b.Id)
            {
                next++;
            }
            while (next < occupied.Count && occupied[next] == b.Id)
            {
                inside++;
                next++;
            }
            b.Stage = StageOf(b, g);
            var count = FullGarrisonScan ? CountOccupants(b) : inside;
            var excess = count - CapacityOf(b, g);
            for (var i = _units.Count - 1; i >= 0 && excess > 0; i--)
            {
                var u = _units[i];
                if (u.Garrison != b.Id)
                {
                    continue;
                }
                LeaveBuilding(u, b, excess % g.Doors.Count, excess / g.Doors.Count);
                u.HP = Math.Max(1, u.HP - u.Def.MaxHp * g.EjectDamage / 100);
                excess--;
                count--;
            }
            b.Occupants = count;
            if (count == 0 && !g.Owned)
            {
                b.Owner = 0;
            }
        }
    }
}

using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The ring blast (content weapon.blast; the MOAB type bomb dropped by the Sentinel's MC-130 type plane; docs/design.md, "Massive bomb
// plane"). Where such a bomb goes off (BombBlast), instead of the splash falloff:
//  1. The rings: every unit on the ground within the last ring's radius (a structure by its nearest footprint point; the distance
//     3D on terrain) takes the ring it is in. A ring that destroys destroys it whatever its armour, with whoever is inside (the
//     penetrator's Obliterate: a garrison, a crew, riders); the other rings deal the weapon's damage at the ring's percent (falling
//     linearly to its edge percent across it) through the warhead's table, a crest between halving it (as any splash, outside a
//     destroying ring). Flying aircraft, units inside others (they share their carrier's fate), the airfields' invulnerable
//     pavement and tunnel entrances (step 3) are left out of a destroying ring. Visited in the spatial index's order (by ID).
//  2. Garrisons: a building this blast brings down takes its garrison with it (no collapse roll: everyone inside dies).
//  3. Tunnels: every tunnel entrance within the blast's tunnel radius collapses into rubble and every unit underground at it, or on
//     a passage at a place within the radius, dies (the penetrator's tunnel rule, CollapseTunnels, at this radius).
//  4. Forest: within the flatten radius every standing trunk is felled (a stump, no log: World.Stumps) and every forest cell
//     denser than the blast's flattenTo drops to it (TerrainMap.Flattened; sight from the air opens there: no cover). The digest
//     reads a flattened cell's forest as it was; the world hashes the flattenings (World.Flattens).
//  5. Fire: the warhead's fire block, then the weapon's own (the core and the heavy ring of the MOAB), after the flattening (the
//     flattened scrub burns), under the fire rules' cap.
// The warning: a bomb whose delivery warns (delivery.warn) is in World.IncomingStrikes for every enemy player from its release to
// its impact, with Warned and Struck events in World.IncomingEvents (TerminalSystem.cs, AirStrikeSystem.cs).
public sealed partial class World
{
    private readonly List<ForestFlatten> _flattens = [];
    private readonly List<Unit> _ringNear = [];
    private readonly List<Unit> _ringFallen = [];
    private readonly List<IncomingEvent> _releaseWarnings = []; // this tick's warned releases, into IncomingEvents by RunProjectiles (empty between ticks)

    /// <summary>
    /// Every forest flattening so far (a ring blast's, <see cref="RingBlast.FlattenRaw"/>), in the order they happened: the forest
    /// cells within its radius denser than its density were dropped to it and their standing trunks felled
    /// (<see cref="TerrainMap.Flattened"/> says which cells). Hashed while not empty.
    /// </summary>
    public IReadOnlyList<ForestFlatten> Flattens => _flattens;

    // RingBlastAt sets off ring blast rb of bomb p at `at` (see the top of this file). _hurtBy is the bomber (BombBlast).
    private void RingBlastAt(Projectile p, Vec at, RingBlast rb)
    {
        var w = p.Weapon;
        var by = _hurtBy;
        var outer = rb.Rings[^1].RadiusRaw;
        var m = outer + Fixed.OneRaw;
        _ringFallen.Clear();
        (_blastHas, _blastAt) = (true, at); // directional armour takes it from where it went off (ArmorSystem.cs)
        foreach (var u in Around(at.X.Raw - m, at.Y.Raw - m, at.X.Raw + m, at.Y.Raw + m, _ringNear, true))
        {
            if (!u.Alive || u.Inside != 0 || u.Garrison != 0 ||
                (u.Def.Archetype.Flight is not null && !Grounded(u)) || (u.Def.Archetype.AirBody is not null && !Grounded(u)))
            {
                continue;
            }
            var q = AimPoint(at, u);
            var d = Ground is null ? (q - at).Len().Raw : SplashDistance(at, q);
            var ring = rb.RingAt(d);
            if (ring < 0)
            {
                continue;
            }
            if (rb.Rings[ring].Destroys)
            {
                if (!u.Def.Archetype.Invulnerable && u.Def.Archetype.Tunnel is null)
                {
                    Obliterate(u, by);
                }
                continue;
            }
            if (!w.Warhead.CanHit(ArmorOf(u)))
            {
                continue;
            }
            var pct = rb.PercentAt(d);
            if (Ground is not null && Shielded(at, q))
            {
                pct = pct * ShieldedPercent / 100; // a crest between the blast and the unit
            }
            var hp = u.HP;
            Hurt(u, w.Warhead, w.Damage, pct);
            if (hp > 0 && u.HP <= 0 && u.Def.Archetype.Garrison is not null)
            {
                _ringFallen.Add(u);
            }
        }
        _blastHas = false;
        // 2. The buildings it brought down take their garrisons with them.
        if (_ringFallen.Count > 0)
        {
            foreach (var o in _units)
            {
                if (o.Alive && o.Garrison != 0 && _ringFallen.Exists(b => b.Id == o.Garrison))
                {
                    if (by is not null && by.Owner != o.Owner && by.Owner != 0)
                    {
                        RecordHit(o, by);
                    }
                    o.HP = 0;
                }
            }
        }
        // 3. The tunnels, 4. the forest, 5. the fire.
        if (rb.TunnelRadiusRaw > 0)
        {
            CollapseTunnels(p, at, rb.TunnelRadiusRaw, by);
        }
        if (rb.FlattenRaw > 0)
        {
            FlattenForest(p, at, rb.FlattenRaw, rb.FlattenTo);
        }
        FellByBlast(at, w.Warhead, w.Effect.Splash); // (a warhead the terrain rules fell trees with: none of the MOAB's)
        IgniteByBlast(at, w.Warhead, w.Effect.Splash);
        if (w.Effect.Fire is { } wf)
        {
            IgniteByFire(at, wf, w.Effect.Splash);
        }
    }

    // FlattenForest knocks the forest within r of `at` flat (step 4): each cell whose centre is within r, in row then column order:
    // its standing trunk felled (a stump), its density dropped to `to` when denser. Recorded once (World.Flattens) when it changed anything.
    private void FlattenForest(Projectile p, Vec at, long r, int to)
    {
        if (Ground is not { } g)
        {
            return;
        }
        int cx0 = Math.Max((int)((at.X.Raw - r) >> Fixed.Shift), 0), cx1 = Math.Min((int)((at.X.Raw + r) >> Fixed.Shift), g.Width - 1);
        int cy0 = Math.Max((int)((at.Y.Raw - r) >> Fixed.Shift), 0), cy1 = Math.Min((int)((at.Y.Raw + r) >> Fixed.Shift), g.Height - 1);
        var cells = 0;
        var felled = 0;
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var c = new Cell(cx, cy);
                var trunk = g.HasTrunks && g.TrunkAt(cy * g.Width + cx);
                if (!trunk && g.Forest(c) <= to)
                {
                    continue;
                }
                var q = c.Center();
                long dx = q.X.Raw - at.X.Raw, dy = q.Y.Raw - at.Y.Raw;
                if (dx * dx + dy * dy > r * r)
                {
                    continue;
                }
                if (trunk && g.FellTrunk(c))
                {
                    _stumps.Add(c);
                    _treesUnsorted = true;
                    felled++;
                }
                if (g.Flatten(c, to))
                {
                    cells++;
                }
            }
        }
        if (cells > 0 || felled > 0)
        {
            _flattens.Add(new ForestFlatten(p.Id, at, r, to, Tick, cells, felled));
        }
    }

    // HashFlattens mixes in the forest flattenings: only once there was one, so games without hash as before.
    private void HashFlattens(ref WordHash h)
    {
        if (_flattens.Count == 0)
        {
            return;
        }
        h.Put(0xF1A7);
        h.PutSigned(_flattens.Count);
        foreach (var f in _flattens)
        {
            h.Put(f.Bomb);
            h.PutSigned(f.At.X.Raw);
            h.PutSigned(f.At.Y.Raw);
            h.PutSigned(f.RadiusRaw);
            h.PutSigned(f.To);
            h.Put(f.Tick);
            h.PutSigned(f.Cells);
            h.PutSigned(f.Felled);
        }
    }
}

/// <summary>A forest flattening by a ring blast (<see cref="World.Flattens"/>).</summary>
/// <param name="Bomb">The bomb's projectile ID (its <see cref="BombImpact"/> went off at the same tick).</param>
/// <param name="At">The blast's centre.</param>
/// <param name="RadiusRaw">The radius flattened (raw): every forest cell whose centre is within it.</param>
/// <param name="To">The forest density the flattened cells were dropped to (0 none, 1 scrub, 2 grove).</param>
/// <param name="Tick">The tick it happened.</param>
/// <param name="Cells">How many cells it dropped.</param>
/// <param name="Felled">How many standing trunks it felled (stumps now).</param>
public readonly record struct ForestFlatten(uint Bomb, Vec At, long RadiusRaw, int To, uint Tick, int Cells, int Felled);

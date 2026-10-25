using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The helicopters' attacks (the helicopters night; docs/design.md, "Helicopters"): which target, which weapons, and the
// flying AI's firing positions.
//  - Targets: an Attack order's unit (seen by its side), an Attack-ground order's point (rockets and guns), or one it
//    picks (scanned twice a second, staggered by ID) on Guard (within its leash of the post), attack-move and patrol
//    (within its leash of where it is), or idle in a firing stance (within its weapons' reach of where it hovers): the
//    best weapon's damage a second against it over its distance, ties to the lower ID. Spotted enemy ground units and
//    structures; never aircraft (a parked one only takes splash, as for the jets).
//  - Weapons: every weapon with ammunition that is good against the target (at least a third of the best's damage a
//    second; a missile only when it is the best: no missile at a soldier), reloaded, within its range (slant from its height), within its mount's cone of the
//    nose (fixed missiles and pods; the chin turret wide) and with a line of sight over the terrain and the canopy. Guns hit
//    at once (scattered, a splash); rocket pods fire their salvo (straight lines from its height); missiles fly guided on
//    their own seeker (fire and forget) at its target; the crests still stop them.
//  - Firing positions: a missile attack beyond its guns' reach looks for cover first (the pop-up): points at its standoff
//    range round the target toward where it is (and a quarter nearer), each with a crest or woods hiding it at its hide
//    height and a pop-up height within its reach that clears the line: it flies there low, hides, rises once its missile
//    is ready, fires, ducks and goes again (with no cover: a standoff hover). Guns and rockets: a run to a part of the
//    weapon's range, then sidestepping round the target, nose on it.
public sealed partial class World
{
    // A target it picks is looked for this often (ticks), staggered by ID.
    private const int HeliScanTicks = 10;

    // The pop-up: the margin over the line of sight it rises by (raw: 2 m); it hides at least this long (1 s); it gives up a
    // rise that never clears after this long (10 s); exposed after a launch for this long (0.5 s); it looks for new cover
    // when the target moved this far (8 cells).
    private const long PopMarginRaw = 2 * 512;
    private const int HideTicks = TicksPerSecond, RiseTicks = 10 * TicksPerSecond, ExposedTicks = TicksPerSecond / 2;
    private const long CoverMoveRaw = 8 * Fixed.OneRaw;

    // The target's body over its ground (raw: 1 m), a wall's (2 m).
    private const long HeliBodyRaw = 512, HeliWallRaw = 1024;

    // The cover search's bearings round the target from where the helicopter is: 0, +-15, +-30, +-45, +-60 degrees
    // (cos, sin in 1/16384), nearest the straight line first.
    private static readonly (int Cos, int Sin)[] CoverBearings =
        [(16384, 0), (15826, 4240), (15826, -4240), (14189, 8192), (14189, -8192), (11585, 11585), (11585, -11585), (8192, 14189), (8192, -14189)];

    private readonly List<Hit> _heliHits = []; // scratch
    private readonly List<Unit> _nearHeli = []; // scratch

    // ---- The flight side: firing positions ----

    // Engage flies helicopter u's attack on t (a unit) or a point (t null) at aim for a tick.
    private void Engage(Unit u, HeliState h, ScoutFlight f, RotarySpec r, Unit? t, Vec aim)
    {
        h.AimPoint = aim;
        var wi = t is not null ? PrimaryWeapon(u, t) : HeliGroundWeapon(u);
        if (wi < 0)
        {
            StopAttack(h);
            FlyTo(u, h, f, r, h.Post, Cruise(u), aim, f.HeightRaw, -1, true);
            return;
        }
        var w = u.Def.Weapons[wi];
        var gun = t is not null ? GunReach(u, t) : 0;
        var dist = Len3(aim.X.Raw - u.Pos.X.Raw, aim.Y.Raw - u.Pos.Y.Raw, 0);
        if (w.Effect.Guided is not null && (gun == 0 || dist > gun))
        {
            PopUp(u, h, f, r, t, aim, wi);
        }
        else
        {
            Run(u, h, f, r, aim, w);
        }
        h.Post = u.Pos;
    }

    // Run: a gun or rocket run: close to GunPercent of the weapon's range, then sidestep round the aim (the goal swung 20
    // degrees round it from where it is, one way by its ID), nose on it, at its attack height.
    private void Run(Unit u, HeliState h, ScoutFlight f, RotarySpec r, Vec aim, WeaponDef w)
    {
        if (h.Attack != HeliAttack.Run)
        {
            (h.Attack, h.AttackTicks) = (HeliAttack.Run, 0);
        }
        h.AttackTicks++;
        var d = (long)w.Range.Raw * r.GunPercent / 100;
        long dx = u.Pos.X.Raw - aim.X.Raw, dy = u.Pos.Y.Raw - aim.Y.Raw;
        var dl = Len3(dx, dy, 0);
        if (dl == 0)
        {
            (dx, dy, dl) = (-h.HeadX, -h.HeadY, 16384);
        }
        long ux = dx * d / dl, uy = dy * d / dl;
        var s = (u.Id & 1) == 0 ? RunSin : -RunSin;
        var goal = OnMap(aim.X.Raw + (ux * RunCos - uy * s) / 16384, aim.Y.Raw + (ux * s + uy * RunCos) / 16384);
        h.Cover = goal;
        FlyTo(u, h, f, r, goal, Cruise(u), aim, r.AttackHeightRaw, -1, true);
    }

    // PopUp flies the missile attack from cover (terrain masking and pop-up), or from a standoff hover without cover.
    private void PopUp(Unit u, HeliState h, ScoutFlight f, RotarySpec r, Unit? t, Vec aim, int wi)
    {
        var w = u.Def.Weapons[wi];
        var standoff = w.Range.Raw * r.StandoffPercent / 100;
        var aimH = AimHeight(t, aim);
        var moved = (aim - h.CoverFrom).LenSq() > CoverMoveRaw * CoverMoveRaw;
        var replan = h.Attack is HeliAttack.None or HeliAttack.Run ||
            (moved && h.Attack is HeliAttack.ToCover or HeliAttack.Hide or HeliAttack.Duck or HeliAttack.Standoff) ||
            (h.Attack == HeliAttack.Standoff && h.AttackTicks >= 4 * TicksPerSecond) ||
            (h.Attack == HeliAttack.Rise && h.AttackTicks >= RiseTicks);
        if (replan)
        {
            h.CoverFrom = aim;
            if (FindCover(u, r, aim, aimH, standoff, out var cover, out var pop))
            {
                (h.Attack, h.Cover, h.PopAlt) = (HeliAttack.ToCover, cover, pop);
            }
            else
            {
                h.Attack = HeliAttack.Standoff;
                long dx = u.Pos.X.Raw - aim.X.Raw, dy = u.Pos.Y.Raw - aim.Y.Raw;
                var dl = Math.Max(1, Len3(dx, dy, 0));
                h.Cover = OnMap(aim.X.Raw + dx * standoff / dl, aim.Y.Raw + dy * standoff / dl);
                h.PopAlt = 0;
            }
            h.AttackTicks = 0;
        }
        h.AttackTicks++;
        var ready = h.Reloads[wi] == 0 && HasRound(u, w);
        var hide = GroundRaw(h.Cover) + r.HideHeightRaw;
        switch (h.Attack)
        {
            case HeliAttack.ToCover:
                if (FlyTo(u, h, f, r, h.Cover, Cruise(u), aim, r.HideHeightRaw, -1, true))
                {
                    (h.Attack, h.AttackTicks) = (HeliAttack.Hide, 0);
                }
                break;
            case HeliAttack.Hide:
                FlyTo(u, h, f, r, h.Cover, r.SideSpeed, aim, r.HideHeightRaw, hide, true);
                if (h.AttackTicks >= HideTicks && ready)
                {
                    (h.Attack, h.AttackTicks) = (HeliAttack.Rise, 0);
                }
                break;
            case HeliAttack.Rise:
                FlyTo(u, h, f, r, h.Cover, r.SideSpeed, aim, r.HideHeightRaw, h.PopAlt, true);
                break;
            case HeliAttack.Fire:
                FlyTo(u, h, f, r, h.Cover, r.SideSpeed, aim, r.HideHeightRaw, h.PopAlt, true);
                if (h.AttackTicks >= ExposedTicks)
                {
                    (h.Attack, h.AttackTicks) = (HeliAttack.Duck, 0);
                }
                break;
            case HeliAttack.Duck:
                FlyTo(u, h, f, r, h.Cover, r.SideSpeed, aim, r.HideHeightRaw, hide, true);
                if (u.Altitude.Raw <= hide + 512)
                {
                    (h.Attack, h.AttackTicks) = (HeliAttack.Hide, 0);
                }
                break;
            default:
                FlyTo(u, h, f, r, h.Cover, Cruise(u), aim, r.AttackHeightRaw, -1, true);
                break;
        }
    }

    // AimHeight is the height (absolute, raw) of the point a helicopter aims at: a body over the target's ground, a wall.
    private long AimHeight(Unit? t, Vec aim) => GroundRaw(aim) + (t is { Def.IsStructure: true } ? HeliWallRaw : HeliBodyRaw);

    // FindCover looks for a firing position behind cover for a missile shot at aim (height aimH) from `standoff` out: on
    // bearings round the target toward the helicopter (nearest the straight line first) at the standoff and at three
    // quarters of it, a point within its cover search whose line of sight to the target is blocked at its hide height (a
    // crest, woods) and clears within its pop-up height. The best: the lowest pop-up plus a quarter of the way there.
    private bool FindCover(Unit u, RotarySpec r, Vec aim, long aimH, long standoff, out Vec cover, out long pop)
    {
        (cover, pop) = (default, 0);
        if (Ground is null)
        {
            return false; // a flat map hides nothing
        }
        long dx = u.Pos.X.Raw - aim.X.Raw, dy = u.Pos.Y.Raw - aim.Y.Raw;
        var dl = Len3(dx, dy, 0);
        if (dl == 0)
        {
            (dx, dy, dl) = (-u.Heli!.HeadX, -u.Heli.HeadY, 16384);
        }
        long ux = dx * 16384 / dl, uy = dy * 16384 / dl;
        var best = long.MaxValue;
        var found = false;
        foreach (var ring in (ReadOnlySpan<long>)[standoff, standoff * 3 / 4])
        {
            foreach (var (c, s) in CoverBearings)
            {
                long bx = (ux * c - uy * s) / 16384, by = (ux * s + uy * c) / 16384;
                long px = aim.X.Raw + bx * ring / 16384, py = aim.Y.Raw + by * ring / 16384;
                if (px < 0 || py < 0 || px >= (long)Grid.Width * Fixed.OneRaw || py >= (long)Grid.Height * Fixed.OneRaw)
                {
                    continue;
                }
                var p = P(px, py);
                var away = Len3(px - u.Pos.X.Raw, py - u.Pos.Y.Raw, 0);
                if (r.CoverSearchRaw > 0 && away > r.CoverSearchRaw)
                {
                    continue;
                }
                var g = GroundRaw(p);
                var need = EyeNeed(p, aim, aimH);
                if (need <= g + r.HideHeightRaw + PopMarginRaw)
                {
                    continue; // no cover there: it would be seen hiding
                }
                var up = need + PopMarginRaw;
                if (up - g > r.PopMaxRaw)
                {
                    continue; // the crest is too high to fire over from there
                }
                var score = (up - g) + away / 4;
                if (!found || score < best)
                {
                    (found, best, cover, pop) = (true, score, p, up);
                }
            }
        }
        return found;
    }

    // EyeNeed is the lowest eye height (absolute, raw) at p whose straight line to aim (height aimH) clears the terrain and
    // the canopy every half cell between them; on a flat map the ground.
    internal long EyeNeed(Vec p, Vec aim, long aimH)
    {
        if (Ground is not { } g)
        {
            return 0;
        }
        var need = g.HeightAt(p).Raw;
        long dx = aim.X.Raw - p.X.Raw, dy = aim.Y.Raw - p.Y.Raw;
        var n = (int)(Math.Max(Math.Abs(dx), Math.Abs(dy)) * 2 / Fixed.OneRaw);
        var canopy = SightRules.CanopyRaw;
        for (var i = 1; i < n; i++)
        {
            var q = P(p.X.Raw + dx * i / n, p.Y.Raw + dy * i / n);
            var block = g.HeightAt(q).Raw + canopy[g.Forest(Cell.Of(q))];
            // The line from (p, e) to (aim, aimH) is at e + (aimH - e) i / n over q: it clears when e >= (block n - aimH i) / (n - i).
            var e = (block * n - aimH * i + (n - i) - 1) / (n - i);
            if (e > need)
            {
                need = e;
            }
        }
        return need;
    }

    // ---- The combat side: targets and fire ----

    // RunHeliCombat (end of the combat system: the spatial index is fresh): each helicopter in flight counts its reloads
    // down, goes on with a salvo, keeps or picks its target, and fires what it can; the guns' hits land after.
    private void RunHeliCombat()
    {
        if (!_helis)
        {
            return;
        }
        _heliHits.Clear();
        foreach (var u in _units)
        {
            if (u.Heli is not { } h || !u.Alive)
            {
                continue;
            }
            for (var i = 0; i < h.Reloads.Length; i++)
            {
                if (h.Reloads[i] > 0)
                {
                    h.Reloads[i]--;
                }
            }
            if (h.Phase != HeliPhase.Flying)
            {
                h.Target = 0;
                h.SalvoLeft = 0;
                continue; // on the ground, lifting off, landing or out of control: it does not fire
            }
            if (h.SalvoLeft > 0)
            {
                HeliSalvo(u, h);
            }
            PickHeliTarget(u, h);
            if (u.Homing)
            {
                continue; // homing: it holds its fire
            }
            if (Unit(h.Target) is { } t)
            {
                HeliFire(u, h, t, t.Def.IsStructure ? AimPoint(u.Pos, t) : t.Pos);
            }
            else if (u.Order.Kind == OrderKind.AttackGround)
            {
                HeliFire(u, h, null, u.Order.Cell.Center());
            }
        }
        if (_heliHits.Count > 0)
        {
            ApplyHits(_heliHits);
        }
    }

    // PickHeliTarget keeps or picks helicopter u's target (see the header).
    private void PickHeliTarget(Unit u, HeliState h)
    {
        switch (u.Order.Kind)
        {
            case OrderKind.Attack:
                h.Target = Unit(u.Order.Target) is { } ot && HeliCanTarget(u, ot, true) ? ot.Id : 0;
                return;
            case OrderKind.AttackGround or OrderKind.Move:
                h.Target = 0;
                return;
        }
        var stance = u.Stance is Stance.Guard or Stance.HoldGround or Stance.Aggressive;
        var guard = u.Order.Kind == OrderKind.Guard;
        var roam = u.Order.Kind == OrderKind.AttackMove || (u.Order.Kind == OrderKind.Idle && u.AirMission is not null);
        if (u.Homing || h.Errand != HeliErrand.None || HoldsFire(u.Stance) || (!guard && !roam && !stance))
        {
            h.Target = 0;
            return;
        }
        var reach = HeliReach(u);
        var r = u.Def.Archetype.Flight!.Rotor!;
        var (centre, radius) = guard ? (h.Post, r.LeashRaw + reach) : roam ? (u.Pos, r.LeashRaw + reach) : (h.Post, reach);
        if (Unit(h.Target) is { } cur && HeliCanTarget(u, cur, false) && WithinRaw(centre, cur.Pos, radius))
        {
            return;
        }
        h.Target = 0;
        if ((Tick + u.Id) % HeliScanTicks != 0 || radius <= 0)
        {
            return;
        }
        long bestScore = 0;
        var m = radius + Fixed.OneRaw;
        foreach (var t in Around(centre.X.Raw - m, centre.Y.Raw - m, centre.X.Raw + m, centre.Y.Raw + m, _nearHeli, true))
        {
            if (t.Owner == u.Owner || !WithinRaw(centre, t.Pos, radius) || !HeliCanTarget(u, t, false))
            {
                continue;
            }
            var wi = PrimaryWeapon(u, t);
            var cells = Len3(t.Pos.X.Raw - u.Pos.X.Raw, t.Pos.Y.Raw - u.Pos.Y.Raw, 0) / Fixed.OneRaw;
            var score = Score(u.Def.Weapons[wi], ArmorOf(t)) * 1024 / (cells + 4);
            if (h.Target == 0 || score > bestScore)
            {
                (h.Target, bestScore) = (t.Id, score);
            }
        }
    }

    // HeliCanTarget: helicopter u may attack t: an enemy ground unit or structure out in the open, spotted by its side
    // (seen, on an order), never an aircraft, and some weapon with ammunition can hurt it.
    private bool HeliCanTarget(Unit u, Unit t, bool ordered) =>
        t.Alive && t.Owner != u.Owner && t.Owner != 0 && t.Inside == 0 && t.Garrison == 0 && !t.Def.Archetype.Untargetable &&
        t.Def.Archetype.Flight is null && t.Def.Layer == Layers.Ground && (ordered ? IsVisibleTo(t, u.Owner) : IsSpotted(t, u.Owner)) && HeliCanHit(u, t);

    // HeliCanHit: some weapon of helicopter u with ammunition can hurt t.
    private bool HeliCanHit(Unit u, Unit t) => PrimaryWeapon(u, t) >= 0;

    // HeliWeapon: weapon w is one a helicopter fires at the ground (not an air-to-air or bomb weapon), with a round left.
    private static bool HeliWeapon(Unit u, WeaponDef w) =>
        (w.Effect.Targets & Layers.Ground) != 0 && w.Effect.Interceptor is null && w.Effect.AirGun is null && w.Effect.Delivery is null &&
        w.Effect.SideFire is null && w.Effect.AirStrike is null && w.Effect.Drone is null && HasRound(u, w);

    private static bool HasRound(Unit u, WeaponDef w) => w.Stock < 0 || u.Charges[w.Stock] > 0;

    // Score is weapon w's damage a second (x 1000 / 20) against armour class `armor`: its damage through its warhead, its salvo,
    // over its reload and salvo time.
    private static long Score(WeaponDef w, int armor)
    {
        var e = w.Effect;
        var salvo = Math.Max(1, e.SalvoSize);
        return (long)w.Warhead.Damage(w.Damage, armor, 100) * salvo * 1000 / Math.Max(1, w.Reload + salvo * e.SalvoInterval);
    }

    // PrimaryWeapon is helicopter u's best weapon against t (the most damage a second), or -1.
    private int PrimaryWeapon(Unit u, Unit t)
    {
        var armor = ArmorOf(t);
        var (best, bestScore) = (-1, 0L);
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (!HeliWeapon(u, w) || !w.Warhead.CanHit(armor))
            {
                continue;
            }
            var s = Score(w, armor);
            if (best < 0 || s > bestScore)
            {
                (best, bestScore) = (i, s);
            }
        }
        return best;
    }

    // HeliGroundWeapon is helicopter u's weapon for an attack-ground order (a rocket pod or a gun: never a missile), or -1.
    private int HeliGroundWeapon(Unit u)
    {
        var weapons = u.Def.Weapons;
        var (best, bestScore) = (-1, 0L);
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (!HeliWeapon(u, w) || w.Effect.Guided is not null)
            {
                continue;
            }
            var s = Score(w, 0) + w.Effect.Splash.Raw;
            if (best < 0 || s > bestScore)
            {
                (best, bestScore) = (i, s);
            }
        }
        return best;
    }

    // GunReach is the longest range of helicopter u's unguided weapons with ammunition that hurt t (0: none).
    private long GunReach(Unit u, Unit t)
    {
        long reach = 0;
        foreach (var w in u.Def.Weapons)
        {
            if (HeliWeapon(u, w) && w.Effect.Guided is null && w.Warhead.CanHit(ArmorOf(t)))
            {
                reach = Math.Max(reach, w.Range.Raw);
            }
        }
        return reach;
    }

    // HeliReach is the longest range of helicopter u's weapons with ammunition.
    private static long HeliReach(Unit u)
    {
        long reach = 0;
        foreach (var w in u.Def.Weapons)
        {
            if (HeliWeapon(u, w))
            {
                reach = Math.Max(reach, w.Range.Raw);
            }
        }
        return reach;
    }

    // HeliFire fires every weapon of helicopter u that can at t (or the point aim when t is null): see the header.
    private void HeliFire(Unit u, HeliState h, Unit? t, Vec aim)
    {
        var armor = t is not null ? ArmorOf(t) : 0;
        var weapons = u.Def.Weapons;
        long best = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (HeliWeapon(u, weapons[i]) && (t is null || weapons[i].Warhead.CanHit(armor)))
            {
                best = Math.Max(best, Score(weapons[i], armor));
            }
        }
        var aimH = AimHeight(t, aim);
        long dx = aim.X.Raw - u.Pos.X.Raw, dy = aim.Y.Raw - u.Pos.Y.Raw, dz = aimH - u.Altitude.Raw;
        var slant = Len3(dx, dy, dz);
        bool? los = null;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            var e = w.Effect;
            if (h.Reloads[i] > 0 || !HeliWeapon(u, w) || (t is not null && (!w.Warhead.CanHit(armor) || Score(w, armor) * 3 < best ||
                (e.Guided is not null && Score(w, armor) < best))) ||
                (t is null && e.Guided is not null) || (e.SalvoSize > 1 && h.SalvoLeft > 0) ||
                (e.Guided is not null && h.Attack is HeliAttack.ToCover or HeliAttack.Hide or HeliAttack.Duck) ||
                slant > w.Range.Raw || slant < e.MinRange.Raw || (e.Cone > 0 && !InCone(h, dx, dy, e.Cone)))
            {
                continue;
            }
            los ??= Ground is null || EyeNeed(u.Pos, aim, aimH) <= u.Altitude.Raw;
            if (los != true)
            {
                return; // the crest or the woods hide it: nothing fires
            }
            u.LastShot = Tick;
            h.Reloads[i] = Math.Max(1, w.Reload);
            if (e.SalvoSize > 1)
            {
                (h.SalvoWeapon, h.SalvoLeft, h.SalvoWait) = (i, e.SalvoSize, 0);
                (h.SalvoAim, h.SalvoTarget) = (t is not null ? Lead(u, t, w) : aim, t?.Id ?? 0);
                if (w.Stock >= 0 && !e.AmmoPerRocket)
                {
                    u.MutableCharges[w.Stock]--;
                }
                HeliSalvo(u, h);
                continue;
            }
            if (w.Stock >= 0)
            {
                u.MutableCharges[w.Stock]--;
            }
            if (e.ProjectileSpeed.Raw > 0)
            {
                LaunchHeliShot(u, w, e.Guided is not null && t is not null ? t.Pos : t is not null ? Lead(u, t, w) : aim, t?.Id ?? 0);
                if (e.Guided is not null && h.Attack == HeliAttack.Rise)
                {
                    (h.Attack, h.AttackTicks) = (HeliAttack.Fire, 0); // launched: exposed a moment, then it ducks
                }
                continue;
            }
            _heliHits.Add(new Hit(w, t?.Id ?? 0, Scatter(w, aim, u), u.Id));
        }
    }

    // InCone: the direction (dx, dy) lies within cone degrees of helicopter h's nose.
    private static bool InCone(HeliState h, long dx, long dy, int cone)
    {
        if (cone >= 180)
        {
            return true;
        }
        var dl = Len3(dx, dy, 0);
        if (dl == 0)
        {
            return true;
        }
        var cos = (dx * h.HeadX + dy * h.HeadY) / dl; // 1/16384
        return cos >= HeliCosDeg(cone);
    }

    // CosDeg is the cosine of `deg` degrees (0 to 180) in 1/16384 (the series up to 90, mirrored beyond).
    private static long HeliCosDeg(int deg)
    {
        if (deg > 90)
        {
            return -HeliCosDeg(180 - deg);
        }
        var th = (long)deg * 16384 * 314159 / (180L * 100000);
        return 16384 - th * th / (2L * 16384) + th * th / 16384 * th / 16384 * th / (24L * 16384);
    }

    // HeliSalvo fires the next rocket of helicopter u's salvo when it is due (one round a rocket for a pod counted in rockets).
    private void HeliSalvo(Unit u, HeliState h)
    {
        if (h.SalvoWait > 0 && --h.SalvoWait > 0)
        {
            return;
        }
        var w = u.Def.Weapons[h.SalvoWeapon];
        if (w.Stock >= 0 && w.Effect.AmmoPerRocket)
        {
            if (u.Charges[w.Stock] == 0)
            {
                h.SalvoLeft = 0; // out of rockets: the salvo ends early
                return;
            }
            u.MutableCharges[w.Stock]--;
        }
        LaunchHeliShot(u, w, Scatter(w, h.SalvoAim, u), h.SalvoTarget);
        h.SalvoLeft--;
        h.SalvoWait = w.Effect.SalvoInterval;
    }

    // LaunchHeliShot puts a projectile of helicopter u's weapon w in flight toward `at` (at target): from its height, in a
    // straight line (no arc: a rocket pod fires flat, a missile flies low from where it fired).
    private void LaunchHeliShot(Unit u, WeaponDef w, Vec at, uint target)
    {
        _nextId++;
        var shot = new Projectile(_nextId, u, w, at, target);
        Launch(shot, u);
        if (Ground is not null)
        {
            (shot.Origin, shot.LaunchHeight) = (u.Pos, u.Altitude);
            if (shot.Arcs)
            {
                shot.ArcRise = Fixed.Zero;
            }
        }
        shot.Altitude = u.Altitude;
        _projectiles.Add(shot);
    }
}

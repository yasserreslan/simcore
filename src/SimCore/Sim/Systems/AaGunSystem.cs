using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// The anti-air guns (docs/design.md, "Anti-air guns"; the ZU-23 carrier, the Korkut battery): a ground gun whose airGun has a
// ground block (AaGun). On top of the technical's heavy MG (GroundGun, decision D5):
//  - It engages as it drives (EngagesAir: a crewed vehicle in Guard, Hold ground or Aggressive, on any order), one target at a time.
//  - Its targets: enemy aircraft, airBody units (the FPV drone) and air projectiles (one-way attack drones) of the classes its
//    hitBase lists, below its ceiling, that a radar it is linked to tracks within its air reach (the full chance), or else that its
//    side sees (spotted: its own optics are an airVision) within its optics reach (its unlinked share of the chance).
//  - The pick: with threatOrder by kind first (suicide drones heading at its side, other drones, helicopters, the rest), then
//    nearest (slant), its current target first within its kind, ties to the lower ID; else nearest.
//  - The link reuses the battery mechanism (BatterySystem.cs): a working radar of its side carrying the link's radar tags feeds the
//    guns within the link radius, nearest first (ties to the lower ID), at most maxLinks; radars in ID order, each gun fed once.
//  - Ground fire (the combat system): with airFirst the gun leaves the ground targets it picked itself while it tracks an aircraft;
//    with groundStance the Aggressive stance flips that (ground first). An ordered attack always wins.
//  - Exposed gunners (archetype gunners): a small-arms hit on the vehicle kills one at the content's chance; with none left its
//    gun is silent; a supply depot seats a new one every refillTicks.
// Scratch only (the link table, rebuilt on first use each tick); the new state is Unit.GunnersLost and Unit.GunnerRefill.
public sealed partial class World
{
    private const ulong GunnerSalt = 0x6A23_0000_0000UL;

    // Scratch, not state: this tick's gun links (gun ID, its radar and the radar's antenna height), in radar then distance order.
    private readonly List<(uint Gun, Unit Radar, long Eye)> _gunLinks = [];
    private long _gunLinksTick = -1;
    private readonly List<(Unit Gun, long D)> _linkCands = [];

    // AaGun runs ground AA gun u's weapon w (its airGun has a ground block) for this tick.
    private void AaGun(Unit u, WeaponDef w)
    {
        var g = w.Effect.AirGun!;
        var aa = g.Ground!;
        if (!u.Alive || !u.CanAct || u.Inside != 0 || !EngagesAir(u) || !u.HasGunner || YieldsToGround(u, aa))
        {
            DropTrack(u);
            return;
        }
        var ground = GroundRaw(u.Pos);
        var eye = ground + EyeRaw;
        (Unit Unit, long Eye)? radar = aa.Link is { } link ? GunRadarOf(u, link) : null;
        var pick = new AaPick { Tier = int.MaxValue };
        foreach (var t in _scouts)
        {
            ConsiderAa(u, g, aa, ground, eye, radar, new AirBody(t), ref pick);
        }
        foreach (var b in _airUnits)
        {
            ConsiderAa(u, g, aa, ground, eye, radar, b, ref pick); // the FPV drones
        }
        foreach (var p in _projectiles)
        {
            if (p.Interceptible != InterceptClass.None && !p.Done)
            {
                ConsiderAa(u, g, aa, ground, eye, radar, new AirBody(p), ref pick); // one-way attack drones
            }
        }
        if (pick.Body is not { } target)
        {
            DropTrack(u);
            return;
        }
        if (target.Id != u.AirTarget)
        {
            (u.AirTarget, u.TrackTicks) = (target.Id, 0);
        }
        var aligned = true;
        if (Facing.TryFromDirection(target.Pos - u.Pos, out var want))
        {
            if (u.Def.Turret)
            {
                u.TurretFacing = Facing.TurnToward(u.TurretFacing, want, TurretTurnRate);
                aligned = Math.Abs(Facing.Diff(u.TurretFacing, want)) <= FireAlignFacings;
            }
            else if (!u.Moving)
            {
                u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
                u.TurretFacing = u.Facing;
                aligned = Math.Abs(Facing.Diff(u.Facing, want)) <= FireAlignFacings;
            }
        }
        if (!aligned || u.Reload > 0 || !HasAmmo(u, w))
        {
            return;
        }
        FireGun(u, w, target, eye, Slant(u.Pos, eye, target), pick.Tracked ? 100 : aa.Unlinked);
    }

    // The best candidate so far (AaGun): its kind, whether it is the current target, its squared slant, and whether a linked radar tracks it.
    private struct AaPick
    {
        public AirBody? Body;
        public int Tier;
        public bool Current, Tracked;
        public long D;
    }

    private void ConsiderAa(Unit u, AirGun g, AaGun aa, long ground, long eye, (Unit Unit, long Eye)? radar, AirBody b, ref AaPick pick)
    {
        if (b.Gone || b.Owner == u.Owner || b.Owner == 0 || g.HitOn(b.Class) <= 0 || b.Alt - ground >= g.CeilingRaw)
        {
            return;
        }
        var n = Slant2(u.Pos, eye, b);
        var tracked = radar is { } r && SlantWithin(n, aa.AirRange.Raw) && DetectsAir(r.Unit, r.Eye, b);
        if (!tracked && (!SlantWithin(n, aa.OpticsReachRaw) || !SideSeesAir(u.Owner, b)))
        {
            return;
        }
        var tier = aa.ThreatOrder ? ThreatTier(u.Owner, aa, b) : 0;
        var current = b.Id == u.AirTarget;
        if (pick.Body is { } pb && (tier > pick.Tier || (tier == pick.Tier && (pick.Current ||
            (!current && (n > pick.D || (n == pick.D && b.Id > pb.Id)))))))
        {
            return;
        }
        pick = new AaPick { Body = b, Tier = tier, Current = current, Tracked = tracked, D = n };
    }

    // ThreatTier is air target b's kind for a Korkut type gun of player owner (threatOrder): 0 a suicide drone heading at its side
    // (a one-way attack drone coming down within the threat radius of one of its units, an FPV drone attacking one), 1 any other
    // drone, 2 a helicopter, 3 the rest (jets, bombers, missiles).
    private int ThreatTier(byte owner, AaGun aa, AirBody b)
    {
        if (b.P is { } p)
        {
            return b.Class == InterceptClass.Drone && Threatened(p, owner, aa.ThreatRadius.Raw) ? 0 : b.Class == InterceptClass.Drone ? 1 : 3;
        }
        var t = b.U!;
        if (b.Class == InterceptClass.Fpv)
        {
            return t.Order.Kind == OrderKind.Attack && Unit(t.Order.Target) is { } at && at.Owner == owner ? 0 : 1;
        }
        return b.Class switch
        {
            InterceptClass.Drone or InterceptClass.Scout or InterceptClass.Watcher or InterceptClass.Overseer or InterceptClass.Kaman => 1,
            InterceptClass.AttackHeli or InterceptClass.ArmoredHeli or InterceptClass.LightHeli => 2,
            _ => 3,
        };
    }

    // SideSeesAir: player owner's side sees air target b now (no fog: always): a unit spotted for it, a projectile over a cell it sees.
    private bool SideSeesAir(byte owner, AirBody b) =>
        FogOf(owner) is null || (b.U is { } t ? (t.SeenBy & Bit(owner)) != 0 : SeesPoint(owner, b.Pos));

    // YieldsToGround: AA gun u leaves the air to its ground fire now: on an ordered attack or attack-ground, or with groundStance in
    // the Aggressive stance while it has a ground target it can hit from here (ground first).
    private bool YieldsToGround(Unit u, AaGun aa)
    {
        if (u.Order.Kind == OrderKind.AttackGround || (u.Order.Kind == OrderKind.Attack && Unit(u.Order.Target) is { } ot && AirBodyOfUnit(ot) is null))
        {
            return true;
        }
        return aa.GroundStance && u.Stance == Stance.Aggressive && Unit(u.AutoTarget) is { Alive: true } t && IsSpotted(t, u.Owner) &&
               PickWeapon(u, t, true) >= 0;
    }

    // AirFirstNow: u's AA gun takes it off the ground targets it picked itself this tick: airFirst, tracking an aircraft (air defence
    // runs after combat: last tick's track), and not flipped to ground first by its stance. Also true with every gunner lost.
    private bool HoldsGroundFire(Unit u)
    {
        if (u.Def.Archetype.Gunners is not null && !u.HasGunner)
        {
            return true; // both gunners lost: the gun is silent
        }
        var gi = GroundGunWeapon(u.Def);
        return gi >= 0 && u.Def.Weapons[gi].Effect.AirGun!.Ground is { AirFirst: true } aa && u.AirTarget != 0 &&
               !(aa.GroundStance && u.Stance == Stance.Aggressive) && u.Order.Kind != OrderKind.Attack;
    }

    // GunLinkOf is the radar link of definition d's AA gun, or null.
    private Battery? GunLinkOf(UnitDef d) => GroundGunWeapon(d) is var gi and >= 0 ? d.Weapons[gi].Effect.AirGun!.Ground?.Link : null;

    // GunRadarOf is the radar feeding gun u now (its link), or null.
    private (Unit Unit, long Eye)? GunRadarOf(Unit u, Battery link)
    {
        BuildGunLinks();
        foreach (var (gun, r, eye) in _gunLinks)
        {
            if (gun == u.Id)
            {
                return (r, eye);
            }
        }
        return null;
    }

    // BuildGunLinks fills this tick's gun links (see the header), once a tick.
    private void BuildGunLinks()
    {
        if (_gunLinksTick == Tick)
        {
            return;
        }
        _gunLinksTick = Tick;
        _gunLinks.Clear();
        foreach (var (s, eye) in BatteryRadars())
        {
            var cap = 0;
            _linkCands.Clear();
            foreach (var u in _units)
            {
                if (u.Owner != s.Owner || !u.Alive || u.Inside != 0 || GunLinkOf(u.Def) is not { } b || !s.Def.Archetype.Tags.HasAll(b.RadarTags) ||
                    !WithinRaw(u.Pos, s.Pos, b.LinkRadiusRaw) || Fed(u.Id))
                {
                    continue;
                }
                cap = b.MaxLinks;
                _linkCands.Add((u, (u.Pos - s.Pos).LenSq()));
            }
            _linkCands.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.Gun.Id.CompareTo(b.Gun.Id));
            for (var i = 0; i < _linkCands.Count && (cap == 0 || i < cap); i++)
            {
                _gunLinks.Add((_linkCands[i].Gun.Id, s, eye));
            }
        }
    }

    private bool Fed(uint gun)
    {
        foreach (var l in _gunLinks)
        {
            if (l.Gun == gun)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The radar feeding AA gun <paramref name="gun"/> now (a Korkut type gun's command vehicle: its battery link), or null: no link,
    /// its radar off, dead or out of reach, or its radar already feeds its most guns. Read-only; valid at any time.
    /// </summary>
    public Unit? GunRadar(Unit gun) => GunLinkOf(gun.Def) is { } link && GunRadarOf(gun, link) is { } r ? r.Unit : null;

    /// <summary>The AA guns radar <paramref name="radar"/> feeds now, in the order they were fed (nearest first): the link lines.</summary>
    public List<Unit> RadarGuns(Unit radar)
    {
        BuildGunLinks();
        var list = new List<Unit>();
        foreach (var (gun, r, _) in _gunLinks)
        {
            if (r == radar && Unit(gun) is { } u)
            {
                list.Add(u);
            }
        }
        return list;
    }

    // GunnerHit: a hit of warhead wh from shooter on vehicle t (the hit's index i in the tick's list) may kill one of its exposed gunners.
    private void GunnerHit(Unit t, int wh, uint shooter, int i)
    {
        if (t.Def.Archetype.Gunners is not { } gn || !t.Alive || !t.HasGunner || !gn.SmallArms(wh))
        {
            return;
        }
        if ((int)(Rand.Hash(Match.Setup.Seed, Tick, t.Id | GunnerSalt, shooter | ((ulong)(uint)i << 32)) % 100) < gn.KillChance)
        {
            t.GunnersLost++;
        }
    }

    // RefillGunners: at a supply depot (NearDepot), vehicle u that lost gunners seats a new one every refillTicks; away, the count stops.
    private void RefillGunners(Unit u)
    {
        var gn = u.Def.Archetype.Gunners!;
        if (!u.Alive || u.Inside != 0 || !NearDepot(u))
        {
            u.GunnerRefill = 0;
            return;
        }
        if (u.GunnerRefill == 0)
        {
            u.GunnerRefill = gn.RefillTicks;
        }
        if (--u.GunnerRefill == 0)
        {
            u.GunnersLost--;
            u.GunnerRefill = u.GunnersLost > 0 ? gn.RefillTicks : 0;
        }
    }
}

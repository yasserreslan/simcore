using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The scout system (docs: the game repo's docs/air-defense-design.md, section 10). A scout drone
// (an archetype with a flight block) climbs off its launcher, flies to the point it is sent to and
// orbits it high up: a fixed wing that never stops, its velocity turning at most its turn rate a
// tick. Short of endurance (its lifetime) it turns for home on its own, glides down and is recovered
// at its launcher, which gets the drone back; with its launcher gone it orbits on until it runs dry
// and is lost. It sees for its side like any unit (the vision system); only air defence of its
// class engages it (the air defence system); no gun, rocket or blast reaches it.
public sealed partial class World
{
    // It aims at the point of its circle 30 degrees ahead of where it is (cos, sin in thousandths).
    private const long OrbitAheadCos = 866, OrbitAheadSin = 500;

    // The ground this many ticks ahead counts for the height it holds (it climbs before a ridge).
    private const int ScoutLookAhead = 40;

    // It never flies lower than this above the ground under it (raw: 2 m).
    private const long ScoutFloorRaw = 2 * 512;

    // FlyScout moves scout drone u one tick (called by the air movement system).
    private void FlyScout(Unit u, ScoutFlight f)
    {
        var speed = u.Def.Speed.Raw;
        if (u.Altitude.Raw == 0 && u.Vel == default)
        {
            // Put down in flight (the sandbox): at its height already, orbiting where it is.
            u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos) + f.HeightRaw);
            u.OrbitAt = u.Pos;
        }
        if (u.Order.Kind != OrderKind.Idle)
        {
            if (!u.Homing && u.Order.Kind is OrderKind.Move or OrderKind.Attack)
            {
                u.OrbitAt = u.Order.Cell.Center(); // sent somewhere: it orbits there
            }
            u.Idle(); // it holds no order of its own; homing, it takes none
        }
        var home = Unit(u.Source) is { Alive: true } s && s.Owner == u.Owner && s.Def.Ability(LaunchOf(s, u)) is not null ? s : null;
        if (home is null)
        {
            u.Homing = false; // nowhere to land: it orbits on until it runs dry
        }
        else if (!u.Homing && u.Lifetime > 0 && u.Lifetime <= (home.Pos - u.Pos).Len().Raw / Math.Max(1, speed) + f.ReserveTicks)
        {
            u.Homing = true; // bingo: just enough endurance left to fly home
        }
        if (u.Homing && Within(u.Pos, home!.Pos, f.RecoverRange))
        {
            Recover(u, home, f);
            return;
        }

        // Steer: the velocity turns toward the aim point at most TurnPermille of the speed a tick.
        var aim = u.Homing ? home!.Pos : OrbitPoint(u, f.OrbitRadius.Raw);
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        if (vx == 0 && vy == 0)
        {
            var d0 = Facing.Dir(u.Facing); // off the launcher the way it faces
            (vx, vy) = (d0.X.Raw * speed / Fixed.OneRaw, d0.Y.Raw * speed / Fixed.OneRaw);
        }
        long wx = aim.X.Raw - u.Pos.X.Raw, wy = aim.Y.Raw - u.Pos.Y.Raw;
        var wl = Len3(wx, wy, 0);
        if (wl > 0)
        {
            long dx = wx * speed / wl - vx, dy = wy * speed / wl - vy;
            var max = Math.Max(1, speed * f.TurnPermille / 1000);
            var dl = Len3(dx, dy, 0);
            if (dl > max)
            {
                (dx, dy) = (dx * max / dl, dy * max / dl);
            }
            (vx, vy) = (vx + dx, vy + dy);
        }
        var vl = Len3(vx, vy, 0);
        if (vl > 0)
        {
            (vx, vy) = (vx * speed / vl, vy * speed / vl);
        }
        var pos = OnMap(u.Pos.X.Raw + vx, u.Pos.Y.Raw + vy);
        u.Vel = pos - u.Pos;
        u.Pos = pos;
        if (Facing.TryFromDirection(new Vec(Fixed.FromRaw(vx), Fixed.FromRaw(vy)), out var facing))
        {
            u.Facing = facing;
            u.TurretFacing = facing;
        }

        // Height: its cruise height over the ground under and ahead of it; homing, it glides down
        // to its launcher in time to land there. The climb and sink are capped at ClimbPerTick.
        var under = GroundRaw(pos);
        var want = Math.Max(under, GroundRaw(OnMap(pos.X.Raw + vx * ScoutLookAhead, pos.Y.Raw + vy * ScoutLookAhead))) + f.HeightRaw;
        if (u.Homing)
        {
            var land = GroundRaw(home!.Pos) + f.LaunchHeightRaw;
            var glideTicks = (u.Altitude.Raw - land) / f.ClimbPerTick;
            if ((home.Pos - pos).Len().Raw <= (glideTicks + 1) * speed)
            {
                want = land;
            }
        }
        var alt = u.Altitude.Raw + Math.Clamp(want - u.Altitude.Raw, -f.ClimbPerTick, f.ClimbPerTick);
        alt = Math.Max(alt, under + ScoutFloorRaw);
        u.VelZ = Fixed.FromRaw(alt - u.Altitude.Raw);
        u.Altitude = Fixed.FromRaw(alt);
    }

    // OrbitPoint is where scout u steers to orbit u.OrbitAt at radius r: the point of the circle 30
    // degrees ahead of it (it circles clockwise on the map); far out, that is about the centre, so it
    // flies in and curves onto the circle; inside, it spirals out to it.
    private static Vec OrbitPoint(Unit u, long r)
    {
        var c = u.OrbitAt;
        long rx = u.Pos.X.Raw - c.X.Raw, ry = u.Pos.Y.Raw - c.Y.Raw;
        var rl = Len3(rx, ry, 0);
        if (rl == 0)
        {
            return u.Pos + u.Vel; // over the centre: straight on
        }
        var px = (rx * OrbitAheadCos - ry * OrbitAheadSin) / 1000 * r / rl;
        var py = (rx * OrbitAheadSin + ry * OrbitAheadCos) / 1000 * r / rl;
        return new Vec(Fixed.FromRaw(c.X.Raw + px), Fixed.FromRaw(c.Y.Raw + py));
    }

    // OnMap clamps a point (raw) to the map.
    private Vec OnMap(long x, long y) => new(
        Fixed.FromRaw(Math.Clamp(x, 0, (long)Grid.Width * Fixed.OneRaw - 1)),
        Fixed.FromRaw(Math.Clamp(y, 0, (long)Grid.Height * Fixed.OneRaw - 1)));

    // Recover lands scout u at its launcher: the launcher gets the drone back (up to what it holds)
    // and needs the turnaround before its next launch; the drone leaves the world, not lost.
    private static void Recover(Unit u, Unit home, ScoutFlight f)
    {
        var ab = home.Def.Ability(LaunchOf(home, u))!;
        if (home.Charges[ab.Stock] < home.Def.StockMax[ab.Stock])
        {
            home.MutableCharges[ab.Stock]++;
        }
        home.Cooldown = Math.Max(home.Cooldown, f.TurnaroundTicks);
        u.Recovered = true;
        u.HP = 0;
    }

    // LaunchOf is the component index of launcher's ability that launches u's archetype, or -1.
    private static int LaunchOf(Unit launcher, Unit u)
    {
        var abilities = launcher.Def.Abilities;
        for (var i = 0; i < abilities.Count; i++)
        {
            var ab = abilities[i];
            if (ab.Effect is LaunchEffect l && l.Archetype == u.Def.Archetype.Index)
            {
                return ab.Component;
            }
        }
        return -1;
    }

    // Aloft counts the units of archetype that launcher launched and that still fly.
    internal int Aloft(Unit launcher, int archetype)
    {
        var n = 0;
        foreach (var o in _units)
        {
            if (o.Alive && o.Source == launcher.Id && o.Def.Archetype.Index == archetype)
            {
                n++;
            }
        }
        return n;
    }

    // ScoutLaunchSpent reports whether u is a scout launcher with nothing left to launch and no
    // drone of its own aloft (a single-use station whose drone was lost).
    private bool ScoutLaunchSpent(Unit u)
    {
        var any = false;
        foreach (var ab in u.Def.Abilities)
        {
            if (ab.Effect is not LaunchEffect l || Content.Archetypes[l.Archetype].Flight is null)
            {
                continue;
            }
            any = true;
            if (u.Charges[ab.Stock] > 0 || Aloft(u, l.Archetype) > 0)
            {
                return false;
            }
        }
        return any && Empty(u);
    }
}

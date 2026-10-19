using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The scout system (docs: the game repo's docs/air-defense-design.md, section 10). A scout drone
// (an archetype with a flight block) climbs off its launcher, flies to the point it is sent to and
// orbits it high up: a fixed wing that never stops, its velocity turning at most its turn rate a
// tick. Short of endurance (its lifetime) it turns for home on its own, glides down and is recovered
// at its launcher, which gets the drone back; with its launcher gone it orbits on until it runs dry
// and is lost. Bingo fuel is the only automatic way home (the owner's flight rules, 2026-10-19):
// never because it is out of ammunition; a Rearm order (command 29) sends it to rearm. It sees for its side like any unit (the vision system); only air defence of its
// class engages it (the air defence system); no gun, rocket or blast reaches it.
public sealed partial class World
{
    // It aims at the point of its circle 30 degrees ahead of where it is (cos, sin in thousandths).
    private const long OrbitAheadCos = 866, OrbitAheadSin = 500;

    // The ground this many ticks ahead counts for the height it holds (it climbs before a ridge).
    private const int ScoutLookAhead = 40;

    // How far above its landing height a relaunched drone may be when it is recovered (raw: 3 m).
    private const long LandSlackRaw = 3 * 512;

    // It never flies lower than this above the ground under it (raw: 2 m).
    private const long ScoutFloorRaw = 2 * 512;

    // FlyScout moves scout drone u one tick (called by the air movement system).
    private void FlyScout(Unit u, ScoutFlight f)
    {
        if (u.AirOps is { } ops)
        {
            if (u.AirMission is { } m)
            {
                m.Away = true; // its task resumes at the nearest leg once it flies free again
            }
            FlyAirfield(u, f, ops); // holding, landing, on the ground or climbing out at its airfield
            return;
        }
        var speed = u.Def.Speed.Raw;
        if (u.Altitude.Raw == 0 && u.Vel == default)
        {
            // Put down in flight (the sandbox): at its height already, orbiting where it is.
            u.Altitude = Fixed.FromRaw(GroundRaw(u.Pos) + f.HeightRaw);
            u.OrbitAt = u.Pos;
        }
        var strike = StrikeWeapon(u);
        if (u.AirMission is { } mission)
        {
            GuardHunt(u, f, mission, strike); // on Guard: the one mode that picks targets (the mission system)
        }
        if (strike is not null)
        {
            StrikeOrder(u, strike); // a strike drone (the watcher system): its attack order
        }
        if (u.Order.Kind is not OrderKind.Idle and not OrderKind.Attack || (u.Order.Kind == OrderKind.Attack && strike is null))
        {
            if (!u.Homing && u.Order.Kind is OrderKind.Move or OrderKind.Attack)
            {
                u.OrbitAt = u.Order.Cell.Center(); // sent somewhere: it orbits there
                if (u.Order.Kind == OrderKind.Move)
                {
                    u.AirMission = null; // an orbit: the end of its mission
                }
            }
            u.Idle(); // it holds no order of its own; homing, it takes none
        }
        var home = Unit(u.Source) is { Alive: true } s && s.Owner == u.Owner && s.Def.Ability(LaunchOf(s, u)) is not null ? s : null;
        if (_airfields.Count > 0 && u.Def.Archetype.Aircraft is { Landing: LandingKind.Runway } ac && HomeAirfield(u, ac, home is not null) is { } field)
        {
            // Its home is an airfield (the airfield flight rules): at bingo fuel (with the approach
            // and the holding reserve), or already homing, it goes to hold there. Never because it
            // is out of ammunition (the owner's rules): only a Rearm order sends it to rearm.
            var bingo = u.Lifetime > 0 && u.Lifetime <= AirfieldHomeTicks(u, field, ac) + f.ReserveTicks;
            if (u.Homing || bingo)
            {
                if (!u.Homing)
                {
                    u.HomeReason = HomeReason.Refuel; // the automatic trip: it refuels only, then resumes
                }
                if (u.AirMission is { } away)
                {
                    away.Away = true; // its task resumes at the nearest leg once it flies free again
                }
                JoinAirfield(u, field, ac);
                FlyAirfield(u, f, u.AirOps!);
                return;
            }
            home = null; // no business with its ground control station
        }
        if (home is null)
        {
            u.Homing = false; // nowhere to land: it orbits on until it runs dry
            u.HomeReason = HomeReason.Refuel;
        }
        else if (!u.Homing && u.Lifetime > 0 && u.Lifetime <= HomeTicks(u, home, f) + f.ReserveTicks)
        {
            u.Homing = true; // bingo: just enough endurance left to fly home (the only automatic trip)
            u.HomeReason = HomeReason.Refuel;
        }
        if (u.Homing && u.AirMission is { } trip)
        {
            trip.Away = true; // its task resumes at the nearest leg after the trip home
        }
        // A drone its station relaunches (the Watcher) lands on its apron: it is recovered only once
        // it is down low (within LandSlackRaw of its landing height), circling its station till then.
        if (u.Homing && Within(u.Pos, home!.Pos, f.RecoverRange) &&
            (!f.Relaunch || u.Altitude.Raw <= GroundRaw(home.Pos) + f.LaunchHeightRaw + LandSlackRaw))
        {
            Recover(u, home, f);
            return;
        }

        // Steer (the mission system's flight controller over the drone kinematics): home, or the
        // mission's point (over a strike target, along a route or lane, round its orbit), with edge
        // avoidance first; the velocity turns at most TurnPermille of the speed a tick.
        var aim = u.Homing ? home!.Pos : SteerAim(u, f);
        if (f.Wing is { } wing)
        {
            FlyWing(u, f, wing, aim, u.Homing ? home : null); // a fixed wing: its energy envelope (E4)
            return;
        }
        long vx = u.Vel.X.Raw, vy = u.Vel.Y.Raw;
        if (vx == 0 && vy == 0)
        {
            var d0 = Facing.Dir(u.Facing); // off the launcher the way it faces
            (vx, vy) = (d0.X.Raw * speed / Fixed.OneRaw, d0.Y.Raw * speed / Fixed.OneRaw);
        }
        (vx, vy) = DroneControl(u, f, aim, vx, vy, speed);
        var pos = FlyStep(u, vx, vy);

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

    // HomeTicks is how long scout u needs to get home: the flight there, or for a drone that must be
    // down on its station's apron to be recovered (Relaunch) the longer of that and its descent.
    private long HomeTicks(Unit u, Unit home, ScoutFlight f)
    {
        var fly = (home.Pos - u.Pos).Len().Raw / Math.Max(1, u.Def.Speed.Raw);
        if (!f.Relaunch)
        {
            return fly;
        }
        var descent = (u.Altitude.Raw - GroundRaw(home.Pos) - f.LaunchHeightRaw) / f.ClimbPerTick;
        return Math.Max(fly, descent);
    }

    // OrbitPoint is where scout u steers to orbit u.OrbitAt at radius r: the point of the circle 30
    // degrees ahead of it (it circles clockwise on the map); far out, that is about the centre, so it
    // flies in and curves onto the circle; inside, it spirals out to it.
    private static Vec OrbitPoint(Unit u, long r) => OrbitPoint(u, u.OrbitAt, r);

    // OrbitPoint is the same round centre c (an airfield's hold point); cw false circles the other way.
    private static Vec OrbitPoint(Unit u, Vec c, long r, bool cw = true)
    {
        long rx = u.Pos.X.Raw - c.X.Raw, ry = u.Pos.Y.Raw - c.Y.Raw;
        var rl = Len3(rx, ry, 0);
        if (rl == 0)
        {
            return u.Pos + u.Vel; // over the centre: straight on
        }
        var sin = cw ? OrbitAheadSin : -OrbitAheadSin;
        var px = (rx * OrbitAheadCos - ry * sin) / 1000 * r / rl;
        var py = (rx * sin + ry * OrbitAheadCos) / 1000 * r / rl;
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
        if (f.Relaunch)
        {
            // The owner's rules: only a Rearm order rearms and repairs. Home to refuel (or recalled),
            // the drone keeps its stocks and HP: the station gives them to its next launch of that kind.
            if (u.HomeReason == HomeReason.Rearm)
            {
                (home.KeptStocks, home.KeptHp) = (null, 0);
            }
            else
            {
                (home.KeptStocks, home.KeptHp, home.KeptArchetype) = ([.. u.Charges], u.HP, u.Def.Archetype.Index);
            }
        }
        if (f.Relaunch && !home.RelaunchPending)
        {
            // Refuelled (and rearmed when sent to rearm) after the turnaround, it goes back to where it orbited.
            // (A second drone home while one relaunch waits stays in the station for its next order.)
            home.RelaunchPending = true;
            home.RelaunchCell = Cell.Of(u.OrbitAt);
            home.RelaunchArchetype = u.Def.Archetype.Index;
            if (u.AirMission is { } m)
            {
                home.KeptMission = m.Clone(); // the relaunch resumes its task (E3)
                home.KeptMission.Away = true;
            }
        }
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

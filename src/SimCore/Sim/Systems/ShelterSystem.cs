using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Vehicle shelters (decisions 1000-1014): civilian buildings with a shelter block (bays behind big doors: the machine shed,
// the repair workshop, the warehouse) that a medium vehicle drives into and hides in. A vehicle fits when its real size
// (Archetype.Size) is no longer than a bay and no wider or taller than its door (ShelterSpec.Fits); one vehicle a bay.
//
// An Enter command on a shelter sends the listed vehicles in (soldiers in the same command garrison it as before): nearest
// to the building first (then ID), each takes the free bay whose door is nearest to it; one that does not fit, finds every
// bay taken by its side or the building unusable or visibly held by the enemy is refused (World.OrderRefusals). It drives to
// its door (Approach); there it goes through the door for doorTicks (Entering: still on the map, seen and hit as anywhere),
// then it is in its bay (In): Inside = InShelter, off the map like a vehicle in a garage (nothing sees, targets, hits or
// blocks it; it neither sees nor fires; a radar is off, RadarOn). An enemy side that sees it go in (it is visible to them
// at that moment) gets the building's mark ("vehicle seen entering", ShelterMarks) and keeps it while it sees one of the
// doors. The building is held by the side whose vehicles are inside (ShelterHolder, not Owner: the colour every side sees)
// or whose soldiers garrison it (Owner); nobody else's vehicles or soldiers go in. An enemy finds out at the door: its
// vehicle or soldier is turned away there (ShelterEnemyHeld) and its side gets the mark.
//
// Out: an Exit command on the vehicle (one) or the building (all of its side's), or a move order given to a vehicle
// inside, starts it out through its door (Leaving: doorTicks, still unseen); then it is at its door, and on a move order it
// drives on. Blasts on the building (the shelter's occupant warheads) take OccupantDamage percent of the HP the building
// lost off every vehicle inside. A vehicle killed inside, and every vehicle inside a building that falls, comes out at its
// door; in a fall each survives with the collapse chance at most the collapse HP (as a garrison, RunCollapse). A bulldozer
// starting to clear the building puts them out first, as it does the garrison.
public sealed partial class World
{
    private const int ShelterAtDoor = 2; // cells from its door cell where a vehicle is at its door (stopped: twice that)

    private readonly List<(uint Building, byte Owner)> _shelterHeld = []; // scratch: RunShelterHold's (building, side) pairs

    /// <summary>The map cell in front of shelter building b's door i (bay i's), as the garrison's doors: it turns with the building.</summary>
    public static Cell ShelterDoorCell(Unit b, int i)
    {
        var d = b.Def.Archetype.Shelter!.Doors[i];
        return LayoutCell(b, d.X, d.Y);
    }

    // ShelterBayCell is the footprint cell just inside door i: where a vehicle in that bay stands (off the map).
    private static Cell ShelterBayCell(Unit b, int i)
    {
        var a = b.Def.Archetype;
        var d = a.Shelter!.Doors[i];
        return LayoutCell(b, Math.Clamp(d.X, 0, a.FootprintW - 1), Math.Clamp(d.Y, 0, a.FootprintH - 1));
    }

    /// <summary>Whether vehicle archetype a fits shelter s's bays (its real size against the bay and its door).</summary>
    public static bool FitsShelter(Archetype a, ShelterSpec s) => a.Size is { } z && s.Fits(z);

    /// <summary>Whether u is inside shelter b: in a bay or driving out through its door.</summary>
    public static bool IsShelteredIn(Unit u, Unit b) => u.Shelter == b.Id && u.ShelterStage is ShelterStage.In or ShelterStage.Leaving;

    /// <summary>The vehicles inside shelter building b (in a bay or driving out), in ID order.</summary>
    public List<Unit> ShelteredIn(Unit b)
    {
        var list = new List<Unit>();
        foreach (var u in _units)
        {
            if (IsShelteredIn(u, b) && u.Alive)
            {
                list.Add(u);
            }
        }
        return list;
    }

    // ShelterUsable: b is a shelter standing and finished.
    private static bool ShelterUsable(Unit b) => b.Def.Archetype.Shelter is not null && b.Alive && b.IsBuilt && !b.Rubble;

    /// <summary>Whether shelter building b is held against player: the other side's vehicles are inside, or its soldiers garrison it.</summary>
    public static bool ShelterHeldAgainst(Unit b, byte player) =>
        (b.ShelterHolder != 0 && b.ShelterHolder != player) || (b.Owner != 0 && b.Owner != player);

    // FreeShelterBay is the bay of b whose door is nearest to at (ties: the lowest) that no vehicle of player's uses, or -1.
    private int FreeShelterBay(Unit b, ShelterSpec s, byte player, Vec at)
    {
        var best = -1;
        var bestD = long.MaxValue;
        for (var i = 0; i < s.Bays; i++)
        {
            var used = false;
            foreach (var u in _units)
            {
                if (u.Shelter == b.Id && u.ShelterBay == i && u.Owner == player && u.Alive)
                {
                    used = true;
                    break;
                }
            }
            var d = used ? long.MaxValue : (ShelterDoorCell(b, i).Center() - at).LenSq();
            if (!used && d < bestD)
            {
                (best, bestD) = (i, d);
            }
        }
        return best;
    }

    // ShelterCommand sends player's listed vehicles into shelter b (an Enter command on it): nearest to it first, then by ID,
    // each to the free bay whose door is nearest to it. A vehicle already on its way in keeps its bay.
    private void ShelterCommand(Unit b, byte player, List<Unit> vehicles)
    {
        var s = b.Def.Archetype.Shelter!;
        vehicles.Sort((x, y) =>
        {
            var dx = (x.Pos - b.Pos).LenSq();
            var dy = (y.Pos - b.Pos).LenSq();
            return dx != dy ? dx.CompareTo(dy) : x.Id.CompareTo(y.Id);
        });
        var at = Cell.Of(b.Pos);
        foreach (var u in vehicles)
        {
            if (u.Shelter == b.Id && u.ShelterStage is ShelterStage.Approach or ShelterStage.Entering)
            {
                continue;
            }
            if (!ShelterUsable(b))
            {
                Refuse(u, OrderRefusalReason.ShelterUnusable, b.Id, at);
                continue;
            }
            if (b.Owner != 0 && b.Owner != player)
            {
                Refuse(u, OrderRefusalReason.ShelterEnemyHeld, b.Id, at); // its soldiers in it: the building shows it
                continue;
            }
            if (!FitsShelter(u.Def.Archetype, s))
            {
                Refuse(u, OrderRefusalReason.ShelterTooBig, b.Id, at);
                continue;
            }
            var bay = FreeShelterBay(b, s, player, u.Pos);
            if (bay < 0)
            {
                Refuse(u, OrderRefusalReason.ShelterFull, b.Id, at);
                continue;
            }
            if (u.Shelter != 0)
            {
                EndShelter(u); // bound for another shelter
            }
            var door = ShelterDoorCell(b, bay);
            Assign(u, new Order(OrderKind.Shelter, door, b.Id, 0));
            (u.Shelter, u.ShelterBay, u.ShelterStage, u.ShelterUntil, u.ShelterGo, u.ShelterThen) = (b.Id, bay, ShelterStage.Approach, 0, false, default);
            if (u.Def.Archetype.DeployTicks > 0 && u.Deploy is DeployState.Deployed or DeployState.Deploying)
            {
                StartStow(u); // it packs up first (movement holds it until stowed)
            }
            RequestRoute(u, door);
        }
    }

    // ShelterExit starts sheltered vehicle u out through its door; with go, it drives on to `then` once out (a move order).
    private void ShelterExit(Unit u, bool go, Cell then)
    {
        if (u.ShelterStage == ShelterStage.In)
        {
            (u.ShelterStage, u.ShelterUntil) = (ShelterStage.Leaving, Tick + (uint)Unit(u.Shelter)!.Def.Archetype.Shelter!.DoorTicks);
        }
        if (u.ShelterStage == ShelterStage.Leaving)
        {
            (u.ShelterGo, u.ShelterThen) = (go, then);
        }
    }

    // ShelterExitAll starts every vehicle of player's in shelter b out (an Exit command on the building).
    private void ShelterExitAll(Unit b, byte player)
    {
        foreach (var u in _units)
        {
            if (u.Owner == player && u.Alive && IsShelteredIn(u, b))
            {
                ShelterExit(u, false, default);
            }
        }
    }

    // RunShelters steps every vehicle using a shelter (after boarding): at its door it goes through, through it it is in its
    // bay; driving out, through it it is at its door.
    private void RunShelters()
    {
        foreach (var u in _units)
        {
            if (u.Shelter != 0 && u.Alive)
            {
                ShelterStep(u);
            }
        }
    }

    private void ShelterStep(Unit u)
    {
        var b = Unit(u.Shelter);
        if (u.ShelterStage is ShelterStage.In or ShelterStage.Leaving)
        {
            if (u.ShelterStage == ShelterStage.Leaving && Tick >= u.ShelterUntil && b is not null)
            {
                OutOfShelter(u, b);
            }
            return; // a building that falls puts them out (RunShelterLosses)
        }
        var ordered = u.Order.Kind == OrderKind.Shelter && u.Order.Target == u.Shelter;
        if (b is null || !ShelterUsable(b) || !ordered || !u.CanAct)
        {
            EndShelter(u);
            if (ordered)
            {
                u.Idle();
            }
            return;
        }
        if (u.ShelterStage == ShelterStage.Approach)
        {
            var door = ShelterDoorCell(b, u.ShelterBay).Center();
            var stopped = !u.Moving && !u.PathPending;
            if (!Within(u.Pos, door, Fixed.FromInt(stopped ? 2 * ShelterAtDoor : ShelterAtDoor)))
            {
                if (stopped)
                {
                    EndShelter(u); // it can't get to its door: the order ends
                    u.Idle();
                }
                return;
            }
            if (ShelterHeldAgainst(b, u.Owner))
            {
                TurnedAway(u, b);
                return;
            }
            u.ClearPath();
            (u.Vel, u.DriveSpeed, u.AutoTarget, u.SalvoLeft) = (default, default, 0, 0);
            (u.ShelterStage, u.ShelterUntil) = (ShelterStage.Entering, Tick + (uint)b.Def.Archetype.Shelter!.DoorTicks);
            return;
        }
        if (Tick < u.ShelterUntil)
        {
            return; // going through its door
        }
        if (ShelterHeldAgainst(b, u.Owner))
        {
            TurnedAway(u, b); // the other side got in first
            return;
        }
        IntoShelter(u, b);
    }

    // TurnedAway: u found shelter b held by the other side at its door: its order is refused there and its side gets the mark.
    private void TurnedAway(Unit u, Unit b)
    {
        Refuse(u, OrderRefusalReason.ShelterEnemyHeld, b.Id, Cell.Of(b.Pos));
        b.ShelterMarks |= Bit(u.Owner);
        if (u.Shelter != 0)
        {
            EndShelter(u);
        }
        u.Idle();
    }

    // IntoShelter takes u off the map into its bay. Each enemy side that sees it go in gets the building's mark.
    private void IntoShelter(Unit u, Unit b)
    {
        for (var p = 1; p <= _players.Count; p++)
        {
            if (p != u.Owner && IsVisibleTo(u, (byte)p))
            {
                b.ShelterMarks |= Bit((byte)p);
            }
        }
        u.ClearPath();
        (u.Vel, u.DriveSpeed, u.AutoTarget, u.SalvoLeft, u.AimAt, u.AimLeft) = (default, default, 0, 0, 0, 0);
        u.Inside = InShelter;
        u.ShelterStage = ShelterStage.In;
        u.Pos = ShelterBayCell(b, u.ShelterBay).Center();
        u.Idle();
        b.ShelterHolder = u.Owner;
    }

    // OutOfShelter puts u (in or leaving b) back on the map at its door: on a move order given inside it drives on. The last
    // one out clears the marks of the sides that see its door (they saw it come out).
    private void OutOfShelter(Unit u, Unit b)
    {
        var (go, then) = (u.ShelterGo, u.ShelterThen);
        var door = ShelterDoorCell(b, u.ShelterBay);
        PutOutAtDoor(u, b);
        if (b.ShelterMarks != 0 && ShelteredIn(b).Count == 0)
        {
            for (var p = 1; p <= _players.Count; p++)
            {
                if (SeesCell((byte)p, door))
                {
                    b.ShelterMarks &= ~Bit((byte)p);
                }
            }
        }
        if (go && u.Alive)
        {
            MoveGround([u], then);
        }
    }

    // PutOutAtDoor puts u on the map on the first free cell at its door (a wreck too) and ends its use of the shelter.
    private void PutOutAtDoor(Unit u, Unit b)
    {
        var door = ShelterDoorCell(b, u.ShelterBay);
        u.Inside = 0;
        u.Pos = Grid.TrySpiral(door, 0, out var c) ? c.Center() : door.Center();
        EndShelter(u);
        if (u.Alive)
        {
            u.Idle();
        }
    }

    // EndShelter clears u's shelter state (a vehicle still inside is put out at its door first).
    private void EndShelter(Unit u)
    {
        if (u.ShelterStage is ShelterStage.In or ShelterStage.Leaving && Unit(u.Shelter) is { } b && u.Inside == InShelter)
        {
            u.Inside = 0;
            var door = ShelterDoorCell(b, u.ShelterBay);
            u.Pos = Grid.TrySpiral(door, 0, out var c) ? c.Center() : door.Center();
        }
        (u.Shelter, u.ShelterBay, u.ShelterStage, u.ShelterUntil, u.ShelterGo, u.ShelterThen) = (0, -1, ShelterStage.None, 0, false, default);
    }

    // SeesCell: player sees cell c now (no fog: every cell).
    private bool SeesCell(byte player, Cell c) => FogOf(player) is not { } f || f.Visible(c.X, c.Y);

    // EvacuateShelter puts every vehicle inside b out at its door at once (a bulldozer starting to clear the building).
    private void EvacuateShelter(Unit b)
    {
        foreach (var u in _units)
        {
            if (IsShelteredIn(u, b))
            {
                PutOutAtDoor(u, b);
            }
        }
        b.ShelterHolder = 0;
    }

    // ShelterBlast passes a blast on shelter building b to the vehicles inside: each loses pct percent of the HP b lost.
    private void ShelterBlast(Unit b, ShelterSpec s, int lost)
    {
        var dmg = (int)((long)lost * s.OccupantDamage / 100);
        if (dmg <= 0)
        {
            return;
        }
        foreach (var o in _units)
        {
            if (IsShelteredIn(o, b) && o.Alive)
            {
                o.HP -= dmg;
            }
        }
    }

    // RunShelterLosses runs before ejection: a vehicle killed inside comes out at its door as a wreck (its crew get out
    // there), and every vehicle inside a building that has fallen comes out at its door, each surviving with the collapse
    // chance at most the collapse HP (rolled on the world's generator in vehicle ID order), or destroyed in the ruins.
    private void RunShelterLosses()
    {
        foreach (var u in _units)
        {
            if (u.Shelter == 0 || u.ShelterStage is not (ShelterStage.In or ShelterStage.Leaving))
            {
                continue;
            }
            var b = Unit(u.Shelter);
            var fell = b is null || !b.Alive || b.Rubble;
            if (!fell && u.Alive)
            {
                continue;
            }
            var alive = u.Alive;
            if (b is null)
            {
                u.Inside = 0; // gone without a trace (never: buildings stay as rubble)
                EndShelter(u);
                continue;
            }
            PutOutAtDoor(u, b);
            if (fell && alive && b.Def.Archetype.Shelter is { } s)
            {
                u.HP = _rng.Intn(100) < s.CollapseSurvival ? Math.Max(1, Math.Min(u.HP, u.Def.MaxHp * s.CollapseHp / 100)) : 0;
            }
        }
    }

    // RunShelterHold runs after the garrisons: each shelter's holder is the side whose vehicles are inside (0: none), and a
    // side's mark lasts while it sees one of the doors.
    private void RunShelterHold()
    {
        var held = _shelterHeld;
        held.Clear();
        foreach (var u in _units)
        {
            if (u.Shelter != 0 && u.Alive && u.ShelterStage is ShelterStage.In or ShelterStage.Leaving)
            {
                held.Add((u.Shelter, u.Owner));
            }
        }
        held.Sort();
        var next = 0;
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Shelter is not { } s)
            {
                continue;
            }
            while (next < held.Count && held[next].Building < b.Id)
            {
                next++;
            }
            b.ShelterHolder = next < held.Count && held[next].Building == b.Id ? held[next].Owner : (byte)0;
            if (b.ShelterMarks == 0)
            {
                continue;
            }
            for (var p = 1; p <= _players.Count; p++)
            {
                if ((b.ShelterMarks & Bit((byte)p)) == 0)
                {
                    continue;
                }
                var sees = false;
                for (var i = 0; i < s.Bays && !sees; i++)
                {
                    sees = SeesCell((byte)p, ShelterDoorCell(b, i));
                }
                if (!sees || !b.Alive)
                {
                    b.ShelterMarks &= ~Bit((byte)p);
                }
            }
        }
    }
}

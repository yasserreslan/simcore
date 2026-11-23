using System.Globalization;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimRun;

// INF-E1b-2's scenes (the battle rules' 25: 2, 3, 12, 16, 27, 28, 29 the soldier's part, 34): the soldier's think measured. Each prints
// PASS-LINE lines (met or NOT MET) and REPORT lines; deterministic per seed.
internal static partial class BattleBench
{
    private const int WallX = 27; // scene 2's near wall: a low wall west of red's south end (cover facing blue)
    private const int FarX = 39; // scene 2's far cover: a wall 8 cells behind red

    private static void NearWall(World w)
    {
        for (var y = 22; y <= 27; y++)
        {
            w.Grid.Set(new Cell(WallX, y), Terrain.Blocked);
        }
        for (var y = 14; y <= 26; y++)
        {
            w.Grid.Set(new Cell(FarX, y), Terrain.Blocked);
        }
    }

    private static bool AtNearWall(Unit u) => Cell.Of(u.Pos) is var c && c.X == WallX + 1 && c.Y >= 21 && c.Y <= 28;

    // Scene 2 (near cover vs far cover) and 3 / 34 (before the shooting, alert): red's men by a low wall 2 cells to their side
    // and a wall 8 cells behind them; 2: both attack-move as scene 1; 3 and 34: red idle, blue walks in from 10 cells.
    private static void Cover2(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks, double scene1Median)
    {
        foreach (var idle in new[] { false, true })
        {
            Console.WriteLine(idle ? $"== scenes 3 and 34, before the shooting: red idle by the wall, blue walking in from 10 cells ({seeds} seeds)"
                : $"== scene 2, near cover vs far cover: scene 1 with a low wall 2 cells to red's side and a wall 8 cells behind ({seeds} seeds)");
            var times = new List<double>();
            int near = 0, tookFast = 0, far = 0, before = 0, beforeOf = 0, runToward = 0, firstMoveOk = 0;
            foreach (var seed in Seeds(seeds))
            {
                var m = match(seed);
                var (w, blue, red) = Field(m, Layout.Meeting, idle);
                NearWall(w);
                var nearIds = red.Where(id => w.Unit(id) is { } u && Math.Abs(u.Pos.X.Raw / Fixed.OneRaw - (WallX + 1)) <= 3 &&
                    Cell.Of(u.Pos).Y >= 19).ToList();
                near += nearIds.Count;
                uint first = 0, sight = 0, move = 0;
                var took = new Dictionary<uint, uint>();
                var dead = false;
                while (w.Tick < maxTicks && !dead)
                {
                    w.Step();
                    if (first == 0 && w.ShotEvents.Count > 0)
                    {
                        first = w.Tick;
                        before += nearIds.Count(id => w.Unit(id) is { } u && AtNearWall(u));
                        beforeOf += nearIds.Count;
                    }
                    foreach (var id in red)
                    {
                        if (w.Unit(id) is not { Fight: { } f } u || !Able(u))
                        {
                            continue;
                        }
                        if (sight == 0 && (f.Alert || f.InContact))
                        {
                            sight = w.Tick;
                        }
                        if (move == 0 && f.SpotMoving)
                        {
                            move = w.Tick;
                        }
                        if (AtNearWall(u) && nearIds.Contains(id) && !took.ContainsKey(id))
                        {
                            took[id] = w.Tick;
                        }
                        far += Cell.Of(u.Pos).X >= FarX - 1 && f.InContact ? 1 : 0;
                        var d = u.Intent.Desired;
                        runToward += f.Gait is Gait.Run or Gait.Sprint && World.Dir32(d.X.Raw, d.Y.Raw) is >= 12 and <= 20 && !f.InContact ? 1 : 0;
                    }
                    dead = blue.All(id => !Able(w.Unit(id))) || red.All(id => !Able(w.Unit(id)));
                }
                var start = idle ? sight : first;
                tookFast += took.Values.Count(t => start != 0 && t <= start + 30);
                firstMoveOk += idle && sight != 0 && move != 0 && move <= sight + 20 && (first == 0 || move < first) ? 1 : 0;
                if (!idle)
                {
                    var (w2, b2, r2) = Field(m, Layout.Meeting); // the whole fight again, timed as scene 1 (the same seed: the same play)
                    NearWall(w2);
                    var r = Play(w2, b2, r2, maxTicks);
                    if (r.Decided != 0)
                    {
                        times.Add((r.Decided - r.FirstShot) / 20.0);
                    }
                }
                Console.WriteLine(string.Create(Inv, $"  seed {seed}: first sight {sight}, first spot move {move}, first shot {first}, at the wall {took.Count} of {nearIds.Count}"));
            }
            if (!idle)
            {
                times.Sort();
                var median = times.Count == 0 ? double.NaN : times[times.Count / 2];
                Console.WriteLine($"PASS-LINE red's men within 3 cells of the wall take it within 1.5 s: {tookFast} of {near}: {(tookFast == near ? "met" : "NOT MET")}");
                Console.WriteLine($"PASS-LINE no red man goes to the far cover while blue is in reach: {far} soldier-ticks there: {(far == 0 ? "met" : "NOT MET")}");
                Console.WriteLine(string.Create(Inv, $"PASS-LINE red's fight lasts at least 30% longer than scene 1's: median {median:0.00} s against {scene1Median:0.00} s: {(median >= scene1Median * 1.3 ? "met" : "NOT MET")}"));
            }
            else
            {
                Console.WriteLine($"PASS-LINE scene 3, red takes the wall before blue is in range: {before} of {beforeOf} at the first shot: {(before == beforeOf && beforeOf > 0 ? "met" : "NOT MET")}");
                Console.WriteLine($"PASS-LINE scene 34, red's first moves to position start within 1 s of first sight and before any shot: {firstMoveOk} of {seeds}: {(firstMoveOk == seeds ? "met" : "NOT MET")}");
                Console.WriteLine($"PASS-LINE scene 34, no red man runs toward blue: {runToward} soldier-ticks: {(runToward == 0 ? "met" : "NOT MET")}");
            }
        }
    }

    // Scene 12: a blue rifleman 3 cells from 3 red riflemen behind a low wall (all holding their ground).
    private static void GrenadeScene(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 12, grenade into cover: a blue rifleman 3 cells from red behind a wall ({seeds} seeds)");
        int threw = 0, inZone = 0, reacted = 0;
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m) { CombatOff = _off };
            for (var y = 17; y <= 23; y++)
            {
                w.Grid.Set(new Cell(22, y), Terrain.Blocked);
            }
            var b = w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(20, 20), Facing.East);
            var red = new List<uint>();
            for (var i = 0; i < 3; i++)
            {
                red.Add(w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(23, 19 + i), Facing.West));
            }
            w.Issue(new Command { Tick = 1, Player = m.Players[0].Id, Kind = CommandKind.SetStance, Units = [b], Stance = Stance.HoldGround });
            w.Issue(new Command { Tick = 1, Player = m.Players[1].Id, Kind = CommandKind.SetStance, Units = red, Stance = Stance.HoldGround });
            uint thrown = 0;
            var start = new Dictionary<uint, (Vec Pos, Posture P)>();
            while (w.Tick < Math.Min(maxTicks, 200u))
            {
                w.Step();
                if (thrown == 0 && w.Unit(b) is { Fight.ThrowAt: not 0 })
                {
                    thrown = w.Tick;
                }
                foreach (var g in w.Grenades)
                {
                    if (w.Tick == g.LandsAt)
                    {
                        foreach (var id in red)
                        {
                            if (w.Unit(id) is { } u && (u.Pos - g.At).LenSq() <= 4L * Fixed.OneRaw * Fixed.OneRaw)
                            {
                                start[id] = (u.Pos, u.Fight!.PostureTo);
                            }
                        }
                    }
                    if (w.Tick + 1 == g.GoesOff)
                    {
                        foreach (var (id, s) in start)
                        {
                            inZone++;
                            reacted += w.Unit(id) is { } u && ((u.Pos - s.Pos).LenSq() >= Fixed.OneRaw * Fixed.OneRaw / 4 || u.Fight!.PostureTo == Posture.Prone) ? 1 : 0;
                        }
                        start.Clear();
                    }
                }
            }
            threw += thrown != 0 && thrown <= 40 ? 1 : 0;
            Console.WriteLine($"  seed {seed}: throw started at tick {thrown}");
        }
        Console.WriteLine($"PASS-LINE blue throws within 2 s: {threw} of {seeds}: {(threw == seeds ? "met" : "NOT MET")}");
        Console.WriteLine($"PASS-LINE red men in the blast zone who see it scramble or dive: {reacted} of {inZone}: {(reacted == inZone && inZone > 0 ? "met" : "NOT MET")}");
    }

    // Scene 16: a blue infantry carrier moving slowly east with a blue squad of 6 (both ordered to the same goal) into red's 6
    // holding their ground 14 cells ahead.
    private static void VehicleScene(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 16, moving with a vehicle: an infantry carrier and a squad move together into contact ({seeds} seeds)");
        long ticks = 0, covered = 0, ahead = 0, movingShots = 0, following = 0, near = 0, slow = 0, ordered = 0; // (INF-E1b-10: the why)
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m) { CombatOff = _off };
            var v = w.Spawn((m.Players[0].Find(m.Content, "infantry_carrier") ?? m.Players[0].Find(m.Content, "scout_truck"))!, new Cell(14, 20), Facing.East);
            // Its driver aboard (INF-E1b-2b, a bench fix: without a crew it could not act and never moved).
            World.Board(w.Unit(w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(14, 20), Facing.East))!, w.Unit(v)!);
            var blue = new List<uint>();
            var red = new List<uint>();
            for (var i = 0; i < 6; i++)
            {
                blue.Add(w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(12 - i % 2, 18 + i), Facing.East));
                red.Add(w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(30, 17 + i), Facing.West));
            }
            w.Issue(new Command { Tick = 1, Player = m.Players[0].Id, Kind = CommandKind.FormSquad, Units = blue });
            w.Issue(new Command { Tick = 1, Player = m.Players[1].Id, Kind = CommandKind.SetStance, Units = red, Stance = Stance.HoldGround });
            w.Issue(new Command { Tick = 5, Player = m.Players[0].Id, Kind = CommandKind.Move, Units = [v], Cell = new Cell(40, 20) });
            w.Issue(new Command { Tick = 5, Player = m.Players[0].Id, Kind = CommandKind.Move, Units = [blue[0]], Cell = new Cell(40, 20) });
            var r = w.Content.InfantryCombat!;
            // The carrier keeps to the squad's walk (INF-E1b-2b, a bench fix: at its full speed it left the squad behind and never
            // counted as cover, rules 7.6's speedPercent): the convoy cap the escorts use, set each tick before the movement reads it.
            var pace = w.Unit(blue[0])!.Def.Speed.Raw * r[CombatValue.GaitWalk] / 100;
            while (w.Tick < Math.Min(maxTicks, 600u))
            {
                if (w.Unit(v) is { } cv)
                {
                    (cv.ConvoyCap, cv.ConvoyTick) = (pace, w.Tick);
                }
                w.Step();
                foreach (var se in w.ShotEvents)
                {
                    movingShots += blue.Contains(se.Shooter) && w.Unit(se.Shooter) is { } s && World.Walking(s) ? 1 : 0;
                }
                if (w.Unit(v) is not { Alive: true } veh)
                {
                    break;
                }
                foreach (var id in blue)
                {
                    if (w.Unit(id) is not { Fight: { InContact: true } f } u || !Able(u) || w.SquadOf(id) is not { Mind.Axis: >= 0 and var axis })
                    {
                        continue;
                    }
                    ticks++;
                    covered += w.VehicleCover(Cell.Of(u.Pos), axis, u.Owner, r) != CoverKind.None ? 1 : 0;
                    following += f.Vehicle == v ? 1 : 0;
                    near += (veh.Pos - u.Pos).LenSq() <= 16L * Fixed.OneRaw * Fixed.OneRaw ? 1 : 0;
                    slow += veh.Moving && veh.DriveSpeed.Raw * 100 <= veh.Def.Speed.Raw * r[CombatValue.VehicleCoverSpeedPercent] ? 1 : 0;
                    ordered += u.Order.Kind is OrderKind.Move or OrderKind.AttackMove ? 1 : 0;
                    ahead += u.Pos.X.Raw > veh.Pos.X.Raw + Fixed.OneRaw ? 1 : 0;
                }
            }
        }
        Console.WriteLine(string.Create(Inv, $"PASS-LINE the vehicle covers the soldier from the squad's axis in 90%+ of soldier-ticks in contact: {(ticks == 0 ? 0 : 100.0 * covered / ticks):0.0}% of {ticks}: {(ticks > 0 && covered * 10 >= ticks * 9 ? "met" : "NOT MET")}"));
        Console.WriteLine($"PASS-LINE soldiers fire while walking: {movingShots} shots on the move: {(movingShots > 0 ? "met" : "NOT MET")}");
        Console.WriteLine($"PASS-LINE none is ahead of the vehicle's nose: {ahead} soldier-ticks: {(ahead == 0 ? "met" : "NOT MET")}");
        Console.WriteLine($"REPORT of {ticks} soldier-ticks in contact: following the vehicle (7.6) {following}, within 4 cells of it {near}, the vehicle moving under speedPercent {slow}, the man under a Move or Attack-move {ordered}");
    }

    // Scene 27: 6 blue riflemen in a squad against 6 red in a squad, 4 cells apart, holding their ground.
    private static void SpreadScene(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 27, spread of fire: 6 rifles in a line against 6 in a line at 4 cells ({seeds} seeds)");
        int covered = 0, bad = 0;
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m) { CombatOff = _off };
            var blue = new List<uint>();
            var red = new List<uint>();
            for (var i = 0; i < 6; i++)
            {
                blue.Add(w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(20, 16 + i * 2), Facing.East));
                red.Add(w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(24, 16 + i * 2), Facing.West));
            }
            w.Issue(new Command { Tick = 1, Player = m.Players[0].Id, Kind = CommandKind.FormSquad, Units = blue });
            w.Issue(new Command { Tick = 1, Player = m.Players[1].Id, Kind = CommandKind.FormSquad, Units = red });
            uint first = 0;
            var ok = false;
            while (w.Tick < Math.Min(maxTicks, 400u))
            {
                w.Step();
                first = first == 0 && w.ShotEvents.Count > 0 ? w.Tick : first;
                if (first == 0)
                {
                    continue;
                }
                var counts = red.Where(id => Able(w.Unit(id))).Select(id => blue.Count(b => w.Unit(b) is { } s && Able(s) && s.AutoTarget == id)).ToList();
                if (counts.Count == 0)
                {
                    break;
                }
                bad += counts.Any(c => c >= 3) && counts.Any(c => c == 0) ? 1 : 0;
                ok |= w.Tick <= first + 20 && counts.All(c => c >= 1);
            }
            covered += ok ? 1 : 0;
        }
        Console.WriteLine($"PASS-LINE within 1 s of the first shot every enemy has a shooter: {covered} of {seeds}: {(covered == seeds ? "met" : "NOT MET")}");
        Console.WriteLine($"PASS-LINE no enemy has 3 or more shooters while another has none: {bad} ticks: {(bad == 0 ? "met" : "NOT MET")}");
    }

    // Scene 28: 3 blue riflemen ordered to attack one red rifleman 4 cells off, a second red beside him (all red hold their fire).
    private static void MidBurstScene(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 28, a target dies in mid-burst: 3 rifles on one enemy, a second beside him ({seeds} seeds)");
        int atDead = 0, inBand = 0, firsts = 0;
        var delays = new List<int>();
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m) { CombatOff = _off };
            var blue = new List<uint>();
            for (var i = 0; i < 3; i++)
            {
                blue.Add(w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(20, 19 + i), Facing.East));
            }
            var t1 = w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(24, 20), Facing.West);
            var t2 = w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(24, 22), Facing.West);
            w.Issue(new Command { Tick = 1, Player = m.Players[0].Id, Kind = CommandKind.FormSquad, Units = blue });
            w.Issue(new Command { Tick = 1, Player = m.Players[1].Id, Kind = CommandKind.SetStance, Units = [t1, t2], Stance = Stance.HoldFire });
            w.Issue(new Command { Tick = 30, Player = m.Players[0].Id, Kind = CommandKind.Attack, Units = blue, Target = t1 });
            uint gone = 0, deadAt = 0;
            var firstAt2 = new Dictionary<uint, uint>();
            while (w.Tick < Math.Min(maxTicks, 600u) && firstAt2.Count < 3)
            {
                w.Step();
                if (deadAt == 0 && gone != 0 && w.Unit(t1) is not { Alive: true })
                {
                    deadAt = w.Tick;
                }
                if (gone == 0 && w.Unit(t1) is not { Alive: true } || gone == 0 && w.Unit(t1) is { Life.Wound: WoundState.Down })
                {
                    gone = w.Tick;
                }
                foreach (var se in w.ShotEvents)
                {
                    if (deadAt != 0 && se.Target == t1)
                    {
                        atDead++; // fired after the tick he died (shots in that tick were aimed at a living man)
                    }
                    if (gone != 0 && se.Target == t2 && !firstAt2.ContainsKey(se.Shooter))
                    {
                        firstAt2[se.Shooter] = w.Tick;
                    }
                }
            }
            _ = deadAt;
            foreach (var t in firstAt2.Values)
            {
                firsts++;
                var d = (int)(t - gone - 1);
                delays.Add(d);
                inBand += d >= 4 ? 1 : 0; // never sooner than 4 ticks after his next think (a reload can make it later)
            }
        }
        delays.Sort();
        Console.WriteLine($"PASS-LINE no shot is fired at the dead man: {atDead}: {(atDead == 0 ? "met" : "NOT MET")}");
        Console.WriteLine($"PASS-LINE each shooter's first shot at the second enemy comes 4 to 8 ticks after his next think: {inBand} of {firsts} at 4 or more (delays {string.Join(" ", delays)}; over 8 when his rifle was still reloading): {(inBand == firsts && firsts > 0 ? "met" : "NOT MET")}");
    }

    // Scene 29, the soldier's part: a red rifleman midway between two equal low walls (each 2 cells off) under fire from 2 blue at
    // 5 cells; no squads.
    private static void DitherScene(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        Console.WriteLine($"== scene 29 (the soldier's part), no dithering: a soldier midway between two equal walls under fire ({seeds} seeds)");
        int worst = 0, bob = 0;
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(64, 40), m) { CombatOff = _off };
            w.Grid.Set(new Cell(29, 18), Terrain.Blocked);
            w.Grid.Set(new Cell(29, 22), Terrain.Blocked);
            var red = w.Spawn(m.Players[1].Find(m.Content, "Rifleman")!, new Cell(30, 20), Facing.West);
            var blue = new List<uint>
            {
                w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(25, 19), Facing.East),
                w.Spawn(m.Players[0].Find(m.Content, "Rifleman")!, new Cell(25, 21), Facing.East),
            };
            w.Issue(new Command { Tick = 1, Player = m.Players[0].Id, Kind = CommandKind.SetStance, Units = blue, Stance = Stance.HoldGround });
            w.Issue(new Command { Tick = 1, Player = m.Players[1].Id, Kind = CommandKind.SetStance, Units = [red], Stance = Stance.HoldGround });
            var spots = 0;
            Cell? last = null;
            var lastChange = -100;
            var posture = Posture.Stand;
            var moved = false;
            while (w.Tick < Math.Min(maxTicks, 400u) && w.Unit(red) is { Alive: true, Fight: { } f } u)
            {
                w.Step();
                if (f.HasSpot && f.Spot != last && f.Spot != Cell.Of(u.Pos))
                {
                    (spots, last) = (spots + 1, f.Spot);
                }
                moved |= World.Walking(u);
                if (f.PostureTo != posture)
                {
                    bob += !moved && (int)w.Tick - lastChange < 20 ? 1 : 0;
                    (posture, lastChange, moved) = (f.PostureTo, (int)w.Tick, false);
                }
            }
            worst = Math.Max(worst, spots);
        }
        Console.WriteLine($"PASS-LINE the soldier changes spot at most once in 20 s: at most {worst}: {(worst <= 1 ? "met" : "NOT MET")}");
        Console.WriteLine($"PASS-LINE no soldier changes posture twice within 20 ticks without moving: {bob}: {(bob == 0 ? "met" : "NOT MET")}");
    }
}

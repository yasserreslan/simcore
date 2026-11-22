// INF-HELP (the owner, 2026-11-22, after the game's --demo=battle: "what are they doing laying back?"): -bench-battle support, the
// demo's shape on a flat 200 x 170 field, eight riflemen a squad, every squad formed on Guard, squads of a group 12 cells apart
// side by side (one order a group, as the demo gives it). Blue: an attack group of 5 Attack-moves east; a Hold group of 4 (Hold mode
// facing east) further south. Red: a group of 6 Attack-moves west through blue's attack group; a group of 2 Attack-moves onto the
// Hold group's north end squad only. Per side, the squad-seconds "laying back": not in contact itself, not answering a call, while
// a friendly squad within 25 cells (centre to centre) is in contact; each counted under 13.2's first failed test against that
// friend's call (or the call's own state: full, need met, out of callRadius, no call) and under what its men see (no enemy seen
// now; seen, all out of their weapons' range). The outcome: able men left a side, the squads broken, the deciding tick.
using System.Globalization;
using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimRun;

internal static partial class BattleBench
{
    private static int Support(int seeds, Func<ulong, CompiledMatch> match, uint maxTicks)
    {
        var ticks = Math.Min(maxTicks, 2400u);
        Console.WriteLine($"== support (INF-HELP): blue attack 5 + Hold 4, red 6 + 2 on the Hold group's end, 8 riflemen a squad, {ticks} ticks, {seeds} seeds");
        var total = new Dictionary<string, long>[] { new(), new() };
        var sight = new Dictionary<string, long>[] { new(), new() };
        var lay = new long[2];
        foreach (var seed in Seeds(seeds))
        {
            var m = match(seed);
            var w = new World(new Grid(200, 170), m) { CombatOff = _off };
            var r = w.Content.InfantryCombat!;
            var side = new List<uint>[] { [], [] };
            List<uint> Group(int p, int x, int y0, int n, int face)
            {
                var all = new List<uint>();
                for (var s = 0; s < n; s++)
                {
                    var ids = new List<uint>();
                    for (var i = 0; i < 8; i++)
                    {
                        ids.Add(w.Spawn(m.Players[p].Find(m.Content, "Rifleman")!, new Cell(x + (i / 4) * (p == 0 ? -2 : 2), y0 + s * 12 + (i % 4) * 2), face));
                    }
                    w.Issue(new Command { Tick = 1, Player = m.Players[p].Id, Kind = CommandKind.FormSquad, Units = ids });
                    all.AddRange(ids);
                }
                side[p].AddRange(all);
                return all;
            }
            var blueAttack = Group(0, 40, 20, 5, Facing.East);
            var blueHold = Group(0, 50, 110, 4, Facing.East);
            var redMain = Group(1, 110, 26, 6, Facing.West);
            var redEnd = Group(1, 110, 98, 2, Facing.West);
            w.Issue(new Command { Tick = 5, Player = m.Players[0].Id, Kind = CommandKind.AttackMove, Units = blueAttack, Cell = new Cell(160, 44) });
            w.Issue(new Command { Tick = 5, Player = m.Players[0].Id, Kind = CommandKind.SetSquadMode, Units = blueHold, Cell = new Cell(50, 128), Cell2 = new Cell(90, 128), Index = (int)SquadMode.Hold });
            w.Issue(new Command { Tick = 5, Player = m.Players[1].Id, Kind = CommandKind.AttackMove, Units = redMain, Cell = new Cell(10, 56) });
            w.Issue(new Command { Tick = 5, Player = m.Players[1].Id, Kind = CommandKind.AttackMove, Units = redEnd, Cell = new Cell(45, 104) });
            uint decided = 0;
            var radius = 25L * Fixed.OneRaw;
            var seedLay = new long[2];
            while (w.Tick < ticks)
            {
                w.Step();
                var alive = new[] { side[0].Count(id => Fighting(w, id)), side[1].Count(id => Fighting(w, id)) };
                if (alive[0] == 0 || alive[1] == 0)
                {
                    decided = w.Tick;
                    break;
                }
                if (w.Tick % 20 != 0)
                {
                    continue;
                }
                foreach (var s in w.Squads)
                {
                    if (s.Mind is not { } sm || sm.InContact || sm.HelpOf != 0 || s.Owner is not (1 or 2) || !Centre(w, s, out var sc))
                    {
                        continue;
                    }
                    Squad? near = null;
                    var nd = long.MaxValue;
                    foreach (var c in w.Squads)
                    {
                        if (c != s && c.Owner == s.Owner && c.Mind is { InContact: true } && Centre(w, c, out var cc) && (cc - sc).LenSq() is var d && d <= radius * radius && d < nd)
                        {
                            (near, nd) = (c, d);
                        }
                    }
                    if (near is null)
                    {
                        continue;
                    }
                    var p = s.Owner - 1;
                    lay[p]++;
                    seedLay[p]++;
                    var why = Why(w, s, near, r);
                    total[p][why] = total[p].GetValueOrDefault(why) + 1;
                    var see = Sees(w, s);
                    sight[p][see] = sight[p].GetValueOrDefault(see) + 1;
                }
            }
            var left = new[] { side[0].Count(id => Fighting(w, id)), side[1].Count(id => Fighting(w, id)) };
            var broken = new[] { BrokenSquads(w, 1), BrokenSquads(w, 2) };
            var winner = left[0] == 0 && left[1] == 0 ? "none" : left[1] == 0 ? "blue" : left[0] == 0 ? "red" : left[0] > left[1] ? "blue ahead" : left[1] > left[0] ? "red ahead" : "even";
            Console.WriteLine(string.Create(Inv, $"  seed {seed}: laying back blue {seedLay[0]} red {seedLay[1]} squad-s; able unbroken blue {left[0]}/{side[0].Count} red {left[1]}/{side[1].Count}; broken squads blue {broken[0]} red {broken[1]}; {winner}{(decided > 0 ? $" at {decided / 20.0:0.0} s" : $" at {ticks / 20.0:0} s (time)")}; calls {w.CallStats.Calls} assigned {w.CallStats.Assigned} arrived {w.CallStats.Arrived}"));
        }
        for (var p = 0; p < 2; p++)
        {
            Console.WriteLine($"  {(p == 0 ? "blue" : "red")} laying back {lay[p]} squad-s: why {string.Join(", ", total[p].OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}; its men {string.Join(", ", sight[p].OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
        }
        return 0;
    }

    // Fighting: an able man whose squad (if any) is not broken.
    private static bool Fighting(World w, uint id) =>
        w.Unit(id) is { } u && Able(u) && w.SquadOf(id)?.Mind is not { Spirit: MoraleState.Broken };

    private static int BrokenSquads(World w, byte owner) => w.Squads.Count(s => s.Owner == owner && s.Mind is { Spirit: MoraleState.Broken });

    private static bool Centre(World w, Squad s, out Vec at)
    {
        long x = 0, y = 0, n = 0;
        foreach (var id in s.Members)
        {
            if (w.Unit(id) is { } u && Able(u))
            {
                (x, y, n) = (x + u.Pos.X.Raw, y + u.Pos.Y.Raw, n + 1);
            }
        }
        at = n == 0 ? default : new Vec(Fixed.FromRaw(x / n), Fixed.FromRaw(y / n));
        return n > 0;
    }

    // Why s lays back while friend c fights: 13.2's first failed test against c's call (a helper's: the call it answers), else the
    // call's own state.
    private static string Why(World w, Squad s, Squad c, InfantryCombatRules r)
    {
        if (c.Mind is { HelpOf: not 0 } hm && !hm.Calling && w.Squad(hm.HelpOf) is { Mind.Calling: true } caller)
        {
            c = caller; // a helper makes no call (13.1): the friend's fight is the call it answers
        }
        if (c.Mind is not { Calling: true })
        {
            return "no call";
        }
        var block = w.HelpTest(s, s.Mind!, c, r);
        if (block != HelpBlock.Free)
        {
            return block == HelpBlock.Order ? $"order ({s.Order})" : block.ToString();
        }
        var (need, count) = w.CallNeed(c, r);
        return count >= r[CombatValue.CaMaxHelpers] ? "call full" : need <= 0 ? "need met" : "free, not matched (radius or in turn)";
    }

    private static string Sees(World w, Squad s)
    {
        var seen = false;
        foreach (var id in s.Members)
        {
            if (w.Unit(id) is not { Fight: { } f } u || !Able(u) || u.Def.Weapons.Count == 0)
            {
                continue;
            }
            foreach (var k in f.Contacts)
            {
                if (k.Enemy == 0 || k.Source != ContactSource.SeenNow || w.Unit(k.Enemy) is not { } e)
                {
                    continue;
                }
                seen = true;
                if ((e.Pos - u.Pos).LenSq() <= u.Def.Weapons[0].Range.Raw * u.Def.Weapons[0].Range.Raw)
                {
                    return "sees one in range";
                }
            }
        }
        return seen ? "sees, out of range" : "sees no enemy";
    }
}

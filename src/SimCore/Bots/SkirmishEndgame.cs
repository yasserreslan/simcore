// The skirmish AI's endgame and its unsticking (docs/design.md, "The skirmish AI's stalls"). Matches between two AIs stalled
// for three reasons: the last soldiers of a side sat as drivers in vehicles that could not hurt them (a cargo truck, a rocket
// truck that needs a spotter or is inside its minimum range), so neither side could finish the other; without squads a side
// with no fighters left kept its air defence and launchers at home; and a soldier on his way out could be boxed in at home by
// idle friends that never stand aside (three or more touching each other do not give way), so his side never ran out of fighters.
// So: once a wave finds nothing else to send (the endgame, with or without squads), the drivers of the AI's vehicles that are not
// fighters get out and fight on foot, and a soldier who has not left his cell for UnstickDecisions decisions on a move order has
// the idle friends round him step away from him. No random draws; units in ID order.
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class SkirmishAi
{
    /// <summary>Decisions a unit on a move order may stay in one cell before the idle friends round it step aside.</summary>
    public int UnstickDecisions { get; init; } = 10;

    /// <summary>How near a stuck unit (cells) an idle friend must be to step aside, and how far beyond that it steps.</summary>
    public int UnstickCells { get; init; } = 2;

    private readonly List<(uint Id, Cell Cell, int Still)> _still = []; // units on a move order: where at the last decision, for how many

    internal bool EndgameForTests => _endgame;

    // Dismount: in the endgame, the drivers of the vehicles that are not fighters (no weapon, a launcher, air defence) get out, each
    // when its vehicle has no order and no salvo going; on foot they are fighters, and soldiers are what end a match.
    private void Dismount(List<Command> cmds, uint at, List<Unit> crewed)
    {
        if (!_endgame)
        {
            return;
        }
        var exit = new List<uint>();
        foreach (var v in crewed)
        {
            if (v.Order.Kind == OrderKind.Idle && v.SalvoLeft == 0)
            {
                exit.Add(v.Id);
            }
        }
        if (exit.Count > 0)
        {
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Exit, Units = exit });
        }
    }

    // Crew: in the endgame, soldiers who cannot fight ground units (the Iglas) each take an empty vehicle of the AI's with a weapon
    // that does (a technical), the nearest free one to each soldier, soldiers in ID order, ties to the lower vehicle ID: Iglas
    // alone on both sides could never end a match. Launchers are not taken (their drivers got out of them).
    private void Crew(World w, List<Command> cmds, uint at, List<Unit> guards, TagSet launcherTag)
    {
        if (!_endgame)
        {
            return;
        }
        var empty = new List<Unit>();
        foreach (var v in w.Units)
        {
            if (v.Owner == Player && v.Alive && v.Inside == 0 && v.Driver == 0 && v.Def.Crew is not null && !v.Def.IsStructure &&
                v.Def.Layer == Layers.Ground && v.Def.Speed.Raw > 0 && HitsGround(v) && (launcherTag.IsEmpty || (v.Def.Tags.Bits & launcherTag.Bits) == 0))
            {
                empty.Add(v);
            }
        }
        foreach (var g in guards)
        {
            if (empty.Count == 0)
            {
                return;
            }
            if (g.Def.Crew is not null || g.Order.Kind == OrderKind.Enter || g.Ai != AiState.None || HitsGround(g))
            {
                continue;
            }
            var best = 0;
            for (var i = 1; i < empty.Count; i++)
            {
                if ((empty[i].Pos - g.Pos).LenSq() < (empty[best].Pos - g.Pos).LenSq())
                {
                    best = i;
                }
            }
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Enter, Units = [g.Id], Target = empty[best].Id });
            empty.RemoveAt(best);
        }
    }

    // HitsGround: a weapon of u's engages ground units.
    private static bool HitsGround(Unit u)
    {
        foreach (var wpn in u.Def.Weapons)
        {
            if ((wpn.Effect.Targets & Layers.Ground) != 0)
            {
                return true;
            }
        }
        return false;
    }

    // Unstick: a unit of the AI's on a move or attack-move order, not fighting, that has stayed in one cell for UnstickDecisions
    // decisions has the AI's idle units within UnstickCells of it (no order or a guard post, not fighting) each move UnstickCells
    // + 1 cells straight away from it. The count starts again after.
    private void Unstick(World w, List<Command> cmds, uint at)
    {
        var next = 0;
        var keep = new List<(uint Id, Cell Cell, int Still)>();
        foreach (var u in w.Units)
        {
            if (u.Owner != Player || !u.Alive || !u.CanAct || u.Def.IsStructure || u.Def.Speed.Raw == 0 || u.Def.Layer != Layers.Ground ||
                u.Order.Kind is not (OrderKind.Move or OrderKind.AttackMove) || u.Ai != AiState.None)
            {
                continue;
            }
            while (next < _still.Count && _still[next].Id < u.Id)
            {
                next++;
            }
            var cell = Cell.Of(u.Pos);
            var still = next < _still.Count && _still[next].Id == u.Id && _still[next].Cell == cell ? _still[next].Still + 1 : 0;
            if (still >= UnstickDecisions && StepAside(w, cmds, at, u))
            {
                still = 0;
            }
            keep.Add((u.Id, cell, still));
        }
        _still.Clear();
        _still.AddRange(keep);
    }

    // StepAside orders the AI's idle units round stuck unit s away from it; false when there are none.
    private bool StepAside(World w, List<Command> cmds, uint at, Unit s)
    {
        var any = false;
        foreach (var b in w.Units)
        {
            if (b.Owner != Player || b == s || !b.Alive || !b.CanAct || b.Def.IsStructure || b.Def.Speed.Raw == 0 || b.Def.Layer != Layers.Ground ||
                b.Order.Kind is not (OrderKind.Idle or OrderKind.Guard) || b.Ai != AiState.None || !Near(b.Pos, s.Pos, UnstickCells))
            {
                continue;
            }
            var away = b.Pos - s.Pos;
            if (away.LenSq() == 0)
            {
                continue;
            }
            var to = Cell.Of(b.Pos + away.WithLen(Fixed.FromInt(UnstickCells + 1)));
            to = new Cell(Math.Clamp(to.X, 0, w.Grid.Width - 1), Math.Clamp(to.Y, 0, w.Grid.Height - 1));
            cmds.Add(new Command { Tick = at, Player = Player, Kind = CommandKind.Move, Units = [b.Id], Cell = to });
            any = true;
        }
        return any;
    }
}

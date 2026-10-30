using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private SpecialistJob? TransportJob(World w, Unit carrier)
    {
        if (Directive is { Insertions: false }) { return null; }
        var p = _rules.Foundation!;
        if (_specialists.Count(j => !j.Done && j.Kind == AiSpecialistKind.Transport) >= p.MaxOperations || _situation.Front == Home) { return null; }
        var rotor = carrier.Def.Archetype.Flight?.Rotor?.Riders;
        var seats = rotor?.Seats ?? carrier.Def.Crew?.Passengers ?? 0;
        if (seats == 0 || carrier.Def.Archetype.Flight is not null && rotor is null) { return null; }
        var passengers = _own.Where(u => u.Id != carrier.Id && Mobile(u) && Free(u) && u.Def.Crew is null && u.Def.Builder is null &&
            u.Squad == 0 && !Reserved(u.Id) && !Resting(u.Id) && _ordered.BinarySearch(u.Id) < 0 &&
            (rotor is not null ? u.Def.Tags.Matches(rotor.Tags) : w.CanEnter(u, carrier)))
            .OrderBy(u => Distance(Cell.Of(carrier.Pos), Cell.Of(u.Pos))).ThenBy(u => u.Id)
            .Take(Math.Min(seats, p.TransportPassengers)).ToArray();
        if (passengers.Length == 0) { return null; }
        var target = _operations.FirstOrDefault(o => o.Phase != AiOperationPhase.Withdrawing)?.Objective ?? _situation.Front;
        var destination = Toward(w, target, Home, p.StageCells);
        if (Danger(w, destination, rotor is not null) > Math.Max(1, _situation.OwnStrength)) { return null; }
        if (rotor is null && !Reachable(w, carrier, destination, out destination)) { return null; }
        // Verify dismounted passengers can continue toward the intended objective from their current terrain component.
        if (rotor is null && passengers.Any(u => !Reachable(w, u, destination, out _))) { return null; }
        var job = Job(w, AiSpecialistKind.Transport, carrier, 0, destination);
        job.Pickup = Cell.Of(passengers[0].Pos);
        foreach (var u in passengers.OrderBy(u => u.Id)) { job.Passengers.Add(u.Id); }
        return job;
    }

    private IEnumerable<Unit> Aboard(SpecialistJob j) => j.Passengers.Select(Own).Where(u => u is not null && u.Inside == j.Actor).Select(u => u!);

    private void UpdateTransport(World w, SpecialistJob j, Unit carrier)
    {
        j.Passengers.RemoveAll(id => Own(id) is null);
        if (j.Passengers.Count == 0) { FinishJob(w, j, "transport-passengers-lost"); return; }
        var air = carrier.Def.Archetype.Flight?.Rotor is not null;
        var aboard = Aboard(j).Count();
        switch (j.Stage)
        {
            case 0:
                if (aboard == j.Passengers.Count)
                {
                    j.Stage = 1; j.LastKind = 0;
                    Trace(w, 0, "transport-depart");
                    return;
                }
                if (w.Tick >= j.Since + (uint)_rules.Foundation!.BoardingTicks)
                {
                    AbortTransport(w, j);
                    return;
                }
                if (air)
                { SendJob(w, j, new Command { Kind = CommandKind.HeliLoad, Units = [carrier.Id], Cell = j.Pickup }, "transport-land-for-boarding"); }
                else if (!Near(Cell.Of(carrier.Pos), j.Pickup, _rules.ArrivalCells))
                { SendJob(w, j, new Command { Kind = CommandKind.Move, Units = [carrier.Id], Cell = j.Pickup }, "transport-pickup"); }
                var waiting = j.Passengers.Where(id => Own(id) is { Inside: 0 } u &&
                    (u.Order.Kind != OrderKind.Enter || u.Order.Target != carrier.Id)).ToArray();
                if (waiting.Length > 0) { Emit(w, new Command { Kind = CommandKind.Enter, Units = waiting, Target = carrier.Id }, 0, "transport-board"); }
                break;
            case 1:
                if (air)
                {
                    if (SendJob(w, j, new Command { Kind = CommandKind.HeliUnload, Units = [carrier.Id], Cell = j.Point }, "transport-insert")) { j.Stage = 2; }
                }
                else if (Near(Cell.Of(carrier.Pos), j.Point, _rules.ArrivalCells))
                {
                    if (SendJob(w, j, new Command { Kind = CommandKind.Exit, Units = [carrier.Id] }, "transport-unload")) { j.Stage = 2; }
                }
                else { SendJob(w, j, new Command { Kind = CommandKind.Move, Units = [carrier.Id], Cell = j.Point }, "transport-travel"); }
                break;
            case 2:
                if (aboard == 0) { FinishJob(w, j, "insertion-complete"); }
                break;
            case 3:
                if (aboard == 0) { FinishJob(w, j, "transport-aborted"); }
                else if (air)
                { SendJob(w, j, new Command { Kind = CommandKind.HeliUnload, Units = [carrier.Id], Cell = Clamp(w, Cell.Of(carrier.Pos)) }, "transport-abort-unload"); }
                else { SendJob(w, j, new Command { Kind = CommandKind.Exit, Units = [carrier.Id] }, "transport-abort-unload"); }
                break;
        }
    }

    private void AbortTransport(World w, SpecialistJob j)
    {
        // Stop the carrier and every unboarded passenger in one budget slot. Checking only live
        // Enter orders misses delayed inputs. Keep ownership until this cancellation has executed.
        var units = j.Passengers.Where(id => Own(id) is { Inside: 0 }).Append(j.Actor).ToArray();
        if (!Emit(w, new Command { Kind = CommandKind.Stop, Units = units }, 0, "transport-cancel-boarding")) { return; }
        j.Stage = 3; j.LastKind = 0; j.NextOrder = w.Tick + World.InputDelay + 1;
        Trace(w, 0, "transport-abort");
    }
}

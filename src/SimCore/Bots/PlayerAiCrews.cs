using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Detached driver reservation for a vehicle-crewing workflow.</summary>
public sealed record AiCrewMemory(uint Driver, uint Vehicle, uint Until, uint NextOrder);

public sealed partial class PlayerAi
{
    private readonly List<CrewJob> _crews = [];
    /// <summary>Pending driver jobs, sorted by driver ID.</summary>
    public IReadOnlyList<AiCrewMemory> CrewJobs => _crews.Select(j => new AiCrewMemory(j.Driver, j.Vehicle, j.Until, j.NextOrder)).ToArray();

    private void UpdateCrews(World w)
    {
        foreach (var job in _crews)
        {
            var driver = Own(job.Driver);
            var vehicle = Own(job.Vehicle);
            if (vehicle?.Driver == job.Driver || driver is null || driver.Inside != 0)
            { job.Done = true; continue; }
            if (vehicle is null || vehicle.Rubble || vehicle.Driver != 0 || w.Tick >= job.Until)
            {
                // A Stop supersedes even an Enter input that has not executed yet.
                if (Emit(w, new Command { Kind = CommandKind.Stop, Units = [driver.Id] }, 0, "cancel-crew-assignment"))
                {
                    job.Done = true;
                    _rest.Add((driver.Id, w.Tick + (uint)Math.Max(_rules.RestTicks, World.InputDelay + 1)));
                }
                continue;
            }
            if (w.Tick < job.NextOrder || !Free(driver) || _pending.Any(p => p.Source == driver.Id)) { continue; }
            if (World.CanBoard(driver, vehicle) && Emit(w, new Command { Kind = CommandKind.Enter, Units = [driver.Id], Target = vehicle.Id }, 0, "retry-crew-assignment"))
            {
                _pending.Add(new Pending(driver.Id, -1, CommandKind.Enter, w.Tick + World.InputDelay, vehicle.Id));
                job.NextOrder = w.Tick + World.InputDelay + (uint)_rules.DecisionTicks;
            }
        }
        _crews.RemoveAll(j => j.Done);
    }

    private void ReserveCrew(World w, uint driver, uint vehicle)
    {
        if (_rules.Foundation?.ForceManagement is not { } p) { return; }
        var job = new CrewJob(driver, vehicle, w.Tick + (uint)p.CrewTicks, w.Tick + World.InputDelay + 1);
        var at = _crews.FindIndex(j => j.Driver > driver);
        if (at < 0) { _crews.Add(job); } else { _crews.Insert(at, job); }
    }

    private sealed class CrewJob(uint driver, uint vehicle, uint until, uint next)
    { public readonly uint Driver = driver, Vehicle = vehicle, Until = until; public uint NextOrder = next; public bool Done; }
}

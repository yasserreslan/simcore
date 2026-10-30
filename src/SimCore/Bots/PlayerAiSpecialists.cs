using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Reservations for specialist executors that must not receive competing force orders.</summary>
public enum AiSpecialistKind
{
    /// <summary>Observe a reserved search area from the air.</summary>
    AirRecon,
    /// <summary>Guard an area against hostile aircraft.</summary>
    AirCover,
    /// <summary>Attack a visible weapon-compatible ground target.</summary>
    AirStrike,
    /// <summary>Return for aircraft ammunition, fuel and repairs.</summary>
    Rearm,
    /// <summary>Use a friendly garage's service workflow.</summary>
    Service,
    /// <summary>Position and deploy a radar/air-defense platform.</summary>
    Radar,
    /// <summary>Approach at standoff and engage visible targets.</summary>
    Artillery,
    /// <summary>Bring a repair effect to a friendly damaged unit.</summary>
    Repair,
    /// <summary>Board, carry and unload a reserved passenger group.</summary>
    Transport,
    /// <summary>Guard a friendly cargo truck while its delivery remains active.</summary>
    Escort,
}

/// <summary>A detached specialist assignment view. Stage is executor-specific: acquire, travel, act, release.</summary>
public sealed record AiSpecialistView(uint Id, AiSpecialistKind Kind, uint Unit, uint Target, Cell Point,
    int Stage, uint Deadline, IReadOnlyList<uint> Passengers);

public sealed partial class PlayerAi
{
    private readonly List<SpecialistJob> _specialists = [];
    private uint _jobSerial;

    /// <summary>Specialist reservations, separate from mobile combat-force membership.</summary>
    public IReadOnlyList<AiSpecialistView> SpecialistJobs => _specialists.Select(j => new AiSpecialistView(j.Id, j.Kind,
        j.Actor, j.Target, j.Point, j.Stage, j.Until, j.Passengers.ToArray())).ToArray();

    private bool SpecialistReserved(uint id) => _specialists.Any(j => !j.Done && (j.Actor == id || j.Passengers.BinarySearch(id) >= 0));

    private void Specialists(World w)
    {
        if (_rules.Foundation is not { } p) { return; }
        foreach (var job in _specialists) { UpdateSpecialist(w, job); }
        _specialists.RemoveAll(j => j.Done);
        foreach (var u in _own)
        {
            if (_specialists.Count >= p.SpecialistJobs) { break; }
            if (!u.CanAct || u.Inside != 0 || u.Garrison != 0 || Reserved(u.Id) || Resting(u.Id) ||
                _ordered.BinarySearch(u.Id) >= 0 || u.Rubble || !u.IsBuilt || u.Def.Strike is not null || u.Def.Archetype.Kaman is not null) { continue; }
            var role = RolesOf(u.Def);
            SpecialistJob? j = null;
            if (p.Aircraft && u.Def.Archetype.Flight is not null)
            {
                if (u.Heli?.Phase == HeliPhase.OutOfControl) { continue; }
                var status = w.AirStatusOf(u);

                if (status.FuelPercent <= p.AirFuelPercent || AmmoPercent(u) < p.AirAmmoPercent || (long)u.HP * 100 < (long)u.Def.MaxHp * p.ServiceHpPercent)
                { j = Job(w, AiSpecialistKind.Rearm, u, 0, Home); }
                else if (u.Homing || u.AirOps is { Phase: not FlightPhase.Parked }) { continue; }
                else if (p.Transports && (role & AiRole.Transport) != 0) { j = TransportJob(w, u); }
                else if ((role & AiRole.Strike) != 0 && VisibleStrikeTarget(w, u) is { } target)
                { j = Job(w, AiSpecialistKind.AirStrike, u, target.Id, Cell.Of(target.Pos)); }
                else if ((role & AiRole.Fighter) != 0)
                { j = Job(w, AiSpecialistKind.AirCover, u, 0, SupportAnchor(w)); }
                else
                {
                    var probe = AirProbe(w, u);
                    if (probe >= 0) { j = Job(w, AiSpecialistKind.AirRecon, u, 0, _probes[probe]); j.Probe = probe; }
                }
            }
            else if (Mobile(u) && Free(u))
            {
                if (u.Def.Crew is not null && (long)u.HP * 100 < (long)u.Def.MaxHp * p.ServiceHpPercent)
                {
                    var garage = NearestStore(w, u, g => w.ServiceWorks(g) && g.Def.Archetype.Service is { } s && u.Def.Tags.Matches(s.Tags));
                    if (garage is not null) { j = Job(w, AiSpecialistKind.Service, u, garage.Id, Cell.Of(garage.Pos)); }
                }
                if (j is null && p.Transports && (role & AiRole.Transport) != 0) { j = TransportJob(w, u); }
                if (j is null && (role & AiRole.Repair) != 0)
                {
                    var patient = _own.Where(t => t.Id != u.Id && t.Inside == 0 && t.Def.Layer == Layers.Ground && t.HP < t.Def.MaxHp &&
                        u.Def.Repairs.Any(e => t.Def.Tags.Matches(e.Affects)) && Reachable(w, u, Cell.Of(t.Pos), out _))
                        .OrderBy(t => (long)t.HP * 100 / Math.Max(1, t.Def.MaxHp)).ThenBy(t => t.Id).FirstOrDefault();
                    if (patient is not null) { j = Job(w, AiSpecialistKind.Repair, u, patient.Id, Cell.Of(patient.Pos)); }
                }
                if (j is null && (role & AiRole.Artillery) != 0 && _objectives.FirstOrDefault(o => o.Subject != 0 && o.Kind != AiThreatKind.Air && OffensiveTarget(o.Cell)) is { } objective)
                { j = Job(w, AiSpecialistKind.Artillery, u, objective.Subject, objective.Cell); }
                if (j is null && (role & (AiRole.Radar | AiRole.AntiAir)) != 0 && (role & AiRole.Frontline) == 0)
                { j = Job(w, AiSpecialistKind.Radar, u, 0, SupportAnchor(w)); }
                if (j is null && Fighter(u) && _cargo.FirstOrDefault(c => !c.Done && Own(c.Truck) is not null && !_specialists.Any(s => s.Kind == AiSpecialistKind.Escort && s.Target == c.Truck)) is { } cargo)
                { j = Job(w, AiSpecialistKind.Escort, u, cargo.Truck, Cell.Of(Own(cargo.Truck)!.Pos)); }
            }
            if (j is not null) { _specialists.Add(j); Trace(w, 0, "specialist-" + j.Kind); UpdateSpecialist(w, j); }
        }
    }

    private SpecialistJob Job(World w, AiSpecialistKind kind, Unit actor, uint target, Cell point) =>
        new(++_jobSerial, kind, actor.Id, target, point, w.Tick, w.Tick + (uint)_rules.Foundation!.SpecialistTicks);

    private void UpdateSpecialist(World w, SpecialistJob j)
    {
        if (j.Done) { return; }
        var u = Own(j.Actor);
        if (u is null) { j.Done = true; Trace(w, 0, "specialist-lost"); return; }
        var p = _rules.Foundation!;
        if (j.Kind is AiSpecialistKind.AirStrike or AiSpecialistKind.Artillery && _visible.FirstOrDefault(e => e.Id == j.Target) is { } contact)
        { j.Point = Cell.Of(contact.Pos); }
        if (j.Kind is AiSpecialistKind.AirStrike or AiSpecialistKind.Artillery && !OffensiveTarget(j.Point))
        {
            j.NextOrder = w.Tick;
            if (u.Homing || SendJob(w, j, new Command { Kind = j.Kind == AiSpecialistKind.AirStrike ? CommandKind.ReturnToBase : CommandKind.Stop, Units = [u.Id] }, "strategy-cancel-strike"))
            { FinishJob(w, j, "strategy-strike-cancelled"); }
            return;
        }
        if (j.Kind is AiSpecialistKind.Radar or AiSpecialistKind.AirCover && Directive is { AssaultForces: 0 } && j.Point != Home)
        { j.Point = Home; j.LastKind = 0; j.NextOrder = w.Tick; }
        if (w.Tick >= j.Until)
        {
            if (j.Kind == AiSpecialistKind.Transport && j.Stage == 3 && w.Tick < j.NextOrder) { return; }
            if (j.Kind == AiSpecialistKind.Transport && Aboard(j).Any())
            {
                CancelUnboarded(w, j);
                SendJob(w, j, new Command { Kind = u.Def.Archetype.Flight?.Rotor is null ? CommandKind.Exit : CommandKind.HeliUnload, Units = [u.Id], Cell = Clamp(w, Cell.Of(u.Pos)) }, "transport-abort-unload");
                // Keep passengers reserved until disembarkation finishes, including delayed helicopter landings.
                j.Stage = 3;
            }
            else if (j.Kind == AiSpecialistKind.Transport && j.Stage == 3) { FinishJob(w, j, "transport-aborted"); }
            else if (u.Def.Archetype.Flight is not null && u.Homing)
            { FinishJob(w, j, "specialist-returning"); }
            else if (SendJob(w, j, new Command { Kind = u.Def.Archetype.Flight is null ? CommandKind.Stop : CommandKind.ReturnToBase, Units = [u.Id] }, "specialist-cancel"))
            { FinishJob(w, j, "specialist-expired"); }
            return;
        }
        if (j.Kind is AiSpecialistKind.Artillery or AiSpecialistKind.Radar && AmmoPercent(u) == 0)
        { FinishJob(w, j, "specialist-resupply"); return; }
        if (w.Tick < j.NextOrder) { return; }
        switch (j.Kind)
        {
            case AiSpecialistKind.Rearm:
                if (w.AirStatusOf(u).FuelPercent > p.AirFuelPercent && AmmoPercent(u) >= _rules.ReadyHpPercent &&
                    (long)u.HP * 100 >= (long)u.Def.MaxHp * _rules.ReadyHpPercent && !u.Homing)
                { FinishJob(w, j, "aircraft-ready"); }
                else if (!u.Homing || u.HomeReason != HomeReason.Rearm) { SendJob(w, j, new Command { Kind = CommandKind.Rearm, Units = [u.Id] }, "aircraft-service"); }
                break;
            case AiSpecialistKind.AirRecon:
                if (u.AirMission is { Done: true } || (j.Probe >= 0 && _seen[j.Probe] > j.Since))
                { FinishJob(w, j, "air-search-complete"); }
                else { AirArea(w, j, AirTaskKind.Search, AirTargetKinds.None); }
                break;
            case AiSpecialistKind.AirCover:
                AirArea(w, j, AirTaskKind.Guard, AirTargetKinds.Aircraft | AirTargetKinds.Drones);
                break;
            case AiSpecialistKind.AirStrike:
                var target = _visible.FirstOrDefault(e => e.Id == j.Target && w.PickWeapon(u, e, false) >= 0);
                if (target is null) { FinishJob(w, j, "strike-contact-lost"); }
                else { SendJob(w, j, new Command { Kind = CommandKind.Attack, Units = [u.Id], Target = target.Id }, "air-strike"); }
                break;
            case AiSpecialistKind.Service:
                if (Own(j.Target) is not { } garage || !w.ServiceWorks(garage)) { FinishJob(w, j, "service-unavailable"); }
                else if (u.ServiceAt == 0 && j.Stage > 0 && (long)u.HP * 100 >= (long)u.Def.MaxHp * _rules.ReadyHpPercent)
                { FinishJob(w, j, "vehicle-serviced"); }
                else if (u.ServiceAt == 0 && SendJob(w, j, new Command { Kind = CommandKind.Service, Units = [u.Id], Target = garage.Id }, "garage-service")) { j.Stage = 1; }
                break;
            case AiSpecialistKind.Repair:
                if (Own(j.Target) is not { } patient || patient.HP >= patient.Def.MaxHp) { FinishJob(w, j, "repair-complete"); }
                else { j.Point = Cell.Of(patient.Pos); SendJob(w, j, new Command { Kind = CommandKind.Move, Units = [u.Id], Cell = j.Point }, "repair-follow"); }
                break;
            case AiSpecialistKind.Radar:
                if (!Near(Cell.Of(u.Pos), j.Point, _rules.ArrivalCells)) { MoveSpecialist(w, j, j.Point); }
                else if (u.Def.Archetype.DeployTicks > 0 && u.Deploy == DeployState.Stowed)
                { SendJob(w, j, new Command { Kind = CommandKind.Deploy, Units = [u.Id] }, "radar-deploy"); }
                else if (u.Def.Archetype.DeployTicks == 0)
                { SendJob(w, j, new Command { Kind = CommandKind.Guard, Units = [u.Id], Cell = j.Point }, "air-defense-post"); }
                break;
            case AiSpecialistKind.Artillery:
                UpdateArtillery(w, j, u);
                break;
            case AiSpecialistKind.Escort:
                if (Own(j.Target) is null || !_cargo.Any(c => !c.Done && c.Truck == j.Target)) { FinishJob(w, j, "escort-complete"); }
                else { SendJob(w, j, new Command { Kind = CommandKind.Guard, Units = [u.Id], Target = j.Target }, "escort-cargo"); }
                break;
            case AiSpecialistKind.Transport:
                UpdateTransport(w, j, u);
                break;
        }
        if (!j.Done && u.Def.Archetype.Flight is not null && j.Kind is not (AiSpecialistKind.Rearm or AiSpecialistKind.Transport) &&
            (w.AirStatusOf(u).FuelPercent <= p.AirFuelPercent || AmmoPercent(u) < p.AirAmmoPercent))
        {
            j.Kind = AiSpecialistKind.Rearm; j.LastKind = 0; j.NextOrder = w.Tick;
            Trace(w, 0, "specialist-needs-service");
        }
    }

    private void AirArea(World w, SpecialistJob j, AirTaskKind kind, AirTargetKinds filter)
    {
        var radius = _rules.Foundation!.AirAreaCells;
        SendJob(w, j, new Command { Kind = CommandKind.AirTask, Units = [j.Actor], Mission = kind,
            Cell = Clamp(w, new Cell(j.Point.X - radius, j.Point.Y - radius)),
            Cell2 = Clamp(w, new Cell(j.Point.X + radius, j.Point.Y + radius)), Filter = (byte)filter }, "air-area-task");
    }

    private void UpdateArtillery(World w, SpecialistJob j, Unit u)
    {
        var intel = _intel.FirstOrDefault(c => c.Id == j.Target);
        if (intel is null) { FinishJob(w, j, "artillery-contact-lost"); return; }
        j.Point = intel.Cell;
        var visible = _visible.FirstOrDefault(e => e.Id == j.Target);
        if (visible is not null && w.PickWeapon(u, visible, true) >= 0)
        { SendJob(w, j, new Command { Kind = CommandKind.AttackGround, Units = [u.Id], Cell = Cell.Of(visible.Pos) }, "artillery-fire"); return; }
        var weapon = u.Def.Weapons.Where(p => (p.Effect.Targets & Layers.Ground) != 0).OrderByDescending(p => p.Range.Raw).FirstOrDefault();
        if (weapon is null) { FinishJob(w, j, "artillery-no-weapon"); return; }
        var minimum = Math.Max(1, weapon.Effect.MinRange.Raw / Fixed.OneRaw + _rules.ArrivalCells);
        var maximum = Math.Max(minimum, weapon.Range.Raw / Fixed.OneRaw - _rules.ArrivalCells);
        var stand = (int)Math.Min(int.MaxValue, Math.Clamp(_rules.Foundation!.ArtilleryCells, minimum, maximum));
        var point = Toward(w, intel.Cell, Home, stand);
        if (Reachable(w, u, point, out var at)) { MoveSpecialist(w, j, at); }
    }

    private void MoveSpecialist(World w, SpecialistJob j, Cell to)
    {
        var u = Own(j.Actor)!;
        if (u.Def.Archetype.DeployTicks > 0 && u.Deploy != DeployState.Stowed)
        {
            if (u.Deploy == DeployState.Deployed) { SendJob(w, j, new Command { Kind = CommandKind.Stow, Units = [u.Id] }, "specialist-stow"); }
            return;
        }
        SendJob(w, j, new Command { Kind = CommandKind.Move, Units = [u.Id], Cell = to }, "specialist-position");
    }

    private bool SendJob(World w, SpecialistJob j, Command command, string reason)
    {
        if (w.Tick < j.NextOrder) { return false; }
        var actor = Own(j.Actor);
        if (!(command.Kind == CommandKind.Rearm && actor?.HomeReason != HomeReason.Rearm) &&
            j.LastKind == command.Kind && j.LastTarget == command.Target && j.LastCell == command.Cell &&
            actor is not null && (actor.Order.Kind != OrderKind.Idle || actor.Homing || actor.ServiceAt != 0 ||
                command.Kind == CommandKind.AirTask && actor.AirMission is not null)) { return false; }
        if (!Emit(w, command, 0, reason)) { return false; }
        j.LastKind = command.Kind; j.LastTarget = command.Target; j.LastCell = command.Cell;
        j.NextOrder = w.Tick + (uint)Math.Max(_rules.Foundation!.SpecialistOrderTicks, World.InputDelay + 1);
        return true;
    }

    private void FinishJob(World w, SpecialistJob j, string reason)
    {
        j.Done = true;
        _rest.Add((j.Actor, w.Tick + (uint)_rules.RestTicks));
        foreach (var id in j.Passengers) { _rest.Add((id, w.Tick + (uint)_rules.RestTicks)); }
        Trace(w, 0, reason);
    }

    private int AirProbe(World w, Unit u)
    {
        var best = -1;
        for (var i = 0; i < _probes.Length; i++)
        {
            if (!ProbeAvailable(w, i) || _specialists.Any(j => !j.Done && j.Probe == i)) { continue; }
            if (best < 0 || _seen[i] < _seen[best] || (_seen[i] == _seen[best] &&
                Distance(Cell.Of(u.Pos), _probes[i]) < Distance(Cell.Of(u.Pos), _probes[best]))) { best = i; }
        }
        return best;
    }

    private Unit? VisibleStrikeTarget(World w, Unit u) => _visible.Where(e => e.Def.Layer == Layers.Ground && OffensiveTarget(Cell.Of(e.Pos)) && w.PickWeapon(u, e, false) >= 0)
        .OrderByDescending(e => (long)e.Def.Archetype.Cost - Danger(w, Cell.Of(e.Pos), true) * _rules.Foundation!.DangerWeight)
        .ThenBy(e => e.Id).FirstOrDefault();

    private static int AmmoPercent(Unit u)
    {
        long stock = 0, max = 0;
        for (var i = 0; i < u.Charges.Count; i++) { stock += u.Charges[i]; max += u.StockMax(i); }
        return max == 0 ? 100 : (int)Math.Min(100, stock * 100 / max);
    }

    private static Cell Toward(World w, Cell from, Cell to, int cells)
    {
        long x = to.X - from.X, y = to.Y - from.Y;
        var length = Math.Max(1, Fixed.Sqrt(x * x + y * y));
        var distance = Math.Min(cells, length);
        return Clamp(w, new Cell(from.X + (int)(x * distance / length), from.Y + (int)(y * distance / length)));
    }

    private sealed class SpecialistJob(uint id, AiSpecialistKind kind, uint actor, uint target, Cell point, uint since, uint until)
    {
        public readonly uint Id = id, Actor = actor, Since = since, Until = until;
        public AiSpecialistKind Kind = kind;
        public uint Target = target;
        public Cell Point = point, LastCell, Pickup;
        public uint NextOrder, LastTarget;
        public CommandKind LastKind;
        public int Stage, Probe = -1;
        public bool Done;
        public readonly List<uint> Passengers = [];
    }
}

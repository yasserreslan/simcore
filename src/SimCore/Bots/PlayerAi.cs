using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>The long-lived purpose of a combat force.</summary>
public enum AiMissionKind
{
    /// <summary>Observe a reserved reachable region.</summary>
    Scout,
    /// <summary>Assemble and attack a known objective.</summary>
    Assault,
    /// <summary>Hold home and respond to visible intruders.</summary>
    Defend,
    /// <summary>Return a depleted or stalled force to safety.</summary>
    Recover,
}

/// <summary>A mission's lifecycle.</summary>
public enum AiMissionPhase
{
    /// <summary>Recruit until readiness or the assembly deadline.</summary>
    Assemble,
    /// <summary>Execute the objective.</summary>
    Active,
    /// <summary>Move to safety until arrival or the recovery deadline.</summary>
    Returning,
    /// <summary>Move to an operation's approach and await its coordinated release.</summary>
    Staging,
}

/// <summary>A read-only snapshot for host UI and diagnostics; member arrays are copies.</summary>
public sealed record AiMissionView(uint Id, AiMissionKind Kind, AiMissionPhase Phase, Cell Objective,
    uint Started, uint Deadline, IReadOnlyList<uint> Members, uint Operation = 0, Cell? FinalObjective = null);

/// <summary>A bounded diagnostic event, with a stable reason code. Diagnostics never influence decisions.</summary>
public readonly record struct AiDecision(uint Tick, uint Mission, string Reason, CommandKind? Command);

/// <summary>
/// A host-owned, fog-honest opponent. Call Think before World.Step and issue its returned commands.
/// It never changes unit state or draws the world's RNG. Replay the commands without running the bot.
/// One instance belongs to one player and one live World. CaptureState/RestoreState checkpoint host planner memory.
/// </summary>
public sealed partial class PlayerAi
{
    private readonly PlayerAiRules _rules;
    private readonly List<Unit> _own = [];
    private readonly List<Unit> _visible = [];
    private readonly List<Contact> _memory = [];
    private readonly List<Mission> _missions = [];
    private readonly List<AiDecision> _trace = [];
    private readonly List<(uint Unit, uint Until)> _rest = [];
    private readonly List<Pending> _pending = [];
    private readonly List<uint> _ordered = [];
    private readonly List<Command> _commands = [];
    private World? _world;
    private uint _nextDecision;
    private uint _serial;
    private int _missionCursor;
    private AiFactionPlan? _plan;
    private Cell[] _probes = [];
    private uint[] _seen = [], _retry = [];
    private uint _nextBuild;
    private int _buildCursor;

    /// <summary>Creates an opponent with a validated policy. Policy collections are copied.</summary>
    public PlayerAi(byte player, PlayerAiRules rules)
    {
        if (player is < 1 or > MatchSetup.MaxPlayers) { throw new ArgumentOutOfRangeException(nameof(player)); }
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        Player = player;
        _rules = rules with { Factions = rules.Factions.Select(f => f with
        { Production = f.Production.ToArray(), Construction = f.Construction.ToArray() }).ToArray(),
            Foundation = rules.Foundation is { } p ? p with { Composition = p.Composition.ToArray(),
                Roles = p.Roles.Select(r => r with { AnyTags = r.AnyTags.ToArray() }).ToArray() } : null };
    }

    /// <summary>The controlled player.</summary>
    public byte Player { get; }
    /// <summary>Current base centre, computed only from friendly units.</summary>
    public Cell Home { get; private set; }
    /// <summary>Current missions, as detached diagnostic snapshots.</summary>
    public IReadOnlyList<AiMissionView> Missions => _missions.Select(m => new AiMissionView(m.Id, m.Kind, m.Phase,
        m.Objective, m.Started, m.Deadline, m.Members.ToArray(), m.Operation, m.FinalObjective)).ToArray();
    /// <summary>Most recent lifecycle decisions, oldest first. Capacity comes from the policy.</summary>
    public IReadOnlyList<AiDecision> Decisions => _trace.AsReadOnly();

    /// <summary>Returns normal delayed commands once per decision tick; repeated calls on a tick are harmless.</summary>
    public IReadOnlyList<Command> Think(World w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (_world is not null && !ReferenceEquals(_world, w)) { throw new ArgumentException("A PlayerAi cannot be reused in a different world."); }
        if (w.Player(Player) is null) { throw new ArgumentException("The AI player is absent from this match."); }
        if (w.Result.Over || w.Tick < _nextDecision) { return []; }
        if (_world is null)
        {
            _world = w;
            _plan = _rules.Factions.FirstOrDefault(f => f.Faction == w.Match.Players[Player - 1].Faction.Id);
            var width = (w.Grid.Width + _rules.RegionCells - 1) / _rules.RegionCells;
            var height = (w.Grid.Height + _rules.RegionCells - 1) / _rules.RegionCells;
            _probes = new Cell[width * height];
            _seen = new uint[_probes.Length];
            _retry = new uint[_probes.Length];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                _probes[y * width + x] = new Cell(Math.Min(w.Grid.Width - 1, x * _rules.RegionCells + _rules.RegionCells / 2),
                    Math.Min(w.Grid.Height - 1, y * _rules.RegionCells + _rules.RegionCells / 2));
            }
        }
        _nextDecision = w.Tick + (uint)_rules.DecisionTicks;
        _commands.Clear();
        _ordered.Clear();
        Observe(w);
        RememberContacts(w);
        AssessStrategy(w);
        _pending.RemoveAll(p => w.Tick > p.Due);
        _rest.RemoveAll(r => w.Tick >= r.Until || Own(r.Unit) is null);
        Reconcile(w);
        UpdateOperations(w);
        Crew(w);
        Specialists(w);
        PlanAbilities(w);
        CreateMissions(w);
        var count = Math.Min(_rules.MissionsPerDecision, _missions.Count);
        for (var i = 0; i < count; i++)
        {
            if (_missionCursor >= _missions.Count) { _missionCursor = 0; }
            UpdateMission(w, _missions[_missionCursor++]);
        }
        Support(w);
        Economy(w);
        return _commands.ToArray(); // callers may retain the commands across later Think calls
    }

    private void Observe(World w)
    {
        _own.Clear();
        _visible.Clear();
        long x = 0, y = 0;
        var n = 0;
        foreach (var u in w.Units)
        {
            if (u.Owner == Player)
            {
                if (!u.Alive) { continue; }
                _own.Add(u);
                if (u.Def.IsStructure && u.IsBuilt && !u.Rubble)
                { x += u.Pos.X.Raw; y += u.Pos.Y.Raw; n++; }
            }
            else if (u.Owner != 0 && World.IsVisibleTo(u, Player) && u.Alive)
            {
                // Enemy fields are read only after the visibility boundary.
                _visible.Add(u);
            }
        }
        if (n == 0)
        {
            foreach (var u in _own)
            {
                if (u.Inside != 0 || u.Garrison != 0) { continue; }
                x += u.Pos.X.Raw; y += u.Pos.Y.Raw; n++;
            }
        }
        if (n > 0) { Home = new Cell((int)(x / n / Fixed.OneRaw), (int)(y / n / Fixed.OneRaw)); }
        for (var i = _memory.Count - 1; i >= 0; i--)
        {
            var m = _memory[i];
            // Never resolve a remembered ID through World.Unit: hidden death must not leak.
            if (Sees(w, m.Cell) && !_visible.Any(u => u.Id == m.Id && u.Def.IsStructure && Cell.Of(u.Pos) == m.Cell))
            { _memory.RemoveAt(i); }
        }
        foreach (var u in _visible)
        {
            if (!u.Def.IsStructure) { continue; }
            var at = _memory.FindIndex(m => m.Id >= u.Id);
            var contact = new Contact(u.Id, Cell.Of(u.Pos));
            if (at < 0) { _memory.Add(contact); }
            else if (_memory[at].Id == u.Id) { _memory[at] = contact; }
            else { _memory.Insert(at, contact); }
        }
        if (w.FogOf(Player) is { } history)
        {
            for (var i = 0; i < _probes.Length; i++)
            {
                var c = _probes[i];
                if (history.Visible(c.X, c.Y)) { _seen[i] = w.Tick + 1; }
                else if (_seen[i] == 0 && history.Explored(c.X, c.Y)) { _seen[i] = 1; }
            }
        }
        else
        {
            // Visit only regions near friendly viewers, rather than scanning all viewers for every map probe.
            var width = (w.Grid.Width + _rules.RegionCells - 1) / _rules.RegionCells;
            var height = (w.Grid.Height + _rules.RegionCells - 1) / _rules.RegionCells;
            foreach (var u in _own)
            {
                if (u.Inside != 0 || u.Garrison != 0) { continue; }
                var c = Cell.Of(u.Pos);
                var reach = (int)(u.Def.Vision.Raw / Fixed.OneRaw) + _rules.ReachSearchCells + 1;
                for (var py = Math.Max(0, (c.Y - reach) / _rules.RegionCells); py <= Math.Min(height - 1, (c.Y + reach) / _rules.RegionCells); py++)
                for (var px = Math.Max(0, (c.X - reach) / _rules.RegionCells); px <= Math.Min(width - 1, (c.X + reach) / _rules.RegionCells); px++)
                {
                    var i = py * width + px;
                    if (World.Within(u.Pos, _probes[i].Center(), u.Def.Vision)) { _seen[i] = w.Tick + 1; }
                }
            }
        }
    }

    private bool Sees(World w, Cell cell)
    {
        if (w.FogOf(Player) is { } fog) { return fog.Visible(cell.X, cell.Y); }
        return _own.Any(u => u.Inside == 0 && u.Garrison == 0 && World.Within(u.Pos, cell.Center(), u.Def.Vision));
    }

    private Unit? Own(uint id)
    {
        var lo = 0; var hi = _own.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_own[mid].Id == id) { return _own[mid]; }
            if (_own[mid].Id < id) { lo = mid + 1; } else { hi = mid - 1; }
        }
        return null;
    }

    private bool Emit(World w, Command c, uint mission, string reason, bool exclusive = true)
    {
        if (_commands.Count >= _rules.CommandsPerDecision) { return false; }
        var units = c.Units.Where(id => Own(id) is { CanAct: true } && (!exclusive || _ordered.BinarySearch(id) < 0)).OrderBy(id => id).ToArray();
        if (units.Length == 0) { return false; }
        _commands.Add(c with { Tick = w.Tick + World.InputDelay, Player = Player, Units = units });
        if (exclusive)
        {
            foreach (var id in units) { var at = _ordered.BinarySearch(id); if (at < 0) { _ordered.Insert(~at, id); } }
        }
        Trace(w, mission, reason, c.Kind);
        return true;
    }

    private void Trace(World w, uint mission, string reason, CommandKind? command = null)
    {
        if (_rules.TraceCapacity == 0) { return; }
        if (_trace.Count == _rules.TraceCapacity) { _trace.RemoveAt(0); }
        _trace.Add(new AiDecision(w.Tick, mission, reason, command));
    }

    private bool Reserved(uint id) => _missions.Any(m => !m.Done && m.Members.BinarySearch(id) >= 0) ||
        _cargo.Any(j => !j.Done && j.Truck == id) || SpecialistReserved(id);
    private bool Resting(uint id) => _rest.Any(r => r.Unit == id);
    private static long Distance(Cell a, Cell b) => (a.Center() - b.Center()).LenSq();
    private static bool Near(Cell a, Cell b, int cells) => Distance(a, b) <= (long)cells * cells * Fixed.OneRaw * Fixed.OneRaw;
    private static bool Free(Unit u) => u.Order.Kind is OrderKind.Idle or OrderKind.Guard && u.Ai == AiState.None;
    private static bool Mobile(Unit u) => u.CanAct && u.Inside == 0 && u.Garrison == 0 && !u.Def.IsStructure && u.Def.Speed.Raw > 0 && u.OutOf == 0 && u.ServiceAt == 0;
    private static bool Fighter(Unit u) => Mobile(u) && u.Def.Layer == Layers.Ground && u.Def.Archetype.Flight is null &&
        u.Def.Strike is null && u.Def.Builder is null && u.Def.Cargo is null && u.Def.Archetype.DeployTicks == 0 &&
        u.Def.Weapons.Any(w => w.Effect.Interceptor is null && (w.Effect.Targets & Layers.Ground) != 0);
    private bool Ready(Unit u) => Fighter(u) && Free(u) && !Reserved(u.Id) && !Resting(u.Id) && _ordered.BinarySearch(u.Id) < 0 &&
        !_pending.Any(p => p.Source == u.Id && p.Kind is CommandKind.Enter or CommandKind.UseAbility) &&
        (long)u.HP * 100 >= (long)u.Def.MaxHp * _rules.ReadyHpPercent && Armed(u);
    private static bool Armed(Unit u) => u.Def.Weapons.Any(w => (w.Effect.Targets & Layers.Ground) != 0 &&
        w.Effect.Interceptor is null && (w.Stock < 0 || u.Charges[w.Stock] > 0));

    private readonly record struct Contact(uint Id, Cell Cell);
    private readonly record struct Pending(uint Source, int Def, CommandKind Kind, uint Due, uint Target = 0);
    private sealed class Mission(uint id, AiMissionKind kind, uint tick)
    {
        public readonly uint Id = id;
        public AiMissionKind Kind = kind;
        public AiMissionPhase Phase = AiMissionPhase.Assemble;
        public readonly uint Started = tick;
        public uint Deadline, ProgressAt, NextOrder;
        public long BestDistance = long.MaxValue;
        public Cell Objective;
        public Cell LastCell;
        public CommandKind LastKind;
        public uint LastTarget;
        public uint Contact;
        public uint Operation;
        public int Approach;
        public Cell? FinalObjective;
        public int Probe = -1, LaunchMembers;
        public readonly List<uint> Members = [];
        public bool Done, OutcomeRecorded;
    }
}

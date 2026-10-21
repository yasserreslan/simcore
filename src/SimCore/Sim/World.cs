using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

/// <summary>
/// The complete game state. Everything that affects gameplay lives here, and
/// <see cref="Hash"/> covers all of it.
/// </summary>
public sealed partial class World
{
    /// <summary>The <see cref="Unit.Inside"/> of a unit underground in a tunnel network (never a unit ID).</summary>
    public const uint Underground = uint.MaxValue;

    /// <summary>The <see cref="Unit.Inside"/> of a vehicle in a garage bay being serviced (never a unit ID).</summary>
    public const uint InGarage = uint.MaxValue - 1;

    /// <summary>The fixed simulation rate.</summary>
    public const int TicksPerSecond = 20;

    /// <summary>
    /// How many ticks ahead a player's command is scheduled, giving it time to
    /// reach every peer before it runs.
    /// </summary>
    public const int InputDelay = 2;

    private readonly List<Unit> _units = []; // sorted by ID, ascending
    private readonly List<Projectile> _projectiles = []; // sorted by ID, ascending
    private readonly List<Player> _players = [];
    private readonly List<Command> _pending = []; // sorted by (Tick, Player, Seq); not hashed
    private readonly uint[] _nextSeq; // per player
    private Rand _rng;
    private uint _nextId;

    /// <summary>Returns a world at tick 0 for a compiled match, with no units yet; scenarios spawn them.</summary>
    public World(Grid grid, CompiledMatch match)
    {
        Grid = grid;
        Match = match;
        _rng = new Rand(match.Setup.Seed);
        _nextSeq = new uint[match.Players.Count + 1];
        foreach (var p in match.Players)
        {
            _players.Add(new Player(p));
        }
        InitFog();
    }

    /// <summary>The number of ticks simulated so far.</summary>
    public uint Tick { get; private set; }

    /// <summary>The map.</summary>
    public Grid Grid { get; }

    /// <summary>
    /// The map's terrain (elevation, roads, forest), or null for a flat map without one: then
    /// nothing changes, and the game hashes exactly as before.
    /// </summary>
    public TerrainMap? Ground { get; private set; }

    /// <summary>Gives the world its terrain; only before the first tick, and of the grid's size.</summary>
    /// <exception cref="InvalidOperationException">The world has run, or the sizes differ.</exception>
    public void AttachTerrain(TerrainMap terrain)
    {
        if (Tick != 0 || terrain.Width != Grid.Width || terrain.Height != Grid.Height)
        {
            throw new InvalidOperationException($"terrain: attach a {Grid.Width}x{Grid.Height} terrain before the first tick");
        }
        Ground = terrain;
        _router = null; // routing starts over with the terrain
    }

    /// <summary>The checked setup and every player's unit definitions.</summary>
    public CompiledMatch Match { get; }

    /// <summary>The content the match is played with.</summary>
    public ContentSet Content => Match.Content;

    /// <summary>The live units, sorted by ID, ascending. Drivers are listed too, with <see cref="Unit.Inside"/> set.</summary>
    public IReadOnlyList<Unit> Units => _units;

    /// <summary>The projectiles in flight, sorted by ID, ascending.</summary>
    public IReadOnlyList<Projectile> Projectiles => _projectiles;

    /// <summary>The players, by ID.</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>How the match stands; see <see cref="MatchResult"/>.</summary>
    public MatchResult Result { get; private set; }

    /// <summary>Returns the player with this ID, or null.</summary>
    public Player? Player(byte id) => id >= 1 && id <= _players.Count ? _players[id - 1] : null;

    /// <summary>
    /// Creates a unit and returns its ID. A unit stands at the center of cell
    /// c; a structure's footprint has c as its top-left cell. A garrisonable building starts
    /// neutral (owner 0) and blocks its footprint for ground units, for good (its rubble too);
    /// an owned post (<see cref="GarrisonSpec.Owned"/>) is placed like any other structure.
    /// </summary>
    public uint Spawn(UnitDef def, Cell c, int facing)
    {
        if (def.Archetype.Garrison is { Owned: false })
        {
            var b = Add(def, FootprintCenter(def, c), facing);
            b.Owner = 0;
            for (var y = 0; y < def.Archetype.FootprintH; y++)
            {
                for (var x = 0; x < def.Archetype.FootprintW; x++)
                {
                    Grid.Set(new Cell(c.X + x, c.Y + y), Terrain.Blocked);
                }
            }
            return b.Id;
        }
        if (def.Archetype.Tunnel is not null)
        {
            return PlaceTunnel(def, c, facing, true).Id; // finished, its ground shaped (TunnelGround.cs)
        }
        return Add(def, def.IsStructure ? FootprintCenter(def, c) : c.Center(), facing).Id;
    }

    // FootprintCenter is the middle of a structure whose footprint has c as its top-left cell
    // (unturned, or w x h cells as it stands).
    private static Vec FootprintCenter(UnitDef def, Cell c) => FootprintCenter(c, def.Archetype.FootprintW, def.Archetype.FootprintH);

    private static Vec FootprintCenter(Cell c, int w, int h) => new(
        Fixed.FromRaw(Fixed.FromInt(c.X).Raw + Fixed.FromInt(w).Raw / 2),
        Fixed.FromRaw(Fixed.FromInt(c.Y).Raw + Fixed.FromInt(h).Raw / 2));

    /// <summary>Returns the top-left cell of a structure's footprint.</summary>
    public static Cell FootprintOrigin(Unit u) => new(
        (int)((u.Pos.X.Raw - (long)FootW(u) * Fixed.OneRaw / 2) / Fixed.OneRaw),
        (int)((u.Pos.Y.Raw - (long)FootH(u) * Fixed.OneRaw / 2) / Fixed.OneRaw));

    // Add creates a unit at pos. IDs only grow, so the list stays sorted.
    // Systems that spawn while iterating over units collect first and add after.
    internal Unit Add(UnitDef def, Vec pos, int facing)
    {
        _nextId++;
        var u = new Unit(_nextId, def, pos, FixedPoint.Facing.Wrap(facing));
        if (def.Archetype.Flight is { Endurance: not null } fl)
        {
            u.Tank = u.Lifetime = fl.TankOn(def.Lifetime, Math.Max(Grid.Width, Grid.Height)); // E13, D-FUEL1: a bigger map, a bigger tank
        }
        if (_fog is not null)
        {
            u.SeenBy = 0; // until the vision system has looked (its owner always sees it)
        }
        _units.Add(u);
        NoteStandalone(u); // H1: a radar site's rubble pass (AirfieldDamageSystem.cs)
        _groundRadars |= def.Archetype.GroundRadar.Raw != 0; // H1: the ground radar pass (VisionSystem.cs)
        _airRadars |= (def.IsStructure || def.Archetype.GroundRadar.Raw != 0) && def.Archetype.AirVision.Raw != 0; // H1: a radar (the radar truck too) tracks with no shooter about
        return u;
    }

    /// <summary>Returns the live unit with the given ID, or null.</summary>
    public Unit? Unit(uint id)
    {
        int lo = 0, hi = _units.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_units[mid].Id < id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _units.Count && _units[lo].Id == id ? _units[lo] : null;
    }

    /// <summary>
    /// Reports whether player sees u on screen, may select, target and command it. Without fog of
    /// war that is every unit on the map except enemy hidden ones (traps) the player has not
    /// revealed; with fog (<see cref="MatchSetup.Fog"/>) an enemy must also be spotted by the
    /// player's side this tick (<see cref="Unit.SeenBy"/>). Drivers are inside their vehicles and
    /// soldiers inside buildings: never shown.
    /// </summary>
    public static bool IsVisibleTo(Unit u, byte player) =>
        u.Inside == 0 && u.Garrison == 0 &&
        (u.Owner == player || ((u.SeenBy & Bit(player)) != 0 && (!u.Def.Hidden || (u.RevealedTo & Bit(player)) != 0 || u.ExposeLeft > 0)));

    /// <summary>
    /// Reports whether u is out in the open for player regardless of fog: not inside a vehicle or
    /// building, and not an enemy hidden unit the player has not revealed. Shells and blasts use
    /// this (they hit what is there, seen or not).
    /// </summary>
    public static bool IsRevealed(Unit u, byte player) =>
        u.Inside == 0 && u.Garrison == 0 && (u.Owner == player || !u.Def.Hidden || (u.RevealedTo & Bit(player)) != 0 || u.ExposeLeft > 0);

    /// <summary>Reports whether one of player's units sees u this tick, so player's weapons may fire at it.</summary>
    public static bool IsSpotted(Unit u, byte player) => IsVisibleTo(u, player) && (u.SpottedBy & Bit(player)) != 0;

    internal static uint Bit(byte player) => 1u << player;

    /// <summary>
    /// An optional observer told after each system of <see cref="Step"/> runs, for
    /// profiling tools. It never changes the world and is not part of the game state.
    /// </summary>
    public IStepProbe? Probe { get; set; }

    /// <summary>
    /// How many path searches the last <see cref="Step"/> ran (orders and re-plans). A
    /// measurement for tools, not game state: it is not hashed.
    /// </summary>
    public int PathRequests { get; private set; }

    /// <summary>
    /// Advances the world by one tick. Systems run in a fixed order, and each
    /// visits units in ID order.
    /// </summary>
    public void Step()
    {
        PathRequests = 0;
        var probe = Probe;
        probe?.Begin();
        RunCommands();
        probe?.After(StepPhase.Commands);
        RunVision();
        probe?.After(StepPhase.Vision);
        RunOrders();
        probe?.After(StepPhase.Orders);
        RunDeploy();
        probe?.After(StepPhase.Deploy);
        RunBoarding();
        probe?.After(StepPhase.Boarding);
        RunAbilities();
        probe?.After(StepPhase.Abilities);
        RunPaths();
        probe?.After(StepPhase.Paths);
        RunGroundMovement();
        probe?.After(StepPhase.GroundMovement);
        RunAirMovement();
        probe?.After(StepPhase.AirMovement);
        RunCombat();
        probe?.After(StepPhase.Combat);
        RunAirDefense();
        probe?.After(StepPhase.AirDefense);
        RunProjectiles();
        probe?.After(StepPhase.Projectiles);
        RunStrikes();
        probe?.After(StepPhase.Strikes);
        RunTriggers();
        probe?.After(StepPhase.Triggers);
        RunConstruction();
        probe?.After(StepPhase.Construction);
        RunProduction();
        probe?.After(StepPhase.Production);
        RunSupply();
        RunReloadPads(); // the Patriot-type launcher's reload pad (after supply)
        probe?.After(StepPhase.Supply);
        RunCargo();
        probe?.After(StepPhase.Cargo);
        RunRepair();
        RunAuras(); // healing auras (the assault medic's), after repair
        probe?.After(StepPhase.Repair);
        RunLifetimes();
        probe?.After(StepPhase.Lifetimes);
        RunTunnels();
        probe?.After(StepPhase.Tunnels);
        RunAirfields();
        probe?.After(StepPhase.Airfields);
        RunEjection();
        probe?.After(StepPhase.Ejection);
        RunCollapse();
        probe?.After(StepPhase.Collapse);
        RunCleanup();
        probe?.After(StepPhase.Cleanup);
        RunGarrisons();
        probe?.After(StepPhase.Garrisons);
        RunMatchResult();
        probe?.After(StepPhase.MatchResult);
        Tick++;
    }
}

/// <summary>The systems of one <see cref="World.Step"/>, in the order they run.</summary>
public enum StepPhase
{
    /// <summary>The commands system.</summary>
    Commands,
    /// <summary>The vision system.</summary>
    Vision,
    /// <summary>The orders system.</summary>
    Orders,
    /// <summary>The deploy system.</summary>
    Deploy,
    /// <summary>The boarding system.</summary>
    Boarding,
    /// <summary>The abilities system.</summary>
    Abilities,
    /// <summary>The path system: routes, goals and move intents.</summary>
    Paths,
    /// <summary>The ground movement system.</summary>
    GroundMovement,
    /// <summary>The air movement system.</summary>
    AirMovement,
    /// <summary>The combat system.</summary>
    Combat,
    /// <summary>The air defence system: tracking, locking and firing at air targets.</summary>
    AirDefense,
    /// <summary>The projectiles system.</summary>
    Projectiles,
    /// <summary>The strikes system.</summary>
    Strikes,
    /// <summary>The triggers system.</summary>
    Triggers,
    /// <summary>The construction system.</summary>
    Construction,
    /// <summary>The production system.</summary>
    Production,
    /// <summary>The supply system.</summary>
    Supply,
    /// <summary>The cargo system.</summary>
    Cargo,
    /// <summary>The repair system.</summary>
    Repair,
    /// <summary>The lifetimes system.</summary>
    Lifetimes,
    /// <summary>The tunnels system: rubble, survival, going in, travelling, coming out, the ground returning.</summary>
    Tunnels,
    /// <summary>The airfield system: airfield records follow their pieces (later: landing, service, craters).</summary>
    Airfields,
    /// <summary>The ejection system.</summary>
    Ejection,
    /// <summary>The collapse system.</summary>
    Collapse,
    /// <summary>The cleanup system.</summary>
    Cleanup,
    /// <summary>The garrisons system.</summary>
    Garrisons,
    /// <summary>The match result system.</summary>
    MatchResult,
}

/// <summary>
/// Observes the systems of a tick, for profiling. <see cref="Begin"/> is called before
/// the first system and <see cref="After"/> after each one. Implementations live outside
/// the simulation (they may read clocks); they must never change the world.
/// </summary>
public interface IStepProbe
{
    /// <summary>Called when a tick starts.</summary>
    void Begin();

    /// <summary>Called after the system phase ran.</summary>
    void After(StepPhase phase);
}

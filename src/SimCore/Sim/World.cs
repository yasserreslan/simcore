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
            var pl = new Player(p);
            pl.InitLocks(match.Content.ExclusiveGroups.Count);
            _players.Add(pl);
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

    /// <summary>
    /// Gives the world its terrain; only before the first tick, and of the grid's size. The map's bridges
    /// (<see cref="TerrainMap.BridgeSites"/>) are put up with it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world has run, or the sizes differ.</exception>
    /// <exception cref="MatchSetupException">A map's bridge can't stand where the map puts it (<see cref="BridgeRefusal"/>), or the
    /// terrain rules' trees name a warhead or tag the content lacks.</exception>
    public void AttachTerrain(TerrainMap terrain)
    {
        if (Tick != 0 || terrain.Width != Grid.Width || terrain.Height != Grid.Height)
        {
            throw new InvalidOperationException($"terrain: attach a {Grid.Width}x{Grid.Height} terrain before the first tick");
        }
        Ground = terrain;
        InitConcealment(); // the fog grids count their air viewers apart when concealment from the air applies (ConcealmentSystem.cs)
        ResolveTrees(terrain); // the trees' blast and fall damage lists against the content (TreeSystem.cs)
        ResolveFire(); // the content's forest fire rules, now that there is forest (FireSystem.cs)
        MarkWater(Grid, terrain); // open water on the grid: no ground unit enters it or is put there (WorldWater.cs)
        _router = null; // routing starts over with the terrain
        PlaceMapBridges(terrain); // the map's bridges, decks laid (BridgeSystem.cs)
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
    /// c; a structure's footprint has c as its top-left cell. A garrisonable building (and a
    /// <see cref="Archetype.Neutral"/> civilian structure) starts neutral (owner 0) and blocks its
    /// footprint for ground units, for good (its rubble too); an owned post
    /// (<see cref="GarrisonSpec.Owned"/>) is placed like any other structure. A ground unit asked to stand in open water
    /// stands on the nearest dry cell instead, and a ground vehicle asked to stand in an alley on the nearest cell that is not one. A bridge goes up where asked, unchecked (<see cref="BridgeRefusal"/>), neutral
    /// with its road and deck (BridgeSystem.cs). A structure stands as its archetype lays it out, whatever the facing (the
    /// front to the south); <see cref="Spawn(UnitDef, Cell, int, int)"/> turns one.
    /// </summary>
    public uint Spawn(UnitDef def, Cell c, int facing) => Spawn(def, c, facing, 0);

    /// <summary>
    /// Creates a unit as <see cref="Spawn(UnitDef, Cell, int)"/>; a structure stands with its layout turned quarter quarter
    /// turns clockwise (<see cref="Unit.Quarter"/>: 0 as authored, the front to the south; 1 the front to the west; 2 north;
    /// 3 east). At an odd quarter a non-square footprint stands with width and height swapped (<see cref="Unit.Turned"/>)
    /// and c is the top-left cell of the footprint as it stands; its doors turn with it (<see cref="DoorCell"/>), and a
    /// neutral building blocks the cells it stands on. facing stays the unit's facing, as for the other overload. Quarter 0
    /// is exactly the other overload. docs/design.md, "Turned buildings".
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">quarter is not 0 to 3; or not 0 for a unit that is not a structure, a
    /// factory (<see cref="CanTurn"/>), a tunnel entrance, an airfield piece or a bridge (these turn by their facing).</exception>
    public uint Spawn(UnitDef def, Cell c, int facing, int quarter)
    {
        var a = def.Archetype;
        if (quarter is < 0 or > 3 ||
            (quarter != 0 && (!def.IsStructure || !CanTurn(a) || a.Airfield is not null || a.Bridge is not null)))
        {
            throw new ArgumentOutOfRangeException(nameof(quarter), quarter, $"spawn: \"{a.Id}\" can't stand at quarter {quarter}");
        }
        if (a.Bridge is not null)
        {
            return SpawnBridge(def, c, facing).Id;
        }
        var (w, h) = Footprint(a, quarter * (FixedPoint.Facing.Count / 4));
        if (a.PlacedNeutral)
        {
            var b = Add(def, FootprintCenter(c, w, h), facing);
            b.Owner = 0;
            b.Quarter = quarter;
            b.Turned = TurnsAt(a, quarter * (FixedPoint.Facing.Count / 4));
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    Grid.Set(new Cell(c.X + x, c.Y + y), Terrain.Blocked);
                }
            }
            return b.Id;
        }
        if (a.Tunnel is not null)
        {
            return PlaceTunnel(def, c, facing, true).Id; // finished, its ground shaped (TunnelGround.cs)
        }
        if (!def.IsStructure && def.Layer == Layers.Ground && a.Flight is null && (InWater(c) || (IsAlley(c) && KeepsOffAlleys(def))) &&
            TrySpiralFor(def, c, 0, out var dry))
        {
            c = dry; // no ground unit stands in the water: the nearest dry cell (WorldWater.cs); no vehicle in an alley: the nearest cell that is none (WorldRoads.cs)
        }
        var u = Add(def, def.IsStructure ? FootprintCenter(c, w, h) : c.Center(), facing);
        if (quarter != 0)
        {
            u.Quarter = quarter;
            u.Turned = TurnsAt(a, quarter * (FixedPoint.Facing.Count / 4));
        }
        return u.Id;
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
        if (def.Archetype.Flight is { Rotor: not null })
        {
            u.Heli = new HeliState(u.Facing, def.Weapons.Count) { Since = Tick }; // a helicopter (in the air unless it is built onto a pad)
            _helis = true;
        }
        _units.Add(u);
        if (def.Archetype.Bridge is not null)
        {
            _bridgeList.Add(u); // bridges are never removed: the list stays in ID order (BridgeSystem.cs)
        }
        NoteStandalone(u); // H1: a radar site's rubble pass (AirfieldDamageSystem.cs)
        _groundRadars |= def.Archetype.GroundRadar.Raw != 0; // H1: the ground radar pass (VisionSystem.cs)
        NoteRadarSpotter(u); // the stealth multirole: the radar spotting pass (RadarSpotSystem.cs)
        _truckReloads |= def.TruckReloadWeapon >= 0; // the howitzer: the trucks' handover pass (HowitzerSystem.cs)
        _reveals |= Flashes(def); // the howitzer: the muzzle flash pass (HowitzerSystem.cs)
        _airRadars |= (def.IsStructure || def.Archetype.GroundRadar.Raw != 0 || def.Archetype.CueLock != 0) && def.Archetype.AirVision.Raw != 0; // H1: a radar (the radar truck, the mobile radar too) tracks with no shooter about
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
        ClearLogisticsEvents(); // the last tick's pallets, unloads and truck loads (LogisticsSystem.cs)
        _airliftEvents.Clear(); // the last tick's boardings, drive-offs and losses (AirliftSystem.cs)
        ClearSmokeEvents(); // the last tick's smoke screens popped, expired and missile locks broken (SmokeSystem.cs)
        ClearArmorHits(); // the last tick's hits on directional armour (ArmorSystem.cs)
        ClearEmplacementEvents(); // the last tick's howitzer shots, flashes, truck rounds and crew changes (HowitzerSystem.cs)
        _bridgeEvents.Clear(); // the last tick's bridge stages, falls and spans laid again (BridgeSystem.cs)
        _treeEvents.Clear(); // the last tick's pushes, trees falling and fallen trunks gone (TreeSystem.cs)
        _fireEvents.Clear(); // the last tick's cells catching fire, burning out and going cold (FireSystem.cs)
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
        RunTrees(); // pushes on tree trunks, trees falling, fallen trunks gone (TreeSystem.cs)
        probe?.After(StepPhase.GroundMovement);
        RunFire(); // forest fire: cells burning out, spreading, smoke, infantry burnt and walking out (FireSystem.cs)
        probe?.After(StepPhase.Fire);
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
        RunHeliDamage(); // helicopters brought down go out of control; their crashes (HeliPadSystem.cs)
        RunBridges(); // after the tick's damage and repair: stages, spans falling and laid again (BridgeSystem.cs)
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
        SortTrees(); // the tick's falls in order (TreeSystem.cs)
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
    /// <summary>The ground movement system, then the tree step (pushes on tree trunks, trees falling, fallen trunks gone).</summary>
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
    /// <summary>The airfield system: airfield records follow their pieces (later: landing, service, craters); then the bridges.</summary>
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
    /// <summary>The fire system (forest fire), run right after the ground movement and tree step (listed last to keep the numbers above).</summary>
    Fire,
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

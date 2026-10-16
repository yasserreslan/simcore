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
    private readonly Pathfinder _pf = new();
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
    }

    /// <summary>The number of ticks simulated so far.</summary>
    public uint Tick { get; private set; }

    /// <summary>The map.</summary>
    public Grid Grid { get; }

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
    /// c; a structure's footprint has c as its top-left cell.
    /// </summary>
    public uint Spawn(UnitDef def, Cell c, int facing)
    {
        var pos = c.Center();
        if (def.IsStructure)
        {
            var a = def.Archetype;
            pos = new Vec(
                Fixed.FromRaw(Fixed.FromInt(c.X).Raw + Fixed.FromInt(a.FootprintW).Raw / 2),
                Fixed.FromRaw(Fixed.FromInt(c.Y).Raw + Fixed.FromInt(a.FootprintH).Raw / 2));
        }
        return Add(def, pos, facing).Id;
    }

    // Add creates a unit at pos. IDs only grow, so the list stays sorted.
    // Systems that spawn while iterating over units collect first and add after.
    internal Unit Add(UnitDef def, Vec pos, int facing)
    {
        _nextId++;
        var u = new Unit(_nextId, def, pos, FixedPoint.Facing.Wrap(facing));
        _units.Add(u);
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
    /// Reports whether player sees u on screen. Phase 1 has no fog of war, so
    /// that is every unit on the map except enemy hidden ones (traps) the
    /// player has not revealed. Drivers are inside their vehicles and never shown.
    /// </summary>
    public static bool IsVisibleTo(Unit u, byte player) =>
        u.Inside == 0 && (u.Owner == player || !u.Def.Hidden || (u.RevealedTo & Bit(player)) != 0);

    /// <summary>Reports whether one of player's units sees u this tick, so player's weapons may fire at it.</summary>
    public static bool IsSpotted(Unit u, byte player) => IsVisibleTo(u, player) && (u.SpottedBy & Bit(player)) != 0;

    internal static uint Bit(byte player) => 1u << player;

    /// <summary>
    /// Advances the world by one tick. Systems run in a fixed order, and each
    /// visits units in ID order.
    /// </summary>
    public void Step()
    {
        RunCommands();
        RunVision();
        RunOrders();
        RunDeploy();
        RunBoarding();
        RunAbilities();
        RunGroundMovement();
        RunAirMovement();
        RunCombat();
        RunProjectiles();
        RunStrikes();
        RunTriggers();
        RunSupply();
        RunRepair();
        RunLifetimes();
        RunEjection();
        RunCleanup();
        RunMatchResult();
        Tick++;
    }
}

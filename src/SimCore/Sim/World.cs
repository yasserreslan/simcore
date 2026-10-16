using SimCore.FixedPoint;
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
    private readonly List<Player> _players = [];
    private readonly List<Command> _pending = []; // sorted by (Tick, Player, Seq); not hashed
    private readonly uint[] _nextSeq; // per player
    private readonly Pathfinder _pf = new();
    private Rand _rng;
    private uint _nextId;

    /// <summary>Returns a world at tick 0 with players 1..players.</summary>
    public World(Grid grid, ulong seed, int players)
    {
        Grid = grid;
        _rng = new Rand(seed);
        _nextSeq = new uint[players + 1];
        for (var i = 1; i <= players; i++)
        {
            _players.Add(new Player((byte)i));
        }
    }

    /// <summary>The number of ticks simulated so far.</summary>
    public uint Tick { get; private set; }

    /// <summary>The map.</summary>
    public Grid Grid { get; }

    /// <summary>The live units, sorted by ID, ascending.</summary>
    public IReadOnlyList<Unit> Units => _units;

    /// <summary>The players, by ID.</summary>
    public IReadOnlyList<Player> Players => _players;

    /// <summary>Creates a unit at the center of cell c and returns its ID.</summary>
    public uint Spawn(UnitTypeId type, byte owner, Cell c, int facing)
    {
        _nextId++;
        var u = new Unit(_nextId, owner, type, c.Center(), Facing.Wrap(facing), UnitTypes.Of(type).MaxHP);
        _units.Add(u); // IDs only grow, so the list stays sorted
        return u.Id;
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
    /// Advances the world by one tick. Systems run in a fixed order, and each
    /// visits units in ID order.
    /// </summary>
    public void Step()
    {
        RunCommands();
        RunOrders();
        RunMovement();
        RunCombat();
        RunCleanup();
        Tick++;
    }
}

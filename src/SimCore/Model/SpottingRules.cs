namespace SimCore.Model;

/// <summary>
/// The cover a ground unit stands in, for spotting (<see cref="SpottingRules"/>): the forest density of its cell, or a town
/// (open ground or scrub beside a standing building).
/// </summary>
public enum SpotCover : byte
{
    /// <summary>Open ground (no forest, no building beside it).</summary>
    Open,

    /// <summary>Scrub (bushes).</summary>
    Scrub,

    /// <summary>A grove.</summary>
    Grove,

    /// <summary>Dense forest.</summary>
    Dense,

    /// <summary>Open ground or scrub with a standing building on its cell or a cell next to it (the sight blocker layer).</summary>
    Town,
}

/// <summary>What a ground unit is doing, for spotting (<see cref="SpottingRules"/>).</summary>
public enum SpotState : byte
{
    /// <summary>It did not move last tick and has not fired lately.</summary>
    Still,

    /// <summary>It moved last tick (its velocity is not zero) and has not fired lately.</summary>
    Moving,

    /// <summary>It fired within <see cref="SpottingRules.FiredTicks"/> ticks.</summary>
    Fired,
}

/// <summary>
/// Spotting under fog of war (fog phase F4e; the terrain rules' optional <c>spotting</c> block; docs/design.md, "Spotting"):
/// seeing a cell is not spotting the unit in it. With fog of war on a map with terrain, a ground observer spots an enemy ground
/// unit (infantry or a vehicle out on the map) only when the unit's cell is in the observer's own sight and the level distance is
/// within the observer's sight x its optics x the unit's concealment x the table's percent for the unit's cover and state, all
/// percents (/ 100 each). An observer standing in the woods sees <see cref="WoodsSightPercent"/> as far over woods cells, and
/// the forest it looks through from inside a wood does not limit it while the line stays in that wood.
/// </summary>
public sealed class SpottingRules
{
    /// <summary>The number of cover kinds (<see cref="SpotCover"/>).</summary>
    public const int Covers = 5;

    /// <summary>The number of states (<see cref="SpotState"/>).</summary>
    public const int States = 3;

    /// <summary>The percent per cover and state, by <c>cover * States + state</c>.</summary>
    public required IReadOnlyList<int> Table { get; init; }

    /// <summary>The concealment of infantry (foot), percent.</summary>
    public required int ConcealmentFoot { get; init; }

    /// <summary>The concealment of wheeled vehicles (light vehicles), percent.</summary>
    public required int ConcealmentWheeled { get; init; }

    /// <summary>The concealment of tracked vehicles (tanks), percent.</summary>
    public required int ConcealmentTracked { get; init; }

    /// <summary>The observers whose optics are not 100: archetype ID (ordinal order) and percent.</summary>
    public required IReadOnlyList<(string Archetype, int Percent)> Optics { get; init; }

    /// <summary>A unit that fired within this many ticks is <see cref="SpotState.Fired"/>.</summary>
    public required int FiredTicks { get; init; }

    /// <summary>Per forest density 0 to 3, whether a cell of it is the woods (an observer in it sees further over them).</summary>
    public required IReadOnlyList<bool> Woods { get; init; }

    /// <summary>How far an observer standing in the woods sees over woods cells, percent of its sight (open cells: 100).</summary>
    public required int WoodsSightPercent { get; init; }

    /// <summary>The table's percent for a cover and a state.</summary>
    public int Percent(SpotCover cover, SpotState state) => Table[(int)cover * States + (int)state];

    /// <summary>The concealment of a unit of this locomotor, percent (0 for anything but foot, wheeled and tracked).</summary>
    public int ConcealmentOf(Locomotor locomotor) => locomotor switch
    {
        Locomotor.Foot => ConcealmentFoot,
        Locomotor.Wheeled => ConcealmentWheeled,
        Locomotor.Tracked => ConcealmentTracked,
        _ => 0,
    };

    internal void Digest(Action<long> put)
    {
        put(0x5907);
        foreach (var v in Table)
        {
            put(v);
        }
        put(ConcealmentFoot);
        put(ConcealmentWheeled);
        put(ConcealmentTracked);
        put(Optics.Count);
        foreach (var (id, percent) in Optics)
        {
            put(id.Length);
            foreach (var ch in id)
            {
                put(ch);
            }
            put(percent);
        }
        put(FiredTicks);
        foreach (var b in Woods)
        {
            put(b ? 1 : 0);
        }
        put(WoodsSightPercent);
    }
}

using SimCore.FixedPoint;

namespace SimCore.Model;

/// <summary>How steep a cell is, for one movement class (thresholds in <see cref="MoveClass"/>).</summary>
public enum SlopeClass : byte
{
    /// <summary>Under the gentle threshold.</summary>
    Flat,

    /// <summary>From the gentle threshold.</summary>
    Gentle,

    /// <summary>From the steep threshold.</summary>
    Steep,

    /// <summary>From the cliff threshold: impassable to everyone.</summary>
    Cliff,
}

/// <summary>
/// The surface of a road cell (<see cref="None"/> off-road). Content lists per-kind values in the
/// order highway, paved, gravel, dirt (<see cref="MoveClass.RoadKinds"/>). <see cref="Alley"/> and
/// <see cref="Footpath"/> are the two foot kinds (docs/design.md, "Alleys and footpaths"): they have no
/// values of their own in the rules and never take a vehicle road's cell.
/// </summary>
public enum RoadKind : byte
{
    /// <summary>Not a road.</summary>
    None,

    /// <summary>A highway: four lanes, a central barrier (between towns on big maps).</summary>
    Highway,

    /// <summary>Asphalt or concrete: town streets, rural roads, village lanes, airfield pavement (the default).</summary>
    Paved,

    /// <summary>A packed gravel road.</summary>
    Gravel,

    /// <summary>A dirt track: two ruts with grass between.</summary>
    Dirt,

    /// <summary>
    /// A narrow old-town street of stone paving and steps: only the foot movement class enters it (every other ground class
    /// finds it blocked, like a wall); for foot it costs what paved costs and changes no speed.
    /// </summary>
    Alley,

    /// <summary>
    /// A worn earth path across fields and ridges: blocks and slows nobody, vehicles treat it as plain ground; the foot
    /// class pays what a paved road costs on it (infantry routes follow it) and goes no faster.
    /// </summary>
    Footpath,
}

/// <summary>
/// What a standing tree trunk, and a fallen one, is to a movement class (docs/design.md, "Trees as obstacles"; content
/// <c>trunks</c>).
/// </summary>
public enum TrunkMode : byte
{
    /// <summary>Nothing: it walks between the trunks and over fallen ones at its forest speed (infantry).</summary>
    Ignore,

    /// <summary>A wall: it steers between standing trunks and round fallen ones (jeeps, light trucks).</summary>
    Avoid,

    /// <summary>
    /// It pushes a standing trunk over at low speed (<see cref="MoveClass.PushSpeedPercent"/>, <see cref="MoveClass.PushTicks"/>;
    /// routes pay <see cref="MoveClass.PushCost"/>); a fallen trunk is a wall to it unless <see cref="MoveClass.CrossFallen"/>.
    /// </summary>
    Push,
}

/// <summary>
/// How one class of mover deals with terrain: slope thresholds (percent grade), speed and path
/// cost per slope class, the extra cost of climbing, roads and forest. Speeds and percents are
/// 0 to any positive value; a speed or cost of 0 means impassable. Path costs are per cell, with
/// open flat ground at 10.
/// </summary>
public sealed class MoveClass
{
    /// <summary>The class ID (content).</summary>
    public required string Id { get; init; }

    /// <summary>The locomotor it applies to.</summary>
    public required Locomotor Locomotor { get; init; }

    /// <summary>The smallest unit radius it applies to (a heavier class of the same locomotor).</summary>
    public required Fixed MinRadius { get; init; }

    /// <summary>The largest unit radius it applies to (a lighter class of the same locomotor), or none.</summary>
    public Fixed? MaxRadius { get; init; }

    /// <summary>The grade (percent) where Gentle, Steep and Cliff begin.</summary>
    public required int GentleFrom { get; init; }

    /// <inheritdoc cref="GentleFrom"/>
    public required int SteepFrom { get; init; }

    /// <inheritdoc cref="GentleFrom"/>
    public required int CliffFrom { get; init; }

    /// <summary>Speed percent going up and going down, per <see cref="SlopeClass"/> (0: impassable).</summary>
    public required IReadOnlyList<int> SpeedUp { get; init; }

    /// <inheritdoc cref="SpeedUp"/>
    public required IReadOnlyList<int> SpeedDown { get; init; }

    /// <summary>Path cost of a cell per <see cref="SlopeClass"/> (0: impassable; flat open ground 10).</summary>
    public required IReadOnlyList<int> Cost { get; init; }

    /// <summary>Extra cost, in percent, of a step that climbs (from <see cref="UphillFull"/> grade on).</summary>
    public required int UphillCostPercent { get; init; }

    /// <summary>
    /// The climb's dead zone (docs/design.md, "Straight routes"): a step whose rise, centre to centre, is under this grade
    /// (percent of the step's length) pays nothing for climbing; from <see cref="UphillFull"/> on it pays all of
    /// <see cref="UphillCostPercent"/>, a share in between in proportion. 0 and 0: any rise pays it all (the rules before).
    /// </summary>
    public int UphillFrom { get; init; }

    /// <inheritdoc cref="UphillFrom"/>
    public int UphillFull { get; init; }

    /// <summary>
    /// A trip no longer than this (cells, straight) ignores roads: they cost as the ground beside them (docs/design.md,
    /// "Straight routes"), except a road within <see cref="ShortTripRoad"/> cells of the straight line whose way saves at
    /// least <see cref="ShortTripSaving"/> percent. 0: every trip weighs roads at their cost (the rules before).
    /// </summary>
    public int ShortTrip { get; init; }

    /// <inheritdoc cref="ShortTrip"/>
    public int ShortTripRoad { get; init; }

    /// <inheritdoc cref="ShortTrip"/>
    public int ShortTripSaving { get; init; }

    /// <summary>
    /// Whether a cell reads as flat for movement only by the lip rule (own its grade, moved what movement reads): a step onto or off
    /// it pays no climb, whatever its corners rise.
    /// </summary>
    public static bool OnLip(byte own, byte moved, int gentleFrom) => moved < own && moved < gentleFrom;

    /// <summary>
    /// The rises (of cell-centre sums: the four corners' heights added, raw) where a step of this class starts to pay for
    /// climbing and where it pays all of it, straight or diagonal (the grade times the step's length).
    /// </summary>
    public (int From, int Full) ClimbRise(bool diagonal) => (RiseOf(UphillFrom, diagonal), RiseOf(UphillFull, diagonal));

    // RiseOf: a grade's rise over a straight (1,024 raw) or diagonal (1,448) step, in centre sums (4 x raw).
    private static int RiseOf(int grade, bool diagonal) => diagonal ? grade * 4 * 1448 / 100 : grade * 4 * (int)Fixed.OneRaw / 100;

    /// <summary>
    /// The extra percent a step of this class pays for a rise of <paramref name="riseSum"/> (cell-centre sums, to minus
    /// from), straight or diagonal: 0 going level or down and in the dead zone, all of <see cref="UphillCostPercent"/> from
    /// <see cref="UphillFull"/> grade on.
    /// </summary>
    public int ClimbPercent(int riseSum, bool diagonal) => Climb(riseSum, ClimbRise(diagonal), UphillCostPercent);

    /// <summary>The climbing percent of a rise against a class's <see cref="ClimbRise"/> pair (the searches' inner loops).</summary>
    internal static int Climb(int rise, (int From, int Full) at, int uphill) =>
        rise <= at.From ? 0 : rise >= at.Full ? uphill : (int)((long)uphill * (rise - at.From) / (at.Full - at.From));

    /// <summary>The number of road kinds with per-kind values (highway, paved, gravel, dirt).</summary>
    public const int RoadKinds = 4;

    /// <summary>
    /// The cost of a road cell in percent of the same cell off-road, per road kind in the order
    /// highway, paved, gravel, dirt (<see cref="RoadCostOf"/>).
    /// </summary>
    public required IReadOnlyList<int> RoadCost { get; init; }

    /// <summary>
    /// The speed on a road cell in percent of the same cell off-road (slope and forest still
    /// apply), per road kind in the order highway, paved, gravel, dirt (<see cref="RoadSpeedOf"/>).
    /// </summary>
    public required IReadOnlyList<int> RoadSpeed { get; init; }

    /// <summary>The cost percent of a paved road cell (what a single road cost meant before road kinds).</summary>
    public int RoadCostPercent => RoadCost[(int)RoadKind.Paved - 1];

    /// <summary>
    /// The cost percent of a road cell of this kind (100 off-road). An alley or a footpath costs the foot class what a
    /// paved road does and every other class nothing less than the ground beside it (100).
    /// </summary>
    public int RoadCostOf(RoadKind kind) => kind switch
    {
        RoadKind.None => 100,
        RoadKind.Alley or RoadKind.Footpath => Locomotor == Locomotor.Foot ? RoadCost[(int)RoadKind.Paved - 1] : 100,
        _ => RoadCost[(int)kind - 1],
    };

    /// <summary>The speed percent of a road cell of this kind (100 off-road, and on an alley or a footpath: no speed change).</summary>
    public int RoadSpeedOf(RoadKind kind) => kind is RoadKind.None or RoadKind.Alley or RoadKind.Footpath ? 100 : RoadSpeed[(int)kind - 1];

    /// <summary>
    /// Whether this class can enter an alley: only the foot class does; every other ground class finds the cell blocked
    /// (cost and speed 0, like a wall).
    /// </summary>
    public bool EntersAlleys => Locomotor == Locomotor.Foot;

    /// <summary>Cost and speed percent per forest density 0 to 3.</summary>
    public required IReadOnlyList<int> ForestCostPercent { get; init; }

    /// <inheritdoc cref="ForestCostPercent"/>
    public required IReadOnlyList<int> ForestSpeedPercent { get; init; }

    /// <summary>The forest density from which it cannot enter (4: never blocked).</summary>
    public required int ForestBlockedFrom { get; init; }

    /// <summary>What a standing or fallen tree trunk is to it (<see cref="TrunkMode.Ignore"/> unless the rules say otherwise).</summary>
    public TrunkMode Trunks { get; init; }

    /// <summary>
    /// A pusher's path cost of a cell with a standing trunk, in percent of the same cell without one (and of a fallen trunk's
    /// cell with <see cref="CrossFallen"/>): routes go between trees when the way round is short and push one over when not.
    /// </summary>
    public int PushCost { get; init; } = 100;

    /// <summary>A pusher's top speed, in percent, while it pushes a trunk or rolls over a fallen one (its terrain speed, if lower, stays).</summary>
    public int PushSpeedPercent { get; init; } = 100;

    /// <summary>The ticks of pushing contact after which the trunk falls (a pusher).</summary>
    public int PushTicks { get; init; }

    /// <summary>Whether a pusher rolls over a fallen trunk (at its push speed and cost); otherwise a fallen trunk is a wall to it.</summary>
    public bool CrossFallen { get; init; }

    /// <summary>
    /// The path cost of a burning cell (forest fire; docs/design.md, "Forest fire"), in percent of the same cell not burning;
    /// 0 (the default): a wall while it burns (no route through it; a body in it is pushed out like from any wall).
    /// </summary>
    public int FireCost { get; init; }

    /// <summary>
    /// Whether a cell whose forest fire burnt or a blast thinned a level down keeps the cost and speed of the forest it had for
    /// this class (infantry pick their way through the snags, ash and stumps as through the wood: their routing caches stay as
    /// they were, docs/design.md, "Forest fire"); otherwise it moves as the thinner forest it is now.
    /// </summary>
    public bool ThinnedForestAsBefore { get; init; }

    /// <summary>Whether a fallen trunk lying in a cell lets it in (at its push speed and cost: a pusher with <see cref="CrossFallen"/>; infantry ignore it).</summary>
    public bool EntersFallen => Trunks == TrunkMode.Ignore || (Trunks == TrunkMode.Push && CrossFallen);

    /// <summary>
    /// Launchers that deploy (rocket trucks) set up only on cells of a lower grade (percent);
    /// by default the steep threshold (Flat and Gentle ground).
    /// </summary>
    public int DeployMaxGrade { get; init; } = int.MaxValue;

    /// <summary>The slope class of a cell with this grade.</summary>
    public SlopeClass Classify(int grade) =>
        grade >= CliffFrom ? SlopeClass.Cliff : grade >= SteepFrom ? SlopeClass.Steep : grade >= GentleFrom ? SlopeClass.Gentle : SlopeClass.Flat;
}

/// <summary>
/// The terrain rules: the movement classes, in order. A unit takes the last class whose
/// locomotor matches, whose <see cref="MoveClass.MinRadius"/> it reaches and whose
/// <see cref="MoveClass.MaxRadius"/> (if any) it doesn't pass. Loaded from
/// <c>content/terrain/rules.json</c>; they only take part in a game (and its hash) with a terrain
/// map, so flat maps without one are unchanged.
/// </summary>
public sealed class TerrainRules
{
    /// <summary>The movement classes, in content order.</summary>
    public required IReadOnlyList<MoveClass> Classes { get; init; }

    /// <summary>
    /// The lip rule (docs/design.md, "Straight routes"): for movement a cell's grade is the rise over a window of this many cells
    /// round it, per cell of the window, when that is less than its own. 0: its own grade (the rules before).
    /// </summary>
    public int LipWindow { get; init; }

    /// <summary>The grade (percent: of a cell's 2 m) above which a cell keeps its own grade whatever the window says: a wall stays a wall.</summary>
    public int LipRise { get; init; }

    /// <summary>
    /// A window two cells narrower than <see cref="LipWindow"/> counts too, this many percent less: its grade less this, if that is
    /// more than the wide window's, is the cell's (a short bank stays steep; a step of the lip's height still reads flat).
    /// </summary>
    public int LipSlack { get; init; }

    /// <summary>How terrain and forest block sight under fog of war, or null (the map's rules have no vision block: <see cref="VisionRules.Open"/>).</summary>
    public VisionRules? Vision { get; init; }

    /// <summary>The standing and fallen tree trunks (docs/design.md, "Trees as obstacles"), or null: no trunks, forest is undergrowth only.</summary>
    public TreeRules? Trees { get; init; }

    /// <summary>
    /// Spotting under fog of war (fog phase F4e; docs/design.md, "Spotting"), or null: whatever a side sees on the fog grid it
    /// spots, as before.
    /// </summary>
    public SpottingRules? Spotting { get; init; }

    /// <summary>The class index for a unit of this locomotor and radius, or -1 (structures, aircraft).</summary>
    public int ClassOf(Locomotor locomotor, Fixed radius)
    {
        var best = -1;
        for (var i = 0; i < Classes.Count; i++)
        {
            if (Classes[i].Locomotor == locomotor && radius.Raw >= Classes[i].MinRadius.Raw &&
                (Classes[i].MaxRadius is not { } max || radius.Raw <= max.Raw))
            {
                best = i;
            }
        }
        return best;
    }

    /// <summary>The class index for a unit definition, or -1.</summary>
    public int ClassOf(UnitDef d) => ClassOf(d.Archetype.Locomotor, d.Archetype.Radius);

    /// <summary>A 64-bit FNV-1a digest of every rule, for the game hash.</summary>
    public ulong Digest()
    {
        var h = 14695981039346656037UL;
        void Put(long v)
        {
            for (var i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (8 * i));
                h *= 1099511628211UL;
            }
        }
        void PutString(string s)
        {
            Put(s.Length);
            foreach (var ch in s)
            {
                Put(ch);
            }
        }
        Put(Classes.Count);
        foreach (var c in Classes)
        {
            Put((long)c.Locomotor);
            Put(c.MinRadius.Raw);
            if (c.MaxRadius is { } maxR)
            {
                Put(maxR.Raw); // only for classes that have one: older rules digest as before
            }
            Put(c.GentleFrom);
            Put(c.SteepFrom);
            Put(c.CliffFrom);
            foreach (var list in new[] { c.SpeedUp, c.SpeedDown, c.Cost, c.ForestCostPercent, c.ForestSpeedPercent, c.RoadCost, c.RoadSpeed })
            {
                Put(list.Count);
                foreach (var v in list)
                {
                    Put(v);
                }
            }
            Put(c.UphillCostPercent);
            if (c.UphillFrom != 0 || c.UphillFull != 0)
            {
                Put(0xC11B); // only with a dead zone: older rules digest as before
                Put(c.UphillFrom);
                Put(c.UphillFull);
            }
            if (c.ShortTrip != 0)
            {
                Put(0x5407); // only with a short trip rule: older rules digest as before
                Put(c.ShortTrip);
                Put(c.ShortTripRoad);
                Put(c.ShortTripSaving);
            }
            Put(c.ForestBlockedFrom);
            Put(c.DeployMaxGrade);
        }
        if (LipWindow != 0)
        {
            Put(0x11F5); // only with the lip rule: older rules digest as before
            Put(LipWindow);
            Put(LipRise);
            Put(LipSlack);
        }
        Vision?.Digest(Put); // only with a vision block: older rules digest as before
        if (Trees is { } trees)
        {
            // Only with a trees block: older rules digest as before.
            Put(0x7EE5);
            foreach (var v in trees.TrunkChance)
            {
                Put(v);
            }
            Put(trees.FallenLengthCells);
            Put(trees.FallenTicks);
            Put(trees.Fell.Count);
            foreach (var (warhead, percent) in trees.Fell)
            {
                PutString(warhead);
                Put(percent);
            }
            Put(trees.FellMinRadius.Raw);
            Put(trees.FallDamagePercent);
            Put(trees.FallDamageTags.Count);
            foreach (var tag in trees.FallDamageTags)
            {
                PutString(tag);
            }
            foreach (var c in Classes)
            {
                Put((long)c.Trunks);
                Put(c.PushCost);
                Put(c.PushSpeedPercent);
                Put(c.PushTicks);
                Put(c.CrossFallen ? 1 : 0);
            }
            if (trees.BlastThins)
            {
                Put(0x7415); // only rules whose blasts thin the forest: older rules digest as before
            }
            if (Classes.Any(c => c.FireCost != 0 || c.ThinnedForestAsBefore))
            {
                Put(0xF14E); // only rules that price burning or thinned cells: older rules digest as before
                foreach (var c in Classes)
                {
                    Put(c.FireCost);
                    Put(c.ThinnedForestAsBefore ? 1 : 0);
                }
            }
        }
        Spotting?.Digest(Put); // only with a spotting block: older rules digest as before
        return h;
    }
}

/// <summary>
/// The tree trunks of a map's forest (content <c>trees</c> in the terrain rules; docs/design.md, "Trees as obstacles"). Each
/// 2 x 2 block of cells (a slot) may hold one standing trunk, in one of its four cells, both picked by
/// <see cref="Sim.TreeTrunks.SlotHash"/>: the trunk stands when that cell's forest density gives it the chance. Movement
/// classes say what a trunk is to them (<see cref="MoveClass.Trunks"/>).
/// </summary>
public sealed class TreeRules
{
    /// <summary>The chance, in percent, that a slot whose picked cell has this forest density holds a trunk, per density [none, scrub, grove, dense].</summary>
    public required IReadOnlyList<int> TrunkChance { get; init; }

    /// <summary>The cells a fallen trunk covers along its direction, from the cell beside its stump (fewer where the map ends or a building or water stops it).</summary>
    public required int FallenLengthCells { get; init; }

    /// <summary>The ticks a fallen trunk lies before it is gone (clear ground after).</summary>
    public required int FallenTicks { get; init; }

    /// <summary>
    /// The warheads whose blasts blow trees down, by warhead ID (ordinal order), each with the percent of its weapon's splash
    /// radius within which standing trunks fall (away from the impact). A warhead not listed fells nothing.
    /// </summary>
    public IReadOnlyList<(string Warhead, int Percent)> Fell { get; init; } = [];

    /// <summary>The smallest radius a felling blast blows trees down in, whatever its splash (an anti-tank missile hits with none).</summary>
    public Fixed FellMinRadius { get; init; }

    /// <summary>The percent of its max HP a unit with one of <see cref="FallDamageTags"/> loses when a trunk falls across its cell.</summary>
    public int FallDamagePercent { get; init; }

    /// <summary>The tags of the units a falling trunk hurts (infantry); none: nobody.</summary>
    public IReadOnlyList<string> FallDamageTags { get; init; } = [];

    /// <summary>
    /// Whether a cell whose tree a blast blew down loses one forest level (docs/design.md, "Forest fire": blasted cells open
    /// sight, decision 456's follow-up); pushed trees thin nothing.
    /// </summary>
    public bool BlastThins { get; init; }

    /// <summary>Whether any density has trunks at all.</summary>
    public bool Any
    {
        get
        {
            foreach (var c in TrunkChance)
            {
                if (c > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

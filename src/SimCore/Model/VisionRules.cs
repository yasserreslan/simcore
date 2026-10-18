namespace SimCore.Model;

/// <summary>
/// How terrain and forest block sight under fog of war (<see cref="MatchSetup.Fog"/>): the eye
/// and target heights of ground units, the canopy height of each forest density, and how far one
/// sees into a wood. Heights are raw fixed point (512 a metre). Part of the terrain rules (the
/// optional "vision" block of content/terrain/rules.json) and hashed with them when present.
/// </summary>
public sealed class VisionRules
{
    /// <summary>The eye height of a soldier above the ground, raw.</summary>
    public required int EyeFootRaw { get; init; }

    /// <summary>The eye height of a vehicle above the ground, raw.</summary>
    public required int EyeVehicleRaw { get; init; }

    /// <summary>How high above a cell's ground a unit must be seen to count as seen there, raw.</summary>
    public required int TargetRaw { get; init; }

    /// <summary>The canopy height per forest density 0 to 3, raw: it raises the surface that blocks sight.</summary>
    public required IReadOnlyList<int> CanopyRaw { get; init; }

    /// <summary>What each cell of forest density 0 to 3 a sight line passes through costs.</summary>
    public required IReadOnlyList<int> ForestSight { get; init; }

    /// <summary>A cell is hidden once the forest cells crossed on the way to it add up to this.</summary>
    public required int ForestBudget { get; init; }

    /// <summary>
    /// The rules of a map without a vision block (and of flat maps): soldiers' eyes at 1.7 m, vehicles'
    /// at 2.5 m, targets at 1.5 m, and forest that does not block sight.
    /// </summary>
    public static VisionRules Open { get; } = new()
    {
        EyeFootRaw = 17 * 512 / 10,
        EyeVehicleRaw = 25 * 512 / 10,
        TargetRaw = 15 * 512 / 10,
        CanopyRaw = [0, 0, 0, 0],
        ForestSight = [0, 0, 0, 0],
        ForestBudget = 1,
    };

    internal void Digest(Action<long> put)
    {
        put(EyeFootRaw);
        put(EyeVehicleRaw);
        put(TargetRaw);
        foreach (var v in CanopyRaw)
        {
            put(v);
        }
        foreach (var v in ForestSight)
        {
            put(v);
        }
        put(ForestBudget);
    }
}

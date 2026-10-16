namespace SimCore.Model;

/// <summary>
/// The stats formula, used both when unit definitions are compiled at match
/// start and for aura buffs at run time:
/// <c>final = base × (100 + Σ percent) / 100 + Σ flat</c>, in integers.
/// Sums commute, so the order of modifiers never matters.
/// </summary>
/// <remarks>
/// A stat whose base is 0 stays 0: a unit without a weapon gets no damage
/// from a +2 flat buff, and a structure no speed. Results are clamped to at
/// least 1 for <see cref="Stat.MaxHp"/> and <see cref="Stat.Reload"/> and to
/// at least 0 otherwise.
/// </remarks>
public static class StatPipeline
{
    /// <summary>The number of <see cref="Stat"/> values.</summary>
    public const int StatCount = (int)Stat.BuildRate + 1;

    /// <summary>Applies summed modifiers to one base value.</summary>
    public static long Apply(Stat stat, long baseValue, long percent, long flat)
    {
        if (baseValue == 0)
        {
            return 0;
        }
        var v = baseValue * (100 + percent) / 100 + flat;
        var min = stat is Stat.MaxHp or Stat.Reload ? 1 : 0;
        return Math.Max(v, min);
    }

    /// <summary>Returns the final stats for base values and a set of modifiers.</summary>
    /// <param name="baseValues">One value per <see cref="Stat"/>.</param>
    /// <param name="modifiers">Every modifier that applies, in any order.</param>
    public static long[] Compile(IReadOnlyList<long> baseValues, IEnumerable<Modifier> modifiers)
    {
        var pct = new long[StatCount];
        var flat = new long[StatCount];
        foreach (var m in modifiers)
        {
            pct[(int)m.Stat] += m.Percent;
            flat[(int)m.Stat] += m.Flat;
        }
        var output = new long[StatCount];
        for (var i = 0; i < StatCount; i++)
        {
            output[i] = Apply((Stat)i, baseValues[i], pct[i], flat[i]);
        }
        return output;
    }
}

using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// A soldier's arms (INF-E1a, docs/design.md "Infantry weapons and ammo"): the rounds in each weapon's magazine, the magazine
/// being replaced, the arm in his hands and a switch to the other. Only units whose definition has magazines, carried ammo or a
/// sidearm (<see cref="UnitDef.HasArms"/>) keep one. The carried ammo itself is the weapon's stock (<see cref="Unit.Charges"/>,
/// the magazine's rounds included). Hashed.
/// </summary>
public sealed class Arms
{
    private readonly int[] _mag;

    internal Arms(UnitDef def)
    {
        _mag = new int[def.Weapons.Count];
        for (var i = 0; i < _mag.Length; i++)
        {
            var w = def.Weapons[i];
            if (w.Effect.Magazine > 0)
            {
                _mag[i] = Math.Min(w.Effect.Magazine, w.Stock >= 0 ? def.StockMax[w.Stock] : w.Effect.Magazine); // a full magazine
            }
        }
    }

    /// <summary>The rounds in each weapon's magazine, by weapon index (0 for a weapon without a magazine).</summary>
    public IReadOnlyList<int> Mag => _mag;

    internal int[] MutableMag => _mag;

    /// <summary>The weapon in his hands (an index into <see cref="UnitDef.Weapons"/>): the main arm (0) or the sidearm.</summary>
    public int InHand { get; internal set; }

    /// <summary>The weapon he is switching to, or -1. No weapon fires while he switches.</summary>
    public int SwitchTo { get; internal set; } = -1;

    /// <summary>Ticks left of the switch.</summary>
    public int SwitchLeft { get; internal set; }

    /// <summary>The weapon whose magazine he is replacing, or -1.</summary>
    public int Reloading { get; internal set; } = -1;

    /// <summary>Ticks left of that magazine's reload.</summary>
    public int ReloadLeft { get; internal set; }

    /// <summary>Ticks in a row with no enemy infantry within the main arm's switch distance plus a cell (back to it after 2 s).</summary>
    public int Calm { get; internal set; }

    /// <summary>Whether he is switching arms.</summary>
    public bool Switching => SwitchTo >= 0;

    internal void HashInto(ref WordHash h)
    {
        h.PutSigned(_mag.Length);
        foreach (var m in _mag)
        {
            h.PutSigned(m);
        }
        h.Put4(unchecked((ulong)InHand), unchecked((ulong)SwitchTo), unchecked((ulong)SwitchLeft), unchecked((ulong)Reloading));
        h.PutSigned(ReloadLeft);
        h.PutSigned(Calm);
    }
}

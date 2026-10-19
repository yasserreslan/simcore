using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>A unit run over this tick.</summary>
/// <param name="Victim">The unit crushed.</param>
/// <param name="By">The vehicle that ran it over.</param>
/// <param name="At">Where it died.</param>
public readonly record struct Crush(uint Victim, uint By, Vec At);

// Crushing: a vehicle driving forward kills an enemy it touches in front of it whose crushable
// level is above 0 and below the vehicle's crusher level (infantry 1; trucks, launchers and the
// carrier 2; the dirt bike never crushes). It runs at the start of the combat system, after
// movement and collision (bodies touch, they don't overlap), vehicles in ID order.
public sealed partial class World
{
    private static readonly long CrushSlack = Fixed.OneRaw / 8; // contact: the bodies' radii plus an eighth of a cell

    private readonly List<Unit> _nearCrush = [];
    private readonly List<Crush> _crushes = [];

    /// <summary>The units crushed this tick, in the order it happened (for renderers and logs; not state).</summary>
    public IReadOnlyList<Crush> Crushes => _crushes;

    private void RunCrush()
    {
        _crushes.Clear();
        foreach (var v in _units)
        {
            var level = v.Def.Archetype.Crusher;
            if (level == 0 || !v.Alive || !v.CanAct || v.Inside != 0 || v.Def.Layer != Layers.Ground || v.DriveSpeed.Raw <= 0)
            {
                continue; // only vehicles rolling forward
            }
            var reach = v.Def.Archetype.Radius.Raw + Fixed.OneRaw;
            foreach (var o in Around(v.Pos.X.Raw - reach, v.Pos.Y.Raw - reach, v.Pos.X.Raw + reach, v.Pos.Y.Raw + reach, _nearCrush, true))
            {
                var c = o.Def.Archetype.Crushable;
                if (c == 0 || c >= level || !o.Alive || o.Owner == v.Owner || o.Owner == 0 || o.Inside != 0 || o.Garrison != 0 ||
                    o.Def.Layer != Layers.Ground)
                {
                    continue;
                }
                var d = o.Pos - v.Pos;
                var touch = v.Def.Archetype.Radius.Raw + o.Def.Archetype.Radius.Raw + CrushSlack;
                if (d.LenSq() > touch * touch || v.Heading.X.Raw * d.X.Raw + v.Heading.Y.Raw * d.Y.Raw <= 0)
                {
                    continue; // not touching, or beside or behind it
                }
                o.HP = 0;
                _crushes.Add(new Crush(o.Id, v.Id, o.Pos));
            }
        }
    }
}

// The deterministic game simulation: the whole game minus graphics, sound and
// input devices.
//
// The world changes only through Commands applied by World.Step. Given the
// same starting world and the same commands, every machine computes a
// bit-identical world on every tick; World.Hash proves it. See README.md for
// the determinism rules this namespace follows.
using System.Text.Json.Serialization;
using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>A map grid coordinate.</summary>
/// <param name="X">The column, growing right.</param>
/// <param name="Y">The row, growing down.</param>
public readonly record struct Cell(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y)
{
    /// <summary>Returns the world position of the cell's center.</summary>
    public Vec Center() => new(Fixed.FromInt(X) + Fixed.FromRaw(Fixed.OneRaw / 2), Fixed.FromInt(Y) + Fixed.FromRaw(Fixed.OneRaw / 2));

    /// <summary>Returns the cell containing world position p.</summary>
    public static Cell Of(Vec p) => new(p.X.Floor(), p.Y.Floor());

    /// <summary>Formats the cell as {x y}.</summary>
    public override string ToString() => $"{{{X} {Y}}}";
}

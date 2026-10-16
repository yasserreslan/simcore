using System.Security.Cryptography;
using System.Text;

namespace SimCore.Model;

/// <summary>
/// Serializes a compiled <see cref="ContentSet"/> canonically and hashes it.
/// The serialization walks the sorted collections field by field, so two
/// content directories that compile to the same definitions hash the same no
/// matter how their files are split, ordered or formatted.
/// </summary>
/// <remarks>
/// Integers are 8 little-endian bytes (signed values sign-extended), strings
/// are a length and their UTF-8 bytes, lists are a count and their items.
/// Changing this layout changes every content hash; bump <see cref="Format"/>.
/// </remarks>
public static class ContentHasher
{
    /// <summary>The serialization format tag, hashed first.</summary>
    public const string Format = "simcore-content/1";

    /// <summary>Returns the SHA-256 of set's canonical serialization as lowercase hex.</summary>
    public static string Compute(ContentSet set) =>
        Convert.ToHexString(SHA256.HashData(Serialize(set))).ToLowerInvariant();

    /// <summary>Returns the canonical serialization of set.</summary>
    public static byte[] Serialize(ContentSet set)
    {
        var w = new Writer();
        w.Str(Format);
        w.List(set.Tags, w.Str);
        w.List(set.Warheads, h =>
        {
            w.Str(h.Id);
            w.List(h.Versus, v => w.Int(v));
        });
        w.List(set.Archetypes, a =>
        {
            w.Str(a.Id);
            w.Str(a.Name);
            w.Int(a.Glyph);
            w.UInt(a.Tags.Bits);
            w.Int((int)a.Locomotor);
            w.Int((int)a.Armor);
            w.Int(a.MaxHp);
            w.Int(a.Speed.Raw);
            w.Int(a.TurnRate);
            w.Bool(a.Turret);
            w.Bool(a.Hidden);
            w.Bool(a.Locked);
            w.Int(a.Cost);
            w.Int(a.Budget);
            w.List(a.Slots, v => w.Int(v));
            w.List(a.DefaultComponents, v => w.Int(v));
            w.Int(a.FootprintW);
            w.Int(a.FootprintH);
            w.Int(a.BuildTime);
        });
        w.List(set.Components, c =>
        {
            w.Str(c.Id);
            w.Int((int)c.Slot);
            w.Int(c.Cost);
            w.UInt(c.Requires.Bits);
            w.List(c.Effects, e => WriteEffect(w, e));
        });
        w.List(set.Factions, f =>
        {
            w.Str(f.Id);
            w.Str(f.Name);
            w.List(f.Archetypes, w.Bool);
            w.List(f.Components, w.Bool);
            w.List(f.Modifiers, m =>
            {
                w.UInt(m.Tags.Bits);
                WriteModifier(w, m.Modifier);
            });
        });
        return w.ToArray();
    }

    private static void WriteEffect(Writer w, Effect e)
    {
        switch (e)
        {
            case WeaponEffect x:
                w.Str("weapon");
                w.Int(x.Damage);
                w.Int(x.Range.Raw);
                w.Int(x.Reload);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                w.Int(x.Scatter.Raw);
                w.Int((int)x.Targets);
                break;
            case AuraEffect x:
                w.Str("aura");
                w.Int(x.Radius.Raw);
                w.Int(x.EveryTicks);
                w.Int(x.Heal);
                w.List(x.Modifiers, m => WriteModifier(w, m));
                w.Int((int)x.Affects);
                w.UInt(x.AffectsTags.Bits);
                break;
            case PlacerEffect x:
                w.Str("placer");
                w.Int(x.Archetype);
                w.Int(x.Charges);
                w.Int(x.Cooldown);
                w.Int(x.Range.Raw);
                break;
            case TriggerEffect x:
                w.Str("trigger");
                w.Int(x.Radius.Raw);
                w.Int(x.Damage);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                w.UInt(x.Affects.Bits);
                break;
            case BuilderEffect x:
                w.Str("builder");
                w.Int(x.Rate);
                w.Int(x.Range.Raw);
                break;
            case BuildAssistEffect x:
                w.Str("buildAssist");
                w.Int(x.Bonus);
                w.Int(x.Radius.Raw);
                w.List(x.Unlocks, v => w.Int(v));
                break;
            case RepairEffect x:
                w.Str("repair");
                w.Int(x.Rate);
                w.Int(x.EveryTicks);
                w.Int(x.Radius.Raw);
                w.UInt(x.Affects.Bits);
                break;
            case ModifierEffect x:
                w.Str("modifier");
                WriteModifier(w, x.Modifier);
                break;
            default:
                throw new ArgumentException($"content: effect type {e.GetType().Name} has no canonical form");
        }
    }

    private static void WriteModifier(Writer w, Modifier m)
    {
        w.Int((int)m.Stat);
        w.Int(m.Percent);
        w.Int(m.Flat);
    }

    private sealed class Writer
    {
        private readonly MemoryStream _buf = new();

        public void UInt(ulong v)
        {
            for (var i = 0; i < 8; i++)
            {
                _buf.WriteByte((byte)(v >> (8 * i)));
            }
        }

        public void Int(long v) => UInt(unchecked((ulong)v));

        public void Bool(bool v) => Int(v ? 1 : 0);

        public void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Int(b.Length);
            _buf.Write(b);
        }

        public void List<T>(IReadOnlyList<T> items, Action<T> item)
        {
            Int(items.Count);
            foreach (var x in items)
            {
                item(x);
            }
        }

        public byte[] ToArray() => _buf.ToArray();
    }
}

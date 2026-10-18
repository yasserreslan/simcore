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
    public const string Format = "simcore-content/2";

    /// <summary>Returns the SHA-256 of set's canonical serialization as lowercase hex.</summary>
    public static string Compute(ContentSet set) =>
        Convert.ToHexString(SHA256.HashData(Serialize(set))).ToLowerInvariant();

    /// <summary>Returns the canonical serialization of set.</summary>
    public static byte[] Serialize(ContentSet set)
    {
        var w = new Writer();
        w.Str(Format);
        w.List(set.Tags, w.Str);
        w.List(set.ArmorClasses, w.Str);
        w.List(set.Warheads, h =>
        {
            w.Str(h.Id);
            w.List(h.Versus, v => w.Int(v));
            w.List(h.MaxHpPercent, v => w.Int(v));
        });
        w.List(set.Archetypes, a =>
        {
            w.Str(a.Id);
            w.Str(a.Name);
            w.Int(a.Glyph);
            w.UInt(a.Tags.Bits);
            w.Int((int)a.Locomotor);
            w.Int(a.Armor);
            w.Int(a.MaxHp);
            w.Int(a.Speed.Raw);
            w.Int(a.TurnRate);
            w.Bool(a.Turret);
            w.Int((int)a.WeaponMode);
            w.Int(a.Radius.Raw);
            w.Int(a.DeployTicks);
            w.Int(a.StowTicks);
            w.Int(a.AccelTicks);
            w.Int(a.ExpireWhenEmpty);
            w.Int((int)a.HeightRaw);
            w.Bool(a.Hidden);
            w.Bool(a.Locked);
            w.Int(a.Cost);
            w.Int(a.Vision.Raw);
            w.Int(a.Detect.Raw);
            w.Int(a.AirVision.Raw);
            w.Int(a.Lifetime);
            w.List(a.SlotChoices, choices => w.List(choices, v => w.Int(v)));
            w.List(a.Components, v => w.Int(v));
            w.Bool(a.Crew is not null);
            if (a.Crew is { } crew)
            {
                w.UInt(crew.Tags.Bits);
                w.Int(crew.EnterRange.Raw);
                w.Int(crew.Survival);
                w.Int(crew.ExitDamage);
                w.Int(crew.Passengers);
                w.UInt(crew.PassengerTags.Bits);
            }
            w.Bool(a.Garrison is not null);
            if (a.Garrison is { } g)
            {
                w.Int(g.Capacity);
                w.UInt(g.Tags.Bits);
                w.Int(g.EnterRange.Raw);
                w.Int(g.RangeBonus.Raw);
                w.Int(g.VisionBonus.Raw);
                w.Int(g.OccupantDamage);
                w.List(g.OccupantWarheads, v => w.Int(v));
                w.List(g.Stages, v => w.Int(v));
                w.List(g.StageCapacity, v => w.Int(v));
                w.Int(g.EjectDamage);
                w.Int(g.CollapseSurvival);
                w.Int(g.CollapseHp);
                w.List(g.Doors, p => { w.Int(p.X); w.Int(p.Y); });
                w.Bool(g.Owned);
                w.UInt(g.LauncherTags.Bits);
            }
            w.Bool(a.HighGround is not null);
            if (a.HighGround is { } hg)
            {
                w.Int(hg.PercentPerMetre);
                w.Int(hg.MaxPercent);
                w.Int(hg.RadiusCells);
            }
            w.Int(a.FootprintW);
            w.Int(a.FootprintH);
            w.Int(a.BuildTime);
            w.Bool(a.Production is not null);
            if (a.Production is { } pr)
            {
                w.UInt(pr.Tags.Bits);
                w.Int(pr.Bays);
                w.Int(pr.QueueMax);
                w.Int(pr.DoorTicks);
                w.Int(pr.PushAfter);
                w.Str(pr.Crew);
                w.List(pr.Exits, e => { w.Int(e.X); w.Int(e.Y); });
                w.List(pr.BayPoints, e => { w.Int(e.X); w.Int(e.Y); });
                w.Int(pr.Rally?.X ?? -1);
                w.Int(pr.Rally?.Y ?? -1);
                w.Int(pr.MinHpPercent);
            }
            w.Bool(a.Construction is not null);
            if (a.Construction is { } cs)
            {
                w.Int(cs.StartHp);
                w.List(cs.Stages, v => w.Int(v));
                w.Int(cs.Refund);
                w.List(cs.Helpers, v => w.Int(v));
                w.Int(cs.PadBlend);
            }
        });
        w.List(set.Components, c =>
        {
            w.Str(c.Id);
            w.Int(c.Cost);
            w.UInt(c.Requires.Bits);
            w.Bool(c.Placeholder);
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
            w.Int(f.MaxPresetsPerArchetype);
            w.UInt(f.DefeatedWithout.Bits);
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
                w.Int(x.ProjectileSpeed.Raw);
                w.Int(x.SalvoSize);
                w.Int(x.SalvoInterval);
                w.Int(x.Ammo);
                w.Int(x.MinRange.Raw);
                w.Bool(x.AmmoPerRocket);
                w.Int(x.SplashCenter);
                w.Int(x.SplashEdge);
                w.Int(x.DepotReload?.EveryTicks ?? 0);
                w.Int(x.DepotReload?.AfterFireTicks ?? -1);
                w.Int(x.DepotReload?.Cost ?? 0);
                w.Int(x.DepotReload?.Stowed == true ? 1 : 0);
                w.Int(x.SplashInner.Raw);
                w.Int(x.SplashOuterPercent);
                w.List(x.AutoRange, r => w.Int(r.Raw));
                w.Bool(x.AutoSkipRetreating);
                w.Int(x.MaxMisses);
                w.Int(x.AimTicks);
                w.Int(x.Guided?.MaxFlight.Raw ?? 0);
                w.Int(x.Guided?.TurnPermille ?? 0);
                w.Bool(x.Guided?.RequiresOperatorStill ?? false);
                w.Int(x.Guided?.DropDistance.Raw ?? 0);
                w.List(x.Priority, p => w.Int(p));
                w.Int(x.ArcPercent);
                w.Int((int)x.Intercept);
                w.Bool(x.Drone is not null);
                if (x.Drone is { } dr)
                {
                    w.Int((int)dr.CruiseAboveMax);
                    w.Int((int)dr.ClimbPerTick);
                    w.Int(dr.BoosterTicks);
                    w.Int(dr.DivePermille);
                    w.Int((int)dr.LaunchHeight);
                    w.Int(dr.RoundOffTicks);
                    w.Int(dr.PitchOverTicks);
                }
                w.Bool(x.NoResupply);
                w.Bool(x.Interceptor is not null);
                if (x.Interceptor is { } ic)
                {
                    w.Int(ic.Classes);
                    w.Int(ic.LockTicks);
                    w.Int(ic.Fuse.Raw);
                    w.Int(ic.KillTail);
                    w.Int(ic.KillHead);
                    w.Int(ic.CrossHalf);
                    w.Int(ic.SpeedRef.Raw);
                    w.Int(ic.RangeFar);
                    w.Int(ic.TrackBonus);
                    w.Int(ic.TrackFull);
                    w.Int(ic.KillMin);
                    w.Int(ic.KillMax);
                    w.Int(ic.LeadTicks);
                    if (ic.LaunchHeight != 0)
                    {
                        w.Int((int)ic.LaunchHeight); // only when set: content without it hashes as before
                    }
                }
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
                w.Int(x.MaxActive);
                w.Int(x.PlaceTicks);
                w.Int(x.DepotReload?.EveryTicks ?? 0);
                w.Int(x.DepotReload?.AfterFireTicks ?? -1);
                w.Int(x.DepotReload?.Cost ?? 0);
                break;
            case LaunchEffect x:
                w.Str("launch");
                w.Int(x.Archetype);
                w.Int(x.Charges);
                w.Int(x.Cooldown);
                w.Int(x.Range.Raw);
                break;
            case StrikeEffect x:
                w.Str("strike");
                w.Int(x.Contact.Raw);
                w.Int(x.Damage);
                w.Int(x.Warhead.Index);
                w.Int(x.Splash.Raw);
                break;
            case SupplyEffect x:
                w.Str("supply");
                w.Int(x.Radius.Raw);
                w.Int(x.EveryTicks);
                w.Int(x.Amount);
                w.Int(x.Stock);
                w.Int(x.Restock);
                w.Int(x.ReloadBonus);
                w.Int(x.ConstructedStock);
                break;
            case CargoEffect x:
                w.Str("cargo");
                w.Int(x.Amount);
                w.Int(x.UnloadTicks);
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

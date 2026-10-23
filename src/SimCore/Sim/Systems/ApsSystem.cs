using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

/// <summary>What an active protection system did about a threat (<see cref="ApsEvent"/>).</summary>
public enum ApsEventKind : byte
{
    /// <summary>It engaged the threat and stopped it (a small blast at <see cref="ApsEvent.At"/>, a few cells out): it is gone.</summary>
    Intercepted,

    /// <summary>It engaged the threat and missed (a charge spent): the threat flies on and hits as usual.</summary>
    Failed,

    /// <summary>A threat came in with no charge left: nothing fired.</summary>
    Empty,

    /// <summary>A threat came in while it was still recovering from an engagement (a salvo overwhelms it): nothing fired.</summary>
    Overwhelmed,
}

/// <summary>An active protection system's report of the last tick (<see cref="World.ApsEvents"/>), for renderers and the HUD.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Unit">The protected unit.</param>
/// <param name="Projectile">The threat (a missile or rocket; gone from <see cref="World.Projectiles"/> once intercepted).</param>
/// <param name="Shooter">The unit that fired the threat.</param>
/// <param name="At">Where: the interception point (Intercepted), else the threat's position.</param>
/// <param name="Chance">The chance it had to stop it, percent (Intercepted and Failed; 0 otherwise).</param>
/// <param name="ChargesLeft">The charges left after it.</param>
public readonly record struct ApsEvent(ApsEventKind Kind, uint Unit, uint Projectile, uint Shooter, Vec At, int Chance, int ChargesLeft);

/// <summary>A unit's active protection system (<see cref="World.ApsOf"/>): charges left, the most it carries, ticks until it can engage again.</summary>
/// <param name="Charges">Charges left.</param>
/// <param name="Max">Charges when full.</param>
/// <param name="TicksToReady">Ticks until it can engage again (0: ready).</param>
public readonly record struct ApsStatus(int Charges, int Max, int TicksToReady);

// The APS system (active protection: the tank's Trophy; an aps effect on any ground unit): before each projectile moves, a threat
// (a weapon marked apsThreat: guided anti-tank missiles and direct-fire rockets; never bullets, shells, bombs, artillery or drones)
// aimed at a unit with an APS gets one look once it comes within the APS's range plus its own step this tick. A guided missile
// that lost guidance and an air strike missile whose launch roll missed are not aimed at it any more and are left alone. That
// look: with no charge left, Empty; still recovering from the last engagement (cooldown), Overwhelmed; otherwise it engages,
// spending a charge whatever the outcome, and a seeded roll (the seed, the tick, the unit and the threat: no generator state)
// against the chance (ApsChance) decides: Intercepted (the threat is gone, a guided missile's operator reloads) or Failed (it
// flies on and hits as usual). One look per threat (Projectile.ApsSeen). Projectiles in ID order; integer only.
public sealed partial class World
{
    private const ulong ApsSalt = 0xA950UL << 40; // keeps the APS roll apart from other rolls on the same IDs

    private readonly List<ApsEvent> _apsEvents = [];

    // Whether any unit definition of the match carries an APS or directional armour: a threat's launch point is hashed only then.
    private bool? _launchMatters;

    /// <summary>The active protection systems' reports of the last tick, in projectile order (for renderers; not state).</summary>
    public IReadOnlyList<ApsEvent> ApsEvents => _apsEvents;

    /// <summary>Unit u's active protection system, or null when it has none.</summary>
    public ApsStatus? ApsOf(Unit u) =>
        u.Def.Aps is null || u.Def.ApsStock < 0 ? null
        : new ApsStatus(u.Charges[u.Def.ApsStock], u.Def.StockMax[u.Def.ApsStock], u.ApsReady > Tick ? (int)(u.ApsReady - Tick) : 0);

    /// <summary>
    /// The chance (percent) APS a stops a threat flying at <paramref name="speed"/> (per tick) launched
    /// <paramref name="launchDistance"/> away: its base chance, less its fast penalty for each speed step above its threshold
    /// (rounded up), less its close penalty in proportion as the launch is closer than its close range, never below its minimum.
    /// </summary>
    public static int ApsChance(ApsEffect a, Fixed speed, Fixed launchDistance)
    {
        var chance = a.Chance;
        var over = speed.Raw - a.FastFrom.Raw;
        if (over > 0)
        {
            chance -= (int)((over + a.FastStep.Raw - 1) / a.FastStep.Raw) * a.FastPenalty;
        }
        var close = a.CloseRange.Raw - launchDistance.Raw;
        if (close > 0 && a.CloseRange.Raw > 0)
        {
            chance -= (int)(close * a.ClosePenalty / a.CloseRange.Raw);
        }
        return Math.Max(chance, a.MinChance);
    }

    /// <summary>The chance (percent) unit u's APS would stop threat p now (its speed and how far from u it was launched), or 0.</summary>
    public int ApsChanceAgainst(Unit u, Projectile p) =>
        u.Def.Aps is { } a ? ApsChance(a, p.Weapon.Effect.ProjectileSpeed, (u.Pos - p.Launched).Len()) : 0;

    // ClearApsEvents starts a tick's APS reports (with the projectiles).
    private void ClearApsEvents() => _apsEvents.Clear();

    // LaunchMatters: whether the match has a unit with an APS or directional armour (a threat's launch point is state then).
    private bool LaunchMatters()
    {
        if (_launchMatters is { } m)
        {
            return m;
        }
        var any = false;
        foreach (var d in Match.Defs)
        {
            any |= d.Aps is not null || d.Archetype.DirectionalArmor is not null;
        }
        _launchMatters = any;
        return any;
    }

    // StepAps gives projectile p (a threat not yet looked at) its look by the APS of the unit it is aimed at, when it is close
    // enough; true when it was intercepted (gone).
    private bool StepAps(Projectile p)
    {
        if (p.Target == 0 || Unit(p.Target) is not { Alive: true, Inside: 0, Garrison: 0 } u || u.Def.Aps is not { } a || u.Def.ApsStock < 0 ||
            (p.Weapon.Effect.Guided is not null && !p.Guided) || (p.Weapon.Effect.AirStrike is not null && !p.StrikeHit))
        {
            return false;
        }
        var off = p.Pos - u.Pos;
        var reach = a.Range.Raw + p.Weapon.Effect.ProjectileSpeed.Raw;
        if (off.LenSq() > reach * reach)
        {
            return false; // not yet
        }
        p.ApsSeen = true;
        var stock = u.Def.ApsStock;
        if (u.Charges[stock] == 0)
        {
            _apsEvents.Add(new ApsEvent(ApsEventKind.Empty, u.Id, p.Id, p.Shooter, p.Pos, 0, 0));
            return false;
        }
        if (u.ApsReady > Tick)
        {
            _apsEvents.Add(new ApsEvent(ApsEventKind.Overwhelmed, u.Id, p.Id, p.Shooter, p.Pos, 0, u.Charges[stock]));
            return false;
        }
        u.MutableCharges[stock]--;
        u.ApsReady = Tick + (uint)a.Cooldown;
        var chance = ApsChanceAgainst(u, p);
        var roll = (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, p.Id | ApsSalt) % 100);
        if (roll >= chance)
        {
            _apsEvents.Add(new ApsEvent(ApsEventKind.Failed, u.Id, p.Id, p.Shooter, p.Pos, chance, u.Charges[stock]));
            return false;
        }
        // Stopped a few cells out (at its range, or where it is when it came in closer).
        var at = off.LenSq() > a.Range.Raw * a.Range.Raw ? u.Pos + off.WithLen(a.Range) : p.Pos;
        p.Pos = at;
        p.Done = true;
        if (p.Weapon.Effect.Guided is not null)
        {
            EndGuidance(p); // its operator reloads from now
        }
        _apsEvents.Add(new ApsEvent(ApsEventKind.Intercepted, u.Id, p.Id, p.Shooter, at, chance, u.Charges[stock]));
        return true;
    }
}

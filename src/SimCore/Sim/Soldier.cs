using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>How badly a soldier is hurt (INF-E2a, docs/design.md "Wounds, rescue, bodies, ranks and Retreat"). Values are stable.</summary>
public enum WoundState : byte
{
    /// <summary>Above the wounded share of max HP.</summary>
    Healthy,

    /// <summary>At or under the wounded share: slower, worse aim (posture and suppression penalties are INF-E1b's hooks).</summary>
    Wounded,

    /// <summary>
    /// Under the down share and above 0, on foot on the map: he can't fight or take orders, crawls to cover or a squadmate, and
    /// bleeds out (<see cref="SoldierLife.BleedLeft"/>) unless a medic treats him. Still a target.
    /// </summary>
    Down,
}

/// <summary>A soldier's rank (owner decision 20; shown as chevrons). Values are stable.</summary>
public enum SoldierRank : byte
{
    /// <summary>No effect.</summary>
    Recruit,
    /// <summary>Aim better (content).</summary>
    Regular,
    /// <summary>Aim better, reload faster (content).</summary>
    Veteran,
    /// <summary>Aim better still, reload faster still (content).</summary>
    Elite,
}

/// <summary>
/// A soldier's wounds, tasks (carrying, treating, scavenging) and experience (INF-E2a): only units the content's <c>soldiers</c>
/// block applies to (<see cref="SoldierRules.Applies"/>) keep one (<see cref="Unit.Life"/>). Hashed.
/// </summary>
public sealed class SoldierLife
{
    internal readonly List<(uint Unit, uint Tick)> Damagers = []; // enemies who hurt him lately (by unit ID), for kill shares

    /// <summary>How badly he is hurt.</summary>
    public WoundState Wound { get; internal set; }

    /// <summary>While down: ticks of bleeding left before he dies.</summary>
    public int BleedLeft { get; internal set; }

    /// <summary>While down: a rescue's pressure on the wound stops the bleeding until this tick.</summary>
    public uint BleedPausedUntil { get; internal set; }

    /// <summary>While down: the enemy whose hit put him down (the kill is his if he bleeds out), or 0.</summary>
    public uint DownedBy { get; internal set; }

    /// <summary>While down: the squadmate carrying him, or 0.</summary>
    public uint Carrier { get; internal set; }

    /// <summary>The downed squadmate he carries, or 0 (he doesn't fire meanwhile).</summary>
    public uint Carrying { get; internal set; }

    /// <summary>Where he carries him.</summary>
    public Cell CarryTo { get; internal set; }

    /// <summary>A medic: the downed man he goes to or treats, or 0.</summary>
    public uint Treating { get; internal set; }

    /// <summary>A medic: ticks of treatment left (0 while he walks there).</summary>
    public int TreatLeft { get; internal set; }

    /// <summary>The body he goes to or kneels at (its <see cref="Body.Id"/>), or 0.</summary>
    public uint Scavenging { get; internal set; }

    /// <summary>Ticks of kneeling at the body left (0 while he walks there).</summary>
    public int ScavengeLeft { get; internal set; }

    /// <summary>Where his current task's walk goes (carry, treat, scavenge): another order there ends the task.</summary>
    internal Cell TaskCell { get; set; }

    /// <summary>Experience points.</summary>
    public int Xp { get; internal set; }

    /// <summary>His rank, from <see cref="Xp"/>.</summary>
    public SoldierRank Rank { get; internal set; }

    /// <summary>The tick an enemy last fired at him (hit or miss), 0 if never.</summary>
    public uint ShotAt { get; internal set; }

    /// <summary>In a battle (he fired or was fired at) that hasn't ended yet.</summary>
    public bool InBattle { get; internal set; }

    /// <summary>The last tick a shot was fired within the battle reach of him (his battle's).</summary>
    internal uint BattleShot { get; set; }

    /// <summary>He was wounded or down during his battle.</summary>
    internal bool BattleHurt { get; set; }

    /// <summary>Whether a task keeps him from firing and from his squad slot: carrying, treating or scavenging.</summary>
    public bool Busy => Carrying != 0 || Treating != 0 || Scavenging != 0;

    internal void HashInto(ref WordHash h)
    {
        h.Put4((ulong)Wound | ((ulong)Rank << 8) | (InBattle ? 1UL << 16 : 0) | (BattleHurt ? 1UL << 17 : 0), unchecked((ulong)BleedLeft),
            BleedPausedUntil, DownedBy);
        h.Put4(Carrier, Carrying, unchecked((ulong)(((long)CarryTo.X << 20) ^ (CarryTo.Y & 0xFFFFF))), Treating);
        h.Put4(unchecked((ulong)TreatLeft), Scavenging, unchecked((ulong)ScavengeLeft), unchecked((ulong)(((long)TaskCell.X << 20) ^ (TaskCell.Y & 0xFFFFF))));
        h.Put4(unchecked((ulong)Xp), ShotAt, BattleShot, unchecked((ulong)Damagers.Count));
        foreach (var (u, t) in Damagers)
        {
            h.Put(u);
            h.Put(t);
        }
    }
}

/// <summary>
/// A dead soldier's body (INF-E2a, owner decision 18): it lies for the content's <c>bodies.ticks</c> (at most <c>bodies.max</c>, the
/// oldest going first) and keeps his leftover carried ammo by ammo group, and grenades, for scavenging. Hashed.
/// </summary>
public sealed class Body
{
    internal readonly int[] AmmoBy = new int[(int)AmmoGroup.Launcher + 1];

    internal Body(uint id, byte owner, int archetype, Vec pos, uint until) => (Id, Owner, Archetype, Pos, Until) = (id, owner, archetype, pos, until);

    /// <summary>The dead soldier's unit ID (a scavenge order's target).</summary>
    public uint Id { get; }

    /// <summary>His player.</summary>
    public byte Owner { get; }

    /// <summary>His archetype's index.</summary>
    public int Archetype { get; }

    /// <summary>Where he lies.</summary>
    public Vec Pos { get; }

    /// <summary>The tick the body goes.</summary>
    public uint Until { get; }

    /// <summary>Rounds left on him by ammo group (index: <see cref="AmmoGroup"/>; None unused).</summary>
    public IReadOnlyList<int> Ammo => AmmoBy;

    /// <summary>Grenades left on him (the engine has no grenades yet: 0).</summary>
    public int Grenades { get; internal set; }

    internal void HashInto(ref WordHash h)
    {
        h.Put4(Id, Owner, unchecked((ulong)Archetype), Until);
        h.PutSigned(Pos.X.Raw);
        h.PutSigned(Pos.Y.Raw);
        foreach (var a in AmmoBy)
        {
            h.PutSigned(a);
        }
        h.PutSigned(Grenades);
    }
}

/// <summary>What happened to a soldier (<see cref="World.SoldierEvents"/>). Values are stable; new kinds are appended.</summary>
public enum SoldierEventKind : byte
{
    /// <summary>He is wounded (<see cref="SoldierEvent.Value"/>: his HP).</summary>
    Wounded,
    /// <summary>He went down (<see cref="SoldierEvent.Other"/>: the enemy that put him down, or 0).</summary>
    Down,
    /// <summary>He bled out and died.</summary>
    BledOut,
    /// <summary>A medic (<see cref="SoldierEvent.Other"/>) treated him: back to wounded.</summary>
    Treated,
    /// <summary>A squadmate (<see cref="SoldierEvent.Unit"/>) picked up the downed man <see cref="SoldierEvent.Other"/> to carry him.</summary>
    CarryStart,
    /// <summary>He put him down (<see cref="SoldierEvent.Value"/>: 1 at cover, the bleeding stopped for a while; 0 dropped).</summary>
    CarryStop,
    /// <summary>He took ammo from body <see cref="SoldierEvent.Other"/> (<see cref="SoldierEvent.Value"/>: rounds taken).</summary>
    Scavenged,
    /// <summary>He ranked up (<see cref="SoldierEvent.Value"/>: the new <see cref="SoldierRank"/>).</summary>
    RankUp,
    /// <summary>Squad <see cref="SoldierEvent.Other"/> (<see cref="SoldierEvent.Unit"/>: its leader) started a retreat to <see cref="SoldierEvent.At"/>.</summary>
    RetreatStart,
    /// <summary>Squad <see cref="SoldierEvent.Other"/>'s retreat ended (<see cref="SoldierEvent.Value"/>: 1 there, holding; 0 another order).</summary>
    RetreatEnd,
    /// <summary>He healed back above the wounded share.</summary>
    Healed,
    /// <summary>A body (<see cref="SoldierEvent.Unit"/>: the dead soldier) lies at <see cref="SoldierEvent.At"/>.</summary>
    BodyLies,
    /// <summary>A body went (its time was up, or the most were on the map).</summary>
    BodyGone,
    /// <summary>A medic (<see cref="SoldierEvent.Unit"/>) knelt to treat the downed man <see cref="SoldierEvent.Other"/>.</summary>
    TreatStart,
    /// <summary>He knelt at body <see cref="SoldierEvent.Other"/> to scavenge.</summary>
    ScavengeStart,
}

/// <summary>One soldier event of the last tick (INF-E2a).</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Unit">The soldier (see the kind).</param>
/// <param name="Other">The other unit, body or squad (see the kind), or 0.</param>
/// <param name="Value">A number (see the kind).</param>
/// <param name="At">Where.</param>
public readonly record struct SoldierEvent(SoldierEventKind Kind, uint Unit, uint Other, int Value, Vec At);

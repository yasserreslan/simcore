using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// INF-E1b-4 part 3: reactions to indirect fire and air attacks (battle rules 14.4), a loose soldier's one-man morale (brief 7.3: "a
// one-man squad"), and the cornered routing man (12.4). Content: infantryCombat.reactions (and morale's numbers for the one-man squad).
public sealed partial class World
{
    private bool _airBlast;     // the blast being noted is an air attack's (AirStrikeSystem's runs): 14.4's aircraft line
    private bool _grenadeBlast; // a grenade's: not indirect fire (its reflex is 14.3's)

    // NoteImpact (14.4), from NoteBlast for a blast with a danger zone: indirect fire (a mortar or artillery shell, a bomb) or an air
    // attack's. Each squad with a member within impactNear of it (airNear for an air attack) spreads to fireSpacing while it lasts; the
    // first impact is the squad's (ImpactAt, for impactTicks). Its men think at once (they leave the zone by section 6, InfantryThink).
    // An air attack: a man in the open (no cover toward it, not in a building) goes prone for airTicks if he may (AirProneUntil).
    private void NoteImpact(Vec at, InfantryCombatRules r)
    {
        if (_grenadeBlast)
        {
            return;
        }
        var near = (long)r[_airBlast ? CombatValue.ReAirNear : CombatValue.ReImpactNear] * Fixed.OneRaw / 10;
        var last = (uint)(_airBlast ? r[CombatValue.ReAirTicks] : r[CombatValue.ZoneImpactTicks]);
        foreach (var s in Around(at.X.Raw - near, at.Y.Raw - near, at.X.Raw + near, at.Y.Raw + near, _nearShot, true))
        {
            if (s.Fight is not { } f || !s.Alive || s.Owner == 0 || (s.Pos - at).LenSq() > near * near)
            {
                continue;
            }
            f.Urgent = true;
            if (s.Squad != 0 && Squad(s.Squad)?.Mind is { } m)
            {
                if (!_airBlast && Tick >= m.ImpactUntil)
                {
                    m.ImpactAt = Cell.Of(at); // the first impact (a later one within its time keeps it)
                }
                m.ImpactUntil = _airBlast ? m.ImpactUntil : Tick + last;
                m.SpreadUntil = Math.Max(m.SpreadUntil, Tick + last);
            }
            var c = Cell.Of(s.Pos);
            if (_airBlast && s.Inside == 0 && s.Garrison == 0 && !InCoverToward(c, Dir32(at.X.Raw - s.Pos.X.Raw, at.Y.Raw - s.Pos.Y.Raw), r))
            {
                f.AirProneUntil = Tick + last;
            }
        }
    }

    // NoteAirBlast: NoteBlast for an air attack's blast (the tests' way in; AirStrikeSystem sets _airBlast round its Explode).
    internal void NoteAirBlast(Vec at, Fixed splash)
    {
        _airBlast = true;
        NoteBlast(at, splash);
        _airBlast = false;
    }

    // Spread (14.4): his squad is under indirect fire or an air attack: its men keep fireSpacing apart (6.3's minSpacing).
    private bool Spread(Unit u) => u.Squad != 0 && Squad(u.Squad)?.Mind is { } m && Tick < m.SpreadUntil;

    // ---- A loose soldier's morale (brief 7.3: "Soldiers not in a squad use a one-man squad") ----

    // TickLooseMorale: every squadEvery ticks of his think, the squad's rates for one man: pinned (60+ for over pinnedTicks), quiet
    // (no shot near or hit on him for quietTicks), a friendly medic, tank or IFV within aidReach, in a garrison (the last two only
    // while he is not under effective fire); the rates restore to morale.start, no higher. The events (wounded, down, an enemy he
    // killed) come through LooseMoraleChange. Then his state (LooseState).
    private void TickLooseMorale(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (u.Squad != 0 || (f.LooseMoraleTick != 0 && Tick - f.LooseMoraleTick < (uint)r[CombatValue.SquadEvery]))
        {
            return;
        }
        var dt = f.LooseMoraleTick == 0 ? 0 : (int)Math.Min(Tick - f.LooseMoraleTick, 40u);
        f.LooseMoraleTick = Tick;
        long v = f.LooseMorale100 < 0 ? r[CombatValue.MoStart] * 100 : f.LooseMorale100;
        if (dt > 0 && Able(u))
        {
            long d = 0, up = 0;
            if (f.Suppression >= r[CombatValue.Pinned])
            {
                f.PinnedSince = f.PinnedSince == 0 ? Tick : f.PinnedSince;
                d -= Tick - f.PinnedSince > (uint)r[CombatValue.MoPinnedTicks] ? (long)r[CombatValue.MoPinnedRate] * 5 * dt : 0;
            }
            else
            {
                f.PinnedSince = 0;
            }
            var last = Math.Max(f.ShotNear1, u.LastHitTick);
            var fire = (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
                (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]);
            up += last == 0 || Tick - last >= (uint)r[CombatValue.MoQuietTicks] ? (long)r[CombatValue.MoQuietRate] * 5 * dt : 0;
            up += !fire && LooseAid(u, r) ? (long)r[CombatValue.MoAidRate] * 5 * dt : 0;
            up += !fire && u.Garrison != 0 ? (long)r[CombatValue.MoGarrisonRate] * 5 * dt : 0;
            v = Math.Clamp(v + d, 0, 10000);
            var cap = r[CombatValue.MoStart] * 100L;
            v = v < cap ? Math.Min(cap, v + up) : v;
        }
        f.LooseMorale100 = (int)v;
        LooseState(u, f, r);
    }

    // LooseMoraleChange: an event's points (brief 7.3's table) to a loose soldier's own morale; his state follows at once.
    private void LooseMoraleChange(Unit u, int points)
    {
        if (u.Squad != 0 || u.Fight is not { } f || Combat is not { } r || !_fights || points == 0)
        {
            return;
        }
        var v = f.LooseMorale100 < 0 ? r[CombatValue.MoStart] * 100 : f.LooseMorale100;
        f.LooseMorale100 = Math.Clamp(v + points * 100, 0, 10000);
        LooseState(u, f, r);
    }

    // LooseState (brief 7.3, 12.4): the squad's thresholds for one man (he leads himself: rallyLed if he is a Veteran or Elite); with no
    // order a broken man is cowed (returns fire only) and a routing one never fires; he has no squad's safety point to run to, so he
    // is cornered (ThinkCornered: prone, no fire) until he rallies or gets an order.
    private void LooseState(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var v = Math.Max(0, f.LooseMorale100) / 100;
        var rally = u.Life is { Rank: SoldierRank.Veteran or SoldierRank.Elite } ? r[CombatValue.MoRallyLed] : r[CombatValue.MoRally];
        var spirit = v < r[CombatValue.MoBrokenBelow] || (f.Spirit == MoraleState.Broken && v < rally) ? MoraleState.Broken
            : v >= r[CombatValue.MoSteadyAt] ? MoraleState.Steady : MoraleState.Shaken;
        var routing = v < r[CombatValue.MoRoutBelow] || (f.Routs && v < r[CombatValue.MoRoutEnd]);
        var free = u.Order.Kind == OrderKind.Idle;
        if (spirit != f.Spirit)
        {
            f.Urgent = true;
        }
        (f.Spirit, f.Routs, f.Cowed) = (spirit, routing && free, spirit == MoraleState.Broken && free);
    }

    // LooseAid: a friendly medic (not down), tank or IFV within aidReach of a loose soldier (brief 7.3's +1/s; he is his own leader,
    // which the table doesn't count for one man).
    private bool LooseAid(Unit u, InfantryCombatRules r)
    {
        var reach = (long)r[CombatValue.MoAidReach] * Fixed.OneRaw / 10;
        foreach (var o in Around(u.Pos.X.Raw - reach, u.Pos.Y.Raw - reach, u.Pos.X.Raw + reach, u.Pos.Y.Raw + reach, _nearShot, true))
        {
            if (o == u || o.Owner != u.Owner || !o.Alive || (o.Pos - u.Pos).LenSq() > reach * reach)
            {
                continue;
            }
            if (o.Life is null ? (o.Def.Tags.Bits & r.ArmourTags.Bits) != 0 : Content.Soldiers is { } sr && sr.IsMedic(o.Def.Tags) && !IsDown(o))
            {
                return true;
            }
        }
        return false;
    }

    // ThinkCornered (12.4): a routing man fired on (effective fire) with an enemy within cornerReach who can't run (pinned hard at
    // cornerPinned or more, or no way: standing with no path, or a loose man with no squad's safety point) drops prone and stops
    // firing (the rout never fires); he runs again when that ends.
    private void ThinkCornered(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (!f.Routs)
        {
            f.Cornered = false;
            return;
        }
        var fire = (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
            (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]);
        var reach = (long)r[CombatValue.ReCornerReach] * Fixed.OneRaw / 10;
        var close = MainContact(u) is { } q && (q - u.Pos).LenSq() <= reach * reach;
        var stuck = f.Suppression >= r[CombatValue.ReCornerPinned] || u.Squad == 0 || (!u.Moving && !u.PathPending);
        f.Cornered = u.Squad == 0 || (fire && close && stuck);
        if (f.Cornered && u.Moving)
        {
            u.ClearPath();
        }
    }
}

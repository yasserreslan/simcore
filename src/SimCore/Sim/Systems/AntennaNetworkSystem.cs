using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The antenna network (the Sentinel's early warning; docs/design.md, "Antenna network"; the game repo's
// docs/plans/antenna-network-spec.md, owner 2026-10-30). Archetypes with a network block are its members (content, generic):
//  - antennas (the antenna tower): they report launches, see drones and link;
//  - hubs (the radar station; the radar truck while set up): radars that report launches and end the chains.
// The rules:
//  - Linking: antennas of one side within either one's linkRange of each other form a chain (ID order, union by the lowest ID); a
//    chain is linked when one of its antennas is within its linkRange of a working hub of its side. Rebuilt whenever the working
//    members change (one is added, lost, ruined, a radar truck sets up or packs up); a pure function of hashed state, not hashed.
//  - Launch reports: at the end of each tick every projectile launched this tick (an ID above the tick's first) of a reported class
//    whose launch point lies within a working member's launchReach of another side is reported to that member's side: the launch
//    point, the launcher, the class and the predicted landing point (the projectile's own point, Projectile.Dest: an arc's, a
//    terminal missile's or a drone's aim; for a cruise missile its next waypoint, refreshed while its side's sensors see it,
//    CruiseState.SeenNow). The launcher is seen by that side for launcherTicks, the launch shown for reportTicks; the report stays
//    while its threat flies. It is Linked (it warns) when a hub or an antenna of a linked chain made it, judged then.
//  - Warnings (AirDefenseSystem.cs, an interceptor's network block: the Patriot and THAAD types) for linked reports: (a) a launcher
//    within its weapon's range of the predicted point engages the threat once it is in reach without its radar's track; (b) with a
//    defendRadius it engages it only when the predicted point lay within that radius of its side's units or structures when the
//    warning came (NearestSq, judged again only when a cruise missile's predicted point moves); (c) it leaves the classes of its
//    leave list to its side's leaveTo launchers while one of them can take the threat, and takes its other classes first.
//  - Drones: an antenna sees flying drones of its droneClasses within its droneReach (slant from its top, by the target's signature)
//    for its side: they count as tracked (team cueing, World.AirTracked; AirDefenseSystem.CueBody) and, under fog, a drone unit is
//    spotted (VisionSystem). An unlinked antenna still reports and sees drones; it only does not warn.
// Hashed state: the launch reports (only once one was made). The links and flags are scratch derived from the units.
public sealed partial class World
{
    // Whether any unit ever added is a network member: while none is, the system costs nothing. Not hashed (derived from the units).
    private bool _network;

    // The members ever added, in ID order (they are appended as they are made; the dead and the cancelled sites are dropped when the
    // network is checked). Not hashed.
    private readonly List<Unit> _netUnits = [];

    // Scratch, a pure function of hashed state (the working members, their places and owners): the working members in ID order with
    // their chain and linked flag, the links, and the key they were built from (rebuilt only when it changes). _netTick: the tick
    // they were last checked.
    private readonly List<NetworkMember> _netMembers = [];
    private readonly List<NetworkLink> _netLinks = [];
    private readonly List<(uint Id, long X, long Y, byte Owner, bool Hub)> _netKey = [];
    private readonly List<(uint Id, long X, long Y, byte Owner, bool Hub)> _netKeyNow = [];
    private long _netTick = -1;

    // Scratch, per tick: the launchers that take warnings (an interceptor with a network block), for rule (c).
    private readonly List<(Unit Unit, int Weapon)> _netLaunchers = [];
    private long _netLaunchersTick = -1;

    // The launch reports, sorted by threat then player: hashed (HashLaunchReports).
    private readonly List<LaunchReport> _launchReports = [];

    // The first ID of this tick (_nextId when Step began): a projectile with a higher ID was launched this tick. Not hashed: it is
    // the hashed _nextId at the start of the tick.
    private uint _tickFirstId;

    /// <summary>
    /// The launch reports (docs/design.md, "Antenna network"), sorted by threat (projectile ID) then the player they were made to:
    /// one per threat and side whose network saw the launch. A report stays while its threat flies and until
    /// <see cref="LaunchReport.Until"/>; the client shows the launch point while <see cref="Tick"/> is below that.
    /// </summary>
    public IReadOnlyList<LaunchReport> LaunchReports => _launchReports;

    /// <summary>The report of threat <paramref name="threat"/> (a projectile ID) made to <paramref name="player"/>, or null.</summary>
    public LaunchReport? LaunchReportOf(byte player, uint threat)
    {
        int lo = 0, hi = _launchReports.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var m = _launchReports[mid];
            if (m.Threat < threat || (m.Threat == threat && m.Player < player))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _launchReports.Count && _launchReports[lo].Threat == threat && _launchReports[lo].Player == player ? _launchReports[lo] : null;
    }

    /// <summary>
    /// The network's links now, for the client's lines: every pair of working members of one side that link (two antennas within
    /// either one's link range; an antenna and a hub within the antenna's), the lower ID first, sorted. Recomputed when read.
    /// </summary>
    public IReadOnlyList<NetworkLink> NetworkLinks
    {
        get
        {
            EnsureNetwork();
            return _netLinks;
        }
    }

    /// <summary>
    /// The working network members now (antennas built and standing; hubs whose radar is on: a radar truck set up) in ID order, with
    /// their chain and whether they warn. Recomputed when read.
    /// </summary>
    public IReadOnlyList<NetworkMember> NetworkMembers
    {
        get
        {
            EnsureNetwork();
            return _netMembers;
        }
    }

    /// <summary>
    /// Whether network member <paramref name="u"/> warns its side's missile defence now: a working hub, or an antenna of a chain
    /// linked to one. False for anything else (an unlinked antenna, a packed-up radar truck, a dead member).
    /// </summary>
    public bool NetworkLinked(Unit u) => MemberOf(u) is { Linked: true };

    /// <summary>The chain antenna <paramref name="u"/> belongs to (its lowest antenna ID), or 0 (not a working antenna).</summary>
    public uint NetworkChain(Unit u) => MemberOf(u) is { } m ? m.Chain : 0;

    /// <summary>
    /// The launchers of <paramref name="r"/>'s side warned of its threat now (warning rule a: they take warnings and stand within their
    /// weapon's range of the predicted point), in ID order; empty for an unlinked report or a threat no longer flying. For the client's
    /// "turns toward it": the box faces <see cref="LaunchReport.Predicted"/>.
    /// </summary>
    public List<Unit> WarnedLaunchers(LaunchReport r)
    {
        var list = new List<Unit>();
        if (!r.Linked || ProjectileById(r.Threat) is not { Done: false })
        {
            return list;
        }
        foreach (var u in _units)
        {
            if (u.Owner == r.Player && u.Alive && WarnedWeapon(u) is { } w && w.Effect.Interceptor!.Engages(r.Class) && Within(u.Pos, r.Predicted, w.Range))
            {
                list.Add(u);
            }
        }
        return list;
    }

    /// <summary>The linked reports launcher <paramref name="u"/> is warned of now (its side's, in its reach), in threat order.</summary>
    public List<LaunchReport> LauncherWarnings(Unit u)
    {
        var list = new List<LaunchReport>();
        if (WarnedWeapon(u) is not { } w)
        {
            return list;
        }
        foreach (var r in _launchReports)
        {
            if (r.Player == u.Owner && r.Linked && w.Effect.Interceptor!.Engages(r.Class) && Within(u.Pos, r.Predicted, w.Range) &&
                ProjectileById(r.Threat) is { Done: false })
            {
                list.Add(r);
            }
        }
        return list;
    }

    /// <summary>
    /// What warning rule applies to launcher <paramref name="u"/> and report <paramref name="r"/> now: none (not warned), engage (rule
    /// a), harmless (rule b: its predicted point lay beyond the launcher's defend radius of its side), or left to others (rule c).
    /// </summary>
    public NetworkWarning WarningFor(Unit u, LaunchReport r)
    {
        if (WarnedWeapon(u) is not { } w || !r.Linked || r.Player != u.Owner || !w.Effect.Interceptor!.Engages(r.Class) ||
            ProjectileById(r.Threat) is not { Done: false } p)
        {
            return NetworkWarning.None;
        }
        var ic = w.Effect.Interceptor!;
        if (ic.DefendRadiusRaw != 0 && r.NearestSq > ic.DefendRadiusRaw * ic.DefendRadiusRaw)
        {
            return NetworkWarning.Harmless;
        }
        if (LeftToOthers(u, ic, new AirBody(p)))
        {
            return NetworkWarning.LeftToOthers;
        }
        return Within(u.Pos, r.Predicted, w.Range) ? NetworkWarning.Engage : NetworkWarning.None;
    }

    /// <summary>
    /// The air targets antenna <paramref name="antenna"/> sees now as drones (projectiles and flying units of its drone classes within
    /// its drone reach, enemies only), in ID order: what it shows its side through the fog. Empty for anything but a working antenna.
    /// </summary>
    public List<uint> AntennaDrones(Unit antenna)
    {
        var list = new List<uint>();
        if (antenna.Def.Archetype.Network is not { Hub: false } || !NetWorks(antenna))
        {
            return list;
        }
        var eye = SensorEyeRaw(antenna);
        foreach (var p in _projectiles)
        {
            if (!p.Done && p.Interceptible != InterceptClass.None && p.Owner != antenna.Owner && AntennaSees(antenna, eye, new AirBody(p)))
            {
                list.Add(p.Id);
            }
        }
        foreach (var t in _units)
        {
            if (t.Owner != antenna.Owner && t.Alive && AirBodyOfUnit(t) is { } b && AntennaSees(antenna, eye, b))
            {
                list.Add(t.Id);
            }
        }
        list.Sort();
        return list;
    }

    // NoteNetwork (World.Add): a network member joins the member list (IDs grow, so it stays in ID order).
    private void NoteNetwork(Unit u)
    {
        if (u.Def.Archetype.Network is not null)
        {
            _network = true;
            _netUnits.Add(u);
        }
    }

    // NetWorks: member u works now: an antenna built and standing (alive, not rubble, not inside); a hub whose radar is on (RadarOn:
    // the radar truck only set up).
    private bool NetWorks(Unit u) =>
        u.Def.Archetype.Network is { } n && u.Owner != 0 && (n.Hub ? RadarOn(u) : u.Alive && u.IsBuilt && !u.Rubble && u.Inside == 0);

    private NetworkMember? MemberOf(Unit u)
    {
        if (u.Def.Archetype.Network is null)
        {
            return null;
        }
        EnsureNetwork();
        int lo = 0, hi = _netMembers.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_netMembers[mid].Id < u.Id)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < _netMembers.Count && _netMembers[lo].Id == u.Id ? _netMembers[lo] : null;
    }

    // EnsureNetwork brings the links and chains up to date (once a tick, rebuilt only when the working members changed).
    private void EnsureNetwork()
    {
        if (_netTick == Tick)
        {
            return;
        }
        _netTick = Tick;
        _netUnits.RemoveAll(u => !u.Alive || Unit(u.Id) != u); // dead, or a site cancelled (taken off the map)
        _netKeyNow.Clear();
        foreach (var u in _netUnits)
        {
            if (NetWorks(u))
            {
                _netKeyNow.Add((u.Id, u.Pos.X.Raw, u.Pos.Y.Raw, u.Owner, u.Def.Archetype.Network!.Hub));
            }
        }
        if (_netKeyNow.Count == _netKey.Count)
        {
            var same = true;
            for (var i = 0; i < _netKey.Count && same; i++)
            {
                same = _netKey[i] == _netKeyNow[i];
            }
            if (same)
            {
                return;
            }
        }
        _netKey.Clear();
        _netKey.AddRange(_netKeyNow);
        RebuildNetwork();
    }

    // RebuildNetwork links the working members (_netKey, ID order): antennas to antennas within either one's link range, antennas to
    // hubs within the antenna's; chains by union (the root is the lowest index, so the chain's ID is its lowest antenna ID); a chain
    // with a hub link is linked.
    private void RebuildNetwork()
    {
        _netLinks.Clear();
        _netMembers.Clear();
        var n = _netKey.Count;
        var root = new int[n];
        var hubbed = new bool[n];
        var units = new Unit[n];
        for (var i = 0; i < n; i++)
        {
            root[i] = i;
            units[i] = Unit(_netKey[i].Id)!;
        }
        int Find(int i)
        {
            while (root[i] != i)
            {
                root[i] = root[root[i]];
                i = root[i];
            }
            return i;
        }
        for (var i = 0; i < n; i++)
        {
            var a = _netKey[i];
            var na = units[i].Def.Archetype.Network!;
            for (var j = i + 1; j < n; j++)
            {
                var b = _netKey[j];
                if (b.Owner != a.Owner || (a.Hub && b.Hub))
                {
                    continue;
                }
                var nb = units[j].Def.Archetype.Network!;
                var reach = a.Hub ? nb.LinkRangeRaw : b.Hub ? na.LinkRangeRaw : Math.Max(na.LinkRangeRaw, nb.LinkRangeRaw);
                long dx = b.X - a.X, dy = b.Y - a.Y;
                if (dx * dx + dy * dy > reach * reach)
                {
                    continue;
                }
                _netLinks.Add(new NetworkLink(a.Id, b.Id, a.Hub || b.Hub));
                if (a.Hub)
                {
                    hubbed[j] = true;
                }
                else if (b.Hub)
                {
                    hubbed[i] = true;
                }
                else
                {
                    var (ri, rj) = (Find(i), Find(j));
                    if (ri != rj)
                    {
                        root[Math.Max(ri, rj)] = Math.Min(ri, rj);
                    }
                }
            }
        }
        var linked = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (hubbed[i])
            {
                linked[Find(i)] = true;
            }
        }
        for (var i = 0; i < n; i++)
        {
            var k = _netKey[i];
            _netMembers.Add(k.Hub ? new NetworkMember(k.Id, k.Owner, true, 0, true) : new NetworkMember(k.Id, k.Owner, false, _netKey[Find(i)].Id, linked[Find(i)]));
        }
    }

    // RunNetwork (the end of each tick, after every system that launches): reports past their time and with their threat gone are
    // dropped, cruise missiles' predicted points are refreshed while their side sees them, and this tick's launches are reported.
    private void RunNetwork()
    {
        if (!_network)
        {
            return;
        }
        EnsureNetwork();
        _launchReports.RemoveAll(r => r.Until <= Tick + 1 && ProjectileById(r.Threat) is not { Done: false });
        foreach (var r in _launchReports)
        {
            if (ProjectileById(r.Threat) is { Done: false, Cruise: { } c } p && (c.SeenNow & Bit(r.Player)) != 0)
            {
                var next = CruiseNextPoint(p, c);
                if (next != r.Predicted)
                {
                    r.Predicted = next;
                    r.NearestSq = NearestOwnSq(r.Player, next);
                }
            }
        }
        if (_netMembers.Count == 0)
        {
            return;
        }
        var players = _players.Count;
        foreach (var p in _projectiles)
        {
            if (p.Id <= _tickFirstId || p.Done || p.Interceptible == InterceptClass.None)
            {
                continue;
            }
            for (var pl = 1; pl <= players; pl++)
            {
                if (pl != p.Owner)
                {
                    Report(p, (byte)pl);
                }
            }
        }
    }

    // Report reports launch p to player pl if a working member of his within its launch reach of the launch point reports its class:
    // the detector a linked member (a hub, an antenna of a linked chain) if any, the lowest ID among them; else the lowest ID.
    private void Report(Projectile p, byte pl)
    {
        NetworkMember? best = null;
        NetworkNode? node = null;
        foreach (var m in _netMembers)
        {
            if (m.Owner != pl || (best is { } b0 && (b0.Linked || !m.Linked)))
            {
                continue; // another side's, or no better than the one found (members come in ID order)
            }
            var u = Unit(m.Id)!;
            var nn = u.Def.Archetype.Network!;
            if (nn.Reports(p.Interceptible) && (p.Launched - u.Pos).LenSq() <= nn.LaunchReachRaw * nn.LaunchReachRaw)
            {
                (best, node) = (m, nn);
            }
        }
        if (best is not { } d)
        {
            return;
        }
        var predicted = p.Cruise is { } c ? CruiseNextPoint(p, c) : p.Dest;
        _launchReports.Add(new LaunchReport(p.Id, pl, p.Interceptible, p.Shooter, p.Launched, Tick, d.Id, d.Linked,
            Tick + 1 + (uint)node!.LauncherTicks, Tick + 1 + (uint)node.ReportTicks)
        {
            Predicted = predicted,
            NearestSq = NearestOwnSq(pl, predicted),
        });
    }

    // NearestOwnSq is the squared distance (raw) from point `at` to the nearest unit or structure (its footprint) of player pl standing
    // outside, or long.MaxValue when he has none: the defended-area test of warning rule b (Threatened's measure, every unit).
    private long NearestOwnSq(byte pl, Vec at)
    {
        var best = long.MaxValue;
        foreach (var t in _units)
        {
            if (t.Owner == pl && t.Alive && t.Inside == 0)
            {
                best = Math.Min(best, (AimPoint(at, t) - at).LenSq());
                if (best == 0)
                {
                    break;
                }
            }
        }
        return best;
    }

    // CruiseNextPoint is cruise missile p's next waypoint as its side would judge it: the end of the route's leg (launcher, waypoints,
    // target) nearest to where it is now (ties to the later leg).
    private static Vec CruiseNextPoint(Projectile p, CruiseState c)
    {
        var wps = c.Route.Waypoints;
        var from = p.Origin;
        var best = Int128.MaxValue;
        var next = c.Route.Target.Center();
        for (var i = 0; i <= wps.Count; i++)
        {
            var to = i < wps.Count ? wps[i].Center() : c.Route.Target.Center();
            var d = SegmentDist2(p.Pos, from, to);
            if (d <= best)
            {
                (best, next) = (d, to);
            }
            from = to;
        }
        return next;
    }

    // SegmentDist2 is the squared distance (raw) from q to the segment a-b, exactly.
    private static Int128 SegmentDist2(Vec q, Vec a, Vec b)
    {
        long ux = b.X.Raw - a.X.Raw, uy = b.Y.Raw - a.Y.Raw, qx = q.X.Raw - a.X.Raw, qy = q.Y.Raw - a.Y.Raw;
        var len2 = (Int128)ux * ux + (Int128)uy * uy;
        var dot = (Int128)qx * ux + (Int128)qy * uy;
        if (len2 == 0 || dot <= 0)
        {
            return (Int128)qx * qx + (Int128)qy * qy;
        }
        if (dot >= len2)
        {
            long ex = q.X.Raw - b.X.Raw, ey = q.Y.Raw - b.Y.Raw;
            return (Int128)ex * ex + (Int128)ey * ey;
        }
        var cross = (Int128)ux * qy - (Int128)uy * qx;
        return cross * cross / len2;
    }

    // LinkedReport is the linked (warning) report of threat id to player owner, or null.
    private LaunchReport? LinkedReport(byte owner, uint id) =>
        _launchReports.Count > 0 && LaunchReportOf(owner, id) is { Linked: true } r ? r : null;

    // WarnedWeapon is unit u's interceptor weapon when it takes warnings (a network block: the Patriot and THAAD types), else null.
    private static WeaponDef? WarnedWeapon(Unit u)
    {
        if (u.Def.Archetype.Flight is not null)
        {
            return null;
        }
        var wi = InterceptorWeapon(u.Def);
        return wi >= 0 && u.Def.Weapons[wi].Effect.Interceptor!.Network is not null ? u.Def.Weapons[wi] : null;
    }

    // Warned (warning rule a): launcher u with weapon w is warned of air target p: its side's linked network reported it and u stands
    // within w's range of the predicted point.
    private bool Warned(Unit u, WeaponDef w, Interceptor ic, AirBody p) =>
        ic.Network is not null && p.P is not null && LinkedReport(u.Owner, p.Id) is { } r && Within(u.Pos, r.Predicted, w.Range);

    // WarnedDefended (warning rule b): for a threat its side's linked network reported, whether interceptor ic (with a defendRadius)
    // engages it: the predicted point lay within the radius of its side when the warning came; null when no such report exists.
    private bool? WarnedDefended(Unit u, Interceptor ic, Projectile p) =>
        ic.Network is not null && LinkedReport(u.Owner, p.Id) is { } r ? r.NearestSq <= ic.DefendRadiusRaw * ic.DefendRadiusRaw : null;

    // Deferred (warning rule c): interceptor ic leaves p's class to others and p was reported to its side by a linked network; it takes
    // such a threat only when no launcher it leaves to can (LeftToOthers), and after its other classes.
    private bool Deferred(Unit u, Interceptor ic, AirBody p) =>
        ic.Network is { Leave: not 0 } nw && nw.Leaves(p.Class) && p.P is not null && LinkedReport(u.Owner, p.Id) is not null;

    // LeftToOthers (warning rule c): p is deferred and a launcher of u's side with ic's leaveTo tags can take it: guarding, set up,
    // loaded, engaging its class and within its weapon's range of the predicted point.
    private bool LeftToOthers(Unit u, Interceptor ic, AirBody p)
    {
        if (!Deferred(u, ic, p) || LinkedReport(u.Owner, p.Id) is not { } r)
        {
            return false;
        }
        if (_netLaunchersTick != Tick)
        {
            _netLaunchersTick = Tick;
            _netLaunchers.Clear();
            foreach (var o in _units)
            {
                if (WarnedWeapon(o) is not null)
                {
                    _netLaunchers.Add((o, InterceptorWeapon(o.Def)));
                }
            }
        }
        var tags = ic.Network!.LeaveTo;
        foreach (var (o, wi) in _netLaunchers)
        {
            if (o == u || o.Owner != u.Owner || !o.Alive || !o.CanAct || !SetUp(o) || !GuardsAir(o) || !o.Def.Tags.HasAll(tags))
            {
                continue;
            }
            var w = o.Def.Weapons[wi];
            if (w.Effect.Interceptor!.Engages(r.Class) && HasAmmo(o, w) && Within(o.Pos, r.Predicted, w.Range))
            {
                return true;
            }
        }
        return false;
    }

    // AntennaSees: antenna s (eye at eye) sees air target p as a drone: one of its drone classes within its drone reach, slant, scaled
    // by the target's signature (detect percent).
    private static bool AntennaSees(Unit s, long eye, AirBody p)
    {
        if (s.Def.Archetype.Network is not { Hub: false } n || !n.SeesDrone(p.Class) || p.Gone)
        {
            return false;
        }
        var d = p.Signature.Detect;
        return SlantWithin(Slant2(s.Pos, eye, p), d == 100 ? n.DroneReachRaw : n.DroneReachRaw * d / 100);
    }

    // SpotDronesFromAntennas (vision, under fog, after the air radars): every working antenna spots for its side the enemy drone units it
    // sees (AntennaSees). It only ORs bits.
    private void SpotDronesFromAntennas()
    {
        foreach (var o in _netUnits)
        {
            if (o.Def.Archetype.Network is not { Hub: false } n || !NetWorks(o))
            {
                continue;
            }
            var bit = Bit(o.Owner);
            var eye = SensorEyeRaw(o);
            foreach (var t in AroundFrom(o, n.DroneReachRaw, _nearSee, false))
            {
                if ((t.SpottedBy & bit) == 0 && t.Owner != o.Owner && InAir(t) && AirBodyOfUnit(t) is { } b && AntennaSees(o, eye, b))
                {
                    t.SpottedBy |= bit;
                }
            }
        }
    }

    // RevealReportedLaunchers (vision, after the shot reveals): a reported launcher is spotted by the side it was reported to for the
    // report's launcherTicks, through fog or smoke. It only ORs bits.
    private void RevealReportedLaunchers()
    {
        foreach (var r in _launchReports)
        {
            if (r.LauncherUntil > Tick && Unit(r.Launcher) is { Alive: true, Inside: 0 } u && u.Owner != r.Player)
            {
                u.SpottedBy |= Bit(r.Player);
            }
        }
    }

    // HashLaunchReports mixes in the launch reports, only once one was made (World.Hash and HashReference word by word).
    private void HashLaunchReports(ref WordHash h)
    {
        if (_launchReports.Count == 0)
        {
            return;
        }
        h.Put(0xA27E);
        h.PutSigned(_launchReports.Count);
        foreach (var r in _launchReports)
        {
            h.Put(r.Threat);
            h.Put(r.Player);
            h.Put((byte)r.Class);
            h.Put(r.Launcher);
            h.PutSigned(r.LaunchPoint.X.Raw);
            h.PutSigned(r.LaunchPoint.Y.Raw);
            h.PutSigned(r.Predicted.X.Raw);
            h.PutSigned(r.Predicted.Y.Raw);
            h.Put(r.Tick);
            h.Put(r.Detector);
            h.Put(r.Linked ? 1UL : 0);
            h.Put(r.LauncherUntil);
            h.Put(r.Until);
            h.PutSigned(r.NearestSq);
        }
    }
}

/// <summary>
/// A launch the antenna network reported to a side (<see cref="World.LaunchReports"/>; docs/design.md, "Antenna network").
/// </summary>
public sealed class LaunchReport
{
    internal LaunchReport(uint threat, byte player, InterceptClass cls, uint launcher, Vec launchPoint, uint tick, uint detector, bool linked,
        uint launcherUntil, uint until)
    {
        Threat = threat;
        Player = player;
        Class = cls;
        Launcher = launcher;
        LaunchPoint = launchPoint;
        Tick = tick;
        Detector = detector;
        Linked = linked;
        LauncherUntil = launcherUntil;
        Until = until;
    }

    /// <summary>The threat: the projectile launched (its ID).</summary>
    public uint Threat { get; }

    /// <summary>The player it was reported to.</summary>
    public byte Player { get; }

    /// <summary>What it is (rocket, missile, ballistic, hypersonic, cruise, drone): the client's icon.</summary>
    public InterceptClass Class { get; }

    /// <summary>The unit that launched it (which may be gone).</summary>
    public uint Launcher { get; }

    /// <summary>Where it was launched from (its launcher's position then).</summary>
    public Vec LaunchPoint { get; }

    /// <summary>
    /// Where it is predicted to land: its aim point (an arc's, a terminal missile's, a drone's); a cruise missile's next waypoint,
    /// refreshed while its side's sensors see it.
    /// </summary>
    public Vec Predicted { get; internal set; }

    /// <summary>The tick it was reported (the tick of the launch).</summary>
    public uint Tick { get; }

    /// <summary>The member that reported it: a linked one if any saw it, the lowest ID among them.</summary>
    public uint Detector { get; }

    /// <summary>Whether it warns the missile defence: a hub or an antenna of a linked chain reported it (judged at the report).</summary>
    public bool Linked { get; }

    /// <summary>The first tick its launcher is no longer shown to <see cref="Player"/> (the launcher seen for the reporter's launcherTicks).</summary>
    public uint LauncherUntil { get; }

    /// <summary>The first tick its launch point is no longer shown (the reporter's reportTicks); it stays listed while the threat flies.</summary>
    public uint Until { get; }

    /// <summary>
    /// The squared distance (raw) from <see cref="Predicted"/> to the nearest unit or structure of <see cref="Player"/> when it was
    /// predicted (long.MaxValue when he had none): warning rule b compares it with a launcher's defend radius.
    /// </summary>
    public long NearestSq { get; internal set; }

    /// <summary>The distance from <see cref="Predicted"/> to <see cref="Player"/>'s nearest unit or structure when it was predicted.</summary>
    public Fixed Nearest => NearestSq == long.MaxValue ? Fixed.FromRaw(long.MaxValue) : Fixed.FromRaw(Fixed.Sqrt(NearestSq));
}

/// <summary>A link of the antenna network (<see cref="World.NetworkLinks"/>): two working members of one side, the lower ID first.</summary>
/// <param name="From">The lower ID.</param>
/// <param name="To">The higher ID.</param>
/// <param name="Hub">Whether one end is a hub (a radar station, a set-up radar truck); else two antennas.</param>
public readonly record struct NetworkLink(uint From, uint To, bool Hub);

/// <summary>A working member of the antenna network (<see cref="World.NetworkMembers"/>).</summary>
/// <param name="Id">The unit.</param>
/// <param name="Owner">Its side.</param>
/// <param name="Hub">Whether it is a hub (a radar).</param>
/// <param name="Chain">An antenna's chain (its lowest antenna ID); 0 for a hub.</param>
/// <param name="Linked">Whether it warns: a hub, or an antenna whose chain links to a hub.</param>
public readonly record struct NetworkMember(uint Id, byte Owner, bool Hub, uint Chain, bool Linked);

/// <summary>Which warning rule applies to a launcher and a launch report (<see cref="World.WarningFor"/>).</summary>
public enum NetworkWarning : byte
{
    /// <summary>Not warned: it takes no warnings, the report does not warn, is another side's, not its class or out of its reach.</summary>
    None,

    /// <summary>Rule a: warned and in reach of the predicted point: it engages the threat in its reach without its radar's track.</summary>
    Engage,

    /// <summary>Rule b: the predicted point lay beyond its defend radius of its side: it lets the threat go.</summary>
    Harmless,

    /// <summary>Rule c: it leaves the threat's class to a launcher of its side that can take it (the THAAD type to the Patriot type).</summary>
    LeftToOthers,
}

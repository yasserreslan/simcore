using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Replays;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The helicopters (the helicopters night, 2026-10-21; docs/design.md, "Helicopters"): the Sentinel's attack helicopter (an
// AH-64 type), armoured helicopter (a Mi-28 type) and light helicopter (an AH-6 type) on the rotary flight (they hover, turn
// on the spot, fly sideways, follow the terrain low, never need a runway), their pop-up attacks from behind cover, their
// weapons, their toughness through the interception model, the owner's out-of-control standard and its crashes, the light
// helicopter's riders, and the helipad (built, building onto itself, servicing).
public class HelicopterTests(ITestOutputHelper output)
{
    private const string Apache = "attack_helicopter", Havoc = "armored_helicopter", Bird = "light_helicopter";
    private const long Cell1 = Fixed.OneRaw, Metre = 512;

    private static readonly TerrainRules Rules = TerrainLoader.LoadRules(Path.Combine(TestPaths.RepoRoot(), "content", "terrain", "rules.json"));

    // Player 1 Sentinel, player 2 Resistance, no armies.
    private static World Sentinel(int n, ulong seed = 1, bool fog = false) => Arena.NewSentinel(n, seed, fog ? s => s with { Fog = true } : null);

    // Both players Sentinel (helicopters and the Patriot type on both sides), no armies.
    private static World Duel(int n, ulong seed = 1)
    {
        var setup = TestContent.Sentinel(seed).For(TestContent.Mvp, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(TestContent.Mvp, setup with { Players = [p0, p1] }));
    }

    // A helicopter of player p put down in flight at (x, y) (at its height after a tick), nose east.
    private static Unit Fly(World w, string id, int x, int y, byte p = 1)
    {
        var u = w.Spawn(p, id, x, y, Facing.East);
        w.Step();
        return u;
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0, bool on = false) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target, On = on });

    private static double Cells(long raw) => raw / (double)Cell1;

    private static double Metres(long raw) => raw / (double)Metre;

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static long Dist(Vec a, Vec b) => World.Len3(a.X.Raw - b.X.Raw, a.Y.Raw - b.Y.Raw, 0);

    private static RotarySpec Rotor(World w, string id) => w.Content.Archetype(id)!.Flight!.Rotor!;

    // The angle (degrees) between helicopter u's nose and the direction to p.
    private static int NoseOff(Unit u, Vec p)
    {
        var h = u.Heli!;
        long dx = p.X.Raw - u.Pos.X.Raw, dy = p.Y.Raw - u.Pos.Y.Raw;
        var l = Math.Max(1, World.Len3(dx, dy, 0));
        var cos = (dx * h.HeadX + dy * h.HeadY) / l; // 1/16384
        return (int)(Math.Acos(Math.Clamp(cos / 16384.0, -1, 1)) * 180 / Math.PI);
    }

    // A flat map of n cells with a ridge across x = ridgeX: rising `height` metres over `half` cells either side.
    private static TerrainMap Ridge(int n, int ridgeX, int half, int height)
    {
        var corners = new int[(n + 1) * (n + 1)];
        for (var y = 0; y <= n; y++)
        {
            for (var x = 0; x <= n; x++)
            {
                var d = Math.Abs(x - ridgeX);
                corners[y * (n + 1) + x] = d >= half ? 0 : (int)((long)height * Metre * (half - d) / half);
            }
        }
        return new TerrainMap(n, n, corners, new byte[n * n], Rules);
    }

    // ---- Content ----

    [Fact]
    public void TheHelicoptersAreData()
    {
        var c = TestContent.Mvp;
        var w = Sentinel(64);
        foreach (var id in new[] { Apache, Havoc, Bird })
        {
            var a = c.Archetype(id)!;
            Assert.Equal(FlightKind.Rotary, a.Flight!.Kind);
            Assert.Equal(LandingKind.Vertical, a.Aircraft!.Landing);
            Assert.True(c.Faction("sentinel")!.Archetypes[a.Index], $"{id} is the Sentinel's");
            Assert.True(a.Tags.Matches(c.Archetype("helipad")!.Production!.Tags), "a helipad builds it");
            Assert.True(a.Tags.Matches(c.Archetype("airbase_hangar")!.Production!.Tags), "the main air base's hangar builds it");
            Assert.True(a.Flight.Rotor!.OutOfControl.Ticks == 30 * World.TicksPerSecond, "out of control for 30 s");
        }
        var ap = c.Archetype(Apache)!;
        var hv = c.Archetype(Havoc)!;
        var lb = c.Archetype(Bird)!;
        // The owner's numbers: speeds (m/s = cells/s x 2), HP, cost, build time, crew survival.
        Assert.Equal((200, 375L * Cell1 / 10), ((int)(ap.Speed.Raw * 200 / Cell1), ap.Flight!.Rotor!.MaxSpeed));
        Assert.Equal((600, 3500, 60 * World.TicksPerSecond), (ap.MaxHp, ap.Cost, ap.BuildTime));
        Assert.Equal((650, 3000, 55 * World.TicksPerSecond), (hv.MaxHp, hv.Cost, hv.BuildTime));
        Assert.Equal((1500, 30 * World.TicksPerSecond), (lb.Cost, lb.BuildTime));
        Assert.True(lb.Speed > ap.Speed && ap.Speed > hv.Speed, "the light helicopter is the fastest");
        Assert.Equal((2, 50), (ap.Flight.Rotor.Crew.Count, ap.Flight.Rotor.Crew.Survive));
        Assert.Equal((2, 70), (hv.Flight!.Rotor!.Crew.Count, hv.Flight.Rotor.Crew.Survive));
        Assert.Equal((1, 50, 3, 40), (lb.Flight!.Rotor!.Crew.Count, lb.Flight.Rotor.Crew.Survive, lb.Flight.Rotor.Riders!.Seats, lb.Flight.Rotor.Riders.Survive));
        // The mast radar: vehicles out to 120 cells, the Apache type's only.
        Assert.Equal((Fixed.FromInt(120), true), (ap.GroundRadar, ap.GroundRadarVehicles));
        Assert.Equal(Fixed.Zero, hv.GroundRadar);
        // Weapons: missiles 10% weaker on the Havoc type; its cannon ~30% better on soldiers, a bit weaker on vehicles.
        WeaponDef W(string unit, string comp) => w.Player(1)!.Setup.Find(c, unit)!.Weapons.Single(x => c.Component(comp)!.Effects.Contains(x.Effect));
        Assert.Equal(W(Apache, "heli_missiles").Damage * 9 / 10, W(Havoc, "heli_missiles_light").Damage);
        var inf = c.ArmorClasses.ToList().IndexOf("infantry");
        var light = c.ArmorClasses.ToList().IndexOf("light_vehicle");
        int Dmg(WeaponDef x, int armor) => x.Warhead.Damage(x.Damage, armor, 100);
        var (ac, hc) = (W(Apache, "heli_cannon"), W(Havoc, "heli_cannon_he"));
        Assert.InRange(Dmg(hc, inf) * 100 / Dmg(ac, inf), 125, 135);
        Assert.InRange(Dmg(hc, light) * 100 / Dmg(ac, light), 85, 95);
        // The light helicopter's rockets: 50 a hit on a truck (6 bring down the 300-HP heavy rocket truck).
        var truck = c.Archetype("heavy_rocket_truck")!;
        var lr = W(Bird, "light_heli_rockets");
        Assert.Equal(50, Dmg(lr, truck.Armor));
        Assert.Equal(6, (truck.MaxHp + 49) / 50);
        Assert.Equal(8, lr.Effect.Ammo);
        // The helipad: 12 x 12 cells, 30 s, 800; it builds one helicopter onto itself and services.
        var pad = c.Archetype("helipad")!;
        Assert.Equal((12, 12, 30 * World.TicksPerSecond, 800), (pad.FootprintW, pad.FootprintH, pad.BuildTime, pad.Cost));
        Assert.NotNull(pad.Helipad);
        Assert.Equal((1, true), (pad.Production!.QueueMax, pad.Production.IntoStation));
        // The air classes are appended at the end.
        Assert.Equal(["attackheli", "armoredheli", "lightheli"], c.AirClasses.Skip(13).Take(3).Select(x => x.Id)); // (the Kaman's came after)
        Assert.Equal(InterceptClass.AttackHeli, ap.Flight.Class);
        Assert.Equal(InterceptClass.ArmoredHeli, hv.Flight.Class);
        Assert.Equal(InterceptClass.LightHeli, lb.Flight.Class);
    }

    // ---- Flight ----

    [Fact]
    public void ItHoversWhereItIsSent()
    {
        var w = Sentinel(96);
        var u = Fly(w, Apache, 30, 30);
        var f = w.Content.Archetype(Apache)!.Flight!;
        w.Run(40);
        Assert.InRange(Dist(u.Pos, new Cell(30, 30).Center()), 0, Cell1 / 2);
        Assert.Equal(f.HeightRaw, u.Altitude.Raw);
        Issue(w, CommandKind.Move, u, new Cell(60, 30));
        var start = w.Tick;
        var arrived = 0u;
        long top = 0;
        for (var i = 0; i < 400 && arrived == 0; i++)
        {
            w.Step();
            top = Math.Max(top, World.Len3(u.Heli!.VelX, u.Heli.VelY, 0));
            if (u.Order.Kind == OrderKind.Idle)
            {
                arrived = w.Tick - start;
            }
        }
        output.WriteLine($"30 cells in {F(arrived / 20.0)} s, top speed {F(Cells(top))} cells/s");
        Assert.True(arrived > 0, "it arrives");
        Assert.True(top <= Rotor(w, Apache).MaxSpeed);
        // It stops there and hovers: still, over the point, at its height.
        w.Run(60);
        Assert.InRange(Dist(u.Pos, new Cell(60, 30).Center()), 0, Cell1);
        Assert.Equal(0, u.Heli!.VelX);
        Assert.Equal(0, u.Heli.VelY);
        Assert.InRange(u.Altitude.Raw, f.HeightRaw - Metre, f.HeightRaw + Metre);
    }

    [Fact]
    public void ItTurnsOnTheSpot()
    {
        var w = Sentinel(96);
        var u = Fly(w, Apache, 40, 40); // nose east
        w.Run(10);
        var at = u.Pos;
        var r = Rotor(w, Apache);
        // An enemy technical 9 cells behind it (west): idle in its Guard stance it turns to fight it without moving.
        var t = w.Spawn(2, "technical", 31, 40);
        var turned = 0;
        for (var i = 0; i < 80 && turned == 0; i++)
        {
            w.Step();
            Assert.True(Math.Abs(u.Heli!.Yaw) <= r.YawStep.Sin + 1, "never faster than its yaw rate");
            if (NoseOff(u, t.Pos) <= 5)
            {
                turned = i + 1;
            }
        }
        output.WriteLine($"half a turn in {F(turned / 20.0)} s (yaw {r.YawRate} deg/s), drift {F(Cells(Dist(u.Pos, at)))} cells");
        Assert.InRange(turned, 30, 50); // 180 degrees at 90 a second: 2 s (plus the scan's pick)
        Assert.True(Dist(u.Pos, at) <= Cell1, "on the spot");
    }

    [Fact]
    public void ItFliesSidewaysAndBackwards()
    {
        var w = Sentinel(96);
        var u = Fly(w, Apache, 40, 40);
        var r = Rotor(w, Apache);
        // A gun run on a point (attack-ground: rockets and the cannon): it closes, then sidesteps round it, nose on it.
        var p = new Cell(60, 40);
        Issue(w, CommandKind.AttackGround, u, p);
        long side = 0;
        var noseOn = 0;
        for (var i = 0; i < 300; i++)
        {
            w.Step();
            var h = u.Heli!;
            var lat = Math.Abs((-h.VelX * h.HeadY + h.VelY * h.HeadX) / 16384);
            if (NoseOff(u, p.Center()) <= 15)
            {
                noseOn++;
                side = Math.Max(side, lat);
            }
        }
        output.WriteLine($"sideways at up to {F(Cells(side))} cells/s with its nose on the point ({noseOn} ticks of 300), {F(Cells(Dist(u.Pos, p.Center())))} cells out");
        Assert.True(side >= r.SideSpeed / 2, "it flies sideways with its nose on the target");
        Assert.True(side <= r.SideSpeed + r.AccelPerTick, "never faster sideways than its side speed");
        // Backwards: a short move straight behind it is flown tail first (slower than its side speed: no turn).
        var w2 = Sentinel(96);
        var b = Fly(w2, Apache, 40, 40); // nose east
        Issue(w2, CommandKind.Move, b, new Cell(38, 40));
        long back = 0;
        for (var i = 0; i < 60; i++)
        {
            w2.Step();
            back = Math.Min(back, (b.Heli!.VelX * b.Heli.HeadX + b.Heli.VelY * b.Heli.HeadY) / 16384);
        }
        Assert.True(back < 0, "it backs up");
        Assert.True(NoseOff(b, new Cell(60, 40).Center()) <= 5, "nose still east");
    }

    [Fact]
    public void ItFollowsTheTerrainLow()
    {
        var w = Sentinel(128);
        w.AttachTerrain(Ridge(128, 64, 30, 40)); // a 40 m ridge, 120 m across
        var u = Fly(w, Apache, 20, 64);
        var f = w.Content.Archetype(Apache)!.Flight!;
        Issue(w, CommandKind.Move, u, new Cell(108, 64));
        w.Step();
        long lo = long.MaxValue, hi = 0, maxGround = 0;
        for (var i = 0; i < 600 && u.Order.Kind == OrderKind.Move; i++)
        {
            w.Step();
            var agl = u.Altitude.Raw - w.GroundHeight(u.Pos).Raw;
            (lo, hi) = (Math.Min(lo, agl), Math.Max(hi, agl));
            maxGround = Math.Max(maxGround, w.GroundHeight(u.Pos).Raw);
        }
        output.WriteLine($"over the ridge ({F(Metres(maxGround))} m): {F(Metres(lo))} to {F(Metres(hi))} m over the ground");
        Assert.Equal(OrderKind.Idle, u.Order.Kind);
        Assert.True(maxGround >= 35 * Metre, "it crossed the ridge");
        Assert.InRange(lo, 12 * Metre, f.HeightRaw); // (a 2-in-3 slope at its dash speed: it climbs early, still at most its climb rate)
        Assert.InRange(hi, f.HeightRaw, 60 * Metre);
    }

    // ---- The pop-up ----

    [Fact]
    public void ItHidesBehindARidgeAndFiresOverIt()
    {
        var w = Sentinel(160);
        w.AttachTerrain(Ridge(160, 80, 10, 30)); // a 30 m ridge along x = 80, 40 m across
        var u = Fly(w, Apache, 20, 80);
        var t = w.Spawn(2, "technical", 100, 80); // 20 cells beyond the crest, out of sight of the low ground west of it
        Issue(w, CommandKind.Attack, u, target: t.Id);
        var phases = new List<HeliAttack>();
        long hiddenAlt = -1, firedAlt = -1, hiddenNeed = 0;
        Vec cover = default;
        var launched = false;
        for (var i = 0; i < 1200 && t.Alive; i++)
        {
            w.Step();
            var h = u.Heli!;
            if (phases.Count == 0 || phases[^1] != h.Attack)
            {
                phases.Add(h.Attack);
            }
            var aimH = w.GroundHeight(t.Pos).Raw + Metre;
            if (h.Attack == HeliAttack.Hide && hiddenAlt < 0 && h.AttackTicks > 5)
            {
                (hiddenAlt, hiddenNeed, cover) = (u.Altitude.Raw, w.EyeNeed(u.Pos, t.Pos, aimH), u.Pos);
            }
            if (h.Attack == HeliAttack.Fire && firedAlt < 0)
            {
                firedAlt = u.Altitude.Raw;
                Assert.True(w.EyeNeed(u.Pos, t.Pos, aimH) <= u.Altitude.Raw, "it fires only with a line of sight over the crest");
            }
            launched |= w.Projectiles.Any(p => p.Shooter == u.Id && p.Weapon.Effect.Guided is not null);
        }
        output.WriteLine($"phases {string.Join(" ", phases)}; cover {F(Cells(cover.X.Raw))},{F(Cells(cover.Y.Raw))} on {F(Metres(w.GroundHeight(cover).Raw))} m ground; " +
            $"hiding at {F(Metres(hiddenAlt))} m (the line needs {F(Metres(hiddenNeed))} m); fired at {F(Metres(firedAlt))} m; target {(t.Alive ? "alive" : "destroyed")}");
        Assert.True(hiddenAlt >= 0, "it hid behind the ridge");
        Assert.True(cover.X.Raw < 80 * Cell1, "on its own side of the crest");
        Assert.True(hiddenNeed > hiddenAlt, "hidden: no line of sight to the target from its hiding height");
        Assert.True(launched && firedAlt > hiddenAlt, "it rose to fire");
        Assert.False(t.Alive, "the missile came over the crest and destroyed the technical");
        Assert.Contains(HeliAttack.Rise, phases);
        var fire = phases.IndexOf(HeliAttack.Fire);
        Assert.True(fire > 0 && phases.Skip(fire).Contains(HeliAttack.Duck) || !t.Alive, "it ducks after firing");
    }

    [Fact]
    public void WithoutCoverItHoversAtStandoffAndFires()
    {
        var w = Sentinel(128);
        var u = Fly(w, Apache, 20, 64);
        var t = w.Spawn(2, "technical", 90, 64);
        Issue(w, CommandKind.Attack, u, target: t.Id);
        long firedAt = -1;
        for (var i = 0; i < 600 && t.Alive; i++)
        {
            w.Step();
            if (firedAt < 0 && w.Projectiles.Any(p => p.Shooter == u.Id))
            {
                firedAt = Dist(u.Pos, t.Pos);
                Assert.Equal(HeliAttack.Standoff, u.Heli!.Attack);
            }
        }
        var w0 = w.Player(1)!.Setup.Find(w.Content, Apache)!.Weapons.Single(x => x.Effect.Guided is not null);
        output.WriteLine($"fired from {F(Cells(firedAt))} cells (missile range {F(Cells(w0.Range.Raw))})");
        Assert.False(t.Alive);
        Assert.InRange(firedAt, w0.Range.Raw * 70 / 100, w0.Range.Raw);
    }

    // ---- Weapons ----

    // A helicopter of `heli` against `count` units of `target` (player 2) in the open: the ticks until they are all dead
    // (or `limit`), and the helicopter's shots by weapon.
    private (int Ticks, int[] Shots) Duel(string heli, string target, int count, int limit, ulong seed = 1)
    {
        var w = Sentinel(128, seed);
        var u = Fly(w, heli, 20, 64);
        var foes = new List<Unit>();
        for (var i = 0; i < count; i++)
        {
            foes.Add(w.Spawn(2, target, 60 + (i % 3) * 2, 60 + i / 3 * 2));
        }
        var start = w.Tick;
        Issue(w, CommandKind.AttackMove, u, new Cell(64, 64));
        var shots = new int[u.Def.Weapons.Count];
        var seen = new HashSet<uint>();
        while (w.Tick - start < limit && foes.Any(f => f.Alive))
        {
            w.Step();
            foreach (var p in w.Projectiles)
            {
                if (p.Shooter == u.Id && seen.Add(p.Id))
                {
                    shots[u.Def.Weapons.ToList().IndexOf(p.Weapon)]++;
                }
            }
            for (var i = 0; i < u.Def.Weapons.Count; i++)
            {
                if (u.Heli!.Reloads[i] == u.Def.Weapons[i].Reload && u.Def.Weapons[i].Effect.ProjectileSpeed.Raw == 0)
                {
                    shots[i]++;
                }
            }
        }
        return ((int)(w.Tick - start), shots);
    }

    [Fact]
    public void EachHelicopterFightsSoldiersAndVehiclesWithTheRightWeapons()
    {
        foreach (var heli in new[] { Apache, Havoc, Bird })
        {
            var (inf, si) = Duel(heli, "Rifleman", 6, 1200);
            var (veh, sv) = Duel(heli, "technical", 3, 1600);
            var names = TestContent.Mvp.Archetype(heli)!.Components.Select(i => TestContent.Mvp.Components[i].Id).ToList();
            output.WriteLine($"{heli}: 6 riflemen in {F(inf / 20.0)} s (shots {string.Join(", ", names.Zip(si, (n, k) => $"{n} {k}"))}); " +
                $"3 technicals in {F(veh / 20.0)} s (shots {string.Join(", ", names.Zip(sv, (n, k) => $"{n} {k}"))})");
            Assert.True(inf < 1200, $"{heli} kills the soldiers");
            Assert.True(veh < 1600, $"{heli} kills the technicals");
            var missile = names.FindIndex(n => n.Contains("missiles", StringComparison.Ordinal));
            if (missile >= 0)
            {
                Assert.Equal(0, si[missile]); // no missile at a soldier
                Assert.True(sv[missile] > 0, "missiles at the vehicles");
            }
        }
        // The armoured helicopter's cannon is the better one against soldiers.
        var (ai, _) = Duel(Apache, "Rifleman", 6, 1200);
        var (hi, _) = Duel(Havoc, "Rifleman", 6, 1200);
        Assert.True(hi <= ai, "the Havoc type clears soldiers at least as fast");
    }

    [Fact]
    public void SixLightRocketsBringDownAHeavyRocketTruck()
    {
        var w = Sentinel(128);
        var u = Fly(w, Bird, 20, 64);
        var t = w.Spawn(2, "heavy_rocket_truck", 60, 64);
        Issue(w, CommandKind.Attack, u, target: t.Id);
        var w0 = u.Def.Weapons.Single(x => x.Effect.SalvoSize > 1);
        var stock = w0.Stock;
        var hits = 0;
        var hp = t.HP;
        for (var i = 0; i < 1200 && t.Alive && u.Charges[stock] > 0; i++)
        {
            w.Step();
            if (t.HP < hp)
            {
                hits++;
                hp = t.HP;
            }
        }
        output.WriteLine($"truck {t.HP} HP after {8 - u.Charges[stock]} rockets; {hits} damaging ticks");
        Assert.True(!t.Alive || t.HP <= 300 - 50 * (8 - u.Charges[stock] - 2), "about 50 a rocket");
    }

    // ---- Toughness: hits to out of control ----

    // A helicopter of player 1 hovering 30 m up at mid-map, shot at by `count` of player 2's AA units of `shooter` in a
    // ring 20 cells round it: the hits (AA bursts that hurt it) until it went out of control (0: never, in `limit` ticks).
    private int HitsToOutOfControl(World w, string heli, string shooter, int count, int limit, byte p2 = 2)
    {
        var n = w.Grid.Width;
        var u = Fly(w, heli, n / 2, n / 2);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.HoldFire }); // it does not fight back
        for (var i = 0; i < count; i++)
        {
            var (dx, dy) = ((int)(20 * Math.Cos(i * 2 * Math.PI / count)), (int)(20 * Math.Sin(i * 2 * Math.PI / count)));
            if (w.Player(p2)!.Setup.Find(w.Content, shooter)!.Archetype.Crew is not null)
            {
                w.SpawnCrewed(p2, shooter, n / 2 + dx, n / 2 + dy);
            }
            else
            {
                w.Spawn(p2, shooter, n / 2 + dx, n / 2 + dy);
            }
        }
        var hits = 0;
        for (var i = 0; i < limit; i++)
        {
            w.Step();
            hits += w.AirBursts.Count(b => b.Target == u.Id && b.Kind is AirBurstKind.Hit or AirBurstKind.Kill);
            if (u.Heli!.Phase == HeliPhase.OutOfControl)
            {
                return hits;
            }
            Assert.True(u.Alive, "brought down: out of control, never destroyed outright");
        }
        return 0;
    }

    [Theory]
    [InlineData(Apache, 4, 3)]
    [InlineData(Havoc, 5, 4)]
    [InlineData(Bird, 2, 1)]
    public void HitsBringItDownOutOfControl(string heli, int igla, int sam)
    {
        var a = HitsToOutOfControl(Sentinel(96, 3), heli, "manpads", 8, 4000);
        var b = HitsToOutOfControl(Sentinel(96, 3), heli, "sam_vehicle", 3, 4000);
        output.WriteLine($"{heli}: out of control after {a} Igla hits, {b} air defence missile hits");
        Assert.Equal(igla, a);
        Assert.Equal(sam, b);
    }

    [Fact]
    public void APatriotHitDestroysAHelicopterOutright()
    {
        var w = Duel(160, 5);
        var u = Fly(w, Apache, 80, 80);
        var launcher = w.SpawnCrewed(2, "patriot_launcher", 80, 120).Vehicle;
        var radar = w.SpawnCrewed(2, "radar_truck", 90, 120).Vehicle;
        Issue(w, CommandKind.Deploy, launcher);
        Issue(w, CommandKind.Deploy, radar);
        var crash = (HeliCrash?)null;
        var ooc = false;
        for (var i = 0; i < 2400 && crash is null; i++)
        {
            w.Step();
            ooc |= u.Heli!.Phase == HeliPhase.OutOfControl;
            crash = w.HeliCrashes.FirstOrDefault(c => c.Helicopter == u.Id);
        }
        output.WriteLine($"crash {crash?.Kind} at tick {w.Tick}");
        Assert.NotNull(crash);
        Assert.Equal(HeliCrashKind.AirDestroyed, crash!.Kind);
        Assert.False(ooc, "never out of control: destroyed outright");
        Assert.Empty(crash.Survivors);
        Assert.Contains(w.Wrecks, k => k.Id == u.Id); // its wreck falls
    }

    // The MVP content with one text replaced in the file that has it (exactly once).
    private static ContentSet Edited(params (string From, string To)[] edits)
    {
        var sources = TestContent.MvpSources();
        foreach (var (from, to) in edits)
        {
            var i = sources.FindIndex(x => x.Json.Contains(from, StringComparison.Ordinal));
            Assert.True(i >= 0, $"no file has {from}");
            var json = sources[i].Json;
            var at = json.IndexOf(from, StringComparison.Ordinal);
            sources[i] = sources[i] with { Json = string.Concat(json.AsSpan(0, at), to, json.AsSpan(at + from.Length)) };
        }
        return ContentLoader.Load(sources);
    }

    [Fact]
    public void MachineGunsWearItDownTheArmouredOneLess()
    {
        // The heavy MG's 6 cells (12 m) never reach a helicopter 30 m up: this test gives it 25 cells to compare its classes'
        // hit chances (the attack helicopter 50% a burst, the armoured one 15%) under its 40 m ceiling.
        var c = Edited(("\"damage\": 12, \"range\": 60, \"reload\": 5,", "\"damage\": 12, \"range\": 250, \"reload\": 5,"));
        int Hits(string heli)
        {
            var setup = TestContent.Sentinel(2).For(c, 2);
            setup = setup with { Players = [.. setup.Players.Select(p => p with { Army = [] })] };
            var w = new World(new Grid(96, 96), MatchCompiler.Compile(c, setup));
            var u = Fly(w, heli, 48, 48);
            w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.SetStance, Units = [u.Id], Stance = Stance.HoldFire });
            for (var i = 0; i < 3; i++)
            {
                w.SpawnCrewed(2, "technical", 44 + i * 4, 52);
            }
            var hp = u.HP;
            w.Run(100);
            Assert.Equal(HeliPhase.Flying, u.Heli!.Phase);
            return hp - u.HP;
        }
        var (a, h) = (Hits(Apache), Hits(Havoc));
        output.WriteLine($"5 s under three heavy MGs: attack helicopter -{a} HP, armoured helicopter -{h} HP");
        Assert.True(a > 0, "the heavy MG reaches it 30 m up");
        Assert.True(h * 2 < a, "it barely scratches the armoured one");
    }

    // ---- Out of control ----

    // Brings helicopter u down as a missile hit would (its HP to 0): it goes out of control at the end of the tick.
    private static void BringDown(World w, Unit u)
    {
        u.HP = 0;
        u.KilledBy = KillCause.Missile;
        w.Step();
    }

    [Fact]
    public void OutOfControlItReachesAPadAndSurvives()
    {
        var w = Sentinel(128);
        var pad = w.Spawn(1, "helipad", 70, 60);
        var u = Fly(w, Apache, 30, 66);
        w.Run(20);
        Issue(w, CommandKind.Move, u, new Cell(30, 66)); // (an order it had)
        BringDown(w, u);
        var r = Rotor(w, Apache);
        Assert.Equal(HeliPhase.OutOfControl, u.Heli!.Phase);
        Assert.Equal(u.Def.MaxHp * r.OutOfControl.HpPercent / 100, u.HP);
        Assert.Equal(pad.Id, u.Heli.Pad);
        var st = w.HeliStatusOf(u);
        Assert.True(st.OutOfControl && st.OocTicksLeft > 0 && st.OocSpinRate != 0 && st.OocSinkRate > 0);
        // No player orders reach it.
        Issue(w, CommandKind.Move, u, new Cell(10, 10));
        w.Step();
        Assert.NotEqual(OrderKind.Move, u.Order.Kind);
        var hp0 = u.HP;
        HeliCrash? crash = null;
        var spun = 0;
        for (var i = 0; i < 700 && crash is null; i++)
        {
            w.Step();
            spun += Math.Abs(u.Heli.Yaw);
            crash = w.HeliCrashes.FirstOrDefault(c => c.Helicopter == u.Id);
            Assert.True(u.HP <= hp0, "its HP drains");
            hp0 = u.HP;
        }
        output.WriteLine($"{crash?.Kind} after {F((w.Tick - 22) / 20.0)} s with {u.HP} HP; spun {spun / 16384 * 57} degrees");
        Assert.Equal(HeliCrashKind.HardLanded, crash!.Kind);
        Assert.True(u.Alive && u.HP > 0, "it survives with little HP");
        Assert.Equal(HeliPhase.Landed, u.Heli.Phase);
        Assert.True(Dist(u.Pos, pad.Pos) <= Cell1);
        // Then the pad services it (repairs it) and holds it there.
        w.Run(60 * 20);
        Assert.Equal(u.Def.MaxHp, u.HP);
        Assert.Equal(HeliPhase.Landed, u.Heli.Phase);
        Assert.Equal(AirActivity.Held, w.AirStatusOf(u).Activity);
    }

    [Fact]
    public void OutOfControlWithNoPadItCrashLandsAfterThirtySeconds()
    {
        var crew = 0;
        var trials = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = Sentinel(96, seed);
            var u = Fly(w, Apache, 48, 48);
            BringDown(w, u);
            var start = w.Tick;
            HeliCrash? crash = null;
            while (crash is null && w.Tick - start < 700)
            {
                w.Step();
                crash = w.HeliCrashes.FirstOrDefault(c => c.Helicopter == u.Id);
            }
            Assert.NotNull(crash);
            Assert.Equal(HeliCrashKind.CrashLanded, crash!.Kind);
            Assert.InRange((int)(w.Tick - start), 595, 602); // 30 s
            Assert.False(u.Alive);
            Assert.Contains(w.Wrecks, k => k.Id == u.Id); // a wreck at the crash point (the last metres of its fall)
            Assert.True(crash.Altitude.Raw - w.GroundHeight(crash.At).Raw <= 10 * Metre, "low when it came down");
            crew += crash.Survivors.Count;
            trials += 2;
            foreach (var id in crash.Survivors)
            {
                Assert.Equal("pilot", w.Unit(id)!.Def.Archetype.Id);
            }
        }
        output.WriteLine($"crew survived {crew} of {trials} (the content's 50%)");
        Assert.InRange(crew, trials / 4, trials * 3 / 4);
    }

    [Fact]
    public void OutOfControlAFurtherHitDestroysItInTheAir()
    {
        var w = Sentinel(96);
        var u = Fly(w, Apache, 48, 48);
        BringDown(w, u);
        w.Run(40);
        Assert.Equal(HeliPhase.OutOfControl, u.Heli!.Phase);
        u.HP -= 5; // a hit
        w.Step();
        var crash = w.HeliCrashes.Single(c => c.Helicopter == u.Id);
        Assert.Equal(HeliCrashKind.AirDestroyed, crash.Kind);
        Assert.Empty(crash.Survivors);
        Assert.False(u.Alive);
        Assert.True(w.Wrecks.Any(k => k.Id == u.Id) && w.Wrecks.Single(k => k.Id == u.Id).Altitude.Raw > w.GroundHeight(u.Pos).Raw + 5 * Metre,
            "its wreck falls from the air");
    }

    // ---- The light helicopter's riders ----

    private static void Enter(World w, IEnumerable<Unit> soldiers, Unit heli) =>
        w.Issue(new Command { Tick = w.Tick, Player = heli.Owner, Kind = CommandKind.Enter, Units = [.. soldiers.Select(s => s.Id)], Target = heli.Id });

    // A light helicopter of player 1 hovering at (40, 40) and three riflemen of its side 10 cells east, boarded.
    private (World W, Unit Bird, List<Unit> Riders) Loaded(ulong seed = 1)
    {
        var w = Sentinel(128, seed);
        var b = Fly(w, Bird, 40, 40);
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 50, 38 + 2 * i)).ToList();
        Enter(w, men, b);
        for (var i = 0; i < 600 && men.Any(m => m.Inside != b.Id); i++)
        {
            w.Step();
        }
        return (w, b, men);
    }

    [Fact]
    public void ItLandsToBoardAndTheSoldiersWalkToIt()
    {
        var (w, b, men) = Loaded();
        output.WriteLine($"boarded by tick {w.Tick}; it is {b.Heli!.Phase}, riders {w.RidersOf(b).Count}");
        Assert.All(men, m => Assert.Equal(b.Id, m.Inside));
        Assert.Equal(3, w.RidersOf(b).Count);
        Assert.Equal(HeliPhase.Landed, b.Heli!.Phase); // it set down to take them
        Assert.Equal(3, w.HeliStatusOf(b).Riders);
        // A fourth finds no seat.
        var extra = w.Spawn(1, "Rifleman", 44, 40);
        Enter(w, [extra], b);
        w.Run(100);
        Assert.Equal(0u, extra.Inside);
    }

    [Fact]
    public void ItDropsItsRidersByLanding()
    {
        var (w, b, men) = Loaded();
        var drop = new Cell(90, 40);
        Issue(w, CommandKind.HeliUnload, b, drop);
        var outAt = new List<uint>();
        var landed = false;
        for (var i = 0; i < 800 && outAt.Count < 3; i++)
        {
            w.Step();
            landed |= b.Heli!.Phase == HeliPhase.Landed && Dist(b.Pos, drop.Center()) <= 2 * Cell1;
            if (men.Count(m => m.Inside == 0) > outAt.Count)
            {
                outAt.Add(w.Tick);
            }
        }
        output.WriteLine($"out at ticks {string.Join(", ", outAt)}");
        Assert.True(landed, "it landed at the point");
        Assert.Equal(3, men.Count(m => m.Inside == 0 && m.Alive && Dist(m.Pos, drop.Center()) < 6 * Cell1));
        Assert.Equal(HeliErrand.None, b.Heli!.Errand);
    }

    [Fact]
    public void ItDropsItsRidersHoveringLow()
    {
        var (w, b, men) = Loaded();
        var drop = new Cell(90, 40);
        Issue(w, CommandKind.HeliUnload, b, drop, on: true);
        var r = Rotor(w, Bird);
        long lowest = long.MaxValue;
        var times = new List<uint>();
        for (var i = 0; i < 800 && men.Any(m => m.Inside != 0); i++)
        {
            w.Step();
            if (Dist(b.Pos, drop.Center()) <= 3 * Cell1 && b.Heli!.Phase == HeliPhase.Flying)
            {
                lowest = Math.Min(lowest, b.Altitude.Raw - w.GroundHeight(b.Pos).Raw);
            }
            if (men.Count(m => m.Inside == 0) > times.Count)
            {
                times.Add(w.Tick);
            }
            Assert.NotEqual(HeliPhase.Landed, b.Heli!.Phase); // it never touches down
        }
        output.WriteLine($"riders out at {string.Join(", ", times)}; lowest {F(Metres(lowest))} m");
        Assert.Equal(3, times.Count);
        Assert.InRange(lowest, r.Riders!.DropHeightRaw - Metre, r.Riders.DropHeightRaw + Metre);
        Assert.InRange((int)(times[1] - times[0]), r.Riders.DropTicks - 1, r.Riders.DropTicks + 1); // about 2 s each
        Assert.All(men, m => Assert.True(Dist(m.Pos, drop.Center()) < 6 * Cell1));
    }

    [Fact]
    public void RidersNeverFire()
    {
        var (w, b, men) = Loaded();
        Issue(w, CommandKind.Move, b, new Cell(70, 40));
        foreach (var i in Enumerable.Range(0, 4))
        {
            w.Spawn(2, "Rifleman", 72 + i, 44);
        }
        w.Run(200);
        Assert.All(men, m => Assert.Equal(b.Id, m.Inside));
        Assert.All(men, m => Assert.Equal(0u, m.LastShot)); // inside: they never fire (the helicopter's own guns do)
    }

    [Fact]
    public void RidersRollTheirSurvivalInACrashLanding()
    {
        var (riders, crews, trials) = (0, 0, 0);
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var (w, b, _) = Loaded(seed);
            Issue(w, CommandKind.Move, b, new Cell(100, 100));
            w.Run(40);
            BringDown(w, b);
            HeliCrash? crash = null;
            for (var i = 0; i < 700 && crash is null; i++)
            {
                w.Step();
                crash = w.HeliCrashes.FirstOrDefault(c => c.Helicopter == b.Id);
            }
            Assert.Equal(HeliCrashKind.CrashLanded, crash!.Kind);
            riders += crash.Survivors.Count(id => w.Unit(id)!.Def.Archetype.Id == "assault");
            crews += crash.Survivors.Count(id => w.Unit(id)!.Def.Archetype.Id == "pilot");
            trials++;
            Assert.Equal(4, crash.Survivors.Count + crash.Lost);
            Assert.All(w.Units, u => Assert.NotEqual(b.Id, u.Inside)); // nobody is left inside a wreck
        }
        output.WriteLine($"riders survived {riders} of {trials * 3} (40%), the pilot {crews} of {trials} (50%)");
        Assert.InRange(riders, trials * 3 / 5, trials * 3 * 3 / 5);
    }

    // ---- The helipad ----

    [Fact]
    public void AWorkerBuildsAHelipadThatBuildsAHelicopterOntoItself()
    {
        var w = Sentinel(96);
        var worker = w.Spawn(1, "worker", 30, 30);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = new Cell(34, 34), Unit = "helipad" });
        Unit? pad = null;
        for (var i = 0; i < 1200 && pad is not { IsBuilt: true }; i++)
        {
            w.Step();
            pad = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == "helipad");
        }
        Assert.True(pad is { IsBuilt: true }, "built");
        output.WriteLine($"helipad built at tick {w.Tick}");
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [pad!.Id], Unit = Apache });
        w.Step();
        // One at a time: a second order is refused while it builds.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [pad.Id], Unit = Bird });
        w.Step();
        Assert.Equal(1, pad.ProductionQueue.Count + pad.Bays.Count(b => b.Def >= 0));
        Assert.True(w.PadStatusOf(pad).Building >= 0);
        var start = w.Tick;
        Unit? h = null;
        for (var i = 0; i < 1400 && h is null; i++)
        {
            w.Step();
            h = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Apache);
        }
        Assert.NotNull(h);
        output.WriteLine($"built in {F((w.Tick - start) / 20.0)} s");
        Assert.InRange((int)(w.Tick - start), 1190, 1205); // 60 s
        Assert.Equal(HeliPhase.Landed, h!.Heli!.Phase);
        Assert.Equal(pad.Id, h.Heli.Pad);
        Assert.Equal(pad.Pos, h.Pos);
        Assert.Equal(0, h.Heli.Rotor);
        Assert.Equal(h.Id, w.PadStatusOf(pad).Occupant);
        // A second one is refused while the pad holds one.
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [pad.Id], Unit = Bird });
        w.Step();
        Assert.Equal(0, pad.ProductionQueue.Count + pad.Bays.Count(b => b.Def >= 0));
        // Its first order lifts it off (after the rotor spools up).
        Issue(w, CommandKind.Move, h, new Cell(70, 70));
        w.Run(200);
        Assert.Equal(HeliPhase.Flying, h.Heli.Phase);
        Assert.Equal(0u, w.PadStatusOf(pad).Occupant);
    }

    [Fact]
    public void ThePadRefuelsRearmsAndRepairsOnRearmAndHoldsOnReturnToBase()
    {
        var w = Sentinel(128);
        var pad = w.Spawn(1, "helipad", 20, 20);
        var u = Fly(w, Apache, 80, 80);
        // Spent: half its fuel, its missiles and half its HP.
        var mw = u.Def.Weapons.Single(x => x.Effect.Guided is not null);
        u.MutableCharges[mw.Stock] = 0;
        u.HP = u.Def.MaxHp / 2;
        u.Lifetime = u.Tank / 2;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.AirTask, Mission = AirTaskKind.Patrol, Units = [u.Id], Cell = new Cell(90, 80) });
        w.Step();
        Issue(w, CommandKind.Rearm, u);
        var landedAt = 0u;
        var phases = new List<HeliPhase>();
        for (var i = 0; i < 3000; i++)
        {
            w.Step();
            if (phases.Count == 0 || phases[^1] != u.Heli!.Phase)
            {
                phases.Add(u.Heli!.Phase);
            }
            if (landedAt == 0 && u.Heli!.Phase == HeliPhase.Landed)
            {
                landedAt = w.Tick;
            }
            if (landedAt != 0 && u.Heli!.Phase == HeliPhase.Flying)
            {
                break;
            }
        }
        output.WriteLine($"phases {string.Join(" ", phases)}; serviced on the pad {F((w.Tick - landedAt) / 20.0)} s");
        Assert.True(landedAt > 0, "it landed on its pad");
        Assert.Equal(u.Def.MaxHp, u.HP);
        Assert.InRange(u.Lifetime, u.Tank - 100, u.Tank); // (full, less the lift-off)
        Assert.Equal(8, u.Charges[mw.Stock]);
        Assert.Equal(HeliPhase.Flying, u.Heli!.Phase); // it resumes its patrol
        Assert.NotNull(u.AirMission);
        // Return to base: home, refuelled, held there.
        u.Lifetime = u.Tank / 2;
        Issue(w, CommandKind.ReturnToBase, u);
        w.Run(1500);
        Assert.Equal(HeliPhase.Landed, u.Heli.Phase);
        Assert.Equal(pad.Id, u.Heli.Pad);
        Assert.Equal(u.Tank, u.Lifetime);
        Assert.Equal(AirActivity.Held, w.AirStatusOf(u).Activity);
    }

    [Fact]
    public void AtBingoItGoesHomeToRefuelOnlyThenBackToItsPost()
    {
        var w = Sentinel(128);
        w.Spawn(1, "helipad", 20, 20);
        var u = Fly(w, Apache, 100, 100);
        var post = u.Pos;
        var mw = u.Def.Weapons.Single(x => x.Effect.Guided is not null);
        u.MutableCharges[mw.Stock] = 3;
        u.Lifetime = 600; // 30 s of fuel
        var refuelled = false;
        for (var i = 0; i < 3000; i++)
        {
            w.Step();
            refuelled |= u.Heli!.Phase == HeliPhase.Landed && u.Lifetime == u.Tank;
            if (refuelled && Dist(u.Pos, post) < 2 * Cell1 && u.Heli.Phase == HeliPhase.Flying)
            {
                break;
            }
        }
        Assert.True(refuelled, "it went home to refuel");
        Assert.Equal(3, u.Charges[mw.Stock]); // a refuel stop refuels only
        Assert.True(Dist(u.Pos, post) < 2 * Cell1, "back at its post");
    }

    [Fact]
    public void TheMainAirBasesHangarBuildsHelicoptersOntoItsStands()
    {
        var w = Sentinel(360);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var hangar = w.Unit(af.Hangar)!;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [hangar.Id], Unit = Bird });
        Unit? h = null;
        for (var t = 0; t < 800 && h is null; t++)
        {
            w.Step();
            h = w.Units.FirstOrDefault(u => u.Def.Archetype.Id == Bird);
        }
        Assert.NotNull(h);
        var pad = w.Unit(h!.Heli!.Pad)!;
        output.WriteLine($"built onto {pad.Def.Archetype.Id} at tick {w.Tick}");
        Assert.Equal("airfield_stand", pad.Def.Archetype.Id); // a stand, not a heavy bay
        Assert.Equal(HeliPhase.Landed, h.Heli.Phase);
        Assert.Contains(af.Rows.SelectMany(r => r.Stations), st => st.Occupant == h.Id);
        // It lifts off on an order (no runway) and comes back to a station to rearm.
        Issue(w, CommandKind.Move, h, new Cell(200, 200));
        w.Run(400);
        Assert.Equal(HeliPhase.Flying, h.Heli.Phase);
        Assert.DoesNotContain(af.Rows.SelectMany(r => r.Stations), st => st.Occupant == h.Id);
        Issue(w, CommandKind.Rearm, h);
        w.Run(600);
        Assert.Equal(HeliPhase.Landed, h.Heli.Phase);
        Assert.Contains(af.Rows.SelectMany(r => r.Stations), st => st.Occupant == h.Id);
    }

    // ---- Sensors and orders ----

    [Fact]
    public void ItsMastRadarSpotsVehiclesNotSoldiersForItsSide()
    {
        var w = Sentinel(256, fog: true);
        var u = Fly(w, Apache, 30, 128);
        Issue(w, CommandKind.Stop, u);
        var truck = w.Spawn(2, "technical", 130, 128); // 100 cells off: far beyond its sight, inside its radar
        var man = w.Spawn(2, "Rifleman", 130, 132);
        var far = w.Spawn(2, "technical", 30 + 130, 20); // 130+ cells: beyond the radar
        w.Run(10);
        Assert.True(World.IsSpotted(truck, 1), "a vehicle within 120 cells is spotted");
        Assert.False(World.IsSpotted(man, 1), "a soldier is not (vehicles only)");
        Assert.False(World.IsSpotted(far, 1), "beyond its reach");
        // The armoured helicopter has no mast radar.
        var w2 = Sentinel(256, fog: true);
        Fly(w2, Havoc, 30, 128);
        var t2 = w2.Spawn(2, "technical", 130, 128);
        w2.Run(10);
        Assert.False(World.IsSpotted(t2, 1));
    }

    [Fact]
    public void OnGuardItEngagesWithinItsLeashAndComesBackToItsPost()
    {
        var w = Sentinel(128);
        var u = Fly(w, Apache, 40, 64);
        var post = new Cell(40, 64);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Guard, Units = [u.Id], Cell = post });
        w.Run(20);
        var near = w.Spawn(2, "Rifleman", 60, 64); // 20 cells off: within its 40-cell leash
        var far = w.Spawn(2, "Rifleman", 120, 64); // beyond its leash and its weapons
        var engaged = false;
        for (var i = 0; i < 600 && near.Alive; i++)
        {
            w.Step();
            engaged |= u.Heli!.Target == near.Id;
            Assert.NotEqual(far.Id, u.Heli.Target);
        }
        Assert.True(engaged && !near.Alive, "it engaged and killed the soldier within its leash");
        w.Run(400);
        Assert.True(Dist(u.Pos, post.Center()) <= 2 * Cell1, "back over its post");
        Assert.True(far.Alive);
        Assert.Equal(OrderKind.Guard, u.Order.Kind);
    }

    // ---- Determinism ----

    // A scripted fight on both sides (a Sentinel duel world with a ridge): each side's attack helicopter attacks the other's
    // vehicles, a light helicopter carries riders; the commands issued in order or reversed; the world's hash every tick.
    private static List<ulong> Script(bool reversed)
    {
        var w = Duel(160, 7);
        w.AttachTerrain(Ridge(160, 80, 12, 25));
        var a1 = w.Spawn(1, Apache, 20, 60, Facing.East);
        var a2 = w.Spawn(2, Havoc, 140, 100, Facing.West);
        var b1 = w.Spawn(1, Bird, 20, 100, Facing.East);
        var t2 = w.Spawn(2, "humvee", 110, 60); // (a Sentinel duel: the Humvee replaced its technical)
        var t1 = w.Spawn(1, "humvee", 50, 100);
        var men = Enumerable.Range(0, 3).Select(i => w.Spawn(1, "Rifleman", 24, 96 + i)).ToList();
        w.Spawn(2, "manpads", 120, 80);
        w.Spawn(2, "Rifleman", 100, 120);
        var cmds = new[]
        {
            new Command { Tick = 1, Player = 1, Seq = 1, Kind = CommandKind.Attack, Units = [a1.Id], Target = t2.Id },
            new Command { Tick = 1, Player = 2, Seq = 1, Kind = CommandKind.Attack, Units = [a2.Id], Target = t1.Id },
            new Command { Tick = 1, Player = 1, Seq = 2, Kind = CommandKind.Enter, Units = [.. men.Select(m => m.Id)], Target = b1.Id },
            new Command { Tick = 300, Player = 1, Seq = 3, Kind = CommandKind.HeliUnload, Units = [b1.Id], Cell = new Cell(90, 120), On = true },
        };
        foreach (var c in reversed ? cmds.Reverse() : cmds)
        {
            w.Issue(c);
        }
        var hashes = new List<ulong>();
        for (var t = 0; t < 1500; t++)
        {
            w.Step();
            hashes.Add(w.Hash());
        }
        Assert.False(t2.Alive && t1.Alive, "a Humvee is destroyed");
        return hashes;
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var a = Script(false);
        Assert.Equal(a, Script(false));
        Assert.Equal(a, Script(true));
    }
}

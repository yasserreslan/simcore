using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Tests;

// The Resistance mobile radar (docs/design.md, "Mobile radar"; an SA-6 "Straight Flush" type): set up (8 s; packs up in
// 6 s) it searches the air out to 400 cells, slant, by each target's signature; what it tracks is tracked for its side
// (team cueing: the AA soldiers and the air defence vehicles engage it within their own weapon's range, and the side sees
// it under fog), and its side's ground air defence locks on what it tracks in 70% of its own lock time (cueLock). It never
// adds range. Player 1 is the Sentinel (the jets), player 2 the Resistance.
public class MobileRadarTests
{
    private const int N = 500;
    private const int Y = 250;
    private const string Multirole = "Multirole 500 lb";
    private const string Fighter = "fighter_jet"; // signature 50 / 200 / 60: detected at half the range, locked in twice the time

    private static World NewWorld(ulong seed = 1, bool fog = false) => Arena.NewSentinel(N, seed, fog ? s => s with { Fog = true } : null);

    // Radar: player 2's mobile radar at (x, y), crewed; set up when asked (Deploy, then 8 s and a tick).
    private static Unit Radar(World w, int x, int y = Y, bool setUp = true)
    {
        var r = w.SpawnCrewed(2, "mobile_radar", x, y).Vehicle;
        if (setUp)
        {
            w.Order(2, CommandKind.Deploy, [r]);
            w.Run(r.Def.Archetype.DeployTicks + 2);
            Assert.Equal(DeployState.Deployed, r.Deploy);
        }
        return r;
    }

    // Jet: player 1's jet in flight over (x, y) at the given height in metres (by default about its cruise height, 170 m:
    // 85 cells up), ordered to orbit there (a Move: 22 cells round).
    private static Unit Jet(World w, string name, int x, int y = Y, int metres = 170)
    {
        var j = w.Spawn(1, name, x, y);
        j.Altitude = Fixed.FromRaw(metres * 512L);
        w.Order(1, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    private static long SlantCells(World w, Unit from, Unit jet) =>
        World.Len3(jet.Pos.X.Raw - from.Pos.X.Raw, jet.Pos.Y.Raw - from.Pos.Y.Raw, jet.Altitude.Raw - w.RadarEyeRaw(from)) / Fixed.OneRaw;

    // Engage runs up to ticks and reports the first interceptor shooter launched (its projectile), whether it tracked the
    // jet, the tick it first did, and the nearest slant (cells) the jet came to it.
    private static (Projectile? Shot, bool Tracked, int FirstTrack, long Nearest) Engage(World w, Unit shooter, Unit jet, int ticks)
    {
        var tracked = false;
        var first = -1;
        var nearest = long.MaxValue;
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            if (shooter.AirTarget == jet.Id && !tracked)
            {
                (tracked, first) = (true, t);
            }
            nearest = Math.Min(nearest, SlantCells(w, shooter, jet));
            if (w.Projectiles.FirstOrDefault(p => p.Shooter == shooter.Id && p.Weapon.Effect.Interceptor is not null) is { } shot)
            {
                return (shot, tracked, first, nearest);
            }
        }
        return (null, tracked, first, nearest);
    }

    // ---- Content ----

    [Fact]
    public void ItIsData()
    {
        var c = TestContent.Mvp;
        var r = c.Archetype("mobile_radar")!;
        Assert.Equal((Fixed.FromInt(400), 0, 70, 80), (r.AirVision, r.GroundRadar.Raw, r.CueLock, r.SweepTicks));
        Assert.Equal((160, 120), (r.DeployTicks, r.StowTicks)); // 8 s to set up, 6 s to pack up: quicker than the radar truck's 15 / 12
        Assert.Equal((280, 800, 1600), (r.MaxHp, r.BuildTime, r.Cost));
        Assert.Equal(c.Archetype("sam_vehicle")!.Speed, r.Speed); // it keeps up with the air defence column
        Assert.Equal(Model.Locomotor.Tracked, r.Locomotor);
        Assert.Empty(r.Components); // no weapon
        Assert.True(r.AirVision > c.Archetype("radar_truck")!.AirVision, "wider than the Sentinel radar truck's 360");
        Assert.Equal(0, c.Archetype("radar_truck")!.CueLock); // the radar truck keeps its behaviour: no cued lock
        Assert.Equal(0, c.Archetype("sam_vehicle")!.CueLock);
        Assert.True(c.Faction("resistance")!.Archetypes[r.Index]);
        Assert.False(c.Faction("sentinel")!.Archetypes[r.Index]);
        Assert.True(r.Tags.Matches(c.Archetype("vehicle_garage")!.Production!.Tags), "built at the vehicle garage");
    }

    [Theory]
    [InlineData("\"cueLock\": 70", "\"cueLock\": 101", "cueLock")]
    [InlineData("\"airVision\": 4000, \"cueLock\": 70, \"sweepTicks\": 80", "\"cueLock\": 70", "cueLock needs an airVision")]
    [InlineData("\"airVision\": 4000, \"cueLock\": 70, \"sweepTicks\": 80", "\"sweepTicks\": 80", "sweepTicks needs an airVision")]
    public void BadRadarContentIsRefused(string from, string to, string error)
    {
        var sources = TestContent.MvpSources();
        var i = sources.FindIndex(s => s.Name.EndsWith("archetypes/vehicles.json", StringComparison.Ordinal));
        Assert.Contains(from, sources[i].Json, StringComparison.Ordinal);
        sources[i] = new Content.ContentSource(sources[i].Name, sources[i].Json.Replace(from, to, StringComparison.Ordinal));
        var e = Assert.ThrowsAny<Exception>(() => Content.ContentLoader.Load(sources));
        Assert.Contains(error, e.Message, StringComparison.Ordinal);
    }

    // ---- Search: only set up ----

    [Fact]
    public void PackedUpOrMovingItDoesNotSearch()
    {
        var w = NewWorld();
        var r = Radar(w, 50, setUp: false);
        var j = Jet(w, Multirole, 250);
        w.Run(5);
        Assert.False(w.AirTracked(2, j.Id), "packed up: no air search");
        Assert.Empty(w.RadarTracks(r));
        Assert.False(World.RadarOf(r)!.Value.On);
        Assert.Equal(-1, w.RadarSweep(r));

        w.Order(2, CommandKind.Deploy, [r]);
        var setUpAt = -1;
        for (var t = 1; t <= 200 && setUpAt < 0; t++)
        {
            w.Step();
            if (w.AirTracked(2, j.Id))
            {
                setUpAt = t;
            }
            else
            {
                Assert.NotEqual(DeployState.Deployed, r.Deploy);
            }
        }
        Assert.InRange(setUpAt, 160, 162); // 8 s to set up, then it tracks
        Assert.Equal([j], w.RadarTracks(r));
        Assert.True(World.RadarOf(r)!.Value.On);

        w.Order(2, CommandKind.Move, [r], cell: new Cell(50, 150));
        w.Run(3);
        Assert.True(r.Moving);
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            Assert.False(w.AirTracked(2, j.Id), "on the move: no air search");
            Assert.Empty(w.RadarTracks(r));
        }
        Assert.NotEqual(DeployState.Deployed, r.Deploy);
    }

    // ---- Reach: about 400 cells, by signature ----

    [Theory]
    [InlineData(Multirole, 390, true)]  // about 391 cells of slant from its antenna: inside 400
    [InlineData(Multirole, 410, false)]
    [InlineData(Fighter, 190, true)]    // the fighter's detect 50: 200 cells
    [InlineData(Fighter, 210, false)]
    public void ItDetectsAircraftOutToFourHundredCellsBySignature(string jet, int dx, bool seen)
    {
        var w = NewWorld();
        var r = Radar(w, 50);
        var j = Jet(w, jet, 50 + dx, metres: 60);
        w.Run(1);
        Assert.Equal(seen, w.AirTracked(2, j.Id));
        Assert.Equal(seen, w.RadarTracks(r).Contains(j));
        Assert.InRange(SlantCells(w, r, j), dx - 2, dx + 3); // the premise: its slant from the antenna is about dx
    }

    // ---- Cueing: within the shooters' own range ----

    [Fact]
    public void ItCuesAnAaSoldierOntoAJetHeCannotSeeWithinHisRange()
    {
        // The fighter orbits 48 to 92 cells (level) from the soldier, 85 cells up: about 100 to 130 of slant, inside his
        // Igla's 140 but beyond the 75 at which he detects it.
        var w = NewWorld();
        var aa = w.Spawn(2, "manpads", 50, Y);
        var j = Jet(w, Fighter, 120);
        var alone = Engage(w, aa, j, 400);
        Assert.False(alone.Tracked);
        Assert.Null(alone.Shot);

        w = NewWorld();
        Radar(w, 50, Y + 20);
        aa = w.Spawn(2, "manpads", 50, Y);
        j = Jet(w, Fighter, 120);
        var cued = Engage(w, aa, j, 400);
        Assert.True(cued.Tracked, "the radar cues him");
        Assert.NotNull(cued.Shot);
        Assert.True(cued.Nearest >= 76, $"he never saw it himself: it came within {cued.Nearest} cells");
        Assert.True(SlantCells(w, aa, j) <= 140, "fired within the Igla's range");
    }

    [Fact]
    public void ItCuesAnAirDefenceVehicleOntoAJetItCannotSeeWithinItsRange()
    {
        // The fighter orbits 178 to 222 cells (level) from the vehicle: partly inside its missile's 220 of slant, always
        // beyond the 130 its own radar sees it.
        var w = NewWorld();
        var sam = w.SpawnCrewed(2, "sam_vehicle", 40, Y).Vehicle;
        var j = Jet(w, Fighter, 240);
        var alone = Engage(w, sam, j, 400);
        Assert.False(alone.Tracked);

        w = NewWorld();
        Radar(w, 120);
        sam = w.SpawnCrewed(2, "sam_vehicle", 40, Y).Vehicle;
        j = Jet(w, Fighter, 240);
        var cued = Engage(w, sam, j, 600);
        Assert.True(cued.Tracked);
        Assert.NotNull(cued.Shot);
        Assert.True(cued.Nearest > 130, $"its own radar never saw it: it came within {cued.Nearest} cells");
        Assert.True(SlantCells(w, sam, j) <= 220, "fired within its missile's range");
    }

    [Fact]
    public void ItNeverAddsRange()
    {
        // A multirole orbits 228 cells and more (level) from the soldier (beyond his 140) and 238 from the vehicle (beyond
        // its 220): the radar tracks it the whole time; the soldier never picks it, nobody fires.
        var w = NewWorld();
        var r = Radar(w, 120);
        var aa = w.Spawn(2, "manpads", 50, Y);
        var sam = w.SpawnCrewed(2, "sam_vehicle", 40, Y, Facing.East).Vehicle;
        var j = Jet(w, Multirole, 300);
        var trackedAll = true;
        long aaNearest = long.MaxValue, samNearest = long.MaxValue;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            trackedAll &= w.AirTracked(2, j.Id);
            Assert.Equal(0u, aa.AirTarget);
            aaNearest = Math.Min(aaNearest, SlantCells(w, aa, j));
            samNearest = Math.Min(samNearest, SlantCells(w, sam, j));
        }
        Assert.True(trackedAll, "the radar tracked it");
        Assert.True(aaNearest > 140, $"premise: {aaNearest}");
        Assert.DoesNotContain(w.Projectiles, p => p.Weapon.Effect.Interceptor is not null);
        Assert.Equal(sam.Def.Weapons[0].Effect.Ammo, sam.Charges[0]);
        Assert.True(samNearest > 220, $"premise: {samNearest}");
        Assert.Contains(j, w.RadarTracks(r));
    }

    // ---- The cued lock: 30% faster ----

    // Lock: the tracking ticks shooter kind had locked when it launched at a jet orbiting 30 cells east (it sees it itself),
    // and the ticks from its first track to the launch; with a mobile radar set up 20 cells south when asked.
    private static (int Locked, int TrackToLaunch) Lock(string shooter, string jet, bool radar)
    {
        var w = NewWorld();
        if (radar)
        {
            Radar(w, 50, Y - 20);
        }
        var u = shooter == "manpads" ? w.Spawn(2, shooter, 50, Y) : w.SpawnCrewed(2, shooter, 50, Y).Vehicle;
        var j = Jet(w, jet, 80);
        var start = w.Tick;
        var (shot, tracked, first, _) = Engage(w, u, j, 400);
        Assert.True(tracked);
        Assert.NotNull(shot);
        return (shot.Tracked, (int)(w.Tick - start) - 1 - first);
    }

    [Theory]
    [InlineData("manpads", Multirole, 30, 21)]     // 1.5 s -> 1.05 s
    [InlineData("sam_vehicle", Multirole, 20, 14)] // 1 s -> 0.7 s
    [InlineData("sam_vehicle", Fighter, 40, 28)]   // the fighter's lock 200: 2 s -> 1.4 s
    public void ARadarCuedShooterLocksThirtyPercentFaster(string shooter, string jet, int lockTicks, int cuedTicks)
    {
        var alone = Lock(shooter, jet, false);
        var cued = Lock(shooter, jet, true);
        Assert.Equal(lockTicks, alone.Locked);
        Assert.Equal(cuedTicks, cued.Locked);
        Assert.Equal(lockTicks - cuedTicks, alone.TrackToLaunch - cued.TrackToLaunch); // the same turn to the target, a shorter lock
    }

    [Fact]
    public void ACuedSoldierLocksTheFighterInSeventyPercentOfHisTime()
    {
        // He never sees the fighter himself (85 cells up: beyond his 75); his lock on it is 3 s (lock 200), cued 2.1 s.
        var w = NewWorld();
        Radar(w, 50, Y - 20);
        var aa = w.Spawn(2, "manpads", 50, Y);
        var j = Jet(w, Fighter, 120);
        var shot = Engage(w, aa, j, 600).Shot;
        Assert.NotNull(shot);
        Assert.Equal(42, shot.Tracked);
    }

    [Fact]
    public void OtherSensorsGiveNoCuedLock()
    {
        // An air defence vehicle's radar (no cueLock) beside the soldier: he locks in his own 1.5 s, as before.
        var w = NewWorld();
        var sam = w.SpawnCrewed(2, "sam_vehicle", 50, Y - 20).Vehicle;
        sam.Stance = Stance.HoldFire;
        var aa = w.Spawn(2, "manpads", 50, Y);
        var j = Jet(w, Multirole, 80);
        var (shot, _, _, _) = Engage(w, aa, j, 400);
        Assert.Equal(30, shot!.Tracked);
        Assert.False(w.RadarCued(aa));

        // A packed-up mobile radar gives none either.
        w = NewWorld();
        Radar(w, 50, Y - 20, setUp: false);
        aa = w.Spawn(2, "manpads", 50, Y);
        j = Jet(w, Multirole, 80);
        Assert.Equal(30, Engage(w, aa, j, 400).Shot!.Tracked);
    }

    [Fact]
    public void TheCuedLockLeavesTheKillChanceAsItWas()
    {
        // The lock is quicker; the shot's chance is the same as a lone shooter's (its tracking bonus counts from the lock).
        var permille = new List<int>();
        foreach (var radar in new[] { false, true })
        {
            var w = NewWorld();
            if (radar)
            {
                Radar(w, 50, Y - 20);
            }
            var aa = w.Spawn(2, "manpads", 50, Y);
            var j = Jet(w, Multirole, 80);
            var shot = Engage(w, aa, j, 400).Shot!;
            var ic = shot.Weapon.Effect.Interceptor!;
            Assert.Equal(0, Math.Clamp(shot.Tracked - w.AirLockTicks(aa), 0, ic.TrackFull)); // launched the tick it locked
        }
    }

    // ---- Fog ----

    [Fact]
    public void UnderFogTheSideSeesWhatItTracksAndItsShootersEngageIt()
    {
        var w = NewWorld(fog: true);
        var r = Radar(w, 50, Y + 20);
        var far = Jet(w, Multirole, 400); // 350 cells out: nobody else of its side near
        var aa = w.Spawn(2, "manpads", 50, Y);
        var j = Jet(w, Fighter, 120);
        w.Run(VisionTicksPlus);
        Assert.True(World.IsSpotted(far, 2), "the side sees a jet only the radar tracks");
        Assert.True(World.IsSpotted(j, 2));
        var cued = Engage(w, aa, j, 400);
        Assert.NotNull(cued.Shot);

        // Packed up, it sees nothing in the sky: the far jet drops out of sight.
        w.Order(2, CommandKind.Stow, [r]);
        w.Run(VisionTicksPlus);
        Assert.False(World.IsSpotted(far, 2));
    }

    private const int VisionTicksPlus = 6;

    // ---- For the client ----

    [Fact]
    public void TheClientReadsItsStateTracksSweepAndCuedShooters()
    {
        var w = NewWorld();
        var r = Radar(w, 50, Y - 20);
        var status = World.RadarOf(r)!.Value;
        Assert.Equal((true, Fixed.FromInt(400), Fixed.Zero, Fixed.FromInt(6), 70), (status.On, status.Air, status.Ground, status.Sight, status.CueLock));
        Assert.Equal(7 * 512L, w.RadarEyeRaw(r)); // its antenna, 7 m up on flat ground
        var a = w.RadarSweep(r);
        w.Step();
        var b = w.RadarSweep(r);
        Assert.InRange(a, 0, 999);
        Assert.InRange((b - a + 1000) % 1000, 12, 13); // one turn every 4 s (80 ticks)

        var aa = w.Spawn(2, "manpads", 50, Y);
        var bystander = w.Spawn(2, "manpads", 450, 50); // tracks nothing
        var j = Jet(w, Multirole, 80);
        w.Run(3);
        Assert.Equal(j.Id, aa.AirTarget);
        Assert.True(w.RadarCued(aa));
        Assert.False(w.RadarCued(bystander));
        Assert.Equal(21, w.AirLockTicks(aa));
        Assert.Equal([aa], w.RadarCuedShooters(r));
        Assert.Equal([j], w.RadarTracks(r));
        Assert.Empty(w.RadarCuedShooters(aa)); // not a cueing radar
    }

    // ---- Determinism ----

    [Fact]
    public void TheSameOrdersGiveTheSameHashes()
    {
        ulong[] Run()
        {
            var w = NewWorld(seed: 7, fog: true);
            var r = Radar(w, 60, Y + 20);
            w.Spawn(2, "manpads", 50, Y);
            w.Spawn(2, "manpads", 70, Y - 30);
            w.SpawnCrewed(2, "sam_vehicle", 40, Y + 40);
            Jet(w, Fighter, 160);
            Jet(w, Multirole, 260, Y - 60);
            var hashes = new ulong[700];
            for (var i = 0; i < hashes.Length; i++)
            {
                if (i == 400)
                {
                    w.Order(2, CommandKind.Move, [r], cell: new Cell(80, Y + 20));
                }
                w.Step();
                hashes[i] = w.Hash();
            }
            return hashes;
        }
        Assert.Equal(Run(), Run());
    }
}

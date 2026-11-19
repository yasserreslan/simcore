using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// LANDING-INTERCEPT (2026-11-18): a multirole jet against a strategic bomber coming in to land at its main air base.
public class LandingInterceptTests(ITestOutputHelper output)
{
    private const long Cell1 = Fixed.OneRaw;

    // Both players Sentinel (aircraft on both sides), no starting army.
    private static World Duel(int n, ulong seed)
    {
        var content = TestContent.Mvp;
        var setup = TestContent.Sentinel(seed).For(content, seed);
        var p0 = setup.Players[0] with { Army = [] };
        var p1 = setup.Players[1] with { Army = [], Faction = p0.Faction, Presets = p0.Presets };
        return new World(new Grid(n, n), MatchCompiler.Compile(content, setup with { Players = [p0, p1] }));
    }

    private static void Issue(World w, CommandKind kind, Unit u, Cell cell = default, uint target = 0) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = kind, Units = [u.Id], Cell = cell, Target = target });

    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    // The jets' gun's range falloff reads a big aircraft's slant scaled by sizeRef / span: the multirole's gun 15 cells off a
    // B-52 type (span 23 cells) keeps 89% of its chance (535 permille of 600), as at a jet 3.3 cells off; against a jet (span 5
    // cells, not bigger than sizeRef) it is unchanged (half of 300 at full range); without a span (a drone) as before.
    [Fact]
    public void TheJetsGunFallsOffWithABigTargetsSpan()
    {
        var w = Duel(200, 1);
        var j = w.Spawn(2, "Multirole 500 lb", 100, 100, Facing.East);
        var gun = j.Def.Weapons.Single(x => x.Effect.AirGun is not null);
        var g = gun.Effect.AirGun!;
        Assert.Equal(50 * Cell1 / 10, g.SizeRef.Raw);
        var bomber = w.Content.Archetype("strategic_bomber")!;
        var span = bomber.Aircraft!.Span.Raw;
        Assert.Equal(23 * Cell1, span);
        var c = bomber.Flight!.Class;
        var far = gun.Range.Raw;
        Assert.Equal(535, World.AirGunChance(g, gun, c, 0, far, 0, 100, span));
        Assert.Equal(300, World.AirGunChance(g, gun, c, 0, far, 0)); // no span: the old falloff
        Assert.Equal(600, World.AirGunChance(g, gun, c, 0, 0, 0, 100, span));
        var jet = w.Content.Archetype("multirole_jet")!;
        Assert.Equal(150, World.AirGunChance(g, gun, jet.Flight!.Class, 0, far, 0, 100, jet.Aircraft!.Span.Raw)); // a jet: as before
    }

    // A multirole jet sent at a strategic bomber holding over its main air base follows it round its overhead pattern and down
    // its final approach: its IR missiles each meet two flare salvos (the bomber's rule: 30% of the chance left), its gun is
    // never decoyed and, the size-scaled falloff, hits a burst at least 24% of the time on the pattern (it was 14 to 24%);
    // on touchdown the attack ends (simcore M3b: a landed aircraft is never an attack target, only splash reaches it).
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void AJetOnABomberOnItsApproach(ulong seed)
    {
        var w = Duel(400, seed);
        var core = w.Player(1)!.Setup.Find(w.Content, "airbase_core")!;
        var af = w.SpawnAirBase(core, new Cell(40, 120), 0, []) ?? throw new InvalidOperationException("no base");
        var b = w.Spawn(1, "strategic_bomber", 260, 260, Facing.West);
        w.Step();
        Issue(w, CommandKind.ReturnToBase, b, target: af.Facility);
        Unit? j = null;
        var guns = new List<GunBurst>();
        var flares = new List<CountermeasureSalvo>();
        var launches = new List<AirLaunch>();
        var missileHits = 0;
        var endedOnTouchdown = false;
        for (var t = 0; t < 12000 && b.Alive && b.AirOps is not { Phase: FlightPhase.Parked }; t++)
        {
            w.Step();
            if (j is null && b.AirOps is { Phase: FlightPhase.Holding })
            {
                var vl = Math.Max(1, World.Len3(b.Vel.X.Raw, b.Vel.Y.Raw, 0));
                var x = (int)((b.Pos.X.Raw - b.Vel.X.Raw * 60 * Cell1 / vl) / Cell1);
                var y = (int)((b.Pos.Y.Raw - b.Vel.Y.Raw * 60 * Cell1 / vl) / Cell1);
                j = w.Spawn(2, "Multirole 500 lb", x, y, b.Facing); // 60 cells behind it
                w.Step();
                Issue(w, CommandKind.Attack, j, target: b.Id);
            }
            guns.AddRange(w.GunBursts.Where(g => g.Target == b.Id));
            flares.AddRange(w.Countermeasures.Where(c => c.Aircraft == b.Id));
            launches.AddRange(w.AirLaunches.Where(l => l.Target == b.Id));
            missileHits += w.AirBursts.Count(a => a.Target == b.Id && a.Kind == AirBurstKind.Hit);
            if (j is not null && b.AirOps is { Grounded: true } && !endedOnTouchdown)
            {
                w.Step();
                endedOnTouchdown = j.Order.Kind != OrderKind.Attack;
                Assert.DoesNotContain(w.GunBursts, g => g.Target == b.Id); // never fired at on the ground
            }
        }
        output.WriteLine($"seed {seed}: launches {launches.Count}, flares {flares.Count}, gun {string.Join(", ", guns.Select(g => $"{g.HitPermille}{(g.Hit ? " hit" : "")}"))}; " +
            $"bomber hp {b.HP}, jet ammo {string.Join("/", j!.Charges)}");
        Assert.Equal(2, launches.Count);
        Assert.All(launches, l => Assert.Equal(2, flares.Count(f => f.Missile == l.Missile))); // two flares a missile: a real rule
        Assert.True(guns.Count >= 3, $"{guns.Count} gun bursts on its approach");
        Assert.All(guns, g => Assert.InRange(g.HitPermille, 240, 600));
        Assert.True(endedOnTouchdown, "the attack ends on touchdown");
        Assert.Equal(b.Def.MaxHp - 90 * guns.Count(g => g.Hit) - 340 * missileHits, b.HP); // 90 a burst, 17% a missile hit
    }
}

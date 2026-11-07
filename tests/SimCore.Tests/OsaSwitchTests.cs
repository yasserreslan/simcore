using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// The Osa's radar switch (docs/design.md, decisions 990 to 993; owner 2026-11-07, "yes to all your recommendations"): the Hide stance
// switches the air defence vehicle's search radar off, as it does the mobile radar's, and with its radar off it cannot guide its own
// missiles, so it does not engage. An empty Osa is off too (decision 981's rule for radar vehicles).
public sealed class OsaSwitchTests
{
    private const int N = 1000;
    private const int Y = 500;
    private const string Multirole = "Multirole 500 lb";

    private static World NewWorld(ulong seed = 1) => Arena.NewSentinel(N, seed);

    private static Unit Osa(World w, int x, int y = Y) => w.SpawnCrewed(2, "sam_vehicle", x, y).Vehicle;

    private static void Stance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    // Jet: player 1's jet in flight over (x, y) at 170 m, orbiting there, no flares or chaff (what is measured is the launcher).
    private static Unit Jet(World w, string id, int x, int y = Y)
    {
        var j = w.Spawn(1, id, x, y, Facing.West);
        j.Altitude = Fixed.FromRaw(85 * Fixed.OneRaw);
        j.MutableSalvos[0] = 0;
        w.Order(1, CommandKind.Move, [j], cell: new Cell(x, y));
        return j;
    }

    private static Unit Ears(World w)
    {
        var ears = w.Spawn(1, "sigint_jet", 100, Y, Facing.East);
        ears.Altitude = Fixed.FromRaw(200 * Fixed.OneRaw);
        w.Order(1, CommandKind.Move, [ears], cell: new Cell(100, Y));
        return ears;
    }

    private static int Missiles(World w, Unit shooter) =>
        w.Projectiles.Count(p => p.Shooter == shooter.Id && p.Weapon.Effect.Interceptor is not null);

    // Fires reports whether the Osa launches a missile within ticks steps.
    private static bool Fires(World w, Unit osa, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            w.Step();
            if (Missiles(w, osa) > 0)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void TheOsaHasTheRadarSwitchAndStillNoSetUp()
    {
        var a = TestContent.Mvp.Archetype("sam_vehicle")!;
        Assert.True(a.RadarSwitch);
        Assert.Equal(0, a.DeployTicks);
        Assert.True(a.AirVision.Raw > 0);
    }

    [Fact]
    public void AnOsaSwitchedOnEngagesAJet()
    {
        var w = NewWorld();
        var osa = Osa(w, 200);
        var j = Jet(w, Multirole, 300);
        Assert.True(Fires(w, osa, 400));
        Assert.Equal(j.Id, w.Projectiles.First(p => p.Shooter == osa.Id).Target);
        Assert.True(w.RadarSearching(osa));
    }

    [Fact]
    public void AnOsaHiddenDoesNotEngageAndAnOsaSwitchedBackOnEngagesAgain()
    {
        var w = NewWorld();
        var osa = Osa(w, 200);
        Stance(w, osa, SimCore.Sim.Stance.Hide);
        w.Run(2);
        var j = Jet(w, Multirole, 300);
        Assert.False(Fires(w, osa, 400), "a hidden Osa has no radar to guide with");
        Assert.True(j.Alive);
        Assert.False(w.RadarSearching(osa));

        Stance(w, osa, SimCore.Sim.Stance.Guard);
        Assert.True(Fires(w, osa, 400));
        Assert.True(w.RadarSearching(osa));
    }

    [Fact]
    public void AnOsaHiddenIsNotHeardAndHasNoRadarPicture()
    {
        var w = NewWorld();
        var osa = Osa(w, 300);
        var ears = Ears(w);
        Stance(w, osa, SimCore.Sim.Stance.Hide);
        var air = Jet(w, Multirole, 460); // inside its 260 cells, out of anything it could shoot with anyway while it is off
        var pulse = ears.Def.Archetype.Listen!.PulseTicks;
        w.Run((pulse * 2) + 30);
        Assert.Empty(w.RadarContacts(1));
        Assert.False(w.AirTracked(2, air.Id));
        Assert.Empty(w.RadarTracks(osa));
        Assert.False(w.RadarSearching(osa));
        Assert.False(w.RadarSearching(osa));
        Assert.Equal(0, Missiles(w, osa));

        Stance(w, osa, SimCore.Sim.Stance.Guard);
        w.Run(pulse + 2);
        Assert.True(Assert.Single(w.RadarContacts(1)).Live, "switched on, it is heard again");
        Assert.True(w.RadarSearching(osa));
    }

    // A missile already in flight keeps its target when the Osa is switched off: its seeker is its own (only the SA-6's semi-active
    // missile needs a radar's track all the way).
    [Fact]
    public void AMissileInFlightKeepsFlyingWhenTheOsaIsSwitchedOff()
    {
        var w = NewWorld();
        var osa = Osa(w, 200);
        var j = Jet(w, Multirole, 330);
        Assert.True(Fires(w, osa, 400));
        Stance(w, osa, SimCore.Sim.Stance.Hide);
        w.Step();
        Assert.False(w.RadarSearching(osa));
        var m = w.Projectiles.FirstOrDefault(p => p.Shooter == osa.Id && p.Weapon.Effect.Interceptor is not null && !p.Done);
        if (m is not null)
        {
            Assert.True(m.Guided);
            Assert.Equal(j.Id, m.Target);
        }
    }

    // Switched on and with a driver, the Osa is heard and tracks (the control for the two tests above and below).
    [Fact]
    public void AnOsaSwitchedOnIsHeardAndTracks()
    {
        var w = NewWorld();
        var osa = Osa(w, 300);
        var ears = Ears(w);
        var air = Jet(w, Multirole, 460);
        w.Run(ears.Def.Archetype.Listen!.PulseTicks + 30);
        Assert.True(Assert.Single(w.RadarContacts(1)).Live);
        Assert.True(w.RadarSearching(osa));
        Assert.True(w.AirTracked(2, air.Id));
    }

    // No setup does not mean a free radar (decision 981): with no crew aboard the Osa is off, as the mobile radar is.
    [Fact]
    public void AnEmptyOsaIsOff()
    {
        var w = NewWorld();
        var osa = w.Spawn(2, "sam_vehicle", 300, Y);
        var air = Jet(w, Multirole, 360);
        var ears = Ears(w);
        Assert.Equal(0u, osa.Driver);
        Assert.False(Fires(w, osa, (ears.Def.Archetype.Listen!.PulseTicks * 2) + 30));
        Assert.False(w.RadarSearching(osa));
        Assert.False(w.AirTracked(2, air.Id));
        Assert.Empty(w.RadarContacts(1));
        Assert.Empty(w.RadarTracks(osa));
    }
}

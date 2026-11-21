using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-E1b-1: the ground and the soldier's senses (docs/design.md, "Infantry combat: the ground and the senses").
public class InfantryCombatTests
{
    private static World New(int size = 40, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false; // the arena turns the rules off for other tests' timings; on here (before any unit is added)
        return w;
    }

    private static InfantryCombatRules R(World w) => w.Content.InfantryCombat!;

    private static void Form(World w, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });

    [Fact]
    public void TheBlockLoadsEveryNumberAndTheSmallArms()
    {
        var w = New();
        var r = R(w);
        Assert.Equal(CombatNames.Names.Length, Enum.GetValues<CombatValue>().Length);
        Assert.Equal((8, 16, 120, 10, 22, 55, 60), (r[CombatValue.ContactsPerSoldier], r[CombatValue.ContactsPerSquad], r[CombatValue.AlertRange],
            r[CombatValue.DangerRifle], r[CombatValue.DangerLmg], r[CombatValue.LowFiring], r[CombatValue.Pinned]));
        var rifle = w.Spawn(1, "Rifleman", 5, 5);
        Assert.NotNull(rifle.Fight);
        Assert.Equal(CombatClass.Rifle, r.Weapon(rifle.Def.Weapons[0].Effect)!.Value.Class);
        Assert.Equal((250, 4), (r.Weapon(rifle.Def.Weapons[0].Effect)!.Value.Sound, r.Weapon(rifle.Def.Weapons[0].Effect)!.Value.Suppress));
        Assert.Null(Arena.New(16).Spawn(1, "Rifleman", 5, 5).Fight); // the arena's default: off
    }

    [Fact]
    public void DirectionsAreTheNearestOfThirtyTwoAndStepsWrap()
    {
        Assert.Equal(0, World.Dir32(1000, 0));
        Assert.Equal(8, World.Dir32(0, 1000)); // south: y grows down the map
        Assert.Equal(4, World.Dir32(500, 500));
        Assert.Equal(16, World.Dir32(-7, 0));
        Assert.Equal(-1, World.Dir32(0, 0));
        Assert.Equal(2, World.DirSteps(31, 1));
        Assert.Equal(16, World.DirSteps(0, 16));
    }

    [Fact]
    public void AWallFillsItsSideAndTheTwoNextToItAndProtectsByPosture()
    {
        var w = New();
        w.Grid.Set(new Cell(11, 10), Terrain.Blocked); // rock east of (10, 10): low hard
        var c = new Cell(10, 10);
        Assert.Equal(CoverKind.Low, w.CoverSide(c, 0));
        Assert.Equal(CoverKind.Low, w.CoverSide(c, 1));
        Assert.Equal(CoverKind.Low, w.CoverSide(c, 7));
        Assert.Equal(CoverKind.None, w.CoverSide(c, 4));
        Assert.Equal(55, w.Protection(c, Posture.Kneel, 0, false)); // firing over it
        Assert.Equal(85, w.Protection(c, Posture.Kneel, 0, true)); // ducked
        Assert.Equal(55 * 25 / 100, w.Protection(c, Posture.Stand, 0, false)); // low cover: a standing man's legs
        Assert.Equal(55, w.Protection(c, Posture.Kneel, 5, false)); // 56° off: still the wall (the fill rule's 67.5°)
        Assert.Equal(0, w.Protection(c, Posture.Kneel, 16, false)); // from the west: flanked
    }

    [Fact]
    public void ABuildingIsHighCoverAndTheDataFollowsTheMap()
    {
        var w = New();
        var s = w.Spawn(1, "Rifleman", 5, 5);
        w.Step(); // builds the cover data
        var c = new Cell(20, 20);
        Assert.Equal(CoverKind.None, w.CoverSide(c, 2));
        w.Grid.Set(new Cell(20, 21), Terrain.Blocked); // a wall appears south of it
        w.Step();
        Assert.Equal(CoverKind.Low, w.CoverSide(c, 2));
        w.Grid.Set(new Cell(20, 21), Terrain.Clear);
        w.Step();
        Assert.Equal(CoverKind.None, w.CoverSide(c, 2));
        Assert.True(w.CoverBytes == 40L * 40 * 4 + 3L * 8 * ((40 * 40 + 63) / 64));
        Assert.True(s.Alive);
    }

    [Fact]
    public void ShotsSuppressInCoverLessAndItDecaysAfterAPause()
    {
        var w = New();
        var r = R(w);
        var a = w.Spawn(1, "Rifleman", 10, 10);
        var b = w.Spawn(2, "Rifleman", 19, 10); // beyond each other's range: no shots of their own
        var c = w.Spawn(2, "Rifleman", 30, 30);
        w.Grid.Set(new Cell(29, 30), Terrain.Blocked); // c has low hard cover toward the west
        w.Step();
        w.NoteSuppressingShot(a, a.Def.Weapons[0], b, false, b.Pos);
        Assert.Equal(4, b.Fight!.Suppression);
        w.NoteSuppressingShot(a, a.Def.Weapons[0], b, true, b.Pos);
        Assert.Equal(4 + 4 + 15, b.Fight.Suppression);
        Assert.Equal(w.Tick, b.Fight.ShotNear1);
        w.NoteSuppressingShot(a, a.Def.Weapons[0], c, true, c.Pos);
        Assert.Equal((4 + 15) / 2, c.Fight!.Suppression); // hard cover facing the fire: half
        var before = b.Fight.Suppression100;
        for (var i = 0; i < r[CombatValue.SuppressHoldTicks] - 1; i++)
        {
            w.Step();
            Assert.True(b.Fight.Suppression100 >= before, "no decay during the pause");
        }
    }

    [Fact]
    public void PostureChangesTakeTheirTimeAndHold()
    {
        var w = New();
        var r = R(w);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        w.Step();
        var f = u.Fight!;
        Assert.Equal(Posture.Stand, f.Posture);
        f.InContact = true; // as if an enemy were in reach: the next think kneels him (row 10)
        f.Axis = 0;
        f.ReactAt = 0;
        var crossing = false;
        for (var i = 0; i < 5 + r[CombatValue.StandKneel] + 1; i++)
        {
            w.Step();
            crossing |= f.PostureLeft > 0 && f.PostureTo == Posture.Kneel;
            f.InContact = true; // keep the fake contact through his own thinks
            f.ShotNear1 = w.Tick;
        }
        Assert.True(crossing);
        Assert.Equal(Posture.Kneel, f.Posture);
        Assert.Equal(0, w.PosturePercent(u)); // a kneeling man asked to move gets up first: no speed this tick
        Assert.True(f.PostureLeft > 0 && f.PostureTo == Posture.Stand);
    }

    [Fact]
    public void ASoldierSeesHearsAndSharesWithHisSquad()
    {
        var w = New(64, 3);
        var a1 = w.Spawn(1, "Rifleman", 10, 10);
        var a2 = w.Spawn(1, "Rifleman", 10, 30); // 20 cells off: can't see the enemy
        var e = w.Spawn(2, "Rifleman", 18, 10); // 8 cells from a1: seen
        Form(w, a1, a2);
        for (var i = 0; i < 120 && !a2.Fight!.Contacts.Any(k => k.Enemy == e.Id); i++)
        {
            w.Step();
        }
        Assert.Contains(a2.Fight!.Contacts, k => k.Enemy == e.Id && k.Source == ContactSource.Reported);
        Assert.Contains(a1.Fight!.Contacts, k => k.Enemy == e.Id && k.Source is ContactSource.SeenNow or ContactSource.SeenBefore);
        Assert.True(a1.Fight.ReactAt != 0);
        Assert.True(a1.Fight.Contacts.Count <= R(w)[CombatValue.ContactsPerSoldier]);
        Assert.Contains(w.SquadOf(a1.Id)!.Mind!.Picture, k => k.Enemy == e.Id);
    }

    [Fact]
    public void AShotFarOffIsHeardAsAnAreaTheSameForEveryListener()
    {
        var w = New(80, 5);
        var l1 = w.Spawn(1, "Rifleman", 10, 10);
        var l2 = w.Spawn(1, "Rifleman", 10, 14);
        var shooter = w.Spawn(2, "Rifleman", 30, 12); // 20 cells: beyond sight, inside the rifle's 25-cell sound
        for (var i = 0; i < 40 && (l1.Fight!.LastThink == 0 || l2.Fight!.LastThink == 0); i++)
        {
            w.Step();
        }
        shooter.LastShot = Math.Max(l1.Fight!.LastThink, l2.Fight!.LastThink) + 1;
        w.Run(25);
        var h1 = l1.Fight.Contacts.Where(k => k.Enemy == 0 && k.Source == ContactSource.Heard).ToList();
        var h2 = l2.Fight!.Contacts.Where(k => k.Enemy == 0 && k.Source == ContactSource.Heard).ToList();
        Assert.Single(h1);
        Assert.Single(h2);
        Assert.Equal(h1[0].At, h2[0].At);
        Assert.InRange(h1[0].At.X, 28, 32);
        Assert.InRange(h1[0].At.Y, 10, 14);
    }

    [Fact]
    public void ExposureCountsDangerCoverPostureAndZones()
    {
        long Exposure(bool wall, Posture p)
        {
            var w = New();
            if (wall)
            {
                w.Grid.Set(new Cell(11, 10), Terrain.Blocked);
            }
            var u = w.Spawn(1, "Rifleman", 10, 10);
            var e = w.Spawn(2, "Rifleman", 13, 10); // 3 cells: inside 60% of the rifle's range
            u.Fight!.ContactList.Add(new Contact { Enemy = e.Id, At = Cell.Of(e.Pos), Source = ContactSource.SeenNow, Class = CombatClass.Rifle });
            u.Fight.InContact = true;
            return w.Exposure(u, Cell.Of(u.Pos), p);
        }
        Assert.Equal(1000, Exposure(false, Posture.Stand)); // one rifleman (10) standing in the open: 10.00
        Assert.Equal(800, Exposure(false, Posture.Kneel));
        Assert.Equal(500, Exposure(false, Posture.Prone));
        Assert.Equal(10L * 45 * 80 * 100 * 100 / 1_000_000, Exposure(true, Posture.Kneel)); // behind the wall, firing: 55% protected
    }

    [Fact]
    public void TheSameFightReplaysToTheSameHash()
    {
        ulong Play()
        {
            var w = New(48, 9);
            w.SureHits = false;
            var blue = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "Rifleman", 10, 10 + i)).ToArray();
            var red = Enumerable.Range(0, 4).Select(i => w.Spawn(2, "Rifleman", 14, 10 + i)).ToArray();
            Form(w, blue);
            Form(w, red);
            w.Run(300);
            Assert.Contains(blue.Concat(red), u => u.Fight!.Suppression > 0 || !u.Alive);
            Assert.Equal(w.Hash(), w.HashReference());
            return w.Hash();
        }
        Assert.Equal(Play(), Play());
    }
}

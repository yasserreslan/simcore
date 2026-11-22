using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// INF-E1b-8 (rules 6.6, 14.1, 11.7 step 1, 18): soldiers use buildings on their own, and grenades go into buildings.
public class BuildingCoverTests(ITestOutputHelper output)
{
    private static World New(int size = 48, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false;
        return w;
    }

    private static Squad Form(World w, params Unit[] units)
    {
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.FormSquad, Units = [.. units.Select(u => u.Id)] });
        w.Step();
        return w.SquadOf(units[0].Id)!;
    }

    private static Unit[] Men(World w, byte player, int x0, int y, int n, int dx = 1) =>
        [.. Enumerable.Range(0, n).Select(i => w.Spawn(player, "Rifleman", x0 + i * dx, y))];

    private static Unit House(World w, int x, int y) => w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "house_small")!, new Cell(x, y), Facing.South))!;

    // 6.6: four idle rifles in the open with the enemy to the south and a small house behind them (its door 2 cells back, no cover
    // toward the enemy anywhere else): the house's door is a candidate (inside: high cover from every side) and men go in on their
    // own, by the garrison's entry, never more than it holds; when the squad moves on (a Move order) they come out.
    [Fact]
    public void MenTakeAnEmptyHouseAsCoverAndComeOutWhenTheSquadMovesOn()
    {
        var w = New();
        var house = House(w, 28, 21); // 5 x 5, rows 21 to 25, its door at (30, 26)
        var blue = Men(w, 1, 28, 28, 4, 2);
        var red = Men(w, 2, 27, 33, 4, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        var s = Form(w, blue);
        var inside = 0;
        var first = 0u;
        for (var i = 0; i < 400 && inside == 0; i++)
        {
            w.Step();
            inside = blue.Count(u => u.Garrison == house.Id);
            first = inside > 0 && first == 0 ? w.Tick : first;
        }
        for (var i = 0; i < 100; i++)
        {
            w.Step();
            inside = Math.Max(inside, blue.Count(u => u.Garrison == house.Id));
        }
        var m = s.Mind!;
        output.WriteLine($"first in t{first}, inside {inside} of {house.Def.Archetype.Garrison!.Capacity}; {m.Choice} R {m.Ratio}; men {string.Join(" ", blue.Select(u => $"{Cell.Of(u.Pos)} g{u.Garrison} b{u.Fight!.Building} {u.Order.Kind}"))}");
        Assert.True(first > 0, "a man went into the house on his own");
        Assert.True(inside <= house.Def.Archetype.Garrison!.Capacity);
        Assert.All(blue.Where(u => u.Garrison == house.Id), u => Assert.Equal(house.Id, u.Fight!.Building));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Move, Units = [.. blue.Select(u => u.Id)], Cell = new Cell(10, 28) });
        for (var i = 0; i < 20; i++)
        {
            w.Step();
        }
        output.WriteLine($"after the move: {string.Join(" ", blue.Select(u => $"{Cell.Of(u.Pos)} g{u.Garrison} b{u.Fight!.Building} {u.Order.Kind}"))}");
        Assert.All(blue, u => Assert.Equal(0u, u.Garrison));
    }

    // 6.6: a building with an enemy inside is never a candidate (an Assault's business, 11.7), nor one his side knows is rigged.
    [Fact]
    public void NobodyGoesOnHisOwnIntoAHouseTheEnemyHolds()
    {
        var w = New();
        var house = House(w, 28, 21);
        World.EnterBuilding(w.Spawn(2, "Rifleman", 5, 5), house);
        var blue = Men(w, 1, 28, 28, 4, 2);
        var red = Men(w, 2, 27, 33, 4, 2);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [.. red.Select(u => u.Id)], Stance = Stance.HoldFire });
        Form(w, blue);
        for (var i = 0; i < 400; i++)
        {
            w.Step();
            Assert.All(blue, u => Assert.True(u.Garrison == 0 && u.Fight is not { Building: not 0 }, $"t{w.Tick} {u.Id} heads for or is in the enemy's house"));
        }
    }
    // 14.1, 14.2 (INF-E1b-8, row 1324): an enemy he knows inside a building within throwing range (a man inside is never seen:
    // known by his fire from it, or seen going in within seenTicks): he throws through the window or door on his side (here the window
    // 3 cells off: the door, 2 cells off, is within his own blast clearance), and the blast hurts the man inside (row 1328: at 60% of
    // the centre value; it was the building's occupantDamage share); the squad's cooldown is the building's.
    [Fact]
    public void AGrenadeGoesInThroughTheWindowAndHurtsTheManInside()
    {
        var w = New();
        var house = House(w, 28, 21); // its door at (30, 26)
        var red = w.Spawn(2, "Rifleman", 5, 5);
        World.EnterBuilding(red, house);
        var blue = w.Spawn(1, "Rifleman", 30, 28);
        var hp = red.HP; // (a man inside is never seen: blue knows him by his fire from the house, 4.1's fire reveal)
        blue.HP = 1000; // (he lives through red's fire until his throw)
        uint threw = 0, hurt = 0;
        var to = default(Vec);
        for (var i = 0; i < 300 && hurt == 0; i++)
        {
            w.Step();
            if (threw == 0 && blue.Fight!.ThrowAt != 0)
            {
                (threw, to) = (w.Tick, blue.Fight.ThrowTo);
            }
            hurt = red.HP < hp ? w.Tick : 0;
        }
        output.WriteLine($"blue hp {blue.HP} supp {blue.Fight!.Suppression} {blue.Fight.Posture} t{w.Tick}; contacts {string.Join(" ", blue.Fight!.Contacts.Select(k => $"{k.Enemy}@{k.At}:{k.Source}:{k.Tick}/{k.KnownAt}"))}; threw t{threw} to {Cell.Of(to)}, red hp {hp} -> {red.HP} at t{hurt}, grenades left {blue.Fight.Grenades}");
        Assert.True(threw > 0, "he threw at the man in the house");
        Assert.Equal(new Cell(30, 25), Cell.Of(to)); // the house's south wall, facing him
        Assert.True(hurt > 0 && red.HP < hp, "the blast hurt the man inside");
        Assert.Equal(house.Id, red.Garrison);
    }

    // 14.1 (INF-E1b-9, row 1328; the coordinator's call 2, ~/Desktop/rts-work/handover/inf-e1b9-brief.md): a grenade thrown in through
    // the window or door does 60% of the centre value (grenades.inside: 27 of 45 through the explosive warhead) to every man in that
    // building, no occupant share on top; a blast that hits the building from outside (a shell here) still passes its occupantDamage
    // share (25%). On 5e78ab6 the grenade landed outside and both men took the building's 25% share (10 HP each).
    [Fact]
    public void AGrenadeThrownInHurtsEveryManInsideAtSixtyPercentOfTheCentreAndAShellOutsideByTheBuildingsShare()
    {
        var w = New();
        var house = House(w, 28, 21); // its door at (30, 26)
        var red = w.Spawn(2, "Rifleman", 5, 5);
        var mate = w.Spawn(2, "Rifleman", 6, 5);
        World.EnterBuilding(red, house);
        World.EnterBuilding(mate, house);
        var blue = w.Spawn(1, "Rifleman", 30, 28);
        blue.HP = 1000; // (he lives through red's fire until his throw)
        var (hp, mateHp) = (red.HP, mate.HP);
        var into = 0u;
        for (var i = 0; i < 300 && red.HP == hp; i++)
        {
            w.Step();
            into = w.Grenades.FirstOrDefault().Into is var g and not 0 ? g : into;
        }
        var explosive = w.Content.Warhead("explosive")!;
        var want = explosive.Damage(45, red.Def.Armor, 60);
        output.WriteLine($"t{w.Tick}: into {into}, red {hp} -> {red.HP}, mate {mateHp} -> {mate.HP} (want -{want})");
        Assert.Equal(house.Id, into);
        Assert.Equal(hp - want, red.HP);
        Assert.Equal(mateHp - want, mate.HP);
        (hp, mateHp) = (red.HP, mate.HP);
        var share = house.Def.Archetype.Garrison!.OccupantDamage;
        w.Explode(new Cell(30, 27).Center(), 45, explosive, Fixed.FromInt(2)); // a shell by its door
        output.WriteLine($"a shell by the door: red {hp} -> {red.HP}, mate {mateHp} -> {mate.HP} (share {share}%)");
        Assert.True(red.HP < hp && hp - red.HP <= want * share / 60 + 1, "a blast from outside passes the building's share");
        Assert.Equal(hp - red.HP, mateHp - mate.HP);
    }
}

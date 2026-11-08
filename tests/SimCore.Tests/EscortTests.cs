using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// Escort (ESCORT, 2026-11-08; docs/design.md, "Escort", decisions 1046 to 1057): the order and its refusals, the spots by the group's
// size and the unit's heading, the pace, threats and the leash from the escorted unit, stops and the Haul loop, waiting while it is
// inside, guarding where it fell, the end of the order, many escorts, and the hash.
public class EscortTests(ITestOutputHelper output)
{
    private static double Cells(Vec a, Vec b) => (a - b).Len().Raw / (double)Fixed.OneRaw;

    private static double Speed(Unit u) => u.Vel.Len().Raw / (double)Fixed.OneRaw;

    private static World Duel(int size = 96, bool economy = false) =>
        Arena.NewSentinel(size, 1, s => economy ? s with { Economy = true, StartGold = 0 } : s);

    private static void Escort(World w, IEnumerable<Unit> escorts, Unit ward, byte player = 1) =>
        w.Order(player, CommandKind.Escort, escorts, ward.Id);

    private static int RunUntil(World w, Func<bool> done, int max)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    [Fact]
    public void TheOrderTakesGroundUnitsAndRefusesAircraftItselfLoopsAndOthers()
    {
        var w = Duel();
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 30, 30).Vehicle;
        var humvee = w.SpawnCrewed(1, "humvee", 24, 30).Vehicle;
        var man = w.Spawn(1, "Rifleman", 26, 34);
        var other = w.Spawn(1, "Rifleman", 27, 35);
        var heli = w.Spawn(1, "attack_helicopter", 20, 20);
        var pad = w.Spawn(1, "helipad", 50, 50);
        var foe = w.SpawnCrewed(2, "technical", 80, 80).Vehicle;
        Escort(w, [humvee, man, heli], truck);
        w.Run(1);
        Assert.Equal((OrderKind.Escort, truck.Id), (humvee.Order.Kind, humvee.Order.Target));
        Assert.Equal((OrderKind.Escort, truck.Id), (man.Order.Kind, man.Order.Target));
        Assert.NotEqual(OrderKind.Escort, heli.Order.Kind); // aircraft are not escorts for now
        Assert.Equal(EscortRefusal.Aircraft, w.EscortRefusalOf(heli, truck));
        Assert.Equal(2, w.EscortsOf(truck.Id));
        Assert.Equal(EscortRefusal.Self, w.EscortRefusalOf(truck, truck));
        Assert.Equal(EscortRefusal.Loop, w.EscortRefusalOf(truck, humvee)); // the Humvee escorts the truck
        Assert.Equal(EscortRefusal.NotOwn, w.EscortRefusalOf(other, foe));
        Assert.Equal(EscortRefusal.NotEscortable, w.EscortRefusalOf(other, heli));
        Assert.Equal(EscortRefusal.CannotMove, w.EscortRefusalOf(pad, truck));
        Escort(w, [truck], humvee);
        Escort(w, [other], foe);
        w.Run(1);
        Assert.NotEqual(OrderKind.Escort, truck.Order.Kind);
        Assert.NotEqual(OrderKind.Escort, other.Order.Kind);
        // A chain is fine; closing it is a loop: the truck can't escort the soldier who escorts the man who escorts the truck.
        Escort(w, [other], man);
        w.Run(1);
        Assert.Equal((OrderKind.Escort, man.Id), (other.Order.Kind, other.Order.Target));
        Assert.Equal(EscortRefusal.Loop, w.EscortRefusalOf(truck, other));
        Escort(w, [truck], other);
        w.Run(1);
        Assert.NotEqual(OrderKind.Escort, truck.Order.Kind);
    }

    [Fact]
    public void SpotsGoByTheGroupsSizeFrontFlanksRearThenRings()
    {
        Assert.Equal((0, 1), World.EscortSpot(1, 0));
        Assert.Equal(new[] { (0, 1), (16, 1) }, Enumerable.Range(0, 2).Select(i => World.EscortSpot(2, i)));
        Assert.Equal(new[] { (0, 1), (-8, 1), (8, 1) }, Enumerable.Range(0, 3).Select(i => World.EscortSpot(3, i)));
        Assert.Equal(new[] { (0, 1), (-8, 1), (8, 1), (16, 1), (-4, 2), (4, 2) }, Enumerable.Range(0, 6).Select(i => World.EscortSpot(6, i)));
        Assert.Equal((16, 2), World.EscortSpot(12, 11));
        Assert.Equal((0, 3), World.EscortSpot(13, 12));
        Assert.Equal((-2, 3), World.EscortSpot(14, 13));
        Assert.Equal((16, 3), World.EscortSpot(28, 27));
        Assert.Equal((0, 4), World.EscortSpot(29, 28));
        var spots = Enumerable.Range(0, 28).Select(i => World.EscortSpot(28, i)).ToList();
        Assert.Equal(spots.Count, spots.Distinct().Count());
    }

    [Fact]
    public void FourEscortsFormUpRoundAStandingTruckInfantryCloserIn()
    {
        var w = Duel();
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40, Facing.East).Vehicle;
        var hv = new[] { w.SpawnCrewed(1, "humvee", 30, 36).Vehicle, w.SpawnCrewed(1, "humvee", 30, 44).Vehicle };
        var men = new[] { w.Spawn(1, "Rifleman", 34, 40), w.Spawn(1, "Rifleman", 35, 41) };
        Escort(w, [.. hv, .. men], truck);
        w.Run(400);
        var all = hv.Concat(men).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3 }, all.Select(u => u.Escort!.Spot).Order());
        foreach (var u in all)
        {
            Assert.True(Cells(u.Pos, u.Escort!.Point) <= 1.5, $"{u.Def.Archetype.Id} {Cells(u.Pos, u.Escort.Point):0.0} cells off its spot");
        }
        var front = Assert.Single(all, u => u.Escort!.Spot == 0);
        Assert.True(front.Escort!.Point.X.Raw > truck.Pos.X.Raw + 2 * Fixed.OneRaw); // ahead: the truck faces east
        Assert.True(men.Max(m => Cells(m.Escort!.Point, truck.Pos)) < hv.Min(h => Cells(h.Escort!.Point, truck.Pos)));
    }

    [Fact]
    public void OnTheMoveTheyKeepTheirSpotsAtItsPaceAndCatchUpWhenBehind()
    {
        var w = Duel(160);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 20, 40, Facing.East).Vehicle;
        var lead = w.SpawnCrewed(1, "humvee", 26, 40, Facing.East).Vehicle;
        Escort(w, [lead], truck);
        w.Run(200);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(140, 40));
        double worst = 0, ahead = 0, outran = 0;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            if (t > 100 && truck.Vel.LenSq() > 0)
            {
                worst = Math.Max(worst, Cells(lead.Pos, lead.Escort!.Point));
                ahead = Math.Max(ahead, Cells(lead.Pos, truck.Pos));
                if (Cells(lead.Pos, lead.Escort.Point) < 0.5)
                {
                    outran = Math.Max(outran, Speed(lead) - Speed(truck));
                }
            }
        }
        output.WriteLine($"worst {worst:0.00} cells off, at most {ahead:0.0} cells from the truck, {outran:0.000} cells a tick faster on its spot");
        Assert.True(worst < 2, $"{worst:0.00} cells off its spot");
        Assert.True(outran < 0.08, $"{outran:0.000} cells a tick faster than the truck on its spot");
        Assert.True(lead.Pos.X > truck.Pos.X); // still in front
        // One far behind closes in faster than the truck goes.
        var late = w.SpawnCrewed(1, "humvee", (int)(truck.Pos.X.Raw / Fixed.OneRaw) - 30, 40, Facing.East).Vehicle;
        Escort(w, [late], truck);
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(150, 40));
        var faster = 0;
        for (var t = 0; t < 200; t++)
        {
            w.Step();
            if (Cells(late.Pos, late.Escort!.Point) > 4 && Speed(late) > Speed(truck) + 0.02)
            {
                faster++;
            }
        }
        Assert.True(faster > 40, $"{faster} ticks faster than the truck");
    }

    [Fact]
    public void FromBehindItGoesRoundTheUnitToTheFrontSpot()
    {
        var w = Duel(200);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 30, 40, Facing.East).Vehicle;
        var hv = w.SpawnCrewed(1, "humvee", 24, 40, Facing.East).Vehicle; // right behind it: its spot is ahead
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(190, 40));
        w.Run(20);
        Escort(w, [hv], truck);
        Assert.True(RunUntil(w, () => Cells(hv.Pos, hv.Escort!.Point) < 1.5 && hv.Pos.X > truck.Pos.X, 700) > 0,
            $"{Cells(hv.Pos, hv.Escort!.Point):0.0} cells off its spot ahead");
    }

    [Fact]
    public void TurningRoundTheySwapSpotsInsteadOfCrossing()
    {
        var w = Duel(160);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 60, 40, Facing.East).Vehicle;
        var a = w.SpawnCrewed(1, "humvee", 66, 40, Facing.East).Vehicle; // ahead of it
        var b = w.SpawnCrewed(1, "humvee", 54, 40, Facing.East).Vehicle; // behind it
        Escort(w, [a, b], truck);
        w.Run(200);
        Assert.Equal((0, 1), (a.Escort!.Spot, b.Escort!.Spot));
        w.Order(1, CommandKind.Move, [truck], cell: new Cell(20, 40)); // back the way it came
        var crossed = false;
        for (var t = 0; t < 600; t++)
        {
            w.Step();
            crossed |= a.Pos.X.Raw < truck.Pos.X.Raw - Fixed.OneRaw;
        }
        Assert.Equal((1, 0), (a.Escort!.Spot, b.Escort!.Spot)); // a is behind it now, b in front
        Assert.False(crossed);
        Assert.True(b.Pos.X < truck.Pos.X && a.Pos.X > truck.Pos.X);
    }

    [Fact]
    public void WhatHurtsItComesFirstAndTheLeashCountsFromIt()
    {
        var w = Duel(128);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40, Facing.East).Vehicle;
        truck.HP = 50_000;
        var hv = w.SpawnCrewed(1, "humvee", 46, 40, Facing.East).Vehicle;
        hv.HP = 50_000;
        Escort(w, [hv], truck);
        w.Run(100);
        var near = w.Spawn(2, "Rifleman", 50, 44); // in the Humvee's reach, doing nothing
        near.HP = 50_000;
        near.Stance = Stance.HoldFire;
        var shooter = w.Spawn(2, "Rifleman", 33, 34); // behind the truck: it shoots the truck
        shooter.HP = 50_000;
        w.Order(2, CommandKind.Attack, [shooter], truck.Id);
        Assert.True(RunUntil(w, () => truck.LastHitBy == shooter.Id, 400) > 0);
        Assert.True(RunUntil(w, () => hv.Ai == AiState.Engage && hv.AiTarget == shooter.Id, 40) > 0); // the truck's attacker first
        // Off it runs, past the leash (the Humvee's vision x 2.2) from the truck, not from where the Humvee stood.
        var leash = hv.Def.Vision.Raw * 220 / 100 / (double)Fixed.OneRaw;
        w.Order(2, CommandKind.Move, [shooter], cell: new Cell(4, 4));
        Assert.True(RunUntil(w, () => hv.Ai == AiState.None || hv.AiTarget != shooter.Id, 1200) > 0);
        output.WriteLine($"the chase ended {Cells(shooter.Pos, truck.Pos):0.0} cells from the truck; the leash {leash:0.0}");
        Assert.InRange(Cells(shooter.Pos, truck.Pos), leash - 1.5, leash + 1.5);
        // Back to its spot.
        Assert.True(RunUntil(w, () => hv.Ai == AiState.None && Cells(hv.Pos, hv.Escort!.Point) < 1.5, 600) > 0);
        Assert.Equal(OrderKind.Escort, hv.Order.Kind);
    }

    [Fact]
    public void AnEnemySpottedNearItIsEngaged()
    {
        var w = Duel(96);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40, Facing.East).Vehicle;
        var man = w.Spawn(1, "Rifleman", 44, 40);
        man.HP = 50_000;
        Escort(w, [man], truck);
        w.Run(100);
        var foe = w.Spawn(2, "Rifleman", 41, 48); // in the soldier's sight, within its vision x 1.8 of the truck
        foe.HP = 50_000;
        foe.Stance = Stance.HoldFire;
        Assert.True(Cells(foe.Pos, truck.Pos) <= man.Def.Vision.Raw * 180 / 100 / (double)Fixed.OneRaw);
        Assert.True(RunUntil(w, () => man.Ai == AiState.Engage && man.AiTarget == foe.Id, 100) > 0);
        // Hold fire: escorts don't chase.
        var quiet = w.Spawn(1, "Rifleman", 36, 40);
        quiet.Stance = Stance.HoldFire;
        Escort(w, [quiet], truck);
        w.Run(100);
        Assert.Equal(AiState.None, quiet.Ai);
    }

    [Fact]
    public void TheyHoldAcrossItsHaulLoopAndFormUpWhenItStops()
    {
        var w = Duel(200, economy: true);
        var m = w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, "gold_adit")!, new Cell(40, 20), 0, 0))!;
        w.Spawn(1, "bank_sentinel", 40, 110);
        var miners = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "miner", 40 + 2 * i, 38)).ToList();
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 30, 42).Vehicle;
        var hv = new[] { w.SpawnCrewed(1, "humvee", 24, 46).Vehicle, w.SpawnCrewed(1, "humvee", 22, 40).Vehicle };
        w.Order(1, CommandKind.Mine, miners, m.Id);
        w.Order(1, CommandKind.Haul, [truck], m.Id);
        Escort(w, hv, truck);
        var banked = RunUntil(w, () => w.Player(1)!.Credits >= 300, 8000);
        Assert.True(banked > 0, "the truck never banked");
        Assert.True(RunUntil(w, () => truck.Econ!.Stage == EconStage.Parked, 4000) > 0); // back at the mine
        Assert.Equal(OrderKind.Haul, truck.Order.Kind);
        w.Run(200); // parked: they form up round it
        foreach (var u in hv)
        {
            Assert.Equal((OrderKind.Escort, truck.Id), (u.Order.Kind, u.Order.Target));
            Assert.True(Cells(u.Pos, u.Escort!.Point) <= 2, $"{Cells(u.Pos, u.Escort.Point):0.0} cells off its spot");
            Assert.True(Cells(u.Pos, truck.Pos) <= 8);
        }
    }

    [Fact]
    public void WhileItIsInsideTheyWaitAndPickItUpWhenItComesOut()
    {
        var w = Arena.New(96);
        var house = w.Spawn(1, "house_small", 40, 40);
        var ward = w.Spawn(1, "Rifleman", 40, 50);
        var men = new[] { w.Spawn(1, "Rifleman", 36, 52), w.Spawn(1, "Rifleman", 44, 52) };
        Escort(w, men, ward);
        w.Run(60);
        w.Order(1, CommandKind.Enter, [ward], house.Id);
        Assert.True(RunUntil(w, () => ward.Garrison == house.Id, 600) > 0);
        var door = men[0].Escort!.Last;
        w.Run(200);
        foreach (var u in men)
        {
            Assert.Equal(OrderKind.Escort, u.Order.Kind);
            Assert.Equal(door, u.Escort!.Last);
            Assert.True(Cells(u.Pos, door) <= 5, $"{Cells(u.Pos, door):0.0} cells from where it went in");
        }
        w.Order(1, CommandKind.Exit, [ward]);
        Assert.True(RunUntil(w, () => ward.Garrison == 0, 200) > 0);
        w.Order(1, CommandKind.Move, [ward], cell: new Cell(70, 70));
        w.Run(500);
        Assert.All(men, u => Assert.True(Cells(u.Pos, ward.Pos) <= 5, $"{Cells(u.Pos, ward.Pos):0.0} cells from it"));
    }

    [Fact]
    public void WhenItFallsTheyGuardTheSpotAndAnyOtherOrderEndsIt()
    {
        var w = Duel(96);
        var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40, Facing.East).Vehicle;
        var hv = new[] { w.SpawnCrewed(1, "humvee", 30, 36).Vehicle, w.SpawnCrewed(1, "humvee", 30, 44).Vehicle, w.SpawnCrewed(1, "humvee", 50, 44).Vehicle };
        Escort(w, hv, truck);
        w.Run(50);
        Assert.Equal(3, hv[0].Escort!.Count);
        w.Order(1, CommandKind.Move, [hv[2]], cell: new Cell(80, 80)); // another order: its escort is over
        w.Run(2);
        Assert.Null(hv[2].Escort);
        Assert.Equal(OrderKind.Move, hv[2].Order.Kind);
        Assert.Equal(2, w.EscortsOf(truck.Id));
        Assert.Equal(2, hv[0].Escort!.Count); // the others' spots given out again
        var spot = Cell.Of(truck.Pos);
        truck.HP = 0;
        w.Run(2);
        foreach (var u in hv[..2])
        {
            Assert.Equal(new Order(OrderKind.Guard, spot, 0, 0), u.Order); // Guard area on the spot where it fell
            Assert.Null(u.Escort);
        }
    }

    [Fact]
    public void ManyEscortsTakeTheirOwnSpotsAndItHashesAndReplays()
    {
        static (World W, List<ulong> Hashes) Play()
        {
            var w = Duel(96);
            var truck = w.SpawnCrewed(1, "sentinel_cargo_truck", 40, 40, Facing.East).Vehicle;
            var men = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "Rifleman", 30 + i, 50)).ToList();
            Escort(w, men, truck);
            w.Run(5);
            w.Order(1, CommandKind.Move, [truck], cell: new Cell(70, 60));
            var hashes = new List<ulong>();
            for (var t = 0; t < 400; t++)
            {
                w.Step();
                hashes.Add(w.Hash());
            }
            return (w, hashes);
        }
        var (a, ha) = Play();
        var (_, hb) = Play();
        Assert.Equal(ha, hb);
        var escorts = a.Units.Where(u => u.Escort is not null).ToList();
        Assert.Equal(6, escorts.Count);
        Assert.Equal(Enumerable.Range(0, 6), escorts.Select(u => u.Escort!.Spot).Order());
    }
}

using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Tests;

// Two-stage destruction and repair of airfield pieces, milestone M3a (the game repo's
// docs/airfield-design.md, 1 and 2.2): at 0 HP the facility and the loading shelters become rubble
// at 50% of their max HP (one HP bar) and stop working; builders on a Repair order bring them back
// to 100% and they stand again; rubble hit to 0 again is gone, and the piece is rebuilt from
// scratch on its slot. Open stands, rows and the runway are never damaged. A parked aircraft takes
// splash like a ground unit, and 40% of its max HP when its shelter falls on it.
public class AirfieldRubbleTests
{
    private static readonly Cell FacilityCell = new(30, 40);

    private static UnitDef Def(World w, string id, byte player = 1) => w.Player(player)!.Setup.Find(w.Content, id)!;

    private static (World W, Airfield Af) Field()
    {
        var w = Arena.NewSentinel(160);
        var af = w.SpawnAirfield(Def(w, "airfield_facility"), FacilityCell, 0, [(0, 0)]) ?? throw new InvalidOperationException("no airfield");
        return (w, af);
    }

    private static AirfieldStation Shelter(Airfield af) => af.Rows[0].Stations.Single(s => s.Kind == StationKind.Loading);

    private static AirfieldStation Stand(Airfield af) => af.Rows[0].Stations.Single(s => s.Kind == StationKind.Normal);

    private static int RunUntil(World w, Func<bool> done, int max = 4000)
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

    // Worker spawns a worker beside a piece (inside builder range of its footprint).
    private static Unit WorkerBy(World w, Unit piece)
    {
        var o = World.FootprintOrigin(piece);
        return w.Spawn(1, "worker", o.X + World.FootW(piece) / 2, o.Y - 1);
    }

    [Fact]
    public void AShelterFallsToRubbleAtHalfItsHpAndStopsServing()
    {
        var (w, af) = Field();
        var shelter = w.Unit(Shelter(af).Unit)!;
        var watcher = TestContent.Mvp.Archetype("watcher_drone")!.Aircraft!;
        Assert.Equal(2, w.StationsFor(af, watcher).Count);
        shelter.HP = -40; // overkill makes no difference
        w.Step();
        Assert.True(shelter.Alive);
        Assert.True(shelter.Rubble);
        Assert.Equal(500, shelter.HP);
        Assert.False(World.PieceStands(shelter));
        Assert.Same(shelter, w.Unit(Shelter(af).Unit));
        Assert.Equal([Stand(af)], w.StationsFor(af, watcher));
    }

    [Fact]
    public void RubbleHitAgainIsGoneAndTheShelterIsRebuiltFromScratch()
    {
        var (w, af) = Field();
        var st = Shelter(af);
        var shelter = w.Unit(st.Unit)!;
        shelter.HP = 0;
        w.Step();
        Assert.True(shelter.Rubble);
        shelter.HP = 0;
        w.Step();
        Assert.Null(w.Unit(shelter.Id));
        Assert.Equal(0u, st.Unit);
        Assert.Single(w.Airfields); // the airfield stays

        var slot = World.AirfieldStationCell(af, 0, 0, st.Slot);
        Assert.Equal(World.FootprintOrigin(shelter), slot);
        var worker = w.Spawn(1, "worker", slot.X + 5, slot.Y - 2);
        Assert.False(w.CanPlace(Def(w, "airfield_shelter"), new Cell(slot.X + 1, slot.Y), 0, [worker])); // only on its slot
        Assert.False(w.CanPlace(Def(w, "airfield_stand"), slot, 0, [worker])); // a stand is never rebuilt
        Assert.True(w.CanPlace(Def(w, "airfield_shelter"), slot, 0, [worker]));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = slot, Unit = "airfield_shelter" });
        w.Step();
        var site = w.Unit(st.Unit)!;
        Assert.False(site.IsBuilt);
        Assert.Equal(af.Id, site.Airfield);
        Assert.False(w.CanPlace(Def(w, "airfield_shelter"), slot, 0, [worker])); // taken
        var t = RunUntil(w, () => site.IsBuilt);
        Assert.InRange(t, 400 - 2, 400 + 40); // 20 s, from scratch
        Assert.Equal(site.Def.MaxHp, site.HP);
        Assert.False(site.Rubble);
    }

    [Fact]
    public void BuildersRepairRubbleBackToFullAndItStandsAgain()
    {
        var (w, af) = Field();
        var shelter = w.Unit(Shelter(af).Unit)!;
        shelter.HP = 0;
        w.Step();
        var worker = WorkerBy(w, shelter);
        w.Order(1, CommandKind.Repair, [worker], shelter.Id);
        // One worker restores the whole bar in half the build time (10 s): half the bar in 5 s.
        var t = RunUntil(w, () => !shelter.Rubble);
        Assert.InRange(t, 100, 104);
        Assert.Equal(1000, shelter.HP);
        Assert.Equal(0, shelter.RepairWork);
        Assert.True(World.PieceStands(shelter));
        w.Run(2);
        Assert.Equal(OrderKind.Idle, worker.Order.Kind);
    }

    [Fact]
    public void ADamagedFacilityIsRepairedAtItsRateWithHelpers()
    {
        int Repair(int workers)
        {
            var (w, af) = Field();
            var fac = w.Unit(af.Facility)!;
            fac.HP = 1000; // half of 2000: one worker restores the bar in 22.5 s, half of it in 11.25 s
            var team = Enumerable.Range(0, workers).Select(_ => WorkerBy(w, fac)).ToList();
            w.Order(1, CommandKind.Repair, team, fac.Id);
            return RunUntil(w, () => fac.HP == fac.Def.MaxHp);
        }
        Assert.InRange(Repair(1), 225, 232);
        Assert.InRange(Repair(2), 150, 156); // 100% + 50%
        Assert.InRange(Repair(3), 150, 156); // + 0%
    }

    [Fact]
    public void TheFacilityFallsToRubbleThenIsGoneAndIsRebuiltOnItsSlot()
    {
        var (w, af) = Field();
        var fac = w.Unit(af.Facility)!;
        var slot = World.FootprintOrigin(fac);
        Assert.Equal(slot, w.AirfieldFacilityCell(af));
        fac.HP = 0;
        w.Step();
        Assert.True(fac.Rubble);
        Assert.Equal(1000, fac.HP);
        var worker = w.Spawn(1, "worker", slot.X + 5, slot.Y - 2);
        Assert.False(w.CanPlace(Def(w, "airfield_row"), World.AirfieldRowCell(af, 1, 0), 0, [worker])); // rubble does not stand
        Assert.False(w.CanPlace(Def(w, "airfield_facility"), slot, 0, [worker])); // not gone yet
        fac.HP = 0;
        w.Step();
        Assert.Equal(0u, af.Facility);
        Assert.Single(w.Airfields);
        Assert.True(w.CanPlace(Def(w, "airfield_facility"), slot, 0, [worker]));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Cell = slot, Unit = "airfield_facility" });
        w.Step();
        var site = w.Unit(af.Facility)!;
        Assert.Equal((af.Id, false), (site.Airfield, site.IsBuilt));
        Assert.Single(w.Airfields); // rebuilt into the same airfield, not a new one
        RunUntil(w, () => site.IsBuilt);
        Assert.True(w.CanPlace(Def(w, "airfield_row"), World.AirfieldRowCell(af, 1, 0), 0, [worker]));
    }

    [Fact]
    public void StandsRowsAndRunwayAreNeverDamaged()
    {
        var (w, af) = Field();
        var pieces = new[] { Stand(af).Unit, af.Rows[0].Unit, af.Segments[0], af.Segments[1] }.Select(id => w.Unit(id)!).ToList();
        foreach (var p in pieces)
        {
            w.Explode(p.Pos, 5000, w.Content.Warhead("ballistic")!, Fixed.FromInt(5));
        }
        w.Step();
        Assert.All(pieces, p => Assert.Equal((p.Def.MaxHp, false), (p.HP, p.Rubble)));
    }

    [Fact]
    public void AParkedAircraftTakesSplashAndFortyPercentWhenItsShelterFalls()
    {
        var (w, af) = Field();
        var st = Shelter(af);
        var shelter = w.Unit(st.Unit)!;
        var parked = w.Spawn(1, "watcher_drone", 50, 45);
        parked.Pos = shelter.Pos;
        var flying = w.Spawn(1, "watcher_drone", 50, 45);
        flying.Pos = shelter.Pos;
        var bullet = w.Content.Warhead("bullet")!; // one that can hurt a drone
        w.Explode(shelter.Pos, 100, bullet, Fixed.FromInt(2));
        Assert.Equal(flying.Def.MaxHp, flying.HP); // in the air: blasts pass under it
        Assert.Equal(parked.Def.MaxHp, parked.HP);
        st.Occupant = parked.Id;
        w.Explode(shelter.Pos, 100, bullet, Fixed.FromInt(2));
        Assert.Equal(parked.Def.MaxHp - 60, parked.HP); // splash like a ground unit, on its parked armour (60% for bullets, M3b)
        Assert.Equal(flying.Def.MaxHp, flying.HP);

        var before = parked.HP;
        shelter.HP = 0;
        w.Step();
        Assert.True(shelter.Rubble);
        Assert.Equal(before - parked.Def.MaxHp * 40 / 100, parked.HP);
    }

    [Fact]
    public void RubbleAndRepairAreDeterministicAndHashed()
    {
        ulong Run()
        {
            var (w, af) = Field();
            var shelter = w.Unit(Shelter(af).Unit)!;
            shelter.HP = 0;
            w.Step();
            var worker = WorkerBy(w, shelter);
            w.Order(1, CommandKind.Repair, [worker], shelter.Id);
            w.Run(50);
            return w.Hash();
        }
        Assert.Equal(Run(), Run());

        var (w1, af1) = Field();
        var (w2, _) = Field();
        w1.Unit(Shelter(af1).Unit)!.HP = 0;
        w1.Step();
        w2.Step();
        var a = w1.Hash();
        w1.Unit(Shelter(af1).Unit)!.HP = 1000; // the same HP, but rubble
        Assert.NotEqual(a, w2.Hash());
        Assert.NotEqual(w1.Hash(), w2.Hash());
    }
}

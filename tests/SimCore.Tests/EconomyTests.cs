using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The gold economy (ECON-ENGINE, 2026-11-08; docs/design.md, "Gold economy"; the game repo's docs/plans/economy-spec.md): gold and
// spending opt-in per lobby, the banks, the four gold mines, mining (the miner, the worker, the bulldozer), the cargo truck's Haul
// and Keep stocked, its new speed and the Resistance's bulldozer; a full loop on a small map and the measured rates.
public class EconomyTests(ITestOutputHelper output)
{
    private const int Tps = World.TicksPerSecond;

    private static World Econ(int size = 160, int gold = 0, bool economy = true) =>
        Arena.New(size, 1, s => s with { Economy = economy, StartGold = economy ? gold : 0 });

    private static Unit Mine(World w, string id, int x, int y, int quarter = 0) =>
        w.Unit(w.Spawn(w.Player(1)!.Setup.Find(w.Content, id)!, new Cell(x, y), 0, quarter))!;

    private static void Order(World w, CommandKind kind, IEnumerable<Unit> us, uint target, byte player = 1) =>
        w.Issue(new Command { Tick = w.Tick, Player = player, Kind = kind, Units = [.. us.Select(u => u.Id)], Target = target });

    // Run steps up to max ticks, collecting the economy events, until done (then the tick count) or -1.
    private static int Run(World w, Func<bool> done, int max, List<EconomyEvent>? events = null)
    {
        for (var t = 1; t <= max; t++)
        {
            w.Step();
            events?.AddRange(w.EconomyEvents);
            if (done())
            {
                return t;
            }
        }
        return -1;
    }

    private static int Credits(World w, byte p = 1) => w.Player(p)!.Credits;

    [Fact]
    public void ContentCarriesTheEconomy()
    {
        var c = TestContent.Mvp;
        Archetype A(string id) => c.Archetypes.First(a => a.Id == id);
        foreach (var (id, w, h, slots, gold) in new[] { ("gold_placer", 12, 8, 3, 1500), ("gold_outcrop", 12, 12, 4, 3000), ("gold_adit", 16, 14, 6, 6000), ("gold_pit", 40, 40, 10, 12000) })
        {
            var a = A(id);
            Assert.True(a.Neutral && a.Untargetable && a.Invulnerable && a.Construction is null);
            Assert.Equal((w, h, slots, gold), (a.FootprintW, a.FootprintH, a.Mine!.Work.Count, a.Mine.Gold));
            Assert.NotEmpty(a.Mine.Solid);
            Assert.Equal(a.Mine.Load, a.Mine.Bays[0]);
        }
        // Trucks loading at once: placer 1, outcrop 1, adit 2, pit 3; the extra bays 4 cells along the edge by the art's spot.
        Assert.Equal(new[] { 1, 1, 2, 3 }, new[] { "gold_placer", "gold_outcrop", "gold_adit", "gold_pit" }.Select(id => A(id).Mine!.Bays.Count));
        Assert.Equal(new[] { (39, 130), (79, 130) }, A("gold_adit").Mine!.Bays);
        Assert.Equal(new[] { (390, 316), (390, 276), (390, 356) }, A("gold_pit").Mine!.Bays);
        foreach (var id in new[] { "bank_resistance", "bank_sentinel" })
        {
            var a = A(id);
            Assert.Equal((14, 12, 1500, 60 * Tps), (a.FootprintW, a.FootprintH, a.Cost, a.BuildTime));
            Assert.NotNull(a.Bank);
            Assert.NotNull(a.Construction);
        }
        Assert.Equal((75, 15 * Tps, 60, 10), (A("miner").Cost, A("miner").BuildTime, A("miner").Mining!.GoldPerMinute, A("miner").Mining!.Load));
        Assert.Equal(new MiningSpec(24, 10), A("worker").Mining);
        Assert.Equal(new MiningSpec(36, 20), A("bulldozer").Mining);
        Assert.Empty(A("miner").Components); // unarmed
        foreach (var id in new[] { "cargo_truck", "sentinel_cargo_truck" })
        {
            Assert.Equal(new HaulSpec(300, 5 * Tps, 1, 900), A(id).Haul); // 15 gold a second at a mine: a load in 20 s
            Assert.InRange(A(id).Speed.Raw * 22 - A("medium_rocket_launcher").Speed.Raw * 30, -30, 30); // 22 -> 30 (the launcher keeps 22)
        }
        Assert.Equal(A("cargo_truck").Speed, A("decoy_cargo_truck").Speed);
        Assert.True(A("resistance_barracks").Production!.Builds(A("miner").Tags));
        var res = c.Factions.First(f => f.Id == "resistance").Archetypes.Select((on, i) => on ? c.Archetypes[i].Id : "").ToList();
        var sen = c.Factions.First(f => f.Id == "sentinel").Archetypes.Select((on, i) => on ? c.Archetypes[i].Id : "").ToList();
        foreach (var id in new[] { "miner", "bank_resistance", "bulldozer", "gold_placer", "gold_outcrop", "gold_adit", "gold_pit" }) { Assert.Contains(id, res); }
        foreach (var id in new[] { "miner", "bank_sentinel", "bulldozer", "gold_placer", "gold_pit" }) { Assert.Contains(id, sen); }
        Assert.DoesNotContain("bank_sentinel", res);
    }

    [Fact]
    public void StartGoldNeedsTheEconomy()
    {
        Assert.Throws<MatchSetupException>(() => Arena.New(32, 1, s => s with { StartGold = 100 }));
        Assert.Equal(500, Credits(Econ(32, 500)));
        var plain = TestContent.Battle(1);
        Assert.Equal(plain.Digest(), (plain with { Economy = false, StartGold = 0 }).Digest());
        Assert.NotEqual(plain.Digest(), (plain with { Economy = true }).Digest());
    }

    [Fact]
    public void WithoutTheEconomyNothingIsSpent()
    {
        var w = Econ(64, economy: false);
        var b = w.Spawn(1, "resistance_barracks", 10, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [b.Id], Unit = "miner" });
        w.Run(2);
        Assert.Equal(0, Credits(w));
        Assert.True(b.Bays.Any(x => x.Def >= 0) || b.ProductionQueue.Count > 0);
    }

    [Fact]
    public void ProductionAndConstructionPayAndRefuse()
    {
        var w = Econ(96, 100);
        var b = w.Spawn(1, "resistance_barracks", 10, 10);
        var events = new List<EconomyEvent>();
        void Produce() => w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [b.Id], Unit = "miner" });
        Produce();
        Run(w, () => false, 2, events);
        Assert.Equal(25, Credits(w));
        Produce();
        Run(w, () => false, 2, events);
        Assert.Equal(25, Credits(w));
        var refused = Assert.Single(events, e => e.Kind == EconomyEventKind.NotEnoughGold);
        Assert.Equal((b.Id, 50), (refused.Unit, refused.Amount));
        Assert.Equal("miner", w.Match.Defs[(int)refused.Other].Archetype.Id);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelProduce, Target = b.Id, Index = -1 });
        w.Run(2);
        Assert.Equal(100, Credits(w)); // a cancelled order: all of it back

        var worker = w.Spawn(1, "worker", 40, 40);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Unit = "bank_resistance", Cell = new Cell(50, 50) });
        events.Clear();
        Run(w, () => false, 2, events);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.NotEnoughGold && e.Unit == 0 && e.Amount == 1400);
        Assert.Empty(w.All("bank_resistance"));
        w.Player(1)!.Credits = 2000;
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Build, Units = [worker.Id], Unit = "bank_resistance", Cell = new Cell(50, 50) });
        w.Run(2);
        var site = Assert.Single(w.All("bank_resistance"));
        Assert.Equal(500, Credits(w));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.CancelBuild, Target = site.Id });
        w.Run(2);
        Assert.Equal(500 + 1125, Credits(w)); // a cancelled site: its 75%
    }

    [Fact]
    public void MineBlocksOnlyItsSolidCellsAndTurns()
    {
        foreach (var q in new[] { 0, 1, 2, 3 })
        {
            var w = Econ(96);
            var m = Mine(w, "gold_adit", 20, 20, q);
            var ms = m.Def.Archetype.Mine!;
            var blocked = 0;
            for (var y = 0; y < World.FootH(m); y++)
            {
                for (var x = 0; x < World.FootW(m); x++)
                {
                    blocked += w.Grid.At(new Cell(20 + x, 20 + y)) == Terrain.Blocked ? 1 : 0;
                }
            }
            Assert.Equal(ms.Solid.Count, blocked);
            for (var i = 0; i < ms.Work.Count; i++)
            {
                Assert.NotEqual(Terrain.Blocked, w.Grid.At(Cell.Of(World.WorkPoint(m, i))));
            }
            Assert.NotEqual(Terrain.Blocked, w.Grid.At(Cell.Of(World.LoadPoint(m))));
            Assert.Equal(new MineView(6000, 6000, 100, 0, 0, 6, 0, 2), w.MineStatus(m));
            Assert.Equal(0, m.Owner);
        }
    }

    [Fact]
    public void MinersMineAtTheirRatesAndWalkLoadsToTheBank()
    {
        var w = Econ(160);
        var m = Mine(w, "gold_outcrop", 40, 20);
        var bank = w.Spawn(1, "bank_resistance", 40, 60);
        var miner = w.Spawn(1, "miner", 45, 34);
        var worker = w.Spawn(1, "worker", 47, 34);
        var dozer = w.Spawn(1, "bulldozer", 50, 36);
        Order(w, CommandKind.Mine, [miner, worker, dozer], m.Id);
        var events = new List<EconomyEvent>();
        var started = new int[3];
        var full = new int[3];
        var us = new[] { miner, worker, dozer };
        Run(w, () =>
        {
            for (var i = 0; i < 3; i++)
            {
                if (started[i] == 0 && us[i].Econ is { Stage: EconStage.Working })
                {
                    started[i] = (int)w.Tick;
                }
                if (full[i] == 0 && us[i].Econ!.Carry >= us[i].Def.Archetype.Mining!.Load)
                {
                    full[i] = (int)w.Tick;
                }
            }
            return full.All(f => f > 0);
        }, 3000, events);
        Assert.Equal([200, 500, 667], [.. Enumerable.Range(0, 3).Select(i => full[i] - started[i] + 1)]); // 1 gold/s, 0.4 and 0.6 of it (20 for the dozer)
        Assert.True(Run(w, () => Credits(w) >= 40, 3000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == miner.Id && e.Other == bank.Id && e.Amount == 10);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == dozer.Id && e.Amount == 20);
        Assert.Equal(m.Def.Archetype.Mine!.Gold - m.Econ!.GoldLeft, Credits(w) + miner.Econ!.Carry + worker.Econ!.Carry + dozer.Econ!.Carry);
    }

    [Fact]
    public void FullSlotsWaitBesideTheMine()
    {
        var w = Econ(120);
        var m = Mine(w, "gold_placer", 40, 20);
        w.Spawn(1, "bank_resistance", 40, 60);
        var miners = Enumerable.Range(0, 4).Select(i => w.Spawn(1, "miner", 40 + 2 * i, 34)).ToList();
        Order(w, CommandKind.Mine, miners, m.Id);
        Run(w, () => miners.Count(u => u.Econ!.Stage == EconStage.Working) == 3, 600);
        var waiting = Assert.Single(miners, u => u.Econ!.Stage == EconStage.Waiting);
        Assert.Equal(-1, waiting.Econ!.Slot);
        Assert.Equal(3, w.MineStatus(m)!.Value.Miners);
        var leaver = miners.First(u => u.Econ!.Stage == EconStage.Working);
        Order(w, CommandKind.Stop, [leaver], 0);
        Assert.True(Run(w, () => waiting.Econ!.Stage == EconStage.Working, 600) > 0); // it takes the freed spot
    }

    // One truck and no miners anywhere (HARVEST-ENGINE, the owner 2026-11-18): it parks on the mine's bay, takes 300 in 20 s, banks
    // it at the nearest bank, comes back to the same mine and goes round again; a second truck queues beside the mine meanwhile.
    [Fact]
    public void ATruckHarvestsByItselfAndASecondQueuesAtTheMine()
    {
        var w = Econ(200);
        var m = Mine(w, "gold_outcrop", 40, 20);
        var bank = w.Spawn(1, "bank_resistance", 40, 90);
        var truck = w.SpawnCrewed(1, "cargo_truck", 30, 40).Vehicle;
        var second = w.SpawnCrewed(1, "cargo_truck", 22, 44).Vehicle;
        Assert.Empty(w.All("miner"));
        Order(w, CommandKind.Haul, [truck], m.Id);
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.Parked, 1000) > 0);
        var parked = (int)w.Tick;
        Order(w, CommandKind.Haul, [second], m.Id);
        Assert.Equal(new MineView(3000, 3000, 100, 0, 0, 4, 1, 1), w.MineStatus(m));
        w.Run(300);
        Assert.Equal(EconStage.Queued, second.Econ!.Stage); // the outcrop loads one truck at a time
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.ToBank, 1000) > 0);
        Assert.InRange((int)w.Tick - parked, 399, 401); // 15 gold a second: 300 in 20 s
        Assert.Equal((300, 2700), (truck.Econ!.Carry, m.Econ!.GoldLeft));
        Assert.True(Run(w, () => second.Econ!.Stage == EconStage.Parked, 600) > 0); // it moves up
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.Unloading, 3000, events) > 0);
        var unloading = (int)w.Tick;
        Assert.True(Run(w, () => Credits(w) >= 300, 400, events) > 0);
        Assert.Equal(100, (int)w.Tick - unloading); // 5 s at the bank
        Assert.Contains(events, e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == truck.Id && e.Other == bank.Id && e.Amount == 300);
        Assert.DoesNotContain(events, e => e.Kind == EconomyEventKind.LoadDropped);
        Assert.True(Run(w, () => truck.Econ!.Stage is EconStage.Queued or EconStage.Parked, 3000) > 0); // back to the same mine
        Assert.Equal((OrderKind.Haul, m.Id), (truck.Order.Kind, truck.Order.Target));
        Assert.True(Run(w, () => Credits(w) >= 900, 6000) > 0); // and round again, both trucks
    }

    // The pit loads three trucks at once on its three bays; a fourth queues and takes the first bay freed.
    [Fact]
    public void ThreeTrucksLoadAtAPitAndAFourthQueues()
    {
        var w = Econ(220);
        var m = Mine(w, "gold_pit", 60, 40);
        w.Spawn(1, "bank_resistance", 160, 40);
        var lp = World.LoadPoint(m);
        var (lx, ly) = ((int)(lp.X.Raw / Fixed.OneRaw), (int)(lp.Y.Raw / Fixed.OneRaw));
        var trucks = Enumerable.Range(0, 4).Select(i => w.SpawnCrewed(1, "cargo_truck", lx + 5 + 4 * (i % 2), ly - 9 + 6 * i).Vehicle).ToList();
        Order(w, CommandKind.Haul, trucks, m.Id);
        Assert.True(Run(w, () => trucks.Count(t => t.Econ!.Stage == EconStage.Parked) == 3, 1500) > 0);
        w.Step();
        Assert.Equal(new[] { 0, 1, 2 }, trucks.Where(t => t.Econ!.Stage == EconStage.Parked).Select(t => t.Econ!.Slot).Order());
        var fourth = Assert.Single(trucks, t => t.Econ!.Stage != EconStage.Parked);
        Assert.Equal(EconStage.Queued, fourth.Econ!.Stage);
        Assert.Equal((3, 3), (w.MineStatus(m)!.Value.Loading, w.MineStatus(m)!.Value.Bays));
        for (var i = 1; i < 3; i++)
        {
            Assert.Equal(4.0, (World.LoadBayPoint(m, i) - World.LoadBayPoint(m, 0)).Len().Raw / (double)Fixed.OneRaw, 2); // 4 cells apart
        }
        Assert.True(Run(w, () => fourth.Econ!.Stage == EconStage.Parked, 1500) > 0);
        Assert.Contains(trucks, t => t != fourth && t.Econ!.Carry == 300 && t.Econ.Stage is EconStage.ToBank or EconStage.BankQueued or EconStage.Unloading);
    }

    // A bank takes one truck at a time: the second queues there (a full unload to wait, the next bank further), the third goes on to
    // the next bank (two unloads to wait here, the drive there sooner); all their gold is banked.
    [Fact]
    public void AtATakenBankItQueuesOrGoesOnToTheNextWhicheverIsSooner()
    {
        var w = Econ(160);
        var m = Mine(w, "gold_outcrop", 40, 10);
        var a = w.Spawn(1, "bank_resistance", 40, 60);
        var b = w.Spawn(1, "bank_resistance", 60, 60);
        var up = World.UnloadPoint(a);
        var trucks = Enumerable.Range(0, 3).Select(i => w.SpawnCrewed(1, "cargo_truck", (int)(up.X.Raw / Fixed.OneRaw) - 3 + 3 * i, (int)(up.Y.Raw / Fixed.OneRaw) + 3).Vehicle).ToList();
        foreach (var t in trucks)
        {
            t.Econ = new EconState { Carry = 300 };
        }
        Order(w, CommandKind.Haul, trucks, m.Id);
        var events = new List<EconomyEvent>();
        w.Step();
        Assert.Equal((EconStage.ToBank, a.Id), (trucks[0].Econ!.Stage, trucks[0].Econ!.At)); // it holds the spot, driving in
        Assert.Equal((EconStage.BankQueued, a.Id), (trucks[1].Econ!.Stage, trucks[1].Econ!.At));
        Assert.Equal((EconStage.ToBank, b.Id), (trucks[2].Econ!.Stage, trucks[2].Econ!.At));
        Assert.True(Run(w, () => Credits(w) == 900, 2000, events) > 0);
        Assert.Equal(new[] { a.Id, a.Id, b.Id }, trucks.Select(t => events.First(e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == t.Id).Other));
        var order = trucks.Select(t => events.FindIndex(e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == t.Id)).ToList();
        Assert.True(order[0] < order[1]); // its turn after the first
    }

    // Dry: the truck banks what it carries, goes on to the nearest other mine with gold (nobody needs to work it), then home.
    [Fact]
    public void ADryMineSendsTheTruckToTheNextMineThenHome()
    {
        var w = Econ(200);
        var m = Mine(w, "gold_placer", 40, 20);
        var near = Mine(w, "gold_outcrop", 120, 20);
        var far = Mine(w, "gold_adit", 150, 150);
        w.Spawn(1, "bank_resistance", 40, 100);
        var truck = w.SpawnCrewed(1, "cargo_truck", 30, 40).Vehicle;
        Order(w, CommandKind.Haul, [truck], m.Id);
        m.Econ!.GoldLeft = 15;
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => m.Econ.GoldLeft == 0, 2000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.MineEmpty && e.Player == 1 && e.Unit == m.Id);
        Assert.Equal(EconStage.ToBank, truck.Econ!.Stage); // it leaves with the last 15
        Assert.True(Run(w, () => truck.Order.Target == near.Id, 4000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.HaulMoved && e.Unit == truck.Id && e.Other == near.Id);
        Assert.Equal(15, Credits(w)); // banked first
        Assert.Equal(new MineView(0, 1500, 0, 3, 0, 3, 0, 1), w.MineStatus(m)); // the ruin
        near.Econ!.GoldLeft = 0;
        far.Econ!.GoldLeft = 0; // no gold left anywhere: home
        Assert.True(Run(w, () => truck.Order.Kind == OrderKind.Move, 3000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.HaulMoved && e.Unit == truck.Id && e.Other == 0);
    }

    [Fact]
    public void WithoutABankLoadsWaitAndATruckLosesItsGold()
    {
        var w = Econ(160);
        var m = Mine(w, "gold_placer", 40, 20);
        var miner = w.Spawn(1, "miner", 45, 30);
        Order(w, CommandKind.Mine, [miner], m.Id);
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => miner.Econ is { Stage: EconStage.Waiting, Carry: 10 }, 1000, events) > 0);
        w.Run(100);
        Assert.Single(events, e => e.Kind == EconomyEventKind.NoBank);
        Assert.Equal(10, miner.Econ!.Carry);
        w.Spawn(1, "bank_resistance", 40, 60);
        Assert.True(Run(w, () => Credits(w) == 10, 2000) > 0);
        Order(w, CommandKind.Stop, [miner], 0);

        var truck = w.SpawnCrewed(1, "cargo_truck", 100, 100).Vehicle;
        truck.Econ = new EconState { Carry = 250 };
        Order(w, CommandKind.Haul, [truck], m.Id);
        w.Run(5);
        truck.HP = 0; // destroyed with 250 aboard
        w.Run(400);
        Assert.Equal(10, Credits(w)); // nothing of the truck's arrived
        Assert.DoesNotContain(w.Units, u => u.Id == truck.Id);
    }

    [Fact]
    public void KeepStockedBuysSupplyAtTheGarageAndFillsTheDepot()
    {
        var w = Econ(160, 300);
        var garage = w.Spawn(1, "vehicle_garage", 20, 20);
        var depot = w.Spawn(1, "supply_depot", 80, 80);
        depot.Stock = 600;
        var truck = w.SpawnCrewed(1, "cargo_truck", 60, 60).Vehicle;
        truck.Cargo = 0;
        Order(w, CommandKind.KeepStocked, [truck], depot.Id);
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => truck.Econ is { Stage: EconStage.Waiting }, 2000, events) > 0);
        Assert.Single(events, e => e.Kind == EconomyEventKind.ShortOfGold && e.Other == garage.Id && e.Amount == 100); // 400 for 300
        w.Player(1)!.Credits = 1000;
        Assert.True(Run(w, () => depot.Stock == 1000, 3000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.SupplyBought && e.Amount == 400);
        Assert.Equal(600, Credits(w));
        Assert.Equal(OrderKind.KeepStocked, truck.Order.Kind);
        depot.Stock = 900; // drawn on again: it goes round again for the 100 it lacks
        Assert.True(Run(w, () => depot.Stock == 1000, 3000) > 0);
        Assert.Equal(500, Credits(w));
    }

    // The full loop on a small test map: a truck harvests, the gold rises, a unit is bought, the mine runs dry, and Keep stocked fills
    // a depot out of the harvested gold.
    [Fact]
    public void FullLoop()
    {
        var w = Econ(160);
        var m = Mine(w, "gold_placer", 40, 20);
        w.Spawn(1, "bank_resistance", 40, 70);
        var barracks = w.Spawn(1, "resistance_barracks", 90, 70);
        w.Spawn(1, "vehicle_garage", 90, 110);
        var depot = w.Spawn(1, "supply_depot", 40, 120);
        depot.Stock = 500;
        var hauler = w.SpawnCrewed(1, "cargo_truck", 60, 40).Vehicle;
        var stocker = w.SpawnCrewed(1, "cargo_truck", 60, 120).Vehicle;
        stocker.Cargo = 0;
        Order(w, CommandKind.Haul, [hauler], m.Id);
        Order(w, CommandKind.KeepStocked, [stocker], depot.Id);
        var events = new List<EconomyEvent>();
        var hauled = Run(w, () => events.Exists(e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == hauler.Id), 6000, events);
        Assert.True(hauled > 0);
        Assert.True(Credits(w) >= 300 || events.Exists(e => e.Kind == EconomyEventKind.SupplyBought));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [barracks.Id], Unit = "miner" });
        Assert.True(Run(w, () => w.All("miner").Count == 1, 1000, events) > 0); // bought and trained
        Assert.True(Run(w, () => w.MineStatus(m)!.Value.Stage == 3, 20 * 60 * Tps, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.MineEmpty);
        Assert.True(Run(w, () => hauler.Order.Kind != OrderKind.Haul, 4000, events) > 0); // banked the last, then home
        Assert.True(Run(w, () => depot.Stock == 1000, 4000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.SupplyBought && e.Unit == stocker.Id);
        var spent = events.Where(e => e.Kind == EconomyEventKind.SupplyBought).Sum(e => e.Amount) + 75;
        Assert.Equal(1500 - spent, Credits(w)); // every gold of the mine banked, less the miner and the supply
        output.WriteLine($"full loop: first haul banked at {hauled} ticks, dry by {w.Tick / (60.0 * Tps):F1} min");
    }

    // A deterministic replay: the same commands give the same hashes, and the fast hash matches the reference one.
    [Fact]
    public void EconomyHashesAndReplays()
    {
        ulong[] Play()
        {
            var w = Econ(160, 200);
            var m = Mine(w, "gold_outcrop", 40, 20);
            w.Spawn(1, "bank_resistance", 40, 70);
            var miners = Enumerable.Range(0, 5).Select(i => w.Spawn(1, "miner", 40 + 2 * i, 36)).ToList();
            var truck = w.SpawnCrewed(1, "cargo_truck", 60, 40).Vehicle;
            Order(w, CommandKind.Mine, miners, m.Id);
            Order(w, CommandKind.Haul, [truck], m.Id);
            var hashes = new List<ulong>();
            for (var i = 0; i < 40; i++)
            {
                w.Run(100);
                Assert.Equal(w.HashReference(), w.Hash());
                hashes.Add(w.Hash());
            }
            return [.. hashes];
        }
        Assert.Equal(Play(), Play());
    }

    // The proposed numbers measured (logged; loose bounds): minutes to empty each mine with one truck and with a truck on every bay,
    // the bank 300 m away; and one truck's gold a minute at the adit with the bank 300 m and 1,000 m away.
    [Fact]
    public void MeasuredRates()
    {
        double Minutes(string id, int trucks, int metres)
        {
            var cells = metres / 2;
            var w = Econ(cells + 80);
            var m = Mine(w, id, 30, 25);
            var lp = World.LoadPoint(m);
            w.Spawn(1, "bank_resistance", (int)(lp.X.Raw / Fixed.OneRaw), (int)(lp.Y.Raw / Fixed.OneRaw) + cells + 6);
            var ts = Enumerable.Range(0, trucks).Select(i => w.SpawnCrewed(1, "cargo_truck", (int)(lp.X.Raw / Fixed.OneRaw) - 4 * i, (int)(lp.Y.Raw / Fixed.OneRaw) + 6).Vehicle).ToList();
            Order(w, CommandKind.Haul, ts, m.Id);
            var t = Run(w, () => Credits(w) == m.Def.Archetype.Mine!.Gold, 200 * 60 * Tps);
            Assert.True(t > 0, id);
            return t / (60.0 * Tps);
        }
        foreach (var id in new[] { "gold_placer", "gold_outcrop", "gold_adit", "gold_pit" })
        {
            var bays = TestContent.Mvp.Archetypes.First(a => a.Id == id).Mine!.Bays.Count;
            var one = Minutes(id, 1, 300);
            var full = bays > 1 ? Minutes(id, bays, 300) : one;
            output.WriteLine($"{id}: bank 300 m: one truck banks it all in {one:F1} min, {bays} truck(s) in {full:F1} min");
        }
        foreach (var metres in new[] { 300, 1000 })
        {
            var cells = metres / 2;
            var w = Econ(cells + 80);
            var m = Mine(w, "gold_adit", 30, 10);
            var lp = World.LoadPoint(m);
            w.Spawn(1, "bank_resistance", (int)(lp.X.Raw / Fixed.OneRaw), (int)(lp.Y.Raw / Fixed.OneRaw) + cells + 6);
            Order(w, CommandKind.Haul, [w.SpawnCrewed(1, "cargo_truck", 20, 30).Vehicle], m.Id);
            w.Run(3 * 60 * Tps); // settle
            var from = Credits(w);
            w.Run(20 * 60 * Tps);
            var perMinute = (Credits(w) - from) / 20.0;
            Assert.True(perMinute > 0);
            output.WriteLine($"adit, one truck, bank {metres} m: {perMinute:F0} gold/min");
        }
    }
}

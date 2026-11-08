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
        }
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
            Assert.Equal(new HaulSpec(300, 5 * Tps, 1), A(id).Haul);
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
            Assert.Equal(new MineView(6000, 6000, 100, 0, 0, 6), w.MineStatus(m));
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

    [Fact]
    public void ATruckHaulsToTheBankAndASecondQueues()
    {
        var w = Econ(200);
        var m = Mine(w, "gold_adit", 40, 20);
        var bank = w.Spawn(1, "bank_resistance", 40, 120);
        var miners = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "miner", 40 + 2 * i, 38)).ToList();
        var truck = w.SpawnCrewed(1, "cargo_truck", 30, 40).Vehicle;
        var second = w.SpawnCrewed(1, "cargo_truck", 26, 44).Vehicle;
        Order(w, CommandKind.Mine, miners, m.Id);
        Order(w, CommandKind.Haul, [truck], m.Id);
        w.Run(1);
        Order(w, CommandKind.Haul, [second], m.Id);
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.Parked, 1000) > 0);
        Run(w, () => second.Econ!.Stage == EconStage.Queued, 400);
        Assert.Equal(EconStage.Queued, second.Econ!.Stage);
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.ToBank, 4000, events) > 0);
        Assert.Equal(300, truck.Econ!.Carry);
        Assert.Equal(30, events.Count(e => e.Kind == EconomyEventKind.LoadDropped && e.Other == truck.Id));
        Assert.DoesNotContain(events, e => e.Kind == EconomyEventKind.GoldBanked); // nobody walked: they wait for a truck
        Assert.True(Run(w, () => second.Econ!.Stage == EconStage.Parked, 600) > 0); // the queued truck moves up
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.Unloading, 3000) > 0);
        var unloading = (int)w.Tick;
        Assert.True(Run(w, () => Credits(w) == 300, 400, events) > 0);
        Assert.Equal(100, (int)w.Tick - unloading); // 5 s at the bank
        Assert.Contains(events, e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == truck.Id && e.Other == bank.Id && e.Amount == 300);
        Assert.True(Run(w, () => truck.Econ!.Stage == EconStage.Queued || truck.Econ.Stage == EconStage.Parked, 3000) > 0); // back
    }

    [Fact]
    public void ADryMineIdlesItsMinersAndMovesTheTruckOn()
    {
        var w = Econ(200);
        var m = Mine(w, "gold_placer", 40, 20);
        var other = Mine(w, "gold_outcrop", 120, 20);
        w.Spawn(1, "bank_resistance", 40, 120);
        var miner = w.Spawn(1, "miner", 45, 30);
        var far = w.Spawn(1, "miner", 125, 36);
        var truck = w.SpawnCrewed(1, "cargo_truck", 30, 40).Vehicle;
        Order(w, CommandKind.Mine, [miner], m.Id);
        Order(w, CommandKind.Mine, [far], other.Id);
        Order(w, CommandKind.Haul, [truck], m.Id);
        m.Econ!.GoldLeft = 15;
        var events = new List<EconomyEvent>();
        Assert.True(Run(w, () => m.Econ.GoldLeft == 0, 2000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.MineEmpty && e.Player == 1 && e.Unit == m.Id);
        Assert.True(Run(w, () => miner.Order.Kind == OrderKind.Idle, 2000, events) > 0);
        Assert.Equal(0, miner.Econ!.Carry);
        Assert.True(Run(w, () => truck.Order.Target == other.Id, 4000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.HaulMoved && e.Unit == truck.Id && e.Other == other.Id);
        Assert.Equal(15, Credits(w)); // its last 15 banked first
        Assert.Equal(new MineView(0, 1500, 0, 3, 0, 3), w.MineStatus(m)); // the ruin
        Order(w, CommandKind.Stop, [far], 0); // no miners left anywhere: home
        other.Econ!.GoldLeft = 0;
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

    // The full loop on a small test map: miners mine, a truck hauls, the gold rises, a unit is bought, the mine runs dry, and Keep
    // stocked fills a depot out of the mined gold.
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
        var miners = Enumerable.Range(0, 2).Select(i => w.Spawn(1, "miner", 40 + 2 * i, 34)).ToList();
        var hauler = w.SpawnCrewed(1, "cargo_truck", 60, 40).Vehicle;
        var stocker = w.SpawnCrewed(1, "cargo_truck", 60, 120).Vehicle;
        stocker.Cargo = 0;
        Order(w, CommandKind.Mine, miners, m.Id);
        Order(w, CommandKind.Haul, [hauler], m.Id);
        Order(w, CommandKind.KeepStocked, [stocker], depot.Id);
        var events = new List<EconomyEvent>();
        var mined = Run(w, () => events.Exists(e => e.Kind == EconomyEventKind.LoadDropped), 2000, events);
        var hauled = Run(w, () => events.Exists(e => e.Kind == EconomyEventKind.GoldBanked && e.Unit == hauler.Id), 6000, events);
        Assert.True(mined > 0 && hauled > 0);
        Assert.True(Credits(w) >= 300 || events.Exists(e => e.Kind == EconomyEventKind.SupplyBought));
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Produce, Units = [barracks.Id], Unit = "miner" });
        Assert.True(Run(w, () => w.All("miner").Count == 3, 1000, events) > 0); // bought and trained
        var bought = w.All("miner").Last();
        Order(w, CommandKind.Mine, [bought], m.Id);
        Assert.True(Run(w, () => w.MineStatus(m)!.Value.Stage == 3, 20 * 60 * Tps, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.MineEmpty);
        Assert.True(Run(w, () => depot.Stock == 1000 && miners.All(u => u.Order.Kind == OrderKind.Idle), 4000, events) > 0);
        Assert.Contains(events, e => e.Kind == EconomyEventKind.SupplyBought && e.Unit == stocker.Id);
        var spent = events.Where(e => e.Kind == EconomyEventKind.SupplyBought).Sum(e => e.Amount) + 75;
        Assert.True(Run(w, () => hauler.Order.Kind != OrderKind.Haul, 4000) > 0);
        Assert.Equal(1500 - spent, Credits(w)); // every gold of the mine banked, less the miner and the supply
        output.WriteLine($"full loop: first load {mined} ticks, first haul banked at {mined + hauled}, dry by {w.Tick / (60.0 * Tps):F1} min");
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

    // The proposed numbers measured (logged; loose bounds): minutes to empty each size with full slots and a truck parked, and gold
    // a minute at the adit, six miners, with one truck and on foot, at 300 m and 1,000 m from the bank.
    [Fact]
    public void MeasuredRates()
    {
        foreach (var id in new[] { "gold_placer", "gold_outcrop", "gold_adit", "gold_pit" })
        {
            var w = Econ(200);
            var m = Mine(w, id, 60, 20);
            w.Spawn(1, "bank_resistance", 60, 20 + m.Def.Archetype.FootprintH + 20);
            var n = m.Def.Archetype.Mine!.Work.Count;
            var miners = Enumerable.Range(0, n).Select(i => w.Spawn(1, "miner", 60 + i, 22 + m.Def.Archetype.FootprintH)).ToList();
            var trucks = Enumerable.Range(0, 2).Select(i => w.SpawnCrewed(1, "cargo_truck", 40 + 3 * i, 30).Vehicle).ToList();
            Order(w, CommandKind.Mine, miners, m.Id);
            Order(w, CommandKind.Haul, trucks, m.Id);
            var t = Run(w, () => m.Econ!.GoldLeft == 0, 90 * 60 * Tps);
            Assert.True(t > 0, id);
            var ideal = m.Def.Archetype.Mine.Gold / n; // seconds at 1 gold/s each
            output.WriteLine($"{id}: {n} miners, two trucks: dry in {t / (60.0 * Tps):F1} min (mining alone {ideal / 60.0:F1} min)");
        }
        foreach (var metres in new[] { 300, 1000 })
        {
            foreach (var truck in new[] { true, false })
            {
                var cells = metres / 2;
                var w = Econ(cells + 80);
                var m = Mine(w, "gold_adit", 30, 10);
                w.Spawn(1, "bank_resistance", 31, 10 + 14 + cells);
                var miners = Enumerable.Range(0, 6).Select(i => w.Spawn(1, "miner", 32 + i, 26)).ToList();
                Order(w, CommandKind.Mine, miners, m.Id);
                if (truck)
                {
                    Order(w, CommandKind.Haul, [w.SpawnCrewed(1, "cargo_truck", 20, 20).Vehicle], m.Id);
                }
                w.Run(2 * 60 * Tps); // settle
                var from = Credits(w);
                w.Run(20 * 60 * Tps);
                var perMinute = (Credits(w) - from) / 20.0;
                Assert.True(perMinute > 0);
                output.WriteLine($"adit, 6 miners, bank {metres} m: {(truck ? "one truck" : "on foot")} {perMinute:F0} gold/min");
            }
        }
    }
}

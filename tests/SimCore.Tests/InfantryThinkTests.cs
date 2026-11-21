using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// INF-E1b-2: the soldier's think (docs/design.md, "Infantry combat: the soldier's think").
public class InfantryThinkTests
{
    // Nearest: the least squared distance (raw) between a and b over the next `ticks` ticks.
    private static long Nearest(World w, Unit a, Unit b, int ticks)
    {
        var best = long.MaxValue;
        for (var i = 0; i < ticks; i++)
        {
            w.Step();
            best = Math.Min(best, (a.Pos - b.Pos).LenSq());
        }
        return best;
    }

    private static World New(int size = 40, ulong seed = 1)
    {
        var w = Arena.New(size, seed);
        w.CombatOff = false; // the arena turns the rules off for other tests' timings; on here (before any unit is added)
        return w;
    }

    private static void Stance(World w, Stance st, params Unit[] units) =>
        w.Issue(new Command { Tick = w.Tick, Player = units[0].Owner, Kind = CommandKind.SetStance, Units = [.. units.Select(u => u.Id)], Stance = st });

    [Fact]
    public void TheBlockLoadsTheThinksNumbersAndTheGrenadesCarried()
    {
        var w = New();
        var r = w.Content.InfantryCombat!;
        Assert.Equal((50, 35, 15, 16, 240, 10, 130, 45), (r[CombatValue.GaitWalk], r[CombatValue.GaitBackpedal], r[CombatValue.PosGainNeeded],
            r[CombatValue.PosMaxCandidates], r[CombatValue.PosFloodCells], r[CombatValue.TargetDownThreat], r[CombatValue.TargetSectorIn], r[CombatValue.GrenadeDamage]));
        Assert.Equal(2, w.Spawn(1, "Rifleman", 5, 5).Fight!.Grenades); // the assault carries 2 (brief 6.5)
    }

    [Fact]
    public void ADownEnemyIsATargetOnlyWhenNoAbleOneIsAndNeverForTheMarksman()
    {
        var w = New();
        var blue = w.Spawn(1, "Rifleman", 10, 10);
        var down = w.Spawn(2, "Rifleman", 13, 10);
        Stance(w, Sim.Stance.HoldFire, down);
        down.HP = 4;
        w.Step();
        Assert.Equal(WoundState.Down, down.Life!.Wound);
        var finished = false;
        for (var i = 0; i < 80; i++)
        {
            w.Step();
            finished |= blue.AutoTarget == down.Id;
        }
        Assert.True(finished); // alone, the man on the ground is finished (owner, 15.1)
        var w1 = New();
        var b1 = w1.Spawn(1, "Rifleman", 10, 10);
        var d1 = w1.Spawn(2, "Rifleman", 13, 10);
        var able = w1.Spawn(2, "Rifleman", 13, 12);
        Stance(w1, Sim.Stance.HoldFire, d1, able);
        d1.HP = 4;
        var downFirst = false;
        for (var i = 0; i < 60 && able.Alive && able.Life!.Wound != WoundState.Down; i++)
        {
            w1.Step();
            downFirst |= b1.AutoTarget == d1.Id;
        }
        Assert.False(downFirst); // an enemy who can fight comes first
        var w2 = New();
        var sniper = w2.Spawn(1, "Marksman", 10, 10);
        var d2 = w2.Spawn(2, "Rifleman", 16, 10);
        Stance(w2, Sim.Stance.HoldFire, d2);
        d2.HP = 4;
        w2.Run(80);
        Assert.Equal(WoundState.Down, d2.Life!.Wound);
        Assert.Equal(0u, sniper.AutoTarget); // never the sniper (16.4)
    }

    [Fact]
    public void MovingAwayInContactHeBackpedalsFacingTheEnemy()
    {
        var w = New();
        var blue = w.Spawn(1, "Rifleman", 12, 10);
        var red = w.Spawn(2, "Rifleman", 16, 10);
        Stance(w, Sim.Stance.HoldFire, red, blue); // nobody shoots: red stays a contact
        w.Run(40); // he sees red, reacts and is in contact
        Assert.True(blue.Fight!.InContact);
        w.Order(1, CommandKind.Move, [blue], cell: new Cell(4, 10));
        int back = 0, other = 0;
        for (var i = 0; i < 40; i++)
        {
            w.Step();
            if (World.Walking(blue) && blue.Fight.InContact && blue.Fight.Gait != Gait.None)
            {
                back += blue.Fight.Gait == Gait.Backpedal && Math.Abs(Facing.Diff(blue.Facing, Facing.East)) <= 2 ? 1 : 0;
                other += blue.Fight.Gait == Gait.Backpedal ? 0 : 1;
            }
        }
        Assert.True(back >= 8 && other == 0, $"backpedalling facing east in contact: {back}, other gaits {other}");
    }

    [Fact]
    public void UnderFireHeTakesLowCoverTwoCellsToTheSideButNotCoverFarBehind()
    {
        var w = New();
        w.Grid.Set(new Cell(9, 12), Terrain.Blocked); // a rock: low cover on (10, 11) to (10, 13), facing west
        w.Grid.Set(new Cell(19, 10), Terrain.Blocked); // far behind him
        var blue = w.Spawn(1, "Rifleman", 10, 10);
        var red = w.Spawn(2, "Rifleman", 6, 10);
        Stance(w, Sim.Stance.HoldGround, blue, red);
        w.Run(120);
        var c = Cell.Of(blue.Pos);
        Assert.True(c.X == 10 && c.Y is >= 11 and <= 13, $"at the rock's side (at {c})");
        Assert.True(blue.Fight!.HasSpot);
    }

    [Fact]
    public void ARiflemanThrowsAGrenadeAtAnEnemyBehindAWallAndItGoesOff()
    {
        var w = New();
        for (var y = 8; y <= 12; y++)
        {
            w.Grid.Set(new Cell(12, y), Terrain.Blocked);
        }
        var blue = w.Spawn(1, "Rifleman", 10, 10);
        var red = w.Spawn(2, "Rifleman", 13, 10);
        Stance(w, Sim.Stance.HoldGround, blue, red);
        uint landed = 0;
        for (var i = 0; i < 160 && landed == 0; i++)
        {
            w.Step();
            landed = w.Grenades.Count > 0 ? w.Tick : 0;
        }
        Assert.NotEqual(0u, landed);
        Assert.Equal(1, blue.Fight!.Grenades);
        var hp = red.HP;
        w.Run(60);
        Assert.Empty(w.Grenades);
        Assert.True(red.HP < hp || red.Fight!.PostureTo == Posture.Prone || Cell.Of(red.Pos) != new Cell(13, 10), "he took it, or dived, or ran");
    }

    [Fact]
    public void AnIdleSquadsMedicWalksToAWoundedFriendTenCellsOff()
    {
        var w = Arena.NewSentinel(40);
        var medic = w.Spawn(1, "sentinel_medic", 10, 10);
        var mate = w.Spawn(1, "Rifleman", 11, 10);
        var hurt = w.Spawn(1, "Rifleman", 20, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [medic.Id, mate.Id] });
        w.Run(5);
        hurt.HP = hurt.Def.MaxHp * 30 / 100;
        Assert.True(Nearest(w, medic, hurt, 200) <= 25L * Fixed.OneRaw * Fixed.OneRaw, "the medic comes within 5 cells (his heal reach is 4)"); // before: his walk lost the tick it began
    }

    [Fact]
    public void TheMedicStillReachesAWoundedSquadmateAwayFromHisSlot()
    {
        var w = Arena.NewSentinel(40);
        var medic = w.Spawn(1, "sentinel_medic", 10, 10);
        var mate = w.Spawn(1, "Rifleman", 11, 10);
        var hurt = w.Spawn(1, "Rifleman", 12, 10);
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.FormSquad, Units = [medic.Id, mate.Id, hurt.Id] });
        w.Run(5);
        w.Order(1, CommandKind.Move, [hurt], cell: new Cell(21, 14)); // off his slot, then idle there
        w.Run(120);
        hurt.HP = hurt.Def.MaxHp * 30 / 100;
        Assert.True(Nearest(w, medic, hurt, 200) <= 25L * Fixed.OneRaw * Fixed.OneRaw, "the medic comes within 5 cells of the wounded squadmate");
    }
}

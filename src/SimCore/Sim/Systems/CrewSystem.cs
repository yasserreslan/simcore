namespace SimCore.Sim;

// The crew system: soldiers on an Enter order climb into their vehicle once
// they reach it, and drivers bail out of destroyed vehicles. A vehicle with
// a driver seat can't move or fire while the seat is empty; any driver is as
// good as any other, and only the owner's soldiers may drive.
public sealed partial class World
{
    private void RunBoarding()
    {
        foreach (var u in _units)
        {
            if (u.Order.Kind != OrderKind.Enter || u.Inside != 0)
            {
                continue;
            }
            var v = Unit(u.Order.Target);
            if (v?.Def.Archetype.Garrison is { } g)
            {
                if (!CanGarrison(u, v))
                {
                    u.Idle();
                }
                else if (Within(u.Pos, DoorCell(v, NearestDoor(v, u.Pos)).Center(), g.EnterRange))
                {
                    EnterBuilding(u, v);
                }
                continue;
            }
            if (v is null || !CanBoard(u, v))
            {
                u.Idle();
                continue;
            }
            if (Within(u.Pos, v.Pos, v.Def.Crew!.EnterRange))
            {
                Board(u, v);
            }
        }
    }

    // CanBoard reports whether u may take v's driver seat: v is u's own
    // vehicle with an empty seat, and u is a soldier the seat accepts.
    internal static bool CanBoard(Unit u, Unit v) =>
        v.Alive && u.Alive && v.Id != u.Id && v.Def.Crew is { } crew && v.Owner == u.Owner && v.Driver == 0 &&
        u.Inside == 0 && u.Garrison == 0 && u.Def.Crew is null && u.Def.Tags.Matches(crew.Tags);

    // Board seats u in v at once, wherever u is; scenarios use it to spawn
    // crewed vehicles.
    internal static void Board(Unit u, Unit v)
    {
        u.Idle();
        u.AutoTarget = 0;
        u.SalvoLeft = 0;
        u.Inside = v.Id;
        u.Pos = v.Pos;
        v.Driver = u.Id;
    }

    // Unboard puts v's driver on the first free cell next to v.
    private void Unboard(Unit v)
    {
        var d = Unit(v.Driver);
        v.Driver = 0;
        v.Idle();
        v.AutoTarget = 0;
        v.SalvoLeft = 0;
        if (d is null)
        {
            return;
        }
        d.Inside = 0;
        d.Pos = Grid.TrySpiral(Cell.Of(v.Pos), 1, out var c) ? c.Center() : v.Pos;
    }

    // RunEjection runs just before cleanup: the driver of each destroyed
    // vehicle survives with the vehicle's crew survival chance, rolled on the
    // world's generator in vehicle ID order, and pops out damaged.
    private void RunEjection()
    {
        foreach (var v in _units)
        {
            if (v.Alive || v.Driver == 0)
            {
                continue;
            }
            var d = Unit(v.Driver);
            v.Driver = 0;
            if (d is null)
            {
                continue;
            }
            d.Inside = 0;
            d.Pos = v.Pos;
            var crew = v.Def.Crew!;
            if (_rng.Intn(100) < crew.Survival)
            {
                d.HP = Math.Max(1, d.HP - d.Def.MaxHp * crew.ExitDamage / 100);
            }
            else
            {
                d.HP = 0;
            }
        }
    }
}

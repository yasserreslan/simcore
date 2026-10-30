using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private void PlanAbilities(World w)
    {
        if (_rules.Foundation is not { } policy) { return; }
        foreach (var u in _own)
        {
            if (!u.CanAct || u.Inside != 0 || u.Garrison != 0 || !u.IsBuilt || u.Rubble || u.Cooldown > 0 || Reserved(u.Id) ||
                _ordered.BinarySearch(u.Id) >= 0 || _pending.Any(p => p.Source == u.Id)) { continue; }
            foreach (var a in u.Def.Abilities)
            {
                if (a.Effect is not LaunchEffect || u.Charges[a.Stock] <= 0) { continue; }
                var body = w.Content.Archetypes[a.Archetype];
                uint target = 0;
                var point = Cell.Of(u.Pos);
                if (body.Flight is not null)
                {
                    if (!policy.Aircraft || _own.Any(v => v.Source == u.Id && v.Def.Archetype.Index == a.Archetype)) { continue; }
                }
                else
                {
                    var enemy = _visible.Where(e => OffensiveTarget(Cell.Of(e.Pos)) && World.Within(u.Pos, e.Pos, a.Range))
                        .OrderByDescending(e => e.Def.Cost).ThenBy(e => e.Id).FirstOrDefault();
                    if (enemy is null) { continue; }
                    target = enemy.Id; point = Cell.Of(enemy.Pos);
                }
                if (Emit(w, new Command { Kind = CommandKind.UseAbility, Units = [u.Id],
                    Ability = w.Content.Components[a.Component].Id, Target = target, Cell = point }, 0,
                    body.Flight is null ? "launch-visible-strike" : "launch-observer"))
                { _pending.Add(new Pending(u.Id, -1, CommandKind.UseAbility, w.Tick + World.InputDelay)); }
                break;
            }
        }
    }
}

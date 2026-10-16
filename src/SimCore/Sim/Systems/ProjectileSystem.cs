namespace SimCore.Sim;

// The projectile system: shots in flight travel toward the point they were
// aimed at and explode when they land, hitting whoever is there by then.
public sealed partial class World
{
    private void RunProjectiles()
    {
        foreach (var p in _projectiles)
        {
            var d = p.Dest - p.Pos;
            var speed = p.Weapon.Effect.ProjectileSpeed;
            if (d.Len() > speed)
            {
                p.Pos += d.WithLen(speed);
                continue;
            }
            p.Pos = p.Dest;
            Explode(p.Dest, p.Weapon.Damage, p.Weapon.Warhead, p.Weapon.Effect.Splash);
        }
        _projectiles.RemoveAll(p => p.Pos == p.Dest);
    }
}

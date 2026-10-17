using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// The contract between routing and local movement, one per unit (hashed). The path system
/// (routing: goals, routes, formation slots, re-plans) writes it each tick for every unit that
/// should move and never moves units itself; the movement system (local avoidance, collision,
/// turn rate, clearance, terrain speed) reads it, moves the unit and sets <see cref="Arrived"/>.
/// Tick order: commands, orders, path system, movement system, then the rest.
/// </summary>
/// <param name="Goal">The current local target point (a waypoint, or the formation slot near the end).</param>
/// <param name="Desired">The desired direction scaled to the unit's speed this tick; zero = stop.</param>
/// <param name="Slot">The unit's formation slot offset from the group's goal, or zero.</param>
/// <param name="Arrived">Set by the movement system when the unit has reached its goal.</param>
public readonly record struct MoveIntent(Vec Goal, Vec Desired, Vec Slot, bool Arrived);

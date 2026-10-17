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
/// <param name="Desired">
/// The desired direction at the unit's base speed this tick (terrain-free: the movement system
/// applies slope and forest modifiers and its own braking); full speed toward pass-through
/// waypoints, capped by the distance only at the final goal or slot; zero = stop.
/// </param>
/// <param name="Slot">The unit's formation slot offset from the group's goal, or zero.</param>
/// <param name="Arrived">
/// Set by the movement system within tolerance of Goal (or on a crowd arrival); then it ignores
/// Desired. The path system keeps it for the same Goal and resets it (and Stuck) only with a new
/// Goal; the movement system also resets both when Goal changes.
/// </param>
/// <param name="Stuck">
/// Set by the movement system after 40 ticks with Desired non-zero and no progress toward Goal
/// (best distance not improved by 1/16 cell); cleared when progress resumes or Goal changes. The
/// path system re-plans on it and may clear it when it does.
/// </param>
public readonly record struct MoveIntent(Vec Goal, Vec Desired, Vec Slot, bool Arrived, bool Stuck = false);

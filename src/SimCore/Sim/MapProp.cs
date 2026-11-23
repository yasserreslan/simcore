using SimCore.FixedPoint;

namespace SimCore.Sim;

/// <summary>
/// A prop a map places (INF-ROCK, docs/design.md row 1354): one of its <c>paint</c> entries of layer <c>roadside</c> placed with
/// <c>at</c> (a garden wall piece, a hedge, a fence, a shed, a parked car...; <see cref="TerrainMap.Props"/>), or one a scenario
/// puts down (<see cref="World.PlaceProp"/>). <see cref="Kind"/> is the art's prop name; <see cref="At"/> its centre on the map
/// (cells); <see cref="YawTenths"/> its turn in tenths of a degree, as the client draws it (<c>yaw_deg</c>: the piece's long axis
/// runs along (cos, -sin) on the map, y growing south). What it is to soldiers is content (<c>infantryCombat.coverProps</c>,
/// <see cref="Model.CoverProp"/>): a kind the content does not list is nothing to the engine. It never blocks movement.
/// </summary>
public readonly record struct MapProp(string Kind, Vec At, int YawTenths);

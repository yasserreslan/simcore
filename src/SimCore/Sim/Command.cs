using System.Text.Json.Serialization;

namespace SimCore.Sim;

/// <summary>The type of a player command.</summary>
public enum CommandKind : byte
{
    /// <summary>Drive the units to <see cref="Command.Cell"/>.</summary>
    Move = 1,

    /// <summary>Attack <see cref="Command.Target"/>.</summary>
    Attack,

    /// <summary>Drop the current order.</summary>
    Stop,

    /// <summary>
    /// Walk the soldiers to the player's vehicle <see cref="Command.Target"/> and drive it (an empty
    /// driver seat) or ride in it (a free passenger seat), or into a garrisonable building.
    /// </summary>
    Enter,

    /// <summary>
    /// The drivers of the listed vehicles (or the listed drivers) get out. A listed passenger gets out
    /// behind its vehicle; a listed vehicle with passengers lets all of them out (its driver stays).
    /// A listed building lets everyone out; a listed soldier in a building leaves it.
    /// </summary>
    Exit,

    /// <summary>
    /// Use the ability of component <see cref="Command.Ability"/> on unit
    /// <see cref="Command.Target"/>, or on <see cref="Command.Cell"/> when Target is 0.
    /// </summary>
    UseAbility,

    /// <summary>
    /// Sandbox only: spawn the issuing player's preset or slotless archetype
    /// <see cref="Command.Unit"/> at <see cref="Command.Cell"/> (or the nearest
    /// free cell). Ignored unless the match's scenario is
    /// <see cref="Scenarios.Scenario.SandboxName"/>, so it can never change a
    /// real match. Vehicles spawn empty. A building (garrisonable or a neutral
    /// civilian structure) goes exactly at its top-left cell, turned by
    /// <see cref="Command.Facing"/> (0, 8, 16 or 24; see there).
    /// </summary>
    SandboxSpawn,

    /// <summary>
    /// Fire at the ground at <see cref="Command.Cell"/> (force-fire): each unit
    /// walks into range and fires its first weapon that can hit ground targets
    /// at the cell's centre until it is given another order or runs dry. A unit that can't move (the howitzer) takes only a
    /// point in its range and outside its minimum range; <see cref="Command.Queue"/> adds the point to a ground unit's queue.
    /// </summary>
    AttackGround,

    /// <summary>Set the listed units' <see cref="Unit.WeaponMode"/> to <see cref="Command.Mode"/>.</summary>
    SetWeaponMode,

    /// <summary>Launchers that deploy: stop and set up where they stand (they stay deployed).</summary>
    Deploy,

    /// <summary>Launchers that deploy: pack up (ends a salvo).</summary>
    Stow,

    /// <summary>
    /// Cargo trucks with supply aboard: drive into the radius of the player's supply depot <see cref="Command.Target"/> and unload
    /// everything there; or, when the target is the player's gun that trucks reload (a <c>truckReload</c> weapon: the howitzer), drive
    /// within its reload range and hand it rounds until it is full or the truck is short of one, keeping the rest.
    /// </summary>
    Unload,

    /// <summary>
    /// The listed workers place a construction site of archetype <see cref="Command.Unit"/> with
    /// <see cref="Command.Cell"/> its top-left cell (turned by <see cref="Command.Facing"/>) and build it.
    /// </summary>
    Build,

    /// <summary>The listed workers go on building site <see cref="Command.Target"/>.</summary>
    Resume,

    /// <summary>The owner cancels construction site <see cref="Command.Target"/> (refunding part of its cost).</summary>
    CancelBuild,

    /// <summary>The listed factories queue one <see cref="Command.Unit"/> (a preset or archetype ID).</summary>
    Produce,

    /// <summary>
    /// Factory <see cref="Command.Target"/> cancels an order: <see cref="Command.Index"/> -1 the last
    /// one; 0 to bays-1 the one in that bay; from bays on the waiting ones in order.
    /// </summary>
    CancelProduce,

    /// <summary>The listed factories send what they produce to <see cref="Command.Cell"/>, or next to unit <see cref="Command.Target"/>.</summary>
    SetRally,

    // Values are stable (replays store them): new kinds are appended with the next free value.
    /// <summary>Set the listed units' <see cref="Unit.Stance"/> to <see cref="Command.Stance"/>.</summary>
    SetStance = 19,

    /// <summary>The listed traps go off now (on demand, whatever their auto setting).</summary>
    Detonate = 20,

    /// <summary>The listed traps go off on their own when an enemy steps in (<see cref="Command.On"/> true) or only on demand (false).</summary>
    SetAutoDetonate = 21,

    /// <summary>
    /// The listed units underground travel to the player's tunnel entrance <see cref="Command.Target"/>
    /// and come out there (<see cref="Command.On"/> true: "exit here", "send to entrance") or wait
    /// below it (false). Units that can't reach it, or can't come out of it when told to, are skipped.
    /// </summary>
    TunnelGo = 22,

    /// <summary>
    /// Evacuate the networks of the listed tunnel entrances: every unit underground in them comes out
    /// by the nearest entrance it may come out of (trapped units stay).
    /// </summary>
    TunnelEvacuate = 23,

    /// <summary>
    /// Attack-move the units to <see cref="Command.Cell"/> (A + click on the ground): they move as for
    /// Move, stop to shoot spotted enemies in reach on the way, then carry on.
    /// </summary>
    AttackMove = 24,

    /// <summary>
    /// The units guard <see cref="Command.Cell"/> (several: each its formation slot there), or the
    /// player's unit <see cref="Command.Target"/> (Option+G): see <see cref="OrderKind.Guard"/>.
    /// </summary>
    Guard = 25,

    /// <summary>
    /// The units scatter (Option+S): each moves away from the group's centre by four times its
    /// radius, at least 2 cells (a unit on the centre goes the way it faces), then idles.
    /// </summary>
    Scatter = 26,

    /// <summary>
    /// Repair the player's structure <see cref="Command.Target"/>: a repairable airfield piece
    /// (standing but damaged, or rubble) up to full HP, or a runway segment: its airfield's craters,
    /// then its worn patches. The listed builders go and repair it.
    /// </summary>
    Repair = 27,

    /// <summary>
    /// The listed aircraft go home now (M3b): to the airfield <see cref="Command.Target"/> belongs to
    /// when it is the player's and can take them, else their best home (an airfield, else their
    /// ground control station). Ignored while on the ground or already landing; climbing out, they
    /// turn for home once they fly free.
    /// </summary>
    ReturnToBase = 28,

    /// <summary>
    /// The listed aircraft go to rearm (the owner's flight rules: the only way an aircraft rearms):
    /// home as for <see cref="ReturnToBase"/> (to the airfield <see cref="Command.Target"/> belongs to
    /// when it is the player's and can take them, else their best home: an airfield, preferring a
    /// loading station, else their ground control station), where they are refuelled, rearmed and
    /// repaired, and then they resume their previous task (fly back to where they orbited). Already
    /// landing or parked they take the full service where they are (ending a recall's hold); leaving
    /// (pushing back to climbing out) they come back once they fly free.
    /// </summary>
    Rearm = 29,

    /// <summary>
    /// The listed aircraft take a mission (the mission layer, E3): <see cref="Command.Mission"/> Patrol,
    /// Guard, Search or Roam. Patrol and Guard without <see cref="Command.Cell2"/> fly a route: the
    /// command's <see cref="Command.Cell"/> starts a new one, or with <see cref="Command.Queue"/>
    /// (Shift+click) is appended to the route of the same task (up to 8 waypoints). Guard with
    /// Cell2, Search and Roam work over the area from Cell to Cell2 (corners, inclusive; Search and
    /// Roam without Cell2: a square round Cell twice its sight across each way). Guard engages the
    /// kinds of <see cref="Command.Filter"/> (0: the archetype's default); Search with
    /// <see cref="Command.On"/> starts over when done instead of orbiting the centre. Only Guard picks
    /// targets; the others spot. Bingo fuel takes it home to refuel and it resumes the task; Move
    /// (an orbit) ends it; Attack (a strike) runs on top of it and the task resumes after.
    /// </summary>
    AirTask = 30,

    /// <summary>
    /// Sandbox only (scripted demos and tests; E13): unit <see cref="Command.Target"/>'s HP becomes
    /// <see cref="Command.Index"/>, kept from 1 to its max (it never kills). Ignored in a real match.
    /// </summary>
    SandboxSetHp = 31,

    /// <summary>
    /// The listed aircraft's bomb fire mode becomes <see cref="Command.Index"/> (H2): an index into their bomb weapon's
    /// <see cref="Model.Delivery.FireModes"/> (the ground attack jet: 0 "single", one bomb a pass at each aim point; 1 "salvo4",
    /// a stick of 4 along its path; the supersonic bomber: 0 "row4", a row of 4 across its path at each aim; 1 "block24", 6 rows
    /// of 4 in one pass; the stealth bomber's selected weapon (<see cref="SetBombWeapon"/>): its precision bombs 0 "all16", 1 "group4",
    /// its bunker busters 0 "both", 1 "single"). Aircraft without that many modes are skipped. It applies from the next pass.
    /// </summary>
    SetFireMode = 32,

    /// <summary>
    /// The listed aircraft bomb a line on the ground (H2: "drag a line"): from <see cref="Command.Cell"/> to
    /// <see cref="Command.Cell2"/>, one pass flown along it in that direction, its fire mode's bombs spread evenly from end
    /// to end (one: the line's middle). Only aircraft with free-fall bombs (a delivery block, not guided) take it.
    /// <see cref="Command.Queue"/> (Shift) appends it to their queue of aims (<see cref="Unit.Aims"/>) instead.
    /// </summary>
    AttackLine = 33,

    /// <summary>
    /// The listed vehicles go to be serviced at the player's garage <see cref="Command.Target"/> (a factory with a
    /// <c>service</c> block: the military garage): each drives to a free door (waiting nearby, first come first served, when
    /// none is free), drives in, is reloaded, repaired and refilled, and drives out to the garage's rally point. Only its
    /// side's ground vehicles with one of the service's tags; ignored while the garage is unfinished or too damaged.
    /// </summary>
    Service = 34,

    /// <summary>
    /// The listed helicopters with rider benches (the light helicopter) land at <see cref="Command.Cell"/> (off the map or 0:
    /// where they are) and wait there for riders: soldiers given <see cref="Enter"/> with the helicopter as the target walk
    /// to it and climb aboard. A new order lifts it off.
    /// </summary>
    HeliLoad = 35,

    /// <summary>
    /// The listed helicopters fly to <see cref="Command.Cell"/> (off the map or 0: where they are) and drop their riders
    /// there, one every few ticks onto the free cells round it: by landing, or with <see cref="Command.On"/> by hovering low
    /// (a few metres) without touching down. <see cref="Exit"/> given to a helicopter drops them where it is, landing.
    /// </summary>
    HeliUnload = 36,

    /// <summary>
    /// The listed aircraft with several bomb weapons (the stealth bomber: 0 its precision bombs, 1 its bunker busters) drop bomb weapon
    /// <see cref="Command.Index"/> from their next pass: an index into <see cref="World.BombWeaponsOf"/> (<see cref="Unit.BombWeapon"/>).
    /// Each weapon keeps its own fire mode (<see cref="SetFireMode"/> sets the selected one's). Aircraft without that many are skipped.
    /// </summary>
    SetBombWeapon = 37,

    /// <summary>
    /// The listed aircraft with cargo pallets (the airlifter: a weapon with an <c>airdrop</c> block) airdrop them on the drop zone of
    /// the player's structure <see cref="Command.Target"/> (a field ammo point or a supply stash: <see cref="World.DropZoneOf"/>): a
    /// low run over the zone, its fire mode's pallets a pass (0 "all4", 1 "single": <see cref="SetFireMode"/>), each landing with a
    /// little scatter; a pallet inside the zone adds its supply to the structure's stock, one outside it is lost. With every pallet
    /// gone it flies home to load again (free at a main air base). Ignored for a target that is not a finished drop zone of the
    /// player's or for aircraft without pallets aboard.
    /// </summary>
    Airdrop = 38,

    /// <summary>
    /// The listed aircraft with cargo pallets aboard land at the player's airfield <see cref="Command.Target"/> belongs to (one with
    /// a limited stock: the forward airbase), taxi to a stand, unload their pallets into its stock (one every <c>unloadTicks</c>; what
    /// does not fit stays aboard), take on fuel, and fly home to load again (to the nearest airfield of the player with an unlimited
    /// stock: a main air base). Ignored for a target airfield without a stock, one that cannot take them, or empty aircraft.
    /// </summary>
    Deliver = 39,

    /// <summary>
    /// The listed empty cargo trucks drive to the player's store <see cref="Command.Target"/> (a structure whose supply block is a
    /// <c>cargoSource</c>: a supply stash, a forward airbase, a main air base) and load a full load out of its stock (free from an
    /// unlimited one: a main air base) in the time their cargo takes to unload. Ignored for full trucks or a store that is not one.
    /// </summary>
    LoadCargo = 40,

    /// <summary>
    /// The listed units with smoke launchers (the Humvee) each pop a smoke screen around where they stand
    /// (<see cref="Model.SmokeEffect"/>: a charge each, once their launchers have reloaded). Units without a charge, still
    /// reloading, without a driver or inside something are skipped.
    /// </summary>
    Smoke = 41,

    /// <summary>
    /// The listed aircraft with loadout modes (the stealth multirole: 0 "stealth", 1 "heavy"; <see cref="Model.Archetype.Loadouts"/>)
    /// change to mode <see cref="Command.Index"/>: only parked at a station of their side's airfield that rearms now (a stand or a
    /// shelter of a forward airfield or a main air base, not one drawing on a limited stock), never in the air. The change takes the
    /// archetype's <see cref="Model.Archetype.LoadoutSwitchTicks"/> (<see cref="Unit.LoadoutLeft"/>); it holds the aircraft at its
    /// station (an order given meanwhile launches it once done) and ends with the new mode's full load of every weapon. Ordering
    /// the mode it already has (or is changing from) cancels a change. Aircraft that cannot change now are skipped
    /// (<see cref="World.LoadoutOf"/>: CanSwitch).
    /// </summary>
    SetLoadout = 42,

    /// <summary>
    /// Sandbox only (scripted demos and tests; forest fire): the forest cell <see cref="Command.Cell"/> catches fire, if it can
    /// burn and the fire rules' cap allows, spreading at <see cref="Command.Index"/> percent of the rules' chances (0: it does not
    /// spread; 100: as a fire spreading from a neighbour). Ignored in a real match and without fire rules or terrain.
    /// </summary>
    SandboxIgnite = 43,
}

/// <summary>
/// A player's order, scheduled for a tick. Commands are the only way to change
/// the world, and the only thing peers exchange in multiplayer.
/// </summary>
public sealed record Command
{
    /// <summary>The tick the command runs on.</summary>
    [JsonPropertyName("tick")]
    public uint Tick { get; init; }

    /// <summary>The issuing player.</summary>
    [JsonPropertyName("player")]
    public byte Player { get; init; }

    /// <summary>The per-player order; assigned by <see cref="World.Issue"/> when 0.</summary>
    [JsonPropertyName("seq")]
    public uint Seq { get; init; }

    /// <summary>What to do.</summary>
    [JsonPropertyName("kind")]
    public CommandKind Kind { get; init; }

    /// <summary>The IDs of the units to command.</summary>
    [JsonPropertyName("units")]
    public IReadOnlyList<uint> Units { get; init; } = [];

    /// <summary>The <see cref="CommandKind.Move"/> destination, the <see cref="CommandKind.AttackGround"/> target, or an ability's target cell.</summary>
    [JsonPropertyName("cell")]
    public Cell Cell { get; init; }

    /// <summary>The target unit's ID for Attack, Enter and UseAbility, or 0.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Target { get; init; }

    /// <summary>For <see cref="CommandKind.UseAbility"/>, the ID of the component whose ability to use.</summary>
    [JsonPropertyName("ability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ability { get; init; }

    /// <summary>For <see cref="CommandKind.SetWeaponMode"/>, the mode to set.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Model.WeaponMode Mode { get; init; }

    /// <summary>For <see cref="CommandKind.SetStance"/>, the stance to set.</summary>
    [JsonPropertyName("stance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Stance Stance { get; init; }

    /// <summary>For <see cref="CommandKind.SandboxSpawn"/>, the preset name or slotless archetype ID to spawn.</summary>
    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    /// <summary>
    /// For <see cref="CommandKind.AttackGround"/>: add the point as a drone launcher's second
    /// target instead of replacing its first (Shift+G); for any other ground unit already on an attack-ground order (the
    /// howitzer, the mortar), add the point to its queue (<see cref="Unit.GroundQueue"/>, up to 16): one salvo at each point in
    /// turn, round and round. For <see cref="CommandKind.Attack"/>,
    /// <see cref="CommandKind.AttackGround"/> and <see cref="CommandKind.AttackLine"/> given to an aircraft with bombs on a
    /// strike (H2): append the aim to its queue (<see cref="Unit.Aims"/>) instead of replacing its order (Shift+click).
    /// </summary>
    [JsonPropertyName("queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Queue { get; init; }

    /// <summary>For <see cref="CommandKind.CancelProduce"/>, which order (-1: the last); for <see cref="CommandKind.SetFireMode"/>, the mode's index; for <see cref="CommandKind.SetBombWeapon"/>, the bomb weapon's; for <see cref="CommandKind.SetLoadout"/>, the loadout mode's; for <see cref="CommandKind.SandboxIgnite"/>, the fire's spread percent.</summary>
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Index { get; init; }

    /// <summary>
    /// For <see cref="CommandKind.Build"/>, the site's facing; for <see cref="CommandKind.SandboxSpawn"/> of a building, a
    /// tunnel entrance, an airfield's facility or core or a bridge, its facing. A quarter facing turns a structure's layout
    /// clockwise: 0 as laid out (the front, its doors, to the south), 8 front west, 16 front north, 24 front east; a quarter
    /// turn of a non-square footprint swaps its width and height (World.TurnsAt, Unit.Quarter, docs/design.md "Turned buildings").
    /// </summary>
    [JsonPropertyName("facing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Facing { get; init; }

    /// <summary>For <see cref="CommandKind.AirTask"/>: the task (Patrol, Guard, Search, Roam).</summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AirTaskKind Mission { get; init; }

    /// <summary>For <see cref="CommandKind.AirTask"/> Guard: the target kinds it engages (a <see cref="Model.AirTargetKinds"/> mask; 0 the archetype's default).</summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public byte Filter { get; init; }

    /// <summary>For <see cref="CommandKind.AirTask"/>: the area's second corner (with <see cref="Cell"/>), or null for a route; for <see cref="CommandKind.AttackLine"/>, the line's end.</summary>
    [JsonPropertyName("cell2")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cell? Cell2 { get; init; }

    /// <summary>For <see cref="CommandKind.SetAutoDetonate"/>: on (true) or off; for <see cref="CommandKind.TunnelGo"/>: come out there (true) or wait below; for <see cref="CommandKind.AirTask"/> Search: loop.</summary>
    [JsonPropertyName("on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool On { get; init; }
}

/// <summary>Thrown by <see cref="World.Issue"/> for a command that can never run.</summary>
public class CommandRejectedException(string message) : InvalidOperationException(message);

/// <summary>The command is scheduled for a past tick.</summary>
public sealed class LateCommandException(uint tick, uint now)
    : CommandRejectedException($"sim: command scheduled for a past tick: tick {tick}, now {now}");

/// <summary>The command's player is not in the match.</summary>
public sealed class UnknownPlayerException(byte player)
    : CommandRejectedException($"sim: unknown player: {player}");

public sealed partial class World
{
    /// <summary>
    /// Schedules a command and returns it with its sequence number set.
    /// Commands for the same tick run ordered by player, then sequence, no
    /// matter what order they arrived in.
    /// </summary>
    /// <exception cref="LateCommandException">The tick has already run.</exception>
    /// <exception cref="UnknownPlayerException">The player is not in the match.</exception>
    public Command Issue(Command c)
    {
        if (c.Tick < Tick)
        {
            throw new LateCommandException(c.Tick, Tick);
        }
        if (c.Player == 0 || c.Player >= _nextSeq.Length)
        {
            throw new UnknownPlayerException(c.Player);
        }
        uint seq = c.Seq;
        if (seq == 0)
        {
            _nextSeq[c.Player]++;
            seq = _nextSeq[c.Player];
        }
        else
        {
            _nextSeq[c.Player] = Math.Max(_nextSeq[c.Player], seq);
        }
        c = c with { Seq = seq, Units = [.. c.Units ?? []] };
        _pending.Insert(LowerBound(c), c);
        return c;
    }

    // LowerBound returns the index of the first pending command not ordered
    // before c.
    private int LowerBound(Command c)
    {
        int lo = 0, hi = _pending.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (CompareCommands(_pending[mid], c) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static int CompareCommands(Command a, Command b)
    {
        var d = a.Tick.CompareTo(b.Tick);
        if (d != 0)
        {
            return d;
        }
        d = a.Player.CompareTo(b.Player);
        return d != 0 ? d : a.Seq.CompareTo(b.Seq);
    }
}

# simcore

The deterministic simulation core of a classic-style RTS: the whole game
minus graphics, sound and input. A renderer (Godot 4 .NET or similar) reads
the world and draws it; it never changes it.

Every machine in a match runs its own copy of the sim. Players exchange only
commands, scheduled for a future tick. Same starting world + same commands =
bit-identical world on every tick, on every OS and CPU. That's what makes
lockstep multiplayer and replays work.

The code was ported from Go to C#. The port is bit-exact: the C# sim
reproduces the golden replay hashes recorded by the Go version exactly (and,
checked during the port, the Go hash on every one of its 12,000 ticks), and
the C# bots record the same command list, saved as the same bytes.

## Run it

```bash
dotnet test                                                # all tests, including the 10-minute replay gate
dotnet run --project src/SimRun                            # bot vs bot, prints the map each minute
dotnet run --project src/SimRun -- -every 20 -ticks 2400   # watch every second for 2 minutes
dotnet run --project src/SimRun -- -save m.json            # save the replay
dotnet run --project src/SimRun -- -replay m.json          # play it back
```

SimRun also takes `-seed N`. In the text view, player 1 is uppercase and
player 2 lowercase: `T` tank, `R` rifleman, `K` rocket trooper, `B` buggy,
`A` artillery. `#` is rock.

The library targets `net8.0` (what Godot 4 uses) and `net10.0`; SimRun and
the tests target `net10.0`.

## Layout

| Path | What it is |
| --- | --- |
| `src/SimCore/FixedPoint` | Fixed-point math (1 cell = 1024 units), vectors, 32 facings |
| `src/SimCore/Rng` | Seeded SplitMix64; the only randomness allowed |
| `src/SimCore/Sim` | World, units, commands, pathfinding (A*), movement, combat, hashing |
| `src/SimCore/Scenarios` | Starting maps; `Skirmish` is a 64×64 two-player map |
| `src/SimCore/Bots` | Scripted random player for recording test matches |
| `src/SimCore/Replays` | Record, save, load and play matches |
| `src/SimRun` | Headless runner with a text map view |
| `tests/SimCore.Tests` | xUnit tests; `testdata/` holds the golden replay |

## Determinism rules

`FixedPoint`, `Rng`, `Sim`, `Scenarios` and `Bots` must follow these.
`DeterminismRulesTests` enforces the first five.

1. **No floating point.** No `float`, `double`, `decimal` or float math; use
   `Fixed`. Float results can differ between ARM and x86.
2. **No hash collections.** No `Dictionary`, `HashSet` or `GetHashCode`: hash
   codes are randomized per process. Use lists sorted by ID.
3. **No time, environment, or global randomness.** No `DateTime`,
   `Stopwatch`, `Environment` or `System.Random`. Use the world's `Rand`.
4. **No threads.** No `Thread`, `Task`, `Parallel` or `async`. The tick runs
   on one thread.
5. **No `unsafe` or reflection.**
6. **Fixed order everywhere.** Systems run in a fixed order and visit units by ID.
   Every comparison used for sorting or picking a "best" breaks ties by ID.
7. **Everything that matters is hashed.** New state goes into `World.Hash`.

Integer arithmetic wraps on overflow (the build never enables overflow
checking), integer `/` and `%` truncate toward zero, and `>>` on signed values
is arithmetic: the same on every .NET platform.

## Tests that guard determinism

- `ReplayMatchesLiveMatch`: a recorded 10-minute bot match, saved and loaded,
  replays to the same hash on all 12,000 ticks, twice.
- `GoldenReplay`: a committed replay must produce committed hashes. CI runs it
  on Linux, macOS and Windows, so a cross-platform desync fails the build.
  After an intentional gameplay change, re-record:
  `SIMCORE_UPDATE_GOLDEN=1 dotnet test --filter GoldenReplay`.
- `BotMatchMatchesGoldenCommands`: recording the golden match again from
  scratch yields the committed command list, so the bots and RNG are exact.
- `EncodeMatchesGoldenBytes`: saving a replay writes the committed bytes.
- `CommandOrderIndependentOfArrival`: peers receiving commands in a
  different order still agree.
- `RngTests.KnownSequence`: the generator's output is pinned.

## Not built yet

Unit collision and group steering, harvesting and economy, buildings and
production, projectile travel time, fog of war, skirmish AI, networking.

## Design

The game this engine is built for is designed in [docs/design.md](docs/design.md). [docs/resistance-mvp.html](docs/resistance-mvp.html) is the illustrated MVP walkthrough; download it and open it in a browser.

## License

MIT. See [LICENSE](LICENSE). This repository is the engine only; game art and content live elsewhere.

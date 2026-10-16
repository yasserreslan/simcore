# simcore

The deterministic simulation core of a classic-style RTS: the whole game
minus graphics, sound and input. A renderer (Ebitengine or similar) reads the
world and draws it; it never changes it.

Every machine in a match runs its own copy of the sim. Players exchange only
commands, scheduled for a future tick. Same starting world + same commands =
bit-identical world on every tick, on every OS and CPU. That's what makes
lockstep multiplayer and replays work.

## Run it

```bash
go test ./...                                 # all tests, including the 10-minute replay gate
go run ./cmd/simrun                           # bot vs bot, prints the map each minute
go run ./cmd/simrun -every 20 -ticks 2400     # watch every second for 2 minutes
go run ./cmd/simrun -save m.json              # save the replay
go run ./cmd/simrun -replay m.json            # play it back
```

In the text view, player 1 is uppercase and player 2 lowercase: `T` tank,
`R` rifleman, `K` rocket trooper, `B` buggy, `A` artillery. `#` is rock.

## Layout

| Package | What it is |
| --- | --- |
| `fixed` | Fixed-point math (1 cell = 1024 units), vectors, 32 facings |
| `rng` | Seeded SplitMix64; the only randomness allowed |
| `sim` | World, units, commands, pathfinding (A*), movement, combat, hashing |
| `scenario` | Starting maps; `Skirmish` is a 64×64 two-player map |
| `bot` | Scripted random player for recording test matches |
| `replay` | Record, save, load and play matches; golden replay test |
| `cmd/simrun` | Headless runner with a text map view |

## Determinism rules

`sim`, `fixed` and `rng` must follow these. `TestDeterminismRules` enforces
the first five.

1. **No floats.** Use `fixed.Fixed`. Float results can differ between ARM and x86.
2. **No maps.** Go map iteration order is random. Use slices sorted by ID.
3. **No time, `os`, or global randomness.** Use the world's `rng`.
4. **No goroutines or channels.** The tick runs on one goroutine.
5. **No `unsafe` or `reflect`.**
6. **Fixed order everywhere.** Systems run in a fixed order and visit units by ID.
   Every comparison used for sorting or picking a "best" breaks ties by ID.
7. **Everything that matters is hashed.** New state goes into `World.Hash`.

## Tests that guard determinism

- `TestReplayMatchesLiveMatch`: a recorded 10-minute bot match, saved and
  loaded, replays to the same hash on all 12,000 ticks, twice.
- `TestGoldenReplay`: a committed replay must produce committed hashes. CI runs
  it on Linux, macOS, Windows and 32-bit x86, so a cross-platform desync fails
  the build. After an intentional gameplay change, re-record:
  `go test ./replay -run TestGoldenReplay -update`.
- `TestCommandOrderIndependentOfArrival`: peers receiving commands in a
  different order still agree.

## Not built yet

Unit collision and group steering, harvesting and economy, buildings and
production, projectile travel time, fog of war, skirmish AI, networking.

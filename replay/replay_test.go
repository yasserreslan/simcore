package replay

import (
	"bufio"
	"bytes"
	"flag"
	"fmt"
	"os"
	"strconv"
	"strings"
	"testing"

	"github.com/yasserreslan/simcore/sim"
)

var update = flag.Bool("update", false, "re-record the golden replay and hashes")

const (
	goldenReplay = "testdata/skirmish_seed42.json"
	goldenHashes = "testdata/skirmish_seed42.hashes"
	matchTicks   = 10 * 60 * sim.TicksPerSecond // the 10-minute gate
	checkEvery   = 30 * sim.TicksPerSecond
)

// TestReplayMatchesLiveMatch is the build-order gate for the sim core: a
// recorded 10-minute match, saved and loaded, replays to the same hash on
// every tick, twice in a row.
func TestReplayMatchesLiveMatch(t *testing.T) {
	rec, live, err := Record("skirmish", 7, matchTicks)
	if err != nil {
		t.Fatal(err)
	}
	var buf bytes.Buffer
	if err := Encode(&buf, rec); err != nil {
		t.Fatal(err)
	}
	loaded, err := Decode(&buf)
	if err != nil {
		t.Fatal(err)
	}
	for run := 1; run <= 2; run++ {
		replayed, _, err := Play(loaded)
		if err != nil {
			t.Fatal(err)
		}
		compareHashes(t, fmt.Sprintf("replay run %d", run), live, replayed)
	}
}

// TestGoldenReplay replays a committed match and checks its hashes against
// committed values. CI runs it on Linux, macOS and Windows: the same replay
// must produce the same hashes on every OS and CPU architecture.
//
// After an intentional gameplay change, re-record with:
//
//	go test ./replay -run TestGoldenReplay -update
func TestGoldenReplay(t *testing.T) {
	if *update {
		rec, _, err := Record("skirmish", 42, matchTicks)
		if err != nil {
			t.Fatal(err)
		}
		if err := Save(goldenReplay, rec); err != nil {
			t.Fatal(err)
		}
		hashes, _, err := Play(rec)
		if err != nil {
			t.Fatal(err)
		}
		if err := writeCheckpoints(goldenHashes, hashes); err != nil {
			t.Fatal(err)
		}
	}

	rec, err := Load(goldenReplay)
	if err != nil {
		t.Fatal(err)
	}
	hashes, _, err := Play(rec)
	if err != nil {
		t.Fatal(err)
	}
	want, err := readCheckpoints(goldenHashes)
	if err != nil {
		t.Fatal(err)
	}
	if len(want) == 0 {
		t.Fatal("no golden checkpoints")
	}
	for tick, h := range want {
		if tick < 1 || tick > len(hashes) {
			t.Fatalf("golden tick %d outside the %d-tick replay", tick, len(hashes))
		}
		if got := hashes[tick-1]; got != h {
			t.Fatalf("desync at tick %d: hash %016x, golden %016x", tick, got, h)
		}
	}
}

func compareHashes(t *testing.T, label string, want, got []uint64) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("%s: %d ticks, want %d", label, len(got), len(want))
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("%s: desync at tick %d: %016x vs %016x", label, i+1, got[i], want[i])
		}
	}
}

// Checkpoint files hold "tick hash" lines; hashes[i] is the hash after tick i+1.
func writeCheckpoints(path string, hashes []uint64) error {
	var b strings.Builder
	for i, h := range hashes {
		tick := i + 1
		if tick%checkEvery == 0 || tick == len(hashes) {
			fmt.Fprintf(&b, "%d %016x\n", tick, h)
		}
	}
	return os.WriteFile(path, []byte(b.String()), 0o644)
}

func readCheckpoints(path string) (map[int]uint64, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	out := map[int]uint64{}
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		fields := strings.Fields(sc.Text())
		if len(fields) == 0 {
			continue
		}
		if len(fields) != 2 {
			return nil, fmt.Errorf("bad checkpoint line %q", sc.Text())
		}
		tick, err := strconv.Atoi(fields[0])
		if err != nil {
			return nil, err
		}
		h, err := strconv.ParseUint(fields[1], 16, 64)
		if err != nil {
			return nil, err
		}
		out[tick] = h
	}
	return out, sc.Err()
}

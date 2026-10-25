#!/usr/bin/env python3
"""Prints a before/after table of SimRun -bench results.

    python3 bench/compare.py --before bench/baseline_*.json --after bench/index_*.json
    python3 bench/compare.py --before bench/baseline_*.json            # one side only
    python3 bench/compare.py ... --systems vision,orders,combat,projectiles

Accepts JSON from `SimRun -bench N` or `-move-bench`; movement workloads use
body counts for units, and omit hash timing. Results match by (workload, units).
Times are milliseconds per tick.
"""
import argparse
import json
import sys


def load(paths):
    out = {}
    for p in paths:
        with open(p) as f:
            for r in json.load(f)["results"]:
                out[(r["workload"], r.get("units", r.get("bodies")))] = r
    return out


def fmt(v, digits=2):
    if v is None:
        return "-"
    if isinstance(v, float):
        return f"{v:.{digits}f}"
    return str(v)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--before", nargs="+", required=True)
    ap.add_argument("--after", nargs="*", default=[])
    ap.add_argument("--systems", default="vision,orders,groundMovement,combat,projectiles")
    ap.add_argument("--markdown", action="store_true", help="print a Markdown table")
    a = ap.parse_args()
    before, after = load(a.before), load(a.after)
    systems = [s for s in a.systems.split(",") if s]
    keys = sorted(set(before) | set(after), key=lambda k: (k[1], k[0]))
    cols = ["workload", "units", "ticks", "p50", "p99", "max", "hash", "KB/tick"] + systems
    rows = []
    for k in keys:
        b, f = before.get(k), after.get(k)

        def pair(get, digits=2):
            bv = get(b) if b else None
            if not after:
                return fmt(bv, digits)
            av = get(f) if f else None
            s = f"{fmt(bv, digits)} -> {fmt(av, digits)}"
            if isinstance(bv, (int, float)) and isinstance(av, (int, float)) and av > 0 and bv > 0:
                s += f" ({bv / av:.1f}x)"
            return s

        rows.append([
            k[0], str(k[1]),
            pair(lambda r: r["ticks"], 0),
            pair(lambda r: r["tickMs"]["p50"]),
            pair(lambda r: r["tickMs"]["p99"]),
            pair(lambda r: r["tickMs"]["max"]),
            pair(lambda r: r.get("hashMs", {}).get("mean"), 3),
            pair(lambda r: (r["allocBytesPerTick"]["mean"] if isinstance(r["allocBytesPerTick"], dict) else r["allocBytesPerTick"]) / 1024.0, 1),
        ] + [pair(lambda r, s=s: r["systemsMs"][s]["mean"]) for s in systems])
    if a.markdown:
        print("| " + " | ".join(cols) + " |")
        print("|" + "---|" * len(cols))
        for r in rows:
            print("| " + " | ".join(r) + " |")
        return 0
    widths = [max(len(c), *(len(r[i]) for r in rows)) for i, c in enumerate(cols)] if rows else [len(c) for c in cols]
    print("  ".join(c.ljust(w) for c, w in zip(cols, widths)))
    for r in rows:
        print("  ".join(v.ljust(w) for v, w in zip(r, widths)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

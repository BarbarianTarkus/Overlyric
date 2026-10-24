"""Print the most recently cached lyrics entry, for inspecting what was actually stored.

Usage: python dump_cache.py [count] [max_lines]
"""
import glob
import json
import os
import sys


def main() -> int:
    count = int(sys.argv[1]) if len(sys.argv) > 1 else 1
    max_lines = int(sys.argv[2]) if len(sys.argv) > 2 else 22

    directory = os.path.join(
        os.environ.get("LOCALAPPDATA", ""), "Overlyric", "cache"
    )
    paths = sorted(glob.glob(os.path.join(directory, "*.json")), key=os.path.getmtime, reverse=True)

    if not paths:
        print("cache is empty")
        return 0

    for path in paths[:count]:
        payload = json.load(open(path, encoding="utf-8"))
        result = payload.get("result") or payload.get("Result") or payload
        lines = result.get("lines") or result.get("Lines") or []

        print(f"file     : {os.path.basename(path)}")
        print(f"track    : {result.get('trackName')}")
        print(f"album    : {result.get('albumName')}")
        print(f"lrclibId : {result.get('lrclibId') or result.get('LrclibId')}")
        print(f"synced   : {result.get('isSynced')}   lines: {len(lines)}")
        print(f"--- first {max_lines} lines ---")

        for line in lines[:max_lines]:
            ticks = line.get("startTicks", 0)
            total = ticks / 10_000_000
            minutes, seconds = divmod(total, 60)
            text = line.get("text")
            print(f"  {int(minutes):02d}:{seconds:05.2f}  {text}")

        print()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

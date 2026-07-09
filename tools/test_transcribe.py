#!/usr/bin/env python3
"""Post a WAV file to the sidecar's /transcribe endpoint and print the result.

Usage: python tools/test_transcribe.py <file.wav> [--url http://127.0.0.1:5075] [--prompt "..."]
"""

from __future__ import annotations

import argparse
import base64
import json
import sys
import urllib.request


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("wav")
    parser.add_argument("--url", default="http://127.0.0.1:5075")
    parser.add_argument("--prompt", default="Air traffic control radio.")
    args = parser.parse_args()

    with open(args.wav, "rb") as handle:
        audio_b64 = base64.b64encode(handle.read()).decode("ascii")

    payload = json.dumps({"audio_b64": audio_b64, "prompt": args.prompt}).encode("utf-8")
    request = urllib.request.Request(
        args.url.rstrip("/") + "/transcribe",
        data=payload,
        headers={"Content-Type": "application/json"},
    )

    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            body = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        print(f"HTTP {exc.code}: {exc.read().decode('utf-8', 'replace')}", file=sys.stderr)
        return 1

    print(body.get("text", ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

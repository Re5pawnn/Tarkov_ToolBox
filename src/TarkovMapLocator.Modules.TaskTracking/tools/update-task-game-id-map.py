#!/usr/bin/env python3
"""Build recognition and prerequisite indexes from the generated task catalog."""

from __future__ import annotations

import argparse
import json
import re
from datetime import datetime, timezone
from pathlib import Path


def build_mapping(catalog: dict) -> dict:
    tasks = catalog.get("tasks", [])
    valid_tracking_ids = {
        str(task.get("id") or "").strip()
        for task in tasks
        if str(task.get("id") or "").strip()
    }
    mappings = []
    unmatched = []
    prerequisites = []
    for task in tasks:
        tracking_id = str(task.get("id") or "").strip()
        mode = str(task.get("mode") or "pve").strip().lower()
        game_ids = sorted({
            str(game_id or "").strip().lower()
            for game_id in task.get("gameTaskIds", [])
            if re.fullmatch(r"[0-9a-f]{24}", str(game_id or "").strip(), flags=re.I)
        })
        if game_ids:
            mappings.append({"trackingTaskId": tracking_id, "mode": mode, "gameTaskIds": game_ids})
        else:
            unmatched.append({
                "trackingTaskId": tracking_id,
                "mode": mode,
                "name": str(task.get("name") or "").strip(),
                "reason": "Kaedeori has no unique 24-character game task id",
            })

        previous_ids = sorted({
            str(previous_id or "").strip()
            for previous_id in task.get("previousTaskIds", [])
            if str(previous_id or "").strip() in valid_tracking_ids
            and str(previous_id or "").strip() != tracking_id
        })
        if previous_ids:
            prerequisites.append({
                "trackingTaskId": tracking_id,
                "mode": mode,
                "prerequisiteTrackingTaskIds": previous_ids,
            })

    return {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "sources": catalog.get("sourceApiUrls") or [catalog.get("sourceApiUrl") or catalog.get("sourceUrl")],
        "mappings": mappings,
        "prerequisites": prerequisites,
        "unmatched": unmatched,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    # Retained for compatibility with old update commands; no fan-out is needed now.
    parser.add_argument("--workers", type=int, default=1)
    args = parser.parse_args()

    catalog = json.loads(args.catalog.read_text(encoding="utf-8-sig"))
    result = build_mapping(catalog)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(
        f"wrote {len(result['mappings'])} mappings; "
        f"{len(result['prerequisites'])} prerequisite sets; "
        f"{len(result['unmatched'])} display-only tasks -> {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Build the offline PVP, PVE and seasonal task catalogs from Kaedeori."""

from __future__ import annotations

import argparse
import html
import json
import re
import urllib.parse
import urllib.request
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path


SOURCE_URL = "https://member.kaedeori.com/api/tarkov/betaTask/list"
SOURCE_PAGE_URL = "https://member.kaedeori.com/app/taskslist"
QUERY = {
    "name": "",
    "page": "1",
    "pageSize": "999",
    "includeCompleted": "true",
    "includeUnavailable": "true",
    "lang": "zh",
}
MODES = ("pvp", "pve", "season")
TRADERS = {
    "prapor": ("prapor", "Prapor"),
    "therapist": ("therapist", "Therapist"),
    "fence": ("fence", "Fence"),
    "skier": ("skier", "Skier"),
    "peacekeeper": ("peacekeeper", "Peacekeeper"),
    "mechanic": ("mechanic", "Mechanic"),
    "ragman": ("ragman", "Ragman"),
    "jaeger": ("jaeger", "Jaeger"),
    "lightkeeper": ("lightkeeper", "Lightkeeper"),
    "ref": ("ref", "竞技场裁判"),
    "btr driver": ("btr", "BTR Driver"),
    "btr司机": ("btr", "BTR Driver"),
}
GROUPS = {
    "loyalty1": (0, "好感度等级 I"),
    "loyalty2": (1, "好感度等级 II"),
    "loyalty3": (2, "好感度等级 III"),
    "loyalty4": (3, "好感度等级 IV"),
    "core": (4, "核心任务"),
}


def fetch_json(url: str) -> dict:
    failure: Exception | None = None
    for _attempt in range(3):
        try:
            request = urllib.request.Request(
                url,
                headers={"User-Agent": "TarkovMapLocatorDesktop task catalog updater"},
            )
            with urllib.request.urlopen(request, timeout=60) as response:
                return json.load(response)
        except Exception as exception:
            failure = exception
    raise failure or RuntimeError(f"failed to read {url}")


def clean_text(value: object) -> str:
    text = html.unescape(str(value or "")).replace("\r", "").strip()
    return re.sub(r"[ \t\u3000]+", " ", text)


def clean_objectives(task: dict) -> list[str]:
    text = clean_text(task.get("objectiveText") or task.get("taskRequirementText"))
    if not text or text in {"无", "无改动", "暂无"}:
        return []
    parts = [part.strip(" -•\t") for part in re.split(r"\n+", text) if part.strip(" -•\t")]
    return list(dict.fromkeys(parts))


def normalize_map_name(value: object) -> str:
    name = clean_text(value)
    return "任意地图" if name.casefold() == "any" else name


def trader(task: dict) -> tuple[str, str]:
    raw = clean_text(task.get("betaTrader") or task.get("trader")).casefold()
    if raw in TRADERS:
        return TRADERS[raw]
    key = re.sub(r"[^a-z0-9]+", "-", raw).strip("-") or "unknown"
    display = clean_text(task.get("betaTrader") or task.get("trader")) or key
    return key, display


def group(task: dict) -> tuple[str, int, str]:
    if bool(task.get("betaCore")):
        key = "core"
    else:
        level = max(1, min(4, int(task.get("requiredTraderLevel") or 1)))
        key = f"loyalty{level}"
    order, name = GROUPS[key]
    return key, order, name


def prior_tracking_ids(game_id_map_path: Path | None) -> dict[str, list[str]]:
    if game_id_map_path is None or not game_id_map_path.is_file():
        return {}
    data = json.loads(game_id_map_path.read_text(encoding="utf-8-sig"))
    result: dict[str, list[str]] = defaultdict(list)
    for mapping in data.get("mappings", []):
        tracking_id = clean_text(mapping.get("trackingTaskId"))
        for game_id in mapping.get("gameTaskIds", []):
            game_id = clean_text(game_id).lower()
            if tracking_id and re.fullmatch(r"[0-9a-f]{24}", game_id):
                result[game_id].append(tracking_id)
    return result


def is_primary(task: dict) -> bool:
    definition = clean_text(task.get("betaDefinitionId")).casefold()
    return not definition.startswith(("legacy-fallback:", "supplement:"))


def group_by_source_id(tasks: list[dict]) -> dict[str, list[dict]]:
    result: dict[str, list[dict]] = defaultdict(list)
    for task in tasks:
        result[clean_text(task.get("id")).lower()].append(task)
    return result


def build_catalog(response: dict, source_url: str, game_id_map_path: Path | None, mode: str) -> dict:
    response_data = response.get("data", {})
    source_tasks = response_data.get("data") or response_data.get("tasks") or []
    if len(source_tasks) < 500:
        raise RuntimeError(f"Kaedeori task API shape changed: received {len(source_tasks)} tasks")

    old_tracking_ids = prior_tracking_ids(game_id_map_path)
    used_tracking_ids: set[str] = set()
    records: list[dict] = []

    # Some compatibility rows reuse a legacy game id. Only the primary row may
    # consume that id for log recognition, otherwise one event marks two tasks.
    primary_definition_by_source_id: dict[str, str] = {}
    for source_id, duplicated_tasks in group_by_source_id(source_tasks).items():
        preferred = sorted(
            duplicated_tasks,
            key=lambda task: (
                0 if is_primary(task) else 1,
                int(task.get("betaSequence") or 999999),
                clean_text(task.get("betaDefinitionId")),
            ),
        )[0]
        primary_definition_by_source_id[source_id] = clean_text(preferred.get("betaDefinitionId"))

    for task in source_tasks:
        source_id = clean_text(task.get("id"))
        source_id_lower = source_id.lower()
        definition_id = clean_text(task.get("betaDefinitionId"))
        if not definition_id:
            raise RuntimeError(f"Kaedeori task has no betaDefinitionId: {source_id}")

        game_id_eligible = (
            bool(re.fullmatch(r"[0-9a-f]{24}", source_id_lower))
            and primary_definition_by_source_id.get(source_id_lower) == definition_id
        )
        tracking_id = ""
        if mode == "pve" and game_id_eligible:
            for candidate in old_tracking_ids.get(source_id_lower, []):
                if not candidate.startswith(("pvp:", "season:")) and candidate not in used_tracking_ids:
                    tracking_id = candidate
                    break
        if not tracking_id:
            tracking_id = (
                f"kaedeori:{definition_id}"
                if mode == "pve"
                else f"{mode}:kaedeori:{definition_id}"
            )
        if tracking_id in used_tracking_ids:
            raise RuntimeError(f"Duplicate tracking task id: {tracking_id}")
        used_tracking_ids.add(tracking_id)

        trader_key, trader_name = trader(task)
        group_key, group_order, group_name = group(task)
        name = clean_text(task.get("name"))
        aliases = []
        source_name = clean_text(task.get("sourceTaskName"))
        if source_name and source_name != name:
            aliases.append(source_name)

        records.append(
            {
                "id": tracking_id,
                "mode": mode,
                "sourceTaskId": source_id,
                "sourceDefinitionId": definition_id,
                "name": name,
                "aliases": aliases,
                "gameTaskIds": [source_id_lower] if game_id_eligible else [],
                "sectionKey": trader_key,
                "traderKey": trader_key,
                "traderName": trader_name,
                "groupKey": group_key,
                "groupName": group_name,
                "groupOrder": group_order,
                "requiredTraderLevel": int(task.get("requiredTraderLevel") or 0),
                "sequence": int(task.get("betaSequence") or 0),
                "level": int(task.get("minPlayerLevel") or 0),
                "mapName": normalize_map_name(task.get("map")),
                "objectives": clean_objectives(task),
                "wikiUrl": clean_text(task.get("wikiLink")),
                "previousTaskIds": [],
                "nextTaskIds": [],
                "available": bool(task.get("available", True)),
                "x": group_order,
                "y": int(task.get("betaSequence") or 0),
                "width": 220,
                "height": 78,
                "isSupplemental": False,
                "_previousSourceIds": [clean_text(value).lower() for value in task.get("previousTaskIds", [])],
                "_nextSourceIds": [clean_text(value).lower() for value in task.get("nextTaskIds", [])],
            }
        )

    records_by_source_id: dict[str, list[dict]] = defaultdict(list)
    for record in records:
        records_by_source_id[record["sourceTaskId"].lower()].append(record)

    def resolve(source_id: str) -> str | None:
        candidates = records_by_source_id.get(source_id.lower(), [])
        if not candidates:
            return None
        return sorted(
            candidates,
            key=lambda record: (
                0 if primary_definition_by_source_id.get(source_id.lower()) == record["sourceDefinitionId"] else 1,
                record["sequence"],
                record["id"],
            ),
        )[0]["id"]

    for record in records:
        record["previousTaskIds"] = list(dict.fromkeys(
            resolved
            for source_id in record.pop("_previousSourceIds")
            if (resolved := resolve(source_id)) is not None and resolved != record["id"]
        ))
        record["nextTaskIds"] = list(dict.fromkeys(
            resolved
            for source_id in record.pop("_nextSourceIds")
            if (resolved := resolve(source_id)) is not None and resolved != record["id"]
        ))

    records.sort(key=lambda task: (
        task["traderKey"], task["groupOrder"], task["sequence"], task["name"], task["id"]
    ))
    data_versions = sorted({clean_text(task.get("dataVersion")) for task in source_tasks if task.get("dataVersion")})
    return {
        "schemaVersion": 1,
        "sourceUrl": SOURCE_PAGE_URL,
        "sourceApiUrl": source_url,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "dataVersions": data_versions,
        "tasks": records,
        "lines": [],
        "supplementSources": [],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default=SOURCE_URL)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--game-id-map", type=Path)
    args = parser.parse_args()

    game_id_map_path = args.game_id_map or args.output.parent / "task-game-id-map.json"
    responses: dict[str, tuple[dict, str]] = {}
    if args.input:
        payload = json.loads(args.input.read_text(encoding="utf-8-sig"))
        if not all(mode in payload for mode in MODES):
            raise RuntimeError("Combined input must contain pvp, pve and season responses")
        for mode in MODES:
            responses[mode] = (payload[mode], f"{args.url}?{urllib.parse.urlencode(QUERY | {'gameMode': mode})}")
    else:
        for mode in MODES:
            source_url = f"{args.url}?{urllib.parse.urlencode(QUERY | {'gameMode': mode})}"
            responses[mode] = (fetch_json(source_url), source_url)

    mode_catalogs = [
        build_catalog(response, source_url, game_id_map_path, mode)
        for mode, (response, source_url) in responses.items()
    ]
    all_tasks = [task for mode_catalog in mode_catalogs for task in mode_catalog["tasks"]]
    catalog = {
        "schemaVersion": 1,
        "sourceUrl": SOURCE_PAGE_URL,
        "sourceApiUrl": SOURCE_URL,
        "sourceApiUrls": [source_url for _response, source_url in responses.values()],
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "dataVersions": sorted({
            version
            for mode_catalog in mode_catalogs
            for version in mode_catalog["dataVersions"]
        }),
        "modeTaskCounts": {
            mode: len(mode_catalog["tasks"])
            for mode, mode_catalog in zip(MODES, mode_catalogs, strict=True)
        },
        "tasks": all_tasks,
        "lines": [],
        "supplementSources": [],
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    mapped = sum(bool(task["gameTaskIds"]) for task in catalog["tasks"])
    related = sum(bool(task["previousTaskIds"]) for task in catalog["tasks"])
    counts_summary = ", ".join(
        f"{mode}={catalog['modeTaskCounts'][mode]}" for mode in MODES
    )
    print(
        f"wrote {len(catalog['tasks'])} Kaedeori tasks "
        f"({counts_summary}); "
        f"{mapped} recognition ids; {related} explicit prerequisite sets -> {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Turns the open tasks in a Vikunja account into a file EtchaSketch can import.

Usage:
    VIKUNJA_TOKEN=... python3 tools/vikunja-to-etchasketch.py https://tasks.example.com out.json

The token is a Vikunja API token (Settings -> API tokens) that can read tasks and projects.
Nothing is changed in Vikunja. The file holds your tasks in plain text, so keep it somewhere private
and delete it once it has been imported. See docs/import.md for what carries over.
"""
import json
import os
import sys
import urllib.request

EPOCH = "0001-01-01T00:00:00Z"
PRIORITY = {0: "normal", 1: "low", 2: "normal", 3: "high", 4: "urgent", 5: "urgent"}


def get(base, token, path):
    request = urllib.request.Request(base + "/api/v1" + path, headers={"Authorization": "Bearer " + token})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def fetch_all(base, token, path):
    items, page = [], 1
    while True:
        batch = get(base, token, f"{path}{'&' if '?' in path else '?'}per_page=50&page={page}")
        if not batch:
            return items
        items += batch
        page += 1


def repeat_of(task):
    seconds = task.get("repeat_after") or 0
    if seconds and seconds % 86400 == 0:
        days = seconds // 86400
        if days % 7 == 0:
            return {"every": days // 7, "unit": "weekly"}
        return {"every": days, "unit": "daily"}
    if task.get("repeat_mode") == 1:
        return {"every": 1, "unit": "monthly"}
    return None


def convert(task, projects):
    out = {
        "title": task["title"],
        "source": f"vikunja:{task['id']}",
        "done": bool(task["done"]),
        "priority": PRIORITY.get(task.get("priority", 0), "normal"),
        "project": projects.get(task.get("project_id")),
        "labels": [label["title"] for label in task.get("labels") or []],
    }
    if task.get("description"):
        out["notes"] = task["description"]
    if task.get("due_date") and task["due_date"] != EPOCH:
        out["due"] = task["due_date"][:10]
    if (repeat := repeat_of(task)):
        out["repeat"] = repeat
    return out


def main():
    if len(sys.argv) != 3 or not os.environ.get("VIKUNJA_TOKEN"):
        sys.exit(__doc__)
    base, target = sys.argv[1].rstrip("/"), sys.argv[2]
    token = os.environ["VIKUNJA_TOKEN"]
    projects = {p["id"]: p["title"] for p in get(base, token, "/projects")}
    tasks = [convert(t, projects) for t in fetch_all(base, token, "/tasks")]
    with open(target, "w", encoding="utf-8") as file:
        json.dump({"format": "etchasketch-tasks", "version": 1, "tasks": tasks}, file, indent=2, ensure_ascii=False)
    os.chmod(target, 0o600)
    print(f"Wrote {len(tasks)} tasks ({sum(not t['done'] for t in tasks)} open) to {target}")


if __name__ == "__main__":
    main()

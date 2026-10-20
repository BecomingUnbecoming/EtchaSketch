#!/usr/bin/env python3
"""Turns the open tasks in a Vikunja account into a file EtchaSketch can import.

Usage:
    VIKUNJA_TOKEN=... python3 tools/vikunja-to-etchasketch.py https://tasks.example.com out.json

The token is a Vikunja API token (Settings -> API tokens) that can read tasks and projects.
Nothing is changed in Vikunja. The file holds your tasks in plain text, so keep it somewhere private
and delete it once it has been imported. See docs/import.md for what carries over.
"""
import json
from datetime import datetime
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


RELATION = {
    "blocked": "Blocked by",
    "blocking": "Blocking",
    "follows": "Follows",
    "precedes": "Comes before",
    "duplicates": "Duplicates",
    "duplicateof": "Duplicate of",
    "related": "Related",
    "subtask": "Subtask of",
    "parenttask": "Parent task",
}


# How Vikunja's relations become EtchaSketch links, from this task's side. Vikunja lists each
# relation on both tasks, so the mirror kinds are left to the other task.
LINK = {
    "blocking": "blocks",
    "precedes": "blocks",
    "related": "related",
    "duplicateof": "related",
}


def notes_of(task, titles):
    """The description, then a line for any relation EtchaSketch has no link for."""
    lines = [task["description"].strip()] if task.get("description") else []
    for kind, others in (task.get("related_tasks") or {}).items():
        if kind in LINK or kind in ("blocked", "follows", "duplicates", "parenttask", "subtask"):
            continue
        names = ", ".join(titles.get(other["id"], f"task {other['id']}") for other in others)
        lines.append(f"{RELATION.get(kind, kind.capitalize())}: {names}")
    return "\n\n".join(lines) if lines else None


def relations_of(task):
    """The parent task's source, and links to other tasks by source."""
    related = task.get("related_tasks") or {}
    parents = related.get("parenttask") or []
    parent = f"vikunja:{parents[0]['id']}" if parents else None
    links = [{"kind": LINK[kind], "source": f"vikunja:{other['id']}"}
             for kind, others in related.items() if kind in LINK for other in others]
    return parent, links


def remind_days(task):
    """Reminders set relative to the due date, as whole days before it (the earliest one)."""
    days = [-r["relative_period"] // 86400 for r in task.get("reminders") or []
            if r.get("relative_to") == "due_date" and r.get("relative_period", 0) <= -86400]
    return max(days) if days else None


def remind_minutes(task):
    """Reminders less than a day before the due date, as whole minutes before it (the earliest one)."""
    minutes = [-r["relative_period"] // 60 for r in task.get("reminders") or []
               if r.get("relative_to") == "due_date" and -86400 < r.get("relative_period", 0) <= -60]
    return max(minutes) if minutes else None


def local_due(task):
    """The due date and time in this computer's time zone. Vikunja stores UTC, so the date alone can be a day off."""
    if not task.get("due_date") or task["due_date"] == EPOCH:
        return None, None
    at = datetime.fromisoformat(task["due_date"].replace("Z", "+00:00")).astimezone()
    # Vikunja has no "no time" option; midnight is what a date without a time ends up as.
    time = None if (at.hour, at.minute) == (0, 0) else at.strftime("%H:%M")
    return at.strftime("%Y-%m-%d"), time


def convert(task, projects, titles):
    out = {
        "title": task["title"],
        "source": f"vikunja:{task['id']}",
        "done": bool(task["done"]),
        "priority": PRIORITY.get(task.get("priority", 0), "normal"),
        "project": projects.get(task.get("project_id")),
        "labels": [label["title"] for label in task.get("labels") or []],
    }
    if (notes := notes_of(task, titles)):
        out["notes"] = notes
    parent, links = relations_of(task)
    if parent:
        out["parent"] = parent
    if links:
        out["links"] = links
    if (days := remind_days(task)):
        out["remindDaysBefore"] = days
    due, time = local_due(task)
    if due:
        out["due"] = due
    if time:
        out["dueTime"] = time
        if (minutes := remind_minutes(task)):
            out["remindMinutesBefore"] = minutes
    if (repeat := repeat_of(task)):
        out["repeat"] = repeat
    return out


def main():
    if len(sys.argv) != 3 or not os.environ.get("VIKUNJA_TOKEN"):
        sys.exit(__doc__)
    base, target = sys.argv[1].rstrip("/"), sys.argv[2]
    token = os.environ["VIKUNJA_TOKEN"]
    projects = {p["id"]: p["title"] for p in get(base, token, "/projects")}
    everything = fetch_all(base, token, "/tasks")
    titles = {t["id"]: t["title"] for t in everything}
    tasks = [convert(t, projects, titles) for t in everything]
    with open(target, "w", encoding="utf-8") as file:
        json.dump({"format": "etchasketch-tasks", "version": 1, "tasks": tasks}, file, indent=2, ensure_ascii=False)
    os.chmod(target, 0o600)
    print(f"Wrote {len(tasks)} tasks ({sum(not t['done'] for t in tasks)} open) to {target}")


if __name__ == "__main__":
    main()

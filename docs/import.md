# Importing tasks

EtchaSketch can bring in tasks from another app. On the home screen choose **Import tasks**,
choose the file (or paste its text), choose **Check the file** to see what would happen, then
**Import**.

- Tasks are added as **your own private tasks**. Share any of them afterwards, or set `"shared": true`
  in the file to make them household tasks.
- **Finished tasks are left out.**
- Importing the same file twice adds nothing the second time. Each imported task carries an
  "Imported from …" line in its notes, which is how EtchaSketch recognises it.
- A task that can't be imported (no title, a date it can't read) is listed by number, and the rest
  still go in.

## What carries over

| In the file | In EtchaSketch |
| --- | --- |
| `title` | Title |
| `notes` | Notes |
| `due` (`2026-10-21`) | Due date |
| `priority` (`low`, `normal`, `high`, `urgent`) | Priority |
| `repeat` (`{"every": 2, "unit": "weeks"}`; daily, weekly or monthly) | Repeats |
| `shared`, `done`, `source` | Household task, left out, duplicate check |
| `project`, `labels` | Project and labels |
| `remindDaysBefore` (`1`) | A reminder that many days before the due date, as well as on it |
| `dueTime` (`14:05`) | A time of day on the due date; reminders come at that time |
| `parent` (another task's `source`) | Makes it a step (subtask) of that task |
| `links` (`[{"kind": "blocks", "source": "vikunja:12"}]`; `blocks`, `blockedBy` or `related`) | Links to other tasks |
| `remindMinutesBefore` (`15`) | Another reminder that many minutes before the due time |

Start dates, attachments and comments aren't carried over. Links and parents are made once every task
in the file is in, so they can point at tasks further down the file or imported earlier; one pointing
at a task that isn't there (finished tasks are left out) is listed as a problem and the task still goes in.

**Imported before links existed?** Earlier imports wrote links into the notes as lines like
`Blocked by: Install the SDK`. In the Import panel, **Turn link lines into links** makes them real links
(and "Parent task" lines into subtasks) and removes the lines. A line naming a task it can't find, such
as one that was finished and left out, stays as it is. It's safe to run more than once. The Vikunja
converter turns the due date's time of day into `dueTime` (Vikunja can't store a date without a time, so
midnight counts as "no time"), reminders a number of days before the due date into `remindDaysBefore`,
and reminders less than a day before it into `remindMinutesBefore`. Reminders at a fixed date and time
that isn't tied to the due date aren't carried over.

## The file

```json
{
  "format": "etchasketch-tasks",
  "version": 1,
  "tasks": [
    { "title": "Rotate keys", "due": "2026-10-22", "priority": "high",
      "notes": "Start with the Cloudflare token", "project": "Homelab", "labels": ["b-hands"],
      "remindDaysBefore": 1, "source": "vikunja:41" }
  ]
}
```

## From Vikunja

`tools/vikunja-to-etchasketch.py` reads your open and finished tasks through Vikunja's API and writes
the file. It needs only Python 3 and an API token, and changes nothing in Vikunja:

```sh
VIKUNJA_TOKEN=… python3 tools/vikunja-to-etchasketch.py https://tasks.example.com tasks.json
```

The file is plain text and private, so delete it once you've imported.

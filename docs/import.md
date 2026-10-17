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
| `project`, `labels` | A line in the notes, because there are no projects or labels yet |

Reminders, start dates, attachments, comments and related-task links aren't carried over.
Due dates are, and EtchaSketch reminds you from those.

## The file

```json
{
  "format": "etchasketch-tasks",
  "version": 1,
  "tasks": [
    { "title": "Rotate keys", "due": "2026-10-22", "priority": "high",
      "notes": "Start with the Cloudflare token", "source": "vikunja:41" }
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

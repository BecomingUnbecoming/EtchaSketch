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

## Things from Homebox, records from LubeLogger

On the **Things** tab, **Import from Homebox or LubeLogger** reads the CSV files those apps export.
**Check the file** says what would happen; **Import** does it. Importing the same file again adds nothing.

**Homebox** (Tools → Export inventory) becomes the household's things:

- name, place (as Homebox's path, "Home / Garage / Shelf 2"), first tag as the kind, serial number
- purchase date and price, warranty end date
- the rest (description, notes, make and model, quantity, where it was bought, lifetime warranty,
  custom fields) goes into the notes

Archived and sold items are left out and listed. Each item's notes end with
"Imported from Homebox (asset …)", which is how a second import knows it's already there.
Photos, receipts and attachments aren't carried over.

**LubeLogger** exports one CSV per vehicle and kind of record. Add the vehicle on the Things tab first
(tick "It's a vehicle"), then import each file into it:

- fuel records: recognised by their FuelConsumed column; litres, cost, distance, partial and missed fill-ups
- service records, repairs, upgrades: choose which the file holds
- description, notes and tags go into each entry's note

LubeLogger writes dates the way its server is set up. The file decides: a date like 21/10 can only be day
first, 10/21 only month first. If nothing in the file tells, month first is used (LubeLogger's default),
and the check says how dates were read. Look at a few entries afterwards.

Entries already in the log (same day, kind, distance and cost) are left out. LubeLogger's reminders and
plans aren't carried over; add them as upkeep on the vehicle.

## From Grocy

In **Import tasks**, the **From Grocy** section reads Grocy's API. It needs Grocy's address and an API key
(in Grocy: user menu → Manage API keys → Add). It only reads; nothing in Grocy changes, and the key isn't saved.
**Check Grocy** says what would come in; **Import** brings it.

- **Chores** become shared household chores, labelled `grocy`, with their description (Grocy's formatting
  removed) in the notes:

  | Grocy schedule | In EtchaSketch |
  | --- | --- |
  | Daily, every N days | Every N days |
  | Hourly, a whole number of days (Grocy stores "N days after last done" this way) | Every N days |
  | Hourly, otherwise | Every day, with "In Grocy: every N hours" in the notes |
  | Weekly on one day | Every N weeks |
  | Weekly on several days | One chore per day, like "Bins out (Monday)" and "Bins out (Thursday)" |
  | Monthly / yearly | Every N months / every 12 × N months |
  | Adaptive, manually | No repeat; the notes say what Grocy did |

  **Due dates:** off by default. Each chore keeps its repeat but has no due date, so nothing reminds until
  someone marks it done; it's then due a repeat later. Tick **Keep Grocy's due dates** to bring Grocy's
  next dates and start reminders right away.
- **Stock** becomes the pantry: amount, unit, place, best-before date, and Grocy's minimum stock amount as
  "running low at". Products that aren't in stock and have no minimum are left out.
- **Batteries** become things (kind "Batteries") with a **Charge** upkeep at Grocy's charge interval,
  counted from the last charge Grocy knows.
- **Recipes** come in with their ingredients and method; Grocy's own meal-plan helper recipes are left out.

Importing again adds nothing already brought in: chores are recognised by their Grocy id, pantry items and
recipes by name, batteries by their Grocy id in the notes. Chore history, meal plans and the shopping list
aren't carried over.

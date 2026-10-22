# Exporting

On the home screen choose **Export**, then what to save:

| Choice | File | What it's for |
| --- | --- | --- |
| **Everything** | `.json` | A copy EtchaSketch can import again: a backup, or moving to a new device. |
| **Spreadsheet** | `.csv` | One row per task, for LibreOffice, Excel or any other spreadsheet. |
| **Calendar** | `.ics` | Your open tasks that have a due date, as calendar events with their reminders. |

An export holds **your own tasks and the household's shared ones**, open and finished. Other people's
private tasks, and the notes on their shared tasks, are never included, the same as on screen.

**Importing your own export** (Import tasks → choose the `.json`) brings each task back as itself: same
task, same notes, and nothing doubles up if it's already there. Finished tasks are listed in the file but
left out on import, the same as for any import. The file uses the format in [import.md](import.md), with
a few extra fields (`dueTime`, `remindMinutesBefore`, `alarm`) and each task's `source` set to
`etchasketch:<id>`.

**The household's shared modules** are in the `.json` too, under `"household"`: the pantry, the
shopping list, recipes, the meal plan, things (the inventory), their upkeep and their logs. They're
stored row by row with their ids, so importing the file on a fresh install brings each one back as
itself, and importing it again adds nothing. The import check says what's there ("Household: 12 pantry
items, 3 things"). A file from a newer version still imports; anything this version doesn't know is
skipped. The spreadsheet and calendar files are still tasks only.

The files aren't encrypted. Keep them somewhere private, and delete copies you don't need.

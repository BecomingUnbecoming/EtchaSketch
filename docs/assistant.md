# The household assistant

A copy of EtchaSketch with no screen, for a server at home. It lets other programs (Home Assistant, Open
WebUI, Claude, a script) read and update the household's **shared** tasks. The app itself can't do that:
its data is end-to-end encrypted, so the sync server can't hand tasks to anyone.

## What it can and can't see

The assistant joins with an **assistant code**, which carries the household's key and nobody's private
key. It never receives private tasks or private notes. It can read and add shared tasks, finish them and
comment on them. Anything it adds is shared with the household, made in the name of the person you set
(`ASSISTANT_ACT_AS`).

It appears in **Sync → Devices** as "Household assistant", and removing it there cuts it off like a lost
phone.

## Setting it up

1. In the app: **Sync → Make an assistant code**. It works once, for 15 minutes.
2. On the server, in `deploy/assistant`: copy `.env.example` to `.env` and fill it in. Then:

   ```sh
   docker compose run --rm etchasketch-assistant join <code>
   docker compose run --rm etchasketch-assistant add-caller home-assistant
   docker compose up -d --build
   ```

   `add-caller` prints a token once: put it in that program's settings. Give each program its own
   (`add-caller open-webui`, `add-caller claude`), so one can be cut off with `remove-caller <name>`
   without touching the others.

It syncs every minute and keeps its database key in a separate volume (`/keys`) from its data (`/data`).

## The API

Every request needs `Authorization: Bearer <caller token>`. Dates are `2026-10-21`, times `14:05`.

| Request | Does |
| --- | --- |
| `GET /v1/tasks` (`?project=` `?label=` `?assignedTo=`) | Open shared tasks, with who they're assigned to and what they're waiting on |
| `POST /v1/tasks` `{"title": "Buy milk", "due": "2026-10-22", "dueTime": "18:00", "assignTo": "Kai", "labels": ["shopping"]}` | Adds a shared task |
| `POST /v1/tasks/{id}/complete` | Finishes it. `409` with `waitingOn` if another task blocks it; add `?evenIfBlocked=true` to finish it anyway |
| `POST /v1/tasks/{id}/comments` `{"text": "…"}` | Comments on it |
| `GET /health` | `ok`, no token needed |

Example:

```sh
curl -H "Authorization: Bearer $TOKEN" http://assistant.home:5090/v1/tasks?assignedTo=Robin
```

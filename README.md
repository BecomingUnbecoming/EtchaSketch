# EtchaSketch
Open-source, local-first life app for desktop and mobile. Plans your day in time blocks around your tasks, energy and recharge needs, gamified your way. Connects to self-hosted services like Home Assistant, and shares public rooms and streams without ever exposing private data.

## Project layout

| Project | What it is |
| --- | --- |
| `src/EtchaSketch.Core` | Data models, privacy tiers, the privacy gateway, planner and rules engine. No UI code allowed. |
| `src/EtchaSketch.Data` | Local encrypted storage and sync plumbing. |
| `src/EtchaSketch.UI` | Shared Avalonia views and view models. |
| `src/EtchaSketch.Desktop` | Windows, macOS and Linux app. |
| `src/EtchaSketch.Android` | Android app (no Google Play Services, works on GrapheneOS). |
| `src/EtchaSketch.iOS` | iOS app. |
| `src/EtchaSketch.Server` | Household sync server and relay. |
| `tests/EtchaSketch.Tests` | Unit and architecture tests. |

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/EtchaSketch.Desktop   # run the desktop app
dotnet test tests/EtchaSketch.Tests            # run the tests
```

The Android and iOS projects also need their workloads (`dotnet workload install android ios`).

To sync between devices, run the sync server on a machine at home: see [docs/sync-server.md](docs/sync-server.md).

## License

AGPL-3.0. See [LICENSE](LICENSE).

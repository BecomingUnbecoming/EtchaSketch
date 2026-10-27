# Third-party reference

Every app, library, service, standard and device that EtchaSketch builds on, connects to, imports from
or replaces, with a link to its **official** documentation and its license. Use it when building a
connector or choosing a dependency, so you start from the project's own docs rather than a third-party
guide.

Links were checked on 2026-10-27. **✔** means the link was confirmed as the project's own site in that
check; the others are long-standing official domains (Microsoft, Google, Apple, IETF and similar) that
were not re-checked. Licenses can change between releases: confirm in the project's repository before
you add a dependency. ⚠ marks something to know first (a license change, a retired project, a
replacement).

Rules that apply to every entry: the Android build never depends on Google services, any connector is
optional and removable, and anything a connector brings in is treated as Private until a person
publishes it. See [CONTRIBUTING.md](../CONTRIBUTING.md).

## Contents

- [App stack: languages, frameworks and libraries](#stack) (11)
- [Key storage on each platform](#keys) (4)
- [Platform, builds and distribution](#platform) (12)
- [Standards and protocols](#standards) (13)
- [AI providers and voice](#ai) (12)
- [Connectors and imports: work, tasks and money](#work) (12)
- [Connectors and imports: household](#household) (7)
- [Connectors: notes and documents](#notes) (7)
- [Connectors: media and play](#media) (22)
- [Connectors: home, automation, notifications and messages](#home) (17)
- [Health: apps, platforms and devices](#health) (15)
- [Identity and passwords](#identity) (3)
- [Considered, not chosen, or mentioned only](#considered) (4)
- [Also in the code today](#in-code) (5)

<a id="stack"></a>

## App stack: languages, frameworks and libraries

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **.NET (10, LTS)** | Microsoft's open-source runtime and SDK | The whole app and server run on it; Core, Data, UI, platform launchers and Server are .NET projects. | [learn.microsoft.com](https://learn.microsoft.com/dotnet/) | MIT |
| **C#** | Programming language for .NET | The one language for app, server and tests. | [learn.microsoft.com](https://learn.microsoft.com/dotnet/csharp/) | Open spec (ECMA-334); compiler MIT |
| **Avalonia UI** | Cross-platform XAML UI framework for .NET | UI shell for Windows, macOS, Linux, Android and iOS. Avalonia styles every control from the app's own themes, so a custom look is a design job, not a framework limit. | [docs.avaloniaui.net](https://docs.avaloniaui.net/) ✔ | MIT |
| **.NET for Android / iOS workloads** | .NET SDK workloads that build Android and iOS apps | Build the Android APK (GrapheneOS-safe) and the iOS app. | [learn.microsoft.com](https://learn.microsoft.com/dotnet/android/) | MIT |
| **ASP.NET Core** | .NET web framework | The sync server and relay (EtchaSketch.Server): stores only end-to-end encrypted changes and serves public rooms without open home ports. | [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/) | MIT |
| **CommunityToolkit.Mvvm** | MVVM helpers for .NET UI apps | View models in EtchaSketch.UI. | [learn.microsoft.com](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) | MIT |
| **SQLite** | Embedded SQL database | Local store on every device (local-first). | [sqlite.org](https://sqlite.org/docs.html) | Public domain |
| **SQLCipher** | Encrypted SQLite | Encrypts the local database; the key sits in the OS key store, never in the database. | [www.zetetic.net](https://www.zetetic.net/sqlcipher/documentation/) ✔ | BSD-3-Clause (Community Edition) |
| **xUnit** | .NET test framework | EtchaSketch.Tests, including the privacy-gateway and leak tests every privacy change needs. | [xunit.net](https://xunit.net/) | Apache-2.0 |
| **CameraX** | Android Jetpack camera library | In-app scanner screen on Android. Use it with an open decoder only: CameraX's ML Kit analyzer pulls in Google ML Kit, which the no-Google rule forbids. | [developer.android.com](https://developer.android.com/media/camera/camerax) ✔ | Apache-2.0 |
| **ZXing / ZXing.Net** | Open-source barcode and QR decoding library (Java / .NET port) | Decoder for store barcodes and EtchaSketch QR labels in the in-app scanner. | [github.com](https://github.com/micjahn/ZXing.Net) ✔ | Apache-2.0 |

<a id="keys"></a>

## Key storage on each platform

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Android Keystore** | Android's hardware-backed key store | Holds the database key and connector tokens on Android. | [developer.android.com](https://developer.android.com/privacy-and-security/keystore) | Part of Android (AOSP, Apache-2.0) |
| **Windows Credential Manager / DPAPI** | Windows secret storage APIs | Holds the database key on Windows (the code uses DPAPI through ProtectedData). | [learn.microsoft.com](https://learn.microsoft.com/windows/win32/secauthn/credentials-management) | Part of Windows (proprietary) |
| **Apple Keychain Services** | macOS and iOS secret storage | Holds the database key on macOS and iOS. | [developer.apple.com](https://developer.apple.com/documentation/security/keychain-services) | Part of macOS/iOS (proprietary) |
| **Secret Service API** | freedesktop.org standard for secret storage on Linux | Holds the database key on Linux desktops (GNOME Keyring, KWallet). | [specifications.freedesktop.org](https://specifications.freedesktop.org/secret-service-spec/latest/) | Open specification |

<a id="platform"></a>

## Platform, builds and distribution

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **GrapheneOS** | Privacy-hardened Android OS | Target phone OS. The reason the Android build never uses Google services; every Android gate is tested with sandboxed Google Play off. | [grapheneos.org](https://grapheneos.org/usage) | MIT and Apache-2.0 (mostly) |
| **Google Play Services, Firebase, ML Kit** | Google's proprietary Android services | **Excluded.** No package that depends on them may enter the Android build. | [developers.google.com](https://developers.google.com/android/guides/overview) | Proprietary |
| **GitHub Actions** | CI/CD service on GitHub | Builds and tests every push; signs the APK with the permanent key and publishes the rolling release. | [docs.github.com](https://docs.github.com/actions) | Proprietary service |
| **GitHub Releases** | Release hosting on GitHub | Rolling `latest` release with the APK and desktop builds, which phones and the desktop updater follow. | [docs.github.com](https://docs.github.com/repositories/releasing-projects-on-github/about-releases) | Proprietary service |
| **Obtainium** | Android app that installs and updates apps straight from their release pages | How phones get updates without Google. | [imranr98.github.io](https://imranr98.github.io/Obtainium-Wiki/) ✔ | GPL-3.0 |
| **F-Droid** | Free-software Android app store | Release channel at 1.0 and where Obtainium is installed from. | [f-droid.org](https://f-droid.org/docs/) | GPL-3.0 / AGPL-3.0 |
| **Accrescent** | Security-focused Android app store | Planned release channel at 1.0. | [accrescent.app](https://accrescent.app/docs) ✔ | Open source (check the repo) |
| **TestFlight** | Apple's beta distribution service | Optional iOS beta channel. | [developer.apple.com](https://developer.apple.com/testflight/) | Proprietary service |
| **Binary Eye** | Open-source barcode scanner app for Android | Today's scanner: the app sends a ZXing scan intent to it. Becomes a fallback once in-app scanning lands. | [github.com](https://github.com/markusfisch/BinaryEye) ✔ | MIT |
| **JetBrains Rider** | Commercial .NET IDE | Suggested editor. | [www.jetbrains.com](https://www.jetbrains.com/help/rider/) | Proprietary |
| **VS Code + C# Dev Kit** | Microsoft's editor and its C# extension | Alternative editor. | [code.visualstudio.com](https://code.visualstudio.com/docs/csharp/get-started) | Editor: MIT source, Microsoft binaries; C# Dev Kit: proprietary |
| **VSCodium** | VS Code built without Microsoft telemetry or branding | Editor option; outside the app. | [vscodium.com](https://vscodium.com/) | MIT |

<a id="standards"></a>

## Standards and protocols

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **UnifiedPush** | Open push-notification standard for Android without Google | Phone push. Pushes carry only "wake up"; the app fetches the text itself. | [unifiedpush.org](https://unifiedpush.org/) | Open specification; libraries Apache-2.0 |
| **CalDAV (RFC 4791)** | Calendar sync protocol over WebDAV | Calendar connector, read and write. Standard, so it works with any CalDAV server. | [datatracker.ietf.org](https://datatracker.ietf.org/doc/html/rfc4791) | IETF standard |
| **CardDAV (RFC 6352)** | Contacts sync protocol over WebDAV | Contacts module sync. | [datatracker.ietf.org](https://datatracker.ietf.org/doc/html/rfc6352) | IETF standard |
| **iCalendar / ICS (RFC 5545)** | Calendar data format | ICS subscriptions (school, sports, holidays) and ICS export of dated items. | [datatracker.ietf.org](https://datatracker.ietf.org/doc/html/rfc5545) | IETF standard |
| **OpenID Connect** | Identity layer on OAuth 2.0 | "Sign in with your own OIDC" for the sync server, giving per-person accounts. | [openid.net](https://openid.net/specs/openid-connect-core-1_0.html) | Open standard (OpenID Foundation) |
| **MQTT** | Lightweight publish/subscribe messaging protocol | MQTT connector for sensors and devices outside Home Assistant. | [mqtt.org](https://mqtt.org/mqtt-specification/) | OASIS standard |
| **OPDS** | Open catalog format for ebook libraries | How the Reading room lists books from Calibre-Web or any OPDS server. | [specs.opds.io](https://specs.opds.io/) ✔ | Open specification |
| **Subsonic API / OpenSubsonic** | Music-server API and its open, extended successor | How the built-in player talks to Navidrome and other Subsonic servers. | [opensubsonic.netlify.app](https://opensubsonic.netlify.app/docs) ✔ | Subsonic: proprietary spec; OpenSubsonic: open |
| **IMAP (RFC 9051)** | Mail access protocol | Email connector: mail into the notification center, drafts only, sending always confirmed. | [datatracker.ietf.org](https://datatracker.ietf.org/doc/html/rfc9051) | IETF standard |
| **Matrix** | Open, federated messaging protocol | Messages connector. | [spec.matrix.org](https://spec.matrix.org) ✔ | Apache-2.0 (specification) |
| **Bluetooth Heart Rate Service** | Bluetooth LE profile for heart-rate sensors | Direct Polar H10 connection for beat-to-beat intervals (HRV). | [www.bluetooth.com](https://www.bluetooth.com/specifications/specs/heart-rate-service-1-0/) | Bluetooth SIG specification |
| **Markdown (CommonMark)** | Plain-text formatting standard | Notes export and the Markdown/Logseq folder connector. | [commonmark.org](https://commonmark.org/) | CC BY-SA 4.0 (spec) |
| **OBS Studio + obs-websocket** | Streaming software and its remote-control protocol | Stream mode turns on automatically when OBS reports it is live; the studio window is what OBS captures. | [github.com](https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md) | GPL-2.0 |

<a id="ai"></a>

## AI providers and voice

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **OpenAI-compatible API** | De-facto standard HTTP API for chat, speech and image models | The assistant's main provider interface: any compatible server can be connected. | [platform.openai.com](https://platform.openai.com/docs/api-reference) | Proprietary API (widely re-implemented) |
| **Anthropic API** | API for Anthropic's Claude models | A provider type in "AI connections". Untrusted for Private data unless marked trusted. | [platform.claude.com](https://platform.claude.com/docs) ✔ | Proprietary service |
| **Ollama** | Local model runner with an OpenAI-compatible endpoint | Default local AI provider. | [docs.ollama.com](https://docs.ollama.com/) ✔ | MIT |
| **vLLM** | High-throughput model server | Listed provider option. | [docs.vllm.ai](https://docs.vllm.ai/) ✔ | Apache-2.0 |
| **LM Studio** | Desktop app for running local models with a local API server | Listed provider option. | [lmstudio.ai](https://lmstudio.ai/docs) ✔ | Proprietary (free to use) |
| **LocalAI** | Self-hosted OpenAI-compatible server | Listed provider option. | [localai.io](https://localai.io/docs/) ✔ | MIT |
| **KoboldCpp** | Single-file local model runner with an OpenAI-compatible API | Listed provider option; an alternative to Ollama. | [github.com](https://github.com/LostRuins/koboldcpp/wiki) ✔ | AGPL-3.0 |
| **Open WebUI** | Chat front end for local models | Stays a separate chat app; its planner tool moves to the headless household assistant's API. Also a provider option. ⚠ Since v0.6.6 (April 2025) its license adds a branding clause and is not OSI-approved. | [docs.openwebui.com](https://docs.openwebui.com/) ✔ | Open WebUI License (BSD-3-Clause plus branding clause) |
| **whisper.cpp** | Fast local speech-to-text (OpenAI Whisper models) | Speech to text for voice notes and commands. | [github.com](https://github.com/ggml-org/whisper.cpp) ✔ | MIT |
| **Kokoro TTS (+ Kokoro-FastAPI)** | Small open text-to-speech model, and an OpenAI-style server for it | Spoken reminders and alerts. | [github.com](https://github.com/remsky/Kokoro-FastAPI) ✔ | Apache-2.0 (model and server) |
| **Letta (formerly MemGPT)** | Framework for agents with long-term memory | Optional connector; the assistant's layered memory is built in. | [docs.letta.com](https://docs.letta.com/) ✔ | Apache-2.0 |
| **Stable Diffusion WebUI (AUTOMATIC1111)** | Web UI and HTTP API for Stable Diffusion image models | Optional image endpoint for gamification art from Public-safe prompts. ⚠ Development has slowed; check activity before depending on it. | [github.com](https://github.com/AUTOMATIC1111/stable-diffusion-webui/wiki) ✔ | AGPL-3.0 |

<a id="work"></a>

## Connectors and imports: work, tasks and money

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Vikunja** | Self-hosted to-do app | Import source: a one-time importer exists in tools/. | [vikunja.io](https://vikunja.io/docs/) ✔ | AGPL-3.0 |
| **Plane** | Open-source project management (boards, cycles, Gantt) | Built-in project views cover its job; import, and later sync, for people who run it. | [developers.plane.so](https://developers.plane.so/) ✔ | AGPL-3.0 |
| **Odoo** | Open-source ERP (CRM, invoicing, accounting, HR) | Connector when needed: becomes the book of record for clients, invoices and timesheets through its external API. The built-in Business module is the fallback. | [www.odoo.com](https://www.odoo.com/documentation/18.0/developer/reference/external_api.html) ✔ | LGPL-3.0 (Community); Enterprise proprietary |
| **Kimai** | Self-hosted time tracker | Time log is built in; push connector and import for Kimai users. | [www.kimai.org](https://www.kimai.org/documentation/) ✔ | AGPL-3.0 |
| **Taskwarrior** | Command-line task manager | Built-in filters, tags and dependencies, an `etcha task` CLI, and an import. | [taskwarrior.org](https://taskwarrior.org/docs/) ✔ | MIT |
| **Super Productivity** | Focus timer and to-do app | Its job (Pomodoro, breaks) is built in. | [github.com](https://github.com/johannesjo/super-productivity) ✔ | MIT |
| **Settle Up** | Shared expense splitting app | Its job is built in. | [settleup.io](https://settleup.io/) ✔ | Proprietary |
| **Actual Budget** | Local-first budgeting app | Money connector; Actual stays the book of record. | [actualbudget.org](https://actualbudget.org/docs/) ✔ | MIT |
| **SimpleFIN** | Read-only bank data protocol (and a paid bridge) | Bank data reaches the app only through Actual's SimpleFIN import, so the app never holds bank credentials. | [simplefin.org](https://simplefin.org/protocol.html) ✔ | Open protocol; Bridge is a paid service |
| **Firefly III** | Self-hosted personal finance manager | Read-only tiles for reports and bills. | [docs.firefly-iii.org](https://docs.firefly-iii.org/) ✔ | AGPL-3.0 |
| **Wealthfolio** | Local-first investment tracker (desktop, with a self-hosted web mode) | Read-only portfolio tile; whether it reads an API or an export is still to be checked. | [wealthfolio.app](https://wealthfolio.app/docs/introduction/) ✔ | AGPL-3.0 (check the repo) |
| **Portfolio Performance** | Desktop portfolio analysis program | Read-only tile from its saved portfolio file. | [help.portfolio-performance.info](https://help.portfolio-performance.info/en/) ✔ | EPL-1.0 |

<a id="household"></a>

## Connectors and imports: household

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Grocy** | Self-hosted pantry, chores and household manager | All of its jobs are built in; import for chores, stock, recipes and batteries, and an optional connector. | [github.com](https://github.com/grocy/grocy/wiki) ✔ | MIT |
| **Homebox** | Self-hosted home inventory | Built-in inventory replaces it; one-time import. ⚠ The original project was archived; the maintained continuation is sysadminsmedia/homebox. | [homebox.software](https://homebox.software/en/) ✔ | AGPL-3.0 |
| **Shelf.nu** | Asset tracking with QR codes and check-out | Its "who has it" check-out is built into the inventory. | [github.com](https://github.com/Shelf-nu/shelf.nu/wiki) ✔ | AGPL-3.0 |
| **LubeLogger** | Self-hosted vehicle maintenance log | Vehicles module replaces it; one-time import. | [docs.lubelogger.com](https://docs.lubelogger.com/) ✔ | MIT |
| **Snipe-IT** | IT asset management | Stays outside the app (professional use); later read connector possible. | [snipe-it.readme.io](https://snipe-it.readme.io/docs) | AGPL-3.0 |
| **Part-DB** | Electronics parts inventory | Stays outside the app; later read connector possible. | [docs.part-db.de](https://docs.part-db.de/) ✔ | AGPL-3.0 |
| **Open Food Facts** | Open food product database with a free API | Barcode lookup for the pantry; manual entry always works offline. | [openfoodfacts.github.io](https://openfoodfacts.github.io/openfoodfacts-server/api/) ✔ | Data ODbL; code AGPL-3.0 |

<a id="notes"></a>

## Connectors: notes and documents

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Logseq** | Local-first outliner with linked pages | Linked notes are built in; a Markdown/Logseq folder connector reads an existing graph. | [docs.logseq.com](https://docs.logseq.com/) ✔ | AGPL-3.0 |
| **Obsidian** | Markdown note app | Any Markdown folder works with the Markdown/Logseq folder connector; Obsidian is not targeted separately. | [help.obsidian.md](https://help.obsidian.md/) | Proprietary |
| **AppFlowy** | Open-source Notion alternative | Its job (tables inside notes) is built in. | [docs.appflowy.io](https://docs.appflowy.io/docs) ✔ | AGPL-3.0 |
| **Anytype** | Local-first object-based notes | Its job (whiteboard room) is built in. | [doc.anytype.io](https://doc.anytype.io/) ✔ | Any Source Available License 1.0 (not OSI) |
| **BookStack** | Self-hosted documentation wiki | Read connector: search and read from the app. | [www.bookstackapp.com](https://www.bookstackapp.com/docs/) | MIT |
| **Outline** | Team knowledge base | Read connector. | [docs.getoutline.com](https://docs.getoutline.com/s/hosting) ✔ | BSL 1.1 (source-available) |
| **Paperless-ngx** | Document scanning, OCR and search | Connector: search, open and attach documents; receipts feed budget and pantry suggestions. | [docs.paperless-ngx.com](https://docs.paperless-ngx.com/) | GPL-3.0 |

<a id="media"></a>

## Connectors: media and play

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Jellyfin** | Self-hosted media server | Connector for the Videos and Library rooms. | [jellyfin.org](https://jellyfin.org/docs/) ✔ | GPL-2.0 |
| **Navidrome** | Self-hosted music server (Subsonic API) | Music connector; the built-in player does Feishin's job. | [www.navidrome.org](https://www.navidrome.org/docs/) ✔ | GPL-3.0 |
| **Feishin** | Desktop music client for Navidrome/Jellyfin | Its job is done by the built-in player. | [github.com](https://github.com/jeffvli/feishin) ✔ | GPL-3.0 |
| **Finamp** | Jellyfin music client for phones | Built-in player covers it; can stay installed. | [github.com](https://github.com/jmshrv/finamp) ✔ | MPL-2.0 |
| **Symfonium** | Paid Android music client | Built-in player covers it; can stay installed. | [docs.symfonium.app](https://docs.symfonium.app/) ✔ | Proprietary |
| **Music Assistant (+ Tidal)** | Home Assistant music add-on that plays streaming services such as Tidal | Tidal is played through Music Assistant and controlled via the HA connector. | [www.music-assistant.io](https://www.music-assistant.io/music-providers/tidal/) ✔ | Apache-2.0 (Tidal itself proprietary) |
| **Audiobookshelf** | Self-hosted audiobook and podcast server | Connector: progress, now listening, library room. | [www.audiobookshelf.org](https://www.audiobookshelf.org/docs) ✔ | GPL-3.0 |
| **Calibre** | Ebook manager | Library behind Calibre-Web. | [manual.calibre-ebook.com](https://manual.calibre-ebook.com/) | GPL-3.0 |
| **Calibre-Web** | Web front end for a Calibre library, with OPDS | Reading room via OPDS. | [github.com](https://github.com/janeczku/calibre-web/wiki) ✔ | GPL-3.0 |
| **RomM** | Self-hosted retro game library | Play room connector. | [docs.romm.app](https://docs.romm.app/) ✔ | AGPL-3.0 |
| **Steam Web API** | Valve's API for game libraries | Optional Steam library in the Play room. | [partner.steamgames.com](https://partner.steamgames.com/doc/webapi_overview) | Proprietary service |
| **Playnite** | Windows game launcher and library | ⚠ Replaced by Sunshine + Moonlight; not a connector target. | [api.playnite.link](https://api.playnite.link/docs/) ✔ | MIT |
| **Sunshine** | Self-hosted game-stream host | Outside the app; the Play room offers a "play on phone" link. | [docs.lizardbyte.dev](https://docs.lizardbyte.dev/projects/sunshine/) ✔ | GPL-3.0 |
| **Moonlight** | Game-stream client for phones and PCs | Target of the "play on phone" link. | [moonlight-stream.org](https://moonlight-stream.org/) ✔ | GPL-3.0 |
| **Seerr (Jellyseerr)** | Media request manager | "Request this" button in the Videos room. ⚠ Jellyseerr and Overseerr are merging into Seerr (still in progress in 2026); target Seerr's API. | [docs.seerr.dev](https://docs.seerr.dev/) ✔ | MIT |
| **Prowlarr, Sonarr, Radarr, Lidarr, Readarr** | Media automation (*arr) suite | Outside the app; licensed and owned media only. ⚠ Readarr was retired upstream in 2025; Calibre-Web covers books. | [wiki.servarr.com](https://wiki.servarr.com/) ✔ | GPL-3.0 |
| **Bazarr** | Subtitle manager for the *arr suite | Outside the app. | [wiki.bazarr.media](https://wiki.bazarr.media/) | GPL-3.0 |
| **qBittorrent** | BitTorrent client with a web API | Outside the app. | [github.com](https://github.com/qbittorrent/qBittorrent/wiki) ✔ | GPL-2.0-or-later |
| **FreeTube / LibreTube** | Private YouTube clients (desktop / Android) | Device apps; nothing to build. | [docs.freetubeapp.io](https://docs.freetubeapp.io/) ✔ | FreeTube AGPL-3.0; LibreTube GPL-3.0 |
| **Stremio + Real-Debrid** | Streaming media app and a paid download service | Device app plus paid service; the Videos room can link out. | [www.stremio.com](https://www.stremio.com/) | Stremio: mixed open/proprietary; Real-Debrid: proprietary |
| **Twitch API / YouTube Data API** | Streaming platforms' APIs | Publish flow and stream status. | [dev.twitch.tv](https://dev.twitch.tv/docs/api/) | Proprietary services (YouTube: https://developers.google.com/youtube/v3) |
| **Postiz** | Self-hosted social post scheduler | Schedule posts from the publish flow. | [docs.postiz.com](https://docs.postiz.com/) ✔ | AGPL-3.0 |

<a id="home"></a>

## Connectors: home, automation, notifications and messages

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Home Assistant** | Open-source smart-home hub (REST and WebSocket APIs) | Home tiles, scenes, triggers (presence, sensors, sleep) and to-do bridge; reads tasks through the headless assistant. | [developers.home-assistant.io](https://developers.home-assistant.io/docs/api/rest/) ✔ | Apache-2.0 |
| **Node-RED** | Visual flow-based automation | Webhooks in and out so flows can start app automations and be started by them. | [nodered.org](https://nodered.org/docs/) | Apache-2.0 |
| **n8n** | Workflow automation platform | Same webhook role as Node-RED. ⚠ Fair-code license, not OSI open source. | [docs.n8n.io](https://docs.n8n.io/) ✔ | Sustainable Use License |
| **Eclipse Mosquitto** | MQTT broker | What the MQTT connector usually talks to. | [mosquitto.org](https://mosquitto.org/documentation/) ✔ | EPL-2.0 / EDL-1.0 |
| **Frigate** | NVR with object detection for cameras | Reached only through Home Assistant. | [docs.frigate.video](https://docs.frigate.video/) | MIT |
| **Pi-hole** | Network-wide DNS ad blocker | Small connector so focus mode can block distracting sites. | [docs.pi-hole.net](https://docs.pi-hole.net/) | EUPL-1.2 |
| **ntfy** | Self-hostable push-notification server (UnifiedPush distributor) | Phone push without Google. Payload is "wake up" only. | [docs.ntfy.sh](https://docs.ntfy.sh/) | Apache-2.0 / GPL-2.0 (dual) |
| **Gotify** | Self-hosted push-notification server | Alternative to ntfy in the notification connector. | [gotify.net](https://gotify.net/docs/) | MIT |
| **Miniflux** | Minimalist RSS reader with an API | Feeds in a reading room or the notification center. | [miniflux.app](https://miniflux.app/docs/api) ✔ | Apache-2.0 |
| **TextBee** | Android-phone SMS gateway with an API | SMS to people without the app; always needs a confirmed recipient list. | [textbee.dev](https://textbee.dev/docs) ✔ | MIT (check the repo) |
| **Stalwart** | Mail and collaboration server (CalDAV/CardDAV/WebDAV) | A CalDAV/CardDAV server the calendar and contacts connectors work against. Any standard server works. | [stalw.art](https://stalw.art/docs/collaboration/) ✔ | AGPL-3.0 (Enterprise edition proprietary) |
| **Radicale** | Lightweight CalDAV/CardDAV server | Alternative server for the same connector. | [radicale.org](https://radicale.org/v3.html) | GPL-3.0 |
| **Proton Mail Bridge** | Local IMAP/SMTP bridge for Proton Mail | Lets the IMAP connector read Proton Mail. | [proton.me](https://proton.me/support/mail/bridge/introduction-bridge) ✔ | GPL-3.0 |
| **Open-Meteo** | Free weather API, no account needed | Weather for the morning briefing and outdoor plans. | [open-meteo.com](https://open-meteo.com/en/docs) ✔ | Code AGPL-3.0; data CC BY 4.0 |
| **Uptime Kuma** | Self-hosted uptime monitor | Feeds the home-status tile. | [github.com](https://github.com/louislam/uptime-kuma/wiki) ✔ | MIT |
| **Prometheus Alertmanager** | Alert routing for Prometheus | Feeds the home-status tile like Uptime Kuma. | [prometheus.io](https://prometheus.io/docs/alerting/latest/alertmanager/) ✔ | Apache-2.0 |
| **Astroluma** | Self-hosted start page for services | Its job (service tiles) is built into the Home page. ⚠ Last release January 2025. | [getastroluma.com](https://getastroluma.com/) ✔ | GPL-3.0 |

<a id="health"></a>

## Health: apps, platforms and devices

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Gadgetbridge** | Open-source Android companion for watches and sensors, no cloud | Phone-side hub for heart rate, HRV and sleep. | [gadgetbridge.org](https://gadgetbridge.org/) ✔ | AGPL-3.0 |
| **Bangle.js 2** | Open, hackable smartwatch (Espruino) | First real device for the health module, through Gadgetbridge. | [www.espruino.com](https://www.espruino.com/Bangle.js2) ✔ | Espruino firmware MPL-2.0 |
| **Polar H10 + Polar BLE SDK** | Chest-strap heart-rate sensor and its SDK | Best HRV input: direct Bluetooth or Gadgetbridge. | [github.com](https://github.com/polarofficial/polar-ble-sdk) ✔ | Polar SDK license (custom) |
| **Android Health Connect** | Android's on-device health data store | Opt-in cloud/health platform connector. Check that it works on GrapheneOS without Play. | [developer.android.com](https://developer.android.com/health-and-fitness/health-connect/get-started) ✔ | Proprietary (Google) |
| **Apple HealthKit** | iOS health data store | Opt-in connector. | [developer.apple.com](https://developer.apple.com/documentation/healthkit) | Proprietary (Apple) |
| **Withings (Body+)** | Smart scale and its cloud API | Weight and body composition, opt-in with a cloud warning. | [developer.withings.com](https://developer.withings.com/) ✔ | Proprietary service |
| **Hexoskin** | Smart shirt (breathing, heart rate) and its cloud API | Opt-in cloud connector. | [api.hexoskin.com](https://api.hexoskin.com/docs/) ✔ | Proprietary service |
| **EMAY Sleep O2 ring** | Overnight blood-oxygen ring | File import if its export allows. ⚠ No official developer docs found. | none found | Proprietary device |
| **TempTraq** | Wearable Bluetooth thermometer patch | Illness early-warning input if it can export. ⚠ No official developer docs found. | none found | Proprietary device |
| **WatchPAT ONE** | Disposable home sleep-apnea test | Report goes into Paperless; not a daily feed. | [www.itamar-medical.com](https://www.itamar-medical.com/) ✔ | Proprietary medical device |
| **Welltory** | Commercial HRV stress/energy app | Inspiration only: EtchaSketch builds its own HRV readings from published measures. | [welltory.com](https://welltory.com/) ✔ | Proprietary |
| **Fitbod** | Commercial adaptive workout planner | Inspiration for the built-in workout planner. | [fitbod.me](https://fitbod.me/) | Proprietary |
| **wger** | Self-hosted workout and nutrition manager | Workout log and exercise library are built in; wger import/connector. | [wger.readthedocs.io](https://wger.readthedocs.io/) ✔ | AGPL-3.0 |
| **SparkyFitness** | Self-hosted family fitness and nutrition tracker | Its job (shared household goals) is built in. | [codewithcj.github.io](https://codewithcj.github.io/SparkyFitness) ✔ | GPL-3.0 |
| **InfluxDB** | Time-series database | Optional export target for long-term health charts in Grafana. | [docs.influxdata.com](https://docs.influxdata.com/) ✔ | MIT / Apache-2.0 (open-source editions) |

<a id="identity"></a>

## Identity and passwords

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Authelia** | Self-hosted single sign-on and OpenID Connect provider | An OIDC provider for the sync server's per-person sign-in. | [www.authelia.com](https://www.authelia.com/configuration/identity-providers/openid-connect/provider/) ✔ | Apache-2.0 |
| **Vaultwarden** | Lightweight Bitwarden-compatible server | Link-out only; passwords never enter the app (Secret tier). | [github.com](https://github.com/dani-garcia/vaultwarden/wiki) | AGPL-3.0 |
| **Bitwarden** | Password manager | Same link-out. | [bitwarden.com](https://bitwarden.com/help/) | Clients GPL-3.0; server AGPL-3.0 / Bitwarden License |

<a id="considered"></a>

## Considered, not chosen, or mentioned only

| Name | What it is | Role in EtchaSketch | Official docs | License |
|---|---|---|---|---|
| **Tauri 2** | Rust-based desktop/mobile app framework with a web UI | ⚠ Considered as the app framework; C# + Avalonia chosen instead. | [v2.tauri.app](https://v2.tauri.app/) | MIT / Apache-2.0 |
| **Next.js** | React web framework | ⚠ Considered as a web-stack alternative to Avalonia; rejected because it would mean rewriting the encrypted store, key storage, reminders and sync. | [nextjs.org](https://nextjs.org/docs) | MIT |
| **Capacitor** | Native shell for web apps on mobile | ⚠ Considered as the mobile shell for a web stack; not chosen. | [capacitorjs.com](https://capacitorjs.com/docs) | MIT |
| **Unity / Godot** | Game engines | Mentioned only: C# skills carry over to them. | [docs.godotengine.org](https://docs.godotengine.org/) | Unity proprietary; Godot MIT (Unity docs: https://docs.unity.com/) |

<a id="in-code"></a>

## Also in the code today

Not named in the plans, but already referenced by the repository (Directory.Packages.props, deploy/).

| Name | What it is | Official docs | License |
|---|---|---|---|
| **Microsoft.Data.Sqlite** | ADO.NET provider for SQLite | [learn.microsoft.com](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | MIT |
| **SQLitePCLRaw (bundle_e_sqlcipher)** | Native SQLite/SQLCipher bindings used by Microsoft.Data.Sqlite | [github.com](https://github.com/ericsink/SQLitePCL.raw) | Apache-2.0 |
| **QRCoder** | QR code generator for printable labels | [github.com](https://github.com/codebude/QRCoder) | MIT |
| **System.Security.Cryptography.ProtectedData** | Windows DPAPI wrapper for the database key | [learn.microsoft.com](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata) | MIT |
| **Docker Compose** | How the sync server and headless assistant are deployed (deploy/) | [docs.docker.com](https://docs.docker.com/compose/) | Apache-2.0 |

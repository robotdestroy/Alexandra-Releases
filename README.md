# Alexandra

A voice-first companion for Elite Dangerous on Windows. Stay in the game: hold a keyboard or HOTAS button, ask naturally, and hear a short answer. No command prefix is required.

## Download

- [Latest Windows installer](https://github.com/robotdestroy/Alexandra-Releases/releases/latest)
- [Quick-start guide](docs/READ-ME-FIRST.md)
- Supply your own OpenAI API key during setup. No keys, sponsored access, or account approvals are included.
- Automatic updates are enabled by default for installed releases. Downloaded updates apply on restart.
- Releases are an unsigned beta. Windows may display an Unknown publisher warning. Do not disable Windows security settings.

## What it does

- Natural voice conversations and follow-up questions; press push-to-talk again to interrupt.
- Local game context: ship, modules, cargo, route, missions, engineering materials and recorded construction needs.
- On-demand carrier, stored-ship, module and station records.
- Short combat answers using local cockpit flags, pips, target scans and dated hull reports; session journals include combat after-action summaries. [Capabilities and limits](docs/COMBAT.md).
- Optional brief combat callouts for shield loss, low hull, overheating and heat damage. On by default; disable in Setup. Selected-voice clips use API credit to generate and are reused in memory.
- Public research using web search and an isolated browser, including Inara and community sources.
- Persistent local commander memory for goals, notes, preferences and progress, with journal catch-up after time away.
- One-time voice reminders: “When I dock, remind me to claim my rewards.” Also supports arriving in a named system and touching down. Saved per commander; waits for a quiet moment outside combat. [Reminder details](docs/REMINDERS.md).
- Local Markdown session journals: readable recaps, stated gameplay plans, confirmed community-goal rewards/progress, missions, combat and next-session reminders. Right-click → View most recent session log or View session logs to read or save a copy, even without a Markdown app.
- Search journals by ship used or keyword, with a Dextra-style session header. Browse saved photo thumbnails, enlarge and zoom them, or copy the full-resolution image to the clipboard.
- Spoken landing-pad directions: ask “Where is pad 24?” or “Where is my pad?” Uses local docking records and mapped Coriolis/Orbis/Ocellus layouts; unknown layouts are not guessed.
- Inspect the visible game for visual questions; “take a photo” or “take a screenshot” saves a game image locally in Pictures / Alexandra.
- Caption requested photos: “Take a photo—first landing on this planet.” Active session journals link the local image with your caption, location and time; no automatic photos or image uploads for save-only requests.
- Copy a validated system name. An explicitly requested paste sends only Ctrl+V with Elite's Galaxy Map ready; the player submits the search and plots the route.
- Minimal waveform overlay, configurable voice input, window positioning and optional automatic opening with Elite.

## Privacy and limits

- The app reads the current user's Elite journal folder, not data from this repository's publisher.
- Keys are stored separately from settings, encrypted for the Windows user. The key's account pays for API usage.
- Voice, relevant game context, selected memories and requested game images can be sent to OpenAI after the user enables cloud operation in setup.
- Conversation debug logging is optional and off by default. Logs stay local and may contain conversation details.
- Gameplay session journals are on by default, separate from debug logs. They contain personal game history, explicitly saved goals and short excerpts of stated gameplay plans, not full conversations. They stay in `%LOCALAPPDATA%/AlexandraData/Session Logs`; switch off in Setup. No extra API use. See [session journals](docs/SESSION-JOURNALS.md).
- Research uses an isolated browser, not the user's logged-in browsing profile. Private Inara account access is not implemented.
- No flight automation. Missing or stale information is not proof of current game state.
- See [source publication notes](docs/PUBLIC-SOURCE.md) for audit scope, bundled assets and licensing status.

## Build and test

Requirements: Windows, the .NET 8 SDK, and Microsoft Edge for browser research. A key is needed for live voice/research, not for normal tests.

```powershell
dotnet restore .\Alexandra.sln
dotnet build .\Alexandra.sln -c Release
dotnet test .\Alexandra.sln -c Release --no-build
dotnet run --project .\tools\Alexandra.AppearanceProbe -c Release
```

The public source uses the Windows-provided Segoe UI font. The separately supplied font used in existing preview binaries is not included because redistribution permission has not been established. This is a deliberate visual difference; voice and game-data behavior is unchanged.

Run `src/Alexandra.App/bin/Release/net8.0-windows/Alexandra.App.exe` after building. Source builds do not configure an update feed by default. Test fixtures contain synthetic commanders and records. Tools requiring live services explicitly opt in and can incur API charges; do not enable them unless intended.

## Project structure

- `src/Alexandra.Core`: interfaces and platform-independent policies.
- `src/Alexandra.Infrastructure`: game records, research, speech, browser and input integrations.
- `src/Alexandra.App`: Windows WPF application.
- `tests/Alexandra.Tests`: offline regression tests and sanitized fixtures.
- `tools`: isolated appearance, browser, voice and update probes.
- `scripts`: installer packaging, release verification and guide generation.

## Release maintenance

Use a new version for every binary release. First update the project version, guides and release notes, run tests and the source audit, then commit and push the reviewed source to public `main`. Packaging and publishing require that exact clean, published revision. The release scripts target this repository and require the publisher's authenticated GitHub CLI; credentials are never packaged. Do not overwrite distributed artifacts.

```powershell
.\scripts\verify-public-source.ps1
.\scripts\test-release-source.ps1
# Commit and push the reviewed changes before the next two commands.
.\scripts\build-friends-release.ps1 -Version <new-version> -AllowUnsignedTesterRelease
.\scripts\publish-friends-release.ps1 -Version <new-version> -Publish
```

The historical release tags retain their original release-channel-only content, with anonymized commit metadata. `main` contains the audited source snapshot based on 0.1.24. Old private development history was not published.

New release packages are built from an isolated Git archive, so local or ignored files cannot enter the build. Each includes `LICENSE.txt` and `source.json` identifying the public source revision. The publisher checks that revision, creates a matching release tag without moving existing tags, and adds pinned browse/source-ZIP/license links to the release notes. GitHub's automatic source downloads then contain the matching source. These checks record the build process; they are not cryptographic build attestations or a claim of byte-for-byte reproducible binaries.

## License

Alexandra's source and documentation are available under the [MIT License](LICENSE), attributed to `robotdestroy`. Third-party packages remain under their respective licenses. This does not grant rights to the separately supplied preview font, which is excluded from this repository.

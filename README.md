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
- Public research using web search and an isolated browser, including Inara and community sources.
- Persistent local commander memory for goals, notes, preferences and progress, with journal catch-up after time away.
- Inspect the visible game for visual questions; save a screenshot only when explicitly requested.
- Copy a validated system name. An explicitly requested paste sends only Ctrl+V with Elite's Galaxy Map ready; the player submits the search and plots the route.
- Minimal waveform overlay, configurable voice input, window positioning and optional automatic opening with Elite.

## Privacy and limits

- The app reads the current user's Elite journal folder, not data from this repository's publisher.
- Keys are stored separately from settings, encrypted for the Windows user. The key's account pays for API usage.
- Voice, relevant game context, selected memories and requested game images can be sent to OpenAI after the user enables cloud operation in setup.
- Conversation debug logging is optional and off by default. Logs stay local and may contain conversation details.
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

Use a new version for every binary release. The release scripts target this repository and require the publisher's authenticated GitHub CLI; credentials are never packaged. Do not overwrite distributed artifacts.

```powershell
.\scripts\verify-public-source.ps1
.\scripts\build-friends-release.ps1 -Version <new-version> -AllowUnsignedTesterRelease
.\scripts\publish-friends-release.ps1 -Version <new-version> -Publish
```

The historical release tags retain their original release-channel-only content, with anonymized commit metadata. `main` contains the audited source snapshot based on 0.1.24. Old private development history was not published.

No open-source license has been granted yet. Publishing source for inspection does not imply a license to reuse or redistribute it. Third-party packages remain under their respective licenses.

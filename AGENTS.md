# Alexandra project conventions

- This is the audited public source checkout. Never merge or push private development history into it. Use a GitHub no-reply author/committer email and run `scripts/verify-public-source.ps1` before publishing source.
- The public source uses Windows-provided Segoe UI. Do not add the separately supplied preview font without explicit redistribution permission. Existing installer releases are unchanged by this source publication.

- Dextra development is paused. Do not modify or publish Dextra as part of Alexandra work.
- Alexandra is voice-first: a minimal window and waveform, no response feed or command catalogue.
- Target .NET 8 / Windows WPF. Keep Alexandra.Core free of Windows APIs.
- External game data, speech, controller input and capture integrations stay behind interfaces.
- Never broaden into flight automation. Only copy a validated, unambiguous system name to the clipboard. The user's explicit exception is a requested paste of that verified copy: Ctrl+V only, with Elite foreground, Galaxy Map confirmed, unchanged clipboard, current-turn transcript permission and key-up cleanup. The player selects the search field. Never open menus, press Enter, plot a route or send arbitrary model text/keys.
- Never copy model prose, a station name, multiple alternatives, or an unverified location as a system destination.
- Missing or stale game data is unknown. Do not assume access to private Inara profiles or live stock from old community pages.
- Preserve conversational follow-ups and cancel stale work before speech or clipboard side effects.
- Capture only the game for user-requested screenshots or visual questions (including 'explain this warning'). No silent desktop capture, continuous watching, or persistent audio/analysis images. Save a PNG locally only when the user explicitly asks to take/save a screenshot.
- Store credentials separately and encrypted; never commit keys, user journals, audio or screenshots.
- Add sanitized fixtures and tests for journal/provider changes.

Verification:

```powershell
dotnet restore .\Alexandra.sln
dotnet build .\Alexandra.sln -c Release
dotnet test .\Alexandra.sln -c Release --no-build
```


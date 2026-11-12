# Gameplay session journals

- On by default. Setup → **Open session logs** opens `%LOCALAPPDATA%/Alexandra/Session Logs`.
- One Markdown file per observed Elite `LoadGame` session, closed on `Shutdown`. An app restart or journal-file rollover does not start a new log. Returning to the menu and loading again starts another game session.
- Updated about every three seconds while Alexandra is open. If Alexandra closes first, the next launch catches up from available journals, including completed sessions while away.
- Starts with new activity after the feature is first enabled; it does not create logs for your entire old journal archive. Starting mid-game produces a clearly marked partial session.
- Missing shutdowns use the last observed activity, not an invented finish time. Recorded span is not an active-play timer.
- Natural-language summary, systems visited, docking, purchases/sales, materials collected, engineering work, construction deliveries and ship/commander loss.
- **Combat after-action**: shield-down reports, main-ship/fighter hull observations, heat damage, incoming fire, interdictions, bounty/bond awards and losses. Latest 60 timestamped combat events plus totals; not a total kill counter, continuous health monitor or inferred cause of death. See [combat companion](COMBAT.md).
- Missions started/completed/failed/abandoned and recorded delivery progress come from game evidence. Personal goal changes come from explicitly saved memory edits. Say “My goal is to …”, “Update my goal …” or “I finished that goal.” Being in the right system alone never completes a personal goal.
- **Pick up next time** lists last-known active missions and goals. Mission information can be incomplete or expired; this is not proof of present eligibility.
- Local deterministic summaries: no additional API calls and no full conversation, audio or screenshot archive. Session logs work without cloud access and independently of the memory switch; remembering new personal goals still requires memory enabled.
- Files are readable personal gameplay history. Review before sharing. No commander name/FID is put in filenames; separate profiles have separate hashed session identifiers.
- Disable session journals in Setup to pause. Existing files remain. Forgetting a saved memory does not remove it from historical session files.
- Files are generated, so keep handwritten notes separately. Files remain until you delete them; the internal checkpoint keeps full summaries only for active or not-yet-exported sessions. Deleting a completed log normally leaves it deleted, but deleting an active log can recreate it on the next update.
- No game controls or background screenshots are involved.

# Spoken event reminders

- “When I dock, remind me to claim my community-goal rewards.”
- “When I dock at Galileo, remind me to sell my exploration data.”
- “When I enter Deciat, remind me to check my materials.”
- “When I touch down, remind me to take a photo.”
- “What reminders do I have?” / “Cancel that reminder.” Alexandra asks which if it is ambiguous.

## How they work

- Explicitly requested, one-time reminders. No reminders are created from research suggestions or inferred goals.
- Saved with local commander memory, separate for each commander. Survive app restarts and updates.
- Require commander memory and cloud voice enabled, and Alexandra running alongside a loaded Elite session.
- Trigger from actual Docked, FSDJump/CarrierJump or player-controlled Touchdown journal events after the reminder was created. Startup Location snapshots are not new arrivals.
- Optional exact station/body target; a system-arrival reminder always needs an exact system name. No fuzzy location matches.
- Wait until you and Alexandra are quiet and current combat data reports no combat. Missing telemetry defers speech. Combat callouts take priority.
- If you leave before it can speak, it waits for the matching event again. Starting a new game session also rearms unspoken reminders. It does not announce past events from a completed offline session.
- Speaks “Reminder: …” in your selected voice. One short verified audio clip uses API credit; only the reminder text goes to the voice request, without game history or screenshots. No fallback voice.
- New push-to-talk, Setup, game exit or a changed situation stops playback. Once playback is committed, interrupted reminders are not automatically replayed. Ask what reminders you have to review recent notifications.
- Read/cancel through voice; no new panel. Turning off commander memory pauses reminders without deleting them.
- Maximum 30 pending reminders and 100 retained entries per commander; the voice list includes pending plus the last five notified entries.

Not timers, mission-completion detectors, gameplay actions or automatic photography. A spoken reminder does not mark the underlying goal complete.

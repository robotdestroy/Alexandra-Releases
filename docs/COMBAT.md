# Combat companion

Alexandra can answer cockpit questions from local Elite data, without a web search or a screenshot.
Hold your usual push-to-talk button and ask naturally:

- “Check my pips.”
- “Are my shields up?”
- “What do you know about my target?”
- “Why can't I jump?”
- “How did that fight go?”
- “What happened before I died?”

## What she can use

- Status flags: shields up/down, hardpoints, flight assist, analysis mode, gear, scoop, silent running, heat warning, danger, interdiction and FSD mass-lock/charging/cooldown.
- Distributor pips, decoded from the game's half-pip values; active fire-group index.
- The latest journal target scan, its scan level and available ship/rank/legal status/health/subsystem fields.
- Dated hull reports, kept separate for the main ship and fighter.
- The last recorded installed fit and engineering, invalidated on equipment changes.
- Session combat events: incoming fire, shields down/restored, hull reports, heat damage, interdictions, escapes, awards and losses.

Immediate combat questions use a dedicated local lookup instead of the broader memory/research path.
Live answers are instructed to be one short fact or next step; a sitrep can use two short sentences (up to 45 words) to cover relevant available readings. Percentage follow-ups explicitly explain that own shield percentage is unavailable rather than repeating shield up/down. Normal conversation, research and requested screen inspection still work.

## Freshness and limits

- Elite's Status file updates when values change, not as a heartbeat. Unchanged flags and pips remain available while the same running game process/session is verified. The separate `statusFresh` flag only describes whether the file was written within 15 seconds; answers use `statusAvailable` instead.
- Target and hull percentages are last-reported observations, not continuous telemetry. Hull reports normally occur at damage thresholds.
- No reliable own shield percentage, live ammunition/capacitor charge, range, enemy positions or damage-per-second is available from these records.
- The local tailer checks about every 750 ms and catches up on demand. It covers up to the latest three journal files, ordered by their first event timestamps so legacy filenames cannot displace current sessions. Durable memory can provide additional session history for a relevant review.
- Status older than a session/vehicle/travel transition is rejected. A stopped, restarted or unverifiable game process makes cockpit state unavailable. New commanders, ship changes, target loss, death and repairs clear affected observations.
- A live-data reply is a snapshot at question time. Routine pip, target and health updates no longer interrupt it mid-sentence. First audio must arrive within 15 seconds of the lookup; once started, playback has a fixed 45-second upper bound. Losing polling for over five seconds, leaving the game, death, ship/vehicle changes or a travel/location transition still invalidate the reply. New push-to-talk always interrupts as before. The window shows “Combat changed · ask again” for invalidated replies; no automatic spoken interruption is added.
- Combat reviews group events by gameplay session, not inferred fight boundaries. Reward awards are not total kills or proof of redemption. An event sequence does not establish the cause of damage or death.
- No flight controls, background screenshots or continuous HUD recognition. Optional callouts follow the bounded rules below, not freeform model decisions.

## Optional contextual callouts

- On by default. Right-click Alexandra → **Setup** → uncheck **Combat callouts** → **Save** to disable.
- Main ship only, in normal space, with available, freshly polled cockpit data and danger or a recent incoming-fire report. Merely deploying hardpoints is not combat evidence. No fighter, SRV, on-foot, docked or supercruise callouts.
- **Shields down.** Only after an observed up-to-down transition.
- **Low hull. Forty percent or less.** / **Critical hull. Twenty percent or less.** Only on new journal damage reports while you pilot the main ship, not a loadout snapshot or damage to an NPC-piloted fighter.
- **Heat damage detected.** Only for a new heat-damage event in that combat context.
- **Overheating.** On a new heat warning or a newly active overheating flag, before damage is required. Suppressed if newer status shows the ship has cooled.
- No catch-up announcements on startup, re-enable, journal/ship/session change or after losing fresh data.
- At most one warning per poll; hull takes priority over shield loss, then heat damage, then overheating. A 20-second global cooldown and 90-second per-warning cooldown prevent chatter. Critical hull may break the global cooldown after five seconds.
- Events during push-to-talk, a reply, research, Setup or another callout are consumed without queuing. A short grace period after input/audio adds breathing room.
- Incoming user speech, ordinary replies and opening Setup cancel pending/playing callouts immediately. No fallback Windows voice or spoken error.
- Warnings must still match the current situation and be no more than eight seconds old. Slow generation, stale data, repairs, restored shields or a changed ship/session suppress playback.
- Uses the selected voice and existing Realtime model through a separate, tool-free fixed-phrase request. The returned transcript must match the requested phrase before playback. It does not add messages to the conversation or upload journals/screenshots.
- Completed clips are cached by voice and warning for this launch, in memory only. First generation uses API credit; no cloud call is made for cached playback. Generation failures back off for two minutes. Requires cloud voice consent and a configured key; never weakens those switches.
- These are convenience cues, not a replacement for the game's HUD and warnings. Some events are deliberately skipped, including when the first clip takes too long to generate.

## Session journals

When session journals are enabled, Markdown files include a **Combat after-action** section with totals and a timestamped event sequence. It keeps the latest 60 combat events per session plus totals; truncation is labeled. A partial session remains labeled as such.

No extra AI calls are used to build the local record. Detailed combat facts are sent with a relevant voice question, not continuously streamed to the API. Optional callout generation sends only a fixed warning phrase. Target pilot names and commander identifiers are omitted from combat output.

## Verification

Offline tests replay synthetic combat records, including mixed legacy/current journal names, running/stopped/restarted games, unchanged/missing/malformed Status, main-ship/fighter separation, target scan levels/loss, commander changes, repairs, file replacement, partial lines, session-log migration, canceled/aged replies, callout cooldowns, overheating/cooling, startup silence, voice matching and canceled/outdated playback.
When optional debug logging is enabled, source-session/availability transitions and selected callouts are logged without full cockpit payloads.
They verify data and routing contracts, not live combat coaching quality. Test spoken answers in-game before relying on them.

Data semantics follow Frontier's [Journal Manual](https://hosting.zaonce.net/community/journal/v34/Journal_Manual_v34.pdf), especially Status, ShipTargeted, HullDamage, ShieldState and combat events.

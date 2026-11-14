# Spoken landing-pad directions

- Ask “Where is pad 24?” or “Where is my assigned pad?”
- Local `DockingGranted` supplies station, type and assigned pad. Cancel, denial, timeout, departure, death and new sessions invalidate the assignment.
- Coriolis, Orbis and Ocellus use a mapped 45-pad interior. Directions assume entering with green slot lights on the right, and include front/middle/back plus side and clock position.
- For example, pad 24 is near the entrance, upper side, at 12 o'clock in that orientation.
- Unknown layouts (including surface stations and carriers) are not guessed. The player can ask Alexandra to inspect the current docking display. Images are not saved for visual questions.
- A requested pad is not necessarily the player's assigned pad. Guidance never controls the ship.

References: [Frontier journal specification, docking events](https://hosting.zaonce.net/community/journal/v37/Journal_Manual_v37.pdf), [LandingPad project's standard starport map](https://github.com/bgol/LandingPad/blob/master/lpads/starport.py).

The implementation is original code using factual pad positions; it does not bundle third-party source, images or assets.

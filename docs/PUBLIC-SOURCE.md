# Public source and privacy

This repository contains an audited source snapshot based on Alexandra 0.1.24, public documentation and synthetic test fixtures. It intentionally does not contain private development history.

## Excluded content

- API keys, access tokens and encrypted credential files.
- User settings, journal files, screenshots, captured audio, conversations and commander memory.
- Browser profiles, caches, local build output and diagnostics.
- The separately supplied EuroStyle font. No redistribution permission is recorded; public builds use Windows-provided Segoe UI instead.

The app icon is the existing project asset. NuGet and tool dependencies are restored from their package sources; their licenses remain separate. No additional license for the application source has been selected yet.

## Verification

Before publishing, run `scripts/verify-public-source.ps1` against the tracked files, then build and run the offline tests. This check detects known credential formats and forbidden file types; it is not a guarantee against every possible secret or personal identifier. Review new fixtures, documents and binary metadata manually too.

Use a GitHub no-reply email for public commits. Do not merge or push old private development history into this repository. Branches and existing release tags were rewritten to remove the previous identifying author metadata; existing release assets were not changed. GitHub or prior recipients may retain old cached copies outside current branch/tag history.

Release scripts never create or distribute API keys. Each installation configures its own access. Do not post real logs, keys, screenshots or journals in issues without removing private information.

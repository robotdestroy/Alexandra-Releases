# Public source and privacy

This repository contains an audited source snapshot based on Alexandra 0.1.24, public documentation and synthetic test fixtures. It intentionally does not contain private development history.

## Excluded content

- API keys, access tokens and encrypted credential files.
- User settings, journal files, screenshots, captured audio, conversations and commander memory.
- Browser profiles, caches, local build output and diagnostics.
- The separately supplied EuroStyle font. No redistribution permission is recorded; public builds use Windows-provided Segoe UI instead.

The app icon is the existing project asset. The application's source and documentation are covered by the [MIT License](../LICENSE), attributed to `robotdestroy` without a personal name or email address. NuGet and tool dependencies are restored from their package sources; their licenses remain separate. The excluded preview font is not covered by this license grant.

## Verification

Before publishing, run `scripts/verify-public-source.ps1` against the tracked files, then build and run the offline tests. This check detects known credential formats and forbidden file types; it is not a guarantee against every possible secret or personal identifier. Review new fixtures, documents and binary metadata manually too.

Use a GitHub no-reply email for public commits. Do not merge or push old private development history into this repository. Branches and existing release tags were rewritten to remove the previous identifying author metadata; existing release assets were not changed. GitHub or prior recipients may retain old cached copies outside current branch/tag history.

Release scripts never create or distribute API keys. Each installation configures its own access. Do not post real logs, keys, screenshots or journals in issues without removing private information.

## Release source links

Releases through 0.1.24 predate source publication. Their release notes link explicitly to the audited public snapshot and disclose that their automatic source archives are historical release-channel placeholders, not application code. Those binary assets and tags are not changed to imply a match that was never established.

Future packaging requires a clean checkout whose committed version and revision match public `main`. It builds an isolated archive of that commit, bundles the MIT notice and `source.json`, and verifies the same source metadata in the update package. Publishing refuses mismatched revisions or tags and generates pinned source links. Never use the old private development checkout to build an official release.

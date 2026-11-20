---
name: release
description: Cut a release of this app (version bump, pull request, tag, what the release workflow does). Use only when the owner asks for a release.
---

# Release

1. Bump `version` in `package.json` (and in the plugin project and manifest once the C# plugin exists).
2. Follow `ship-a-change`: branch, checks, pull request into `main`, CI green, owner merges.
3. From an up-to-date `main`: `git tag -a vX.Y.Z -m "vX.Y.Z"` and `git push origin vX.Y.Z`.
4. The tag runs `.github/workflows/release.yml`: it zips `web/` and creates a GitHub release with generated notes.
   The plugin zip and `manifest.json` entry will come from the C# build, which does not exist yet (`docs/design.md`).
5. Verify: `gh run list --workflow Release --limit 1` and `gh release view vX.Y.Z`.

Tagging is outward-facing: get the owner's go-ahead first.

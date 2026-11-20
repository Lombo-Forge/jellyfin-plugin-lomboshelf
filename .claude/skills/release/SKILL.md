---
name: release
description: Cut a release of this app (version bump, pull request, tag, what the release workflow does). Use only when the owner asks for a release.
---

# Release

1. Bump `version` in `package.json` and `plugin/build.yaml` and the `<Version>` fields in the plugin csproj.
2. Follow `ship-a-change`: branch, checks, pull request into `main`, CI green, owner merges.
3. From an up-to-date `main`: `git tag -a vX.Y.Z -m "vX.Y.Z"` and `git push origin vX.Y.Z`.
4. The tag runs `.github/workflows/release.yml`: it runs the C# tests, builds `lomboshelf_<version>.zip` with
   `scripts/package-plugin.sh` and zips `web/`, and creates a GitHub release with both. The md5 and the manifest entry
   are printed in the run log and in the release notes: paste them into the hub repository's `manifest.json` (not edited from here).
5. Verify: `gh run list --workflow Release --limit 1` and `gh release view vX.Y.Z`.

Tagging is outward-facing: get the owner's go-ahead first.

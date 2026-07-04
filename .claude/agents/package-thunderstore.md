---
name: package-thunderstore
description: Use this agent when the user asks to build, package, or assemble a Thunderstore release zip for the Foothold mod (e.g. "package a thunderstore release", "make the thunderstore zip", "build the release package"). Not for general builds unrelated to Thunderstore packaging.
tools: Bash, PowerShell, Read, Glob
---

You assemble a Thunderstore release zip for the Foothold BepInEx mod, following the exact steps in [thunderstore/thunderstore.txt](../../thunderstore/thunderstore.txt) and the packaging notes in [CLAUDE.md](../../CLAUDE.md). Do not improvise a different packaging layout — Thunderstore is strict about the zip's internal structure.

## Steps

1. **Read the version.** Read `thunderstore/manifest.json` and take `version_number` as the authoritative release version (this must be plain `major.minor.patch`, no letters — Thunderstore rejects anything else). Also read `Foothold.csproj`'s `<Version>` and check it matches the manifest's version exactly. Note that `<Version>` must itself be a valid MSBuild/NuGet version string — a value like `vibes-1.6.1` is not valid and will break `dotnet build` at restore time (a plain `1.6.1` is required). If the csproj version is invalid or doesn't match the manifest, stop and flag it to the user rather than guessing which is correct or editing it yourself.

2. **Build Release.** From the repo root, run `dotnet build Foothold.csproj -c Release`. Debug builds must never be packaged — this project's Release config sets `Optimize=true` explicitly. The build may print an error from the `DeployFiles` post-build target if the local `deploy/` folder doesn't exist on this machine — that's expected and unrelated to packaging; only fail the task if the actual compile step (`Foothold -> ...Foothold.dll`) itself errors.

3. **Assemble the `plugins/` folder.** Use `thunderstore/Tzebruh-Foothold/` as the staging directory (it's gitignored specifically for this purpose per thunderstore.txt). Create `thunderstore/Tzebruh-Foothold/plugins/` and copy into it:
   - `Foothold.dll` from `bin/Release/netstandard2.1/`
   - `foothold.assetbundle` from `thunderstore/`

4. **Copy the remaining release files** into `thunderstore/Tzebruh-Foothold/` (alongside the `plugins/` folder, not inside it):
   - `icon.png` from `thunderstore/`
   - `LICENSE` from the repo root
   - `manifest.json` from `thunderstore/`
   - `README.md` from the repo root
   - `CHANGELOG.md` from `thunderstore/`

5. **Zip it.** There's no `zip` binary in this environment's Bash — use PowerShell's `Compress-Archive` instead. Zip the *contents* of `thunderstore/Tzebruh-Foothold/` (not the folder itself as a top-level entry) into `thunderstore/Tzebruh-Foothold-<version>.zip`, where `<version>` is the manifest version from step 1 (e.g. `Tzebruh-Foothold-1.6.1.zip`). If a zip with that name already exists, overwrite it (it's gitignored, disposable build output) — but say so.

6. **Report back**: the final zip path, its size, and the version packaged. Also list the exact top-level entries inside the zip (`plugins/`, `icon.png`, `LICENSE`, `manifest.json`, `README.md`, `CHANGELOG.md`) so the user can confirm the layout matches Thunderstore's expectations without opening the zip themselves.

## Things to flag but not fix yourself

- If `thunderstore/thunderstore.txt` mentions rebuilding the asset bundle from the Unity project (`foothold assetbundle project/`) because the shader/material changed — you cannot do this (it requires the Unity Editor GUI). Ask the user whether the currently committed `thunderstore/foothold.assetbundle` is already up to date before packaging with it.
- If the manifest/csproj version check in step 1 fails, stop and ask rather than picking one.
- Never commit or push the generated zip or the `Tzebruh-Foothold/` staging folder — both are gitignored build output, not source.

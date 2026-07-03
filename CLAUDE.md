# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Foothold is a BepInEx plugin (mod) for the Unity game **PEAK**. It visualizes standable ground by raycasting downward around the player and rendering colored spheres (white/green = standable, red/magenta = not standable) on qualifying surfaces. Nearly all mod logic lives in a single file: [Plugin.cs](Plugin.cs).

## Build

This is a .NET/BepInEx plugin project, built with MSBuild/Visual Studio (`Foothold.sln` / `Foothold.csproj`), targeting `netstandard2.1`.

```
dotnet build Foothold.csproj
```

Key build facts:
- Requires local references to `Assembly-CSharp.dll` and `PhotonUnityNetworking.dll` from a local PEAK install, hardcoded in [Foothold.csproj](Foothold.csproj) as `C:\Program Files (x86)\Steam\steamapps\common\PEAK\PEAK_Data\Managed\`. Building on a machine without PEAK installed at that path will fail unless the `HintPath`s are adjusted.
- NuGet feeds include the BepInEx and Samboy nuget servers in addition to nuget.org (see `RestoreAdditionalProjectSources`).
- There is no automated test suite — this is a Unity/game-runtime plugin, verified by loading it in-game.
- A post-build `DeployFiles` target copies the built DLL to `DeployDir` (default `.\deploy\AdvocatusDiaboli-Foothold-Forked\plugins\`). This directory generally won't exist locally; either create it, override `DeployDir` via a `Config.Build.user.props` file, or ignore the deploy-copy failure if you only need the DLL from `bin/`.

## Packaging for Thunderstore

Release packaging is manual; see [thunderstore/thunderstore.txt](thunderstore/thunderstore.txt) for the exact steps. Summary:
1. Build the DLL in **Release** (`bin/Release/netstandard2.1/Foothold.dll`) — Debug builds are unoptimized and should never be packaged for release.
2. Assemble a `plugins/` folder with `Foothold.dll` + `foothold.assetbundle` (from `thunderstore/`).
3. Zip that folder together with `icon.png`, `LICENSE`, `manifest.json`, `README.md`, `CHANGELOG.md` (all from `thunderstore/` or repo root) into `thunderstore/Tzebruh-Foothold-<version>.zip`.
4. If the asset bundle shader/material changed, rebuild it from the Unity project in `foothold assetbundle project/` and copy the output from `Assets/ModBundles/` into `thunderstore/`.

Bump `Version` in [Foothold.csproj](Foothold.csproj) and `version_number` in [thunderstore/manifest.json](thunderstore/manifest.json) together, and add an entry to [thunderstore/CHANGELOG.md](thunderstore/CHANGELOG.md).

## Architecture

Everything is in the single `Plugin : BaseUnityPlugin` class in [Plugin.cs](Plugin.cs), driven by Unity lifecycle methods (`Awake`, `Update`, `OnSceneLoaded`, `OnDestroy`, `OnGUI`).

**Activation modes** (`Mode` enum: Continuous / Toggle / Trigger / FadeAway) are BepInEx config-driven and change how/when the visualization coroutine runs. `Update()` dispatches based on `configMode.Value` and drives GPU-instanced rendering every frame regardless of mode.

**Grid + raycast model**: The world is scanned on an XZ grid (spacing = `configXZFreq`, converted to fixed-point ints via `safeFreq`/`safeRange` to avoid float drift). For each grid column, `CheckCacheMiss` does one `Physics.RaycastNonAlloc` straight down through a `range`-tall band and records every standable/non-standable hit as a `PositionKey`, keyed by quantized Y, inside a `PositionYList` (one per (x,z) column). `DoRaycastRecursive` recurses to catch points hidden behind concave (non-convex) mesh colliders when `configDetectConcave` is enabled — this is much more expensive and expects `configMaximumPointsPerFrame` to be raised accordingly.

**Two coroutine strategies**:
- `RenderVisualizationCoroutine` — full rescan of the entire range around the camera. Used for Toggle/Trigger/FadeAway modes.
- `RenderChangedVisualizationCoroutine` — incremental: diffs the previous vs. new camera-frustum bounding rects (via `SubtractRect`) to figure out which grid areas newly entered range (rescan), which left the "keep" radius (evict from cache/pool), and which are still in range but need visibility rechecked. Used for Continuous mode. This is the performance-critical path; changes here need to preserve the invariant that cache/pool state stays consistent as the player moves.

**Object pooling**: `PositionKey` (a single ball's position/standability/matrix-slot) and `PositionYList` (a column's dictionary of `PositionKey`s) are both pooled via static `Queue<T>` pools (`GetNew()` / `ReturnToPool()`) to avoid GC churn from constant scanning. `positionCache: Dictionary<(int,int), PositionYList>` is the source of truth for what's currently cached per grid column.

**Rendering**: Uses `Graphics.RenderMeshInstanced` with a growable list of `Matrix4x4[500]` batches (`standableMatrices`/`nonStandableMatrices`), each entry linked back to its owning `PositionKey` via parallel `linked*Matrices` arrays so a ball's transform can be found/removed by index in O(1). Materials are cloned from an instanced-capable material loaded from the `foothold.assetbundle` asset bundle at `Awake()` (search paths include the assembly folder and common asset-bundle filename variants, to tolerate different Thunderstore packaging layouts).

**`FastFrustum`**: a hand-rolled camera frustum struct used for cheap in-frustum checks and for computing quantized XZ bounding rects per-frame, driving what the incremental coroutine treats as "in range."

Scanning is scoped to scenes whose name starts with `"Level_"` or `"Airport"` — everything else (menus, etc.) is a no-op.

## Branching

The `vibes` branch is a separate branch used for AI-assisted development, so that people who don't want to use a version of the mod containing AI-generated code can stick to `main`.

## Credits / contribution history

Originally by Tzebruh; significant contributions and a period of maintainership from cjmanca (AdvocatusDiaboli-Foothold-Forked) and MyShiLingStar. Keep this in mind when reading older code/comments referencing "the fork" — this repo is the canonical upstream again.

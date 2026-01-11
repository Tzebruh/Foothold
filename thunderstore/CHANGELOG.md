# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]
### Added

### Changed

### Fixed

### Removed 

### Deprecated 


## [1.6.0]
### Added
- A config option to specify the transparency of the balls.
- Concave surface detection, allowing it to place points in indents in meshes. Previously, the raycast would ignore the mesh after it touched the mesh once, even if the line exited the mesh and struck the same mesh again due to an indent.

### Changed
- Frustum far clip plane was previously a plane (as a true frustum far clip works), but in practice due to how close this clip plane was, it looked strangely "fish eye" like along the edges. Changed to pythagoras to make it look more natural.

### Fixed
- An issue where the foothold indicators wouldn't render in the kiln sometimes.
- Fixed assetbundle not being found due to flat thunderstore directory packaging, by changing the path it looks for the assetbundle to be the same folder as the dll
- A race condition causing the pause button to sometimes not work.


## [1.5.0]
### Added
- Continuous mode, which scans newly in-range areas and adds points automatically.
- GPU instanced batching, for a sizable performance boost
- A grid based storage structure to make adding/removing specific grid points easier.
- An assetbundle with a shader to support GPU instanced batching
- Scale config option, to change the size of the foothold indicators
- A pause option for continuous mode. Uses the same hotkey as activation mode.
-  


### Changed
- cjmanca took over project with AdvocatusDiaboli-Foothold-Forked. Tzebruh is welcome to have it back by accepting the pull requests.
- Raycasting to RaycastNonAlloc to scan the full vertical world in one pass, rather than brute forcing many smaller rays.

### Fixed
- Many misc performance improvements.


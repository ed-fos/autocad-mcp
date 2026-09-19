# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

AutoCAD compatibility is not encoded in the version number — it is in the release tag and the
asset name (e.g. `v0.1.0` / `Nodal.AcadMcp-acad2027-*.zip`), because supporting a new AutoCAD
year is a retarget rather than a feature bump.

## [Unreleased]

## [0.1.0] — first public release

Initial release. 133 tools across AutoCAD and AutoCAD Architecture.

### Added
- 63 AutoCAD tools: entities, blocks, annotation, layouts and selection, 3D solids, and the
  `run_csharp` / `run_lisp` / `send_command` escape hatches.
- 70 AutoCAD Architecture tools: walls, doors, windows, openings, spaces, slabs, roofs, mass
  elements and groups, column grids, layout grids, structural members, curtain walls, window
  assemblies, railings, stairs, property sets, and generated sections and elevations.
- Per-session authentication on the in-AutoCAD bridge, with `Host` / `Origin` /
  `Content-Type` validation against browser CSRF and DNS rebinding.
- Undo classification (`single-step` / `tool-defined` / `not-undoable`) surfaced to the model,
  so a tool that is not one Ctrl+Z is never advertised as one.
- Automatic instance discovery: the MCP server locates a running AutoCAD through a per-user
  instance file, so start order does not matter and a second AutoCAD takes an ephemeral port
  instead of silently losing the bridge.
- Test suite covering the admission control in front of the code-execution tools.

### Security
- The bridge is opt-in per session: the add-in loads at AutoCAD startup so its commands and
  ribbon exist, but opens no socket until you click **Start** or type `NODALMCPSTART`.
- `set_system_variable` refuses `SECURELOAD`, `TRUSTEDPATHS`, `TRUSTEDDOMAINS`,
  `ACADLSPASDOC`, `LISPSYS`, `AUTOLOAD` and `SECUREREMOTEACCESS`, which govern what AutoCAD
  will load and persist beyond the session.
- See [SECURITY.md](SECURITY.md) for the threat model, including support-path persistence.

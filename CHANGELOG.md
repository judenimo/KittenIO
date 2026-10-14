# Changelog

All notable changes to KittenIO are recorded here.

## Unreleased

- Experimental 200 km launch guidance with body-aware orbital math, ascent,
  coast, circularization, cutoff confirmation, and bounded failure cleanup.
- `kittenio new --template orbit` creates an editable yield-free launch mission.
- Complete idealized Earth-scale trajectory tests, including actual SDK/runtime
  and authenticated loopback transport; live KSA flight remains unverified.
- Ordinary `ksa.wait`, `wait_real_seconds`, `next_tick`, and `wait_until` calls
  handle scheduling automatically in function missions, without `yield`.
- Starter scripts and the scripting guide now use the ordinary-call style.
- Simple `mission(ksa)` function entry points, alongside existing program classes.
- `kittenio new` creates a telemetry starter without overwriting user files.
- A Python scripting guide and public `KsaApi` type for editor completion.
- Current-direction holds through the existing leased thrust-vector route.
- Bounded initial telemetry acquisition and optional real-time predicate deadlines.
- Deterministic generator cleanup and explicit restart after a function mission disconnects.
- Normal sibling imports for file missions and clearer CLI errors.
- Bare `yield` now resumes on the next telemetry frame without skipping a frame.
- Corrected moved runtime imports, including control renewal's timeout/overload cleanup.

## 0.1.0 - 2026-09-05

Initial public release.

- Authenticated, loopback-only protocol-v1 bridge between KSA and Python.
- Immutable flight telemetry, capability reporting, recording, and replay.
- Mission scheduling, waits, finite-state-machine helpers, and time-warp requests.
- Vehicle/session-scoped thrust-vector control with leases and a dead-man timeout.
- Installer, pairing, diagnostics, exact artifact validation, and checksums.
- Pinned support for KSA `2026.9.7.5402` and StarMap `0.4.6`.

Limitations:

- No compatibility support for other KSA, StarMap, or protocol versions.
- No distributed example missions.
- A live KSA acceptance run was waived for this release. Automated protocol,
  contract, and packaging validation is not presented as live-game validation.

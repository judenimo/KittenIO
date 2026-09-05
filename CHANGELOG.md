# Changelog

All notable changes to KittenIO are recorded here.

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

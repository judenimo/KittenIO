# Compatibility

KittenIO deliberately fails closed when the game, loader, or wire contract is
outside the verified release set.

| Component | Supported in v0.1.0 | Validation |
|---|---|---|
| Operating system | Windows | Required for KSA and the mod; the Python SDK is also tested on Linux |
| Python | 3.11, 3.12, 3.13, 3.14 | CI import, test, build, and isolated-install coverage |
| KSA | `2026.9.7.5402` | Product version and pinned assembly SHA-256 |
| StarMap API | `0.4.6`, commit `57846b2` | Product version/commit and pinned DLL SHA-256 |
| KittenIO protocol | v1 | Strict authenticated handshake and schema validation |

An unsupported KSA build prevents game-facing patches from enabling. This is a
safety boundary, not a warning-only check. If the mod reports the supported game
but omits a particular capability, read-only telemetry, recording/replay, and
other advertised capabilities remain usable; callers must check the nested
capability flags. Control and time-warp calls fail when their capabilities are
not available.

StarMap `0.4.6` is the only verified loader. Another build is rejected by release
validation and causes `kittenio doctor` to return a failure when reported by the
running mod.

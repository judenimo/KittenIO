# Engineering guide

The repository is split into `KittenIO.Protocol` (strict DTOs and wire codec),
`KittenIO.Mod` (KSA capture, WebSocket host, control and command coordination),
and the Python SDK under `python/src/kittenio`.

There is one protocol constant (`1`), one telemetry DTO (`FlightStatePayload`),
one Python state (`FlightState`), and one control payload
(`ControlFramePayload`). The C# canonical models live in
`src/KittenIO.Protocol/TelemetryModels.cs`; Python models live in
`python/src/kittenio/wire/models.py`.

The game thread captures one detailed state and publishes it through one
latest-by-key telemetry stream. Target/orbit and manual-input values are merged
into that same snapshot. Control is accepted only for an exact current vehicle
lease and the `thrust_vector_target` route. Landing and time-warp requests use
capability checks.

Camera/view state is not control authority. The exact selected vehicle,
vehicle instance, flight-computer instance, session, and authority epoch bind a
lease across flight/map view changes. The SDK renews the last validated command
on a dedicated thread for at most two seconds without mission-thread progress,
bridging short UI transitions without allowing a wedged mission to hold control
indefinitely. Vehicle changes, loss of controllability, disconnects, and the
server dead-man still release the route.

The runtime does not require 1x simulation speed. Flight programs continue to
receive telemetry and renew validated commands at non-1x rates; algorithms use
the authoritative simulation clock and decide their own operating envelope.
The pinned KSA build natively locks active engine, rotation, and translation
controls above 30x, which remains a game constraint rather than an SDK policy.

Build and test from Windows Git Bash with `./scripts/dev`. Set
`KITTENIO_KSA_REFERENCE` and `KITTENIO_STARMAP_API` when the pinned assemblies
are not discoverable locally. Release validation accepts only KSA
`2026.9.7.5402` and StarMap API `0.4.6` at their recorded hashes. Never
redistribute those proprietary assemblies.

The test suite validates the shared canonical fixtures in both languages,
rejects retired protocol versions and malformed state/control data, exercises
lease/dead-man/reconnect behavior, and verifies the pinned KSA reflection
contract. A separately recorded live smoke run remains necessary after changes
to game-facing capture or actuation. Automated tests do not establish that a
live run occurred.

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

## Python scripting lifecycle

`run` accepts an ordinary synchronous `mission(ksa)` function as well as the
existing `KittenProgram` class/instance surface. The private function adapter
runs on the mission thread, begins after the first valid telemetry frame, and
stops on return. A disconnect stops function missions with
`MissionInterruptedError`; function bodies are never automatically replayed.
Class programs retain their established session-restart callbacks.

Function missions use ordinary calls: `ksa.wait(seconds)` (simulation clock),
`ksa.wait_real_seconds(seconds)`, `ksa.next_tick()`, and
`ksa.wait_until(predicate, timeout=..., description=...)`. No generator syntax,
AST rewriting, or extra mission threads are required. Each call returns the
`FlightState` that completed it. Generator workflows remain available through
the imported wait-condition helpers.

The runtime records the executing program, exact program session, and mission
thread identity. Direct wait calls are admitted only for function missions on
that thread; class callbacks, foreign threads, and nested waits inside a
predicate cannot reenter the runtime. The wait loop drains lifecycle events
without closing its currently executing adapter generator, consumes only the
program's exact session telemetry, and marks progress when a new telemetry
message is consumed. A paused simulation continues to service telemetry and
renewal, but a telemetry outage does not reset the two-second progress grace.
Disconnect or external runtime stop raises `MissionInterruptedError` through
the function stack, unwinding ordinary `finally` blocks and lease contexts.
Predicate deadlines are checked during telemetry gaps and raise directly at
the ordinary call site. Context is cleared in `finally` on every exit path.

The public runner bounds initial telemetry acquisition to 15 real seconds by
default (`connect_timeout`, CLI `--connect-timeout`). This includes connection
and handshake retries. `wait_until` optionally carries a real-time deadline,
armed at the yield point and polled during telemetry gaps. Expiry raises
`WaitTimeoutError`. A satisfied predicate is latched so it cannot expire before
the scheduler's next frame. Yielded waits, including bare `yield`, are armed
against the current frame. Normal generator steps advance at most once per
received frame. Predicate timeouts are thrown back into the suspended generator
on the mission thread, including during telemetry gaps, so scripts can catch
`WaitTimeoutError` around the yield expression. Timeout handlers see the latest
available snapshot and must account for its age before making further decisions.

Generators are explicitly closed on exit and disconnect on the mission thread,
before runtime lease cleanup and network shutdown. This ensures generator
`finally` blocks and `VehicleControl.leased()` contexts unwind deterministically.
Timeouts and entry-point convenience do not alter lease/session binding,
capability checks, the renewal grace period, or the game dead-man timeout.

`VehicleControl.hold_current_direction` rotates vehicle body +X using the
current `body_to_cci` quaternion, then uses the existing complete
`set_thrust_vector` control route. It requires an explicit lease and actual
orientation telemetry; it provides no roll control, horizon following, or
fallback orientation.

`kittenio new` writes a telemetry starter using exclusive file creation. The
template ships as CLI source, without adding example directories or changing
the distribution allowlist. File missions temporarily include their directory
on `sys.path` during import and execution, restoring it afterward. Local
`mission` functions and explicit function selectors are accepted; existing
`Program` class precedence remains unchanged. See [scripting.md](scripting.md).

## Experimental launch guidance

`api/orbit.py` contains a pure telemetry-driven controller and a blocking
`launch_to_orbit` mission helper. The public exports are `OrbitLaunchSettings`,
`OrbitInsertionResult`, `OrbitLaunchError`, and `launch_to_orbit`. The CLI's
`--template orbit` starter uses the same supported ordinary-call interface.

The controller derives bound two-body apsides and time to apoapsis directly
from CCI position/velocity and telemetry body parameters. Radius and altitude
are kept distinct; the reflection-derived native orbit fields are not inputs
to guidance. Initial east comes from inertial minus surface-relative velocity,
with an explicit direction required at degenerate sites.

The phases are altitude-programmed ascent, coast with an approximate half-burn
lead, and radial/tangential feedback circularization. Actual horizontal velocity
defines the insertion direction. Propulsion and orientation telemetry govern
throttle, including minimum-throttle pulses, an acceleration limit, and a
45-degree pointing gate. Launch deadlines, frame deadlines, exact vehicle/body
identity, the existing exact lease, and the existing dead-man remain in force.
No protocol, C# actuation, pairing, or game-version contract is changed.

Staging and warp changes remain explicit; this first controller assumes a
prepared enabled engine stage can complete the flight. A connected failure
attempts engine-off/zero-throttle confirmation before lease release, preserving
the original error when that confirmation fails. Success requires a newer
applied sequence under the current lease and a final orbit within tolerance.

`python/tests/support/orbit_simulation.py` is an idealized test-only plant. The
unit suite exercises a complete Earth-scale flight; integration tests run the
same complete trajectory through the actual Python runtime and authenticated
loopback transport. These do not validate live KSA dynamics or an automatic
Starship staging sequence. See [orbital-launch.md](orbital-launch.md).

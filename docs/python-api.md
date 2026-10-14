# Python API

Mission callbacks, waits, state machines, optimizers, identification helpers,
recording, and replay all use the single `kittenio.FlightState` type.

For an introduction with runnable snippets, see the [scripting guide](scripting.md).

## Mission entry points

- `def mission(ksa: KsaApi): ...` is the simple scripting entry point. It may
  return after one operation or call ordinary `ksa` wait methods for a longer
  sequence. No `yield` or `async` is required.
- `run(mission, *, config=None, config_path=None, connect_timeout=15.0)` runs a
  function, `KittenProgram` class, or `KittenProgram` instance.
- `kittenio new [mission.py]` creates a telemetry starter and never overwrites
  an existing file.
- `kittenio [run] FILE_OR_MODULE [--program NAME] [--config PATH]
  [--connect-timeout SECONDS]` selects and runs a mission.

`KsaApi` is exported for type hints and editor completion. The function starts
after the first telemetry frame, ends when it returns, and stops on disconnect
with `MissionInterruptedError`. It is never replayed automatically. Class
programs retain their callback and session-restart lifecycle.

## Waits

Function missions use ordinary calls. Each method returns the `FlightState`
snapshot that completed the wait:

- `ksa.next_tick()` waits for a distinct telemetry frame.
- `ksa.wait(seconds)` uses `FlightState.player_time_s`; it pauses with the game.
- `ksa.wait_real_seconds(seconds)` waits for a telemetry frame after a real-time duration.
- `ksa.wait_until(predicate, *, timeout=None, description="condition")` waits
  for a telemetry predicate. An optional positive finite timeout uses real
  seconds and raises `WaitTimeoutError` at the call site, including during
  telemetry gaps. The timeout starts when the call begins.

These calls pump telemetry and lifecycle events on the existing mission thread,
maintaining progress for control renewal while fresh telemetry arrives. Missing
telemetry does not extend the existing renewal grace period. A lost connection
or runtime shutdown raises `MissionInterruptedError` and never resumes the
function in a new session. Calls from class callbacks, other threads, and
nested waits inside predicates are rejected.

## Generator wait conditions

Existing generator missions and program `script()` methods can yield the
imported helpers:

- `next_tick()` waits for a distinct telemetry frame; a bare `yield` is equivalent.
- `wait_sim_seconds(seconds)` uses `FlightState.player_time_s`.
- `wait_real_seconds(seconds)` waits for a telemetry frame after a real-time duration.
- `wait_until(predicate, *, timeout=None, description="condition")` evaluates a
  predicate on current telemetry. An optional positive finite timeout is in
  real seconds, starts when yielded, and raises `WaitTimeoutError` even when
  telemetry stops arriving. A satisfied condition remains satisfied.

`ConnectionTimeoutError` reports an expired first-telemetry deadline.
`ControlError`, `MissionInterruptedError`, and `WaitTimeoutError` are also
exported from `kittenio`. Generator cleanup runs on mission exit, exception,
Ctrl+C, and disconnect.

## State and handles

- `ksa.data`: latest `FlightState | None`.
- `ksa.clock`: timing projection from the latest state.
- `ksa.capabilities`: the same nested `CapabilitySet` carried by telemetry.
- `ksa.active_vehicle`: session-bound `Vehicle | None` handle.
- `ksa.vehicles`: bounded session-bound vehicle registry.
- `ksa.manual_control`: raw manual-input telemetry convenience access.
- `ksa.state_age_s` and `ksa.transport_rtt_s`: local freshness diagnostics.

Use `state.vehicle`, `state.applied_control`, and `state.player_time_s` directly.
The old `state.active_vehicle`, `state.control`, and `state.sim_time_s`
projections do not exist.

## Experimental orbital launch

`launch_to_orbit(ksa, settings=None, *, on_phase=None)` runs ascent, coast,
circularization, and confirmed cutoff inside an ordinary function mission.
`OrbitLaunchSettings` defaults to a 200 km circular target and an eastward
heading. `OrbitInsertionResult` reports the exact vehicle/body IDs, final
computed apsis altitudes, and elapsed simulation time. `OrbitLaunchError`
reports an unsupported flight envelope or failed launch.

The enabled engine stage must be prepared before running. This helper does not
guess staging sequences and has been verified only in offline models and
loopback tests. Read [orbital-launch.md](orbital-launch.md) for vehicle
requirements, limits, tuning, cutoff semantics, and game compatibility.

## Capabilities

Check the nested feature that an operation needs:

```python
if state.capabilities.control.thrust_vector_target:
    ...
if state.capabilities.landing.site_definition:
    ...
if state.capabilities.time_warp.control:
    ...
```

## Vehicle control

`VehicleControl` exposes `acquire`, `leased`, `release`, `set_thrust_vector`, `hold_current_direction`,
`set_engine`, `set_throttle`, `clear_overrides`, `stage`, and `set_target`.
`set_thrust_vector` must initialize the complete continuous-control frame before
engine or throttle updates can amend it. Digital attitude and translation
methods are absent. `hold_current_direction(*, engine_on=None, throttle=None)`
initializes that frame from the vehicle's current +X thrust direction and
requires orientation telemetry and an explicit lease. It holds an inertial
direction, not roll or a horizon-relative attitude.

## Recording and replay

`TelemetryRecorder` writes strict JSONL with `protocol_version: 1`.
`TelemetryReplay` accepts only that canonical schema. Older recordings are
intentionally unsupported.

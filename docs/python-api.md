# Python API

Mission callbacks, waits, state machines, optimizers, identification helpers,
recording, and replay all use the single `kittenio.FlightState` type.

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

`VehicleControl` exposes `acquire`, `leased`, `release`, `set_thrust_vector`,
`set_engine`, `set_throttle`, `clear_overrides`, `stage`, and `set_target`.
`set_thrust_vector` must initialize the complete continuous-control frame before
engine or throttle updates can amend it. Digital attitude and translation
methods are absent.

## Recording and replay

`TelemetryRecorder` writes strict JSONL with `protocol_version: 1`.
`TelemetryReplay` accepts only that canonical schema. Older recordings are
intentionally unsupported.

# KittenIO wire protocol version 1

KittenIO has one strict JSON protocol. The endpoint is
`ws://127.0.0.1:8765/kittenio/v1`; every envelope has `version: 1`. Versions 2
and 3 are unsupported and are rejected.

## Handshake

The mod sends `hello` with `supported_versions: [1]` and one nested
`CapabilitySet`. The SDK replies with `welcome`, `selected_version: 1`, and a
new session UUID. The `supported_versions` and `selected_version` fields remain
in the shape so future protocol work does not require redesigning the
handshake, but there is no negotiation branch today.

`hello.capabilities`, `telemetry.capabilities`, and `ksa.capabilities` have the
same nested shape: `control`, `dynamics`, `propulsion`, `rigid_body`,
`atmosphere`, `landing`, `surface`, `applied_control`, `recording_replay`, and
`time_warp`.

## Telemetry

`telemetry.payload` is one same-frame `FlightState` containing:

- frame and simulation timing;
- the nested capability set;
- nullable vehicle, propulsion, rigid-body, atmosphere, surface, and landing
  sections;
- required applied-control acknowledgement state;
- nullable raw manual player input.

The detailed vehicle includes target and orbit data, ORB position/velocity,
CCE/ORB total acceleration, inertial speed, and surface speed. Thrust is
reported only by propulsion. Ambient pressure is reported only by atmosphere.

## Control and commands

A `control` payload is a complete thrust-vector frame with exactly:

```json
{
  "lease_id": "33333333-3333-4333-8333-333333333333",
  "control_seq": 7,
  "mode": "thrust_vector_target",
  "engine_on": null,
  "throttle": 0.65,
  "thrust_vector_target": {"direction_cci": {"x": 0, "y": 1, "z": 0}}
}
```

The direction must be a finite unit vector. A frame cannot contain attitude or
translation axes. Lease freshness, strict sequence ordering, active-vehicle
identity, and dead-man release are enforced by the mod.

Landing-site and time-warp requests are enabled by the corresponding nested
capabilities, not by a protocol-number check. Time warp is game-global and
persists after disconnect; acknowledgement occurs only after a later
authoritative game frame confirms the requested rate.

The canonical schema is [schema/v1/envelope.schema.json](schema/v1/envelope.schema.json).
The shared fixture suite is [fixtures/v1](fixtures/v1/).

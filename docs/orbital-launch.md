# Experimental 200 km orbital launch

This example provides a short, yield-free mission that targets a circular orbit
200 km above the active parent body's mean radius. It is an experimental
controller with offline trajectory coverage, not a live-verified KSA autopilot.

```python
from kittenio import OrbitLaunchSettings, launch_to_orbit


def mission(ksa):
    result = launch_to_orbit(
        ksa,
        OrbitLaunchSettings(target_altitude_m=200_000),
        on_phase=print,
    )
    print(result.periapsis_altitude_m, result.apoapsis_altitude_m)
```

Generate an editable version from Git Bash:

```bash
./scripts/kittenio new user-scripts/orbit_200km.py --template orbit
```

After completing the game compatibility and vehicle preparation below, run it
with `./scripts/kittenio user-scripts/orbit_200km.py`. An installed wheel uses
`kittenio` in place of `./scripts/kittenio`.

## Vehicle and game preparation

Select a rocket on the ground with its intended engine stage enabled, throttle
at zero, and the launch restraints released. It needs enough propellant for
insertion, restartable/throttleable engines, and attitude control capable of
following thrust-vector targets. Start at 1x simulation speed. The controller
accepts pauses and rates through 4x; it does not change the game's warp setting.

The first controller assumes the same enabled engine stage can complete the
flight. It sends no stage requests. A custom multi-step Starship sequence needs
its own tested staging profile before this can be used as a full-stack launch
autopilot. Changing the active vehicle, losing its lease, or exhausting the
available thrust aborts the sequence.

This checkout still targets KSA `2026.9.7.5402` and StarMap `0.4.6`. Updating the
bridge to a newer installed game is a separate prerequisite for live testing;
the version/hash checks remain in force. See [compatibility.md](compatibility.md).

## Flight phases

1. **Ascent:** lift vertically, then follow an altitude-based turn toward the
   launch heading. Limit thrust acceleration and taper throttle as the computed
   apoapsis approaches the target.
2. **Coast:** request engine cutoff and point along the horizontal velocity.
   Estimate time to apoapsis and begin the insertion burn early by approximately
   half the estimated burn duration.
3. **Circularization:** use radius, radial velocity, and tangential velocity
   feedback with gravity/centrifugal compensation. Finish only after both
   computed apsides remain within the target tolerance and radial speed is
   small for two simulation seconds.
4. **Cutoff:** request engine off and zero throttle, wait for a newer applied
   control sequence from the current lease, then check the final orbit again
   before returning a result.

Coasting and a burn near apoapsis are standard orbital-insertion concepts;
NASA's [planetary-orbits introduction](https://science.nasa.gov/learn/basics-of-space-flight/chapter5-1/)
explains that sequence. The feedback gains and pitch schedule here are
KittenIO's experimental implementation and require vehicle-specific validation.

The controller derives two-body osculating apsides from CCI position, velocity,
and the body's gravitational parameter. It subtracts the body's mean radius
to report altitude. It does not assume that the native `orbit.apoapsis_m` and
`orbit.periapsis_m` fields use an altitude convention. No Earth constants enter
the flight implementation; body radius and gravity come from telemetry.

East is inferred from the difference between inertial and surface-relative
velocity. An explicitly supplied `launch_direction_cci` is required when local
east cannot be inferred, such as a nonrotating body or a polar launch site.
The heading is an initial launch direction, not an exact inclination guarantee.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `target_altitude_m` | `200_000` | Circular-orbit altitude above mean body radius |
| `heading_deg` | `90` | Initial heading, clockwise from local north |
| `turn_start_height_m` | `500` | Height above the starting radius where the pitch turn starts |
| `turn_end_height_m` | `80_000` | Height where the ascent target becomes horizontal |
| `maximum_acceleration_mps2` | `30` | Maximum commanded thrust acceleration |
| `orbit_tolerance_m` | `5_000` | Allowed error of each final apsis |
| `maximum_sim_seconds` | `5_400` | Whole-flight simulation-time limit |
| `maximum_real_seconds` | `7_200` | Whole-flight wall-clock limit, including pauses |
| `telemetry_timeout_s` | `3` | Deadline for each fresh frame and cutoff confirmation |
| `launch_direction_cci` | `None` | Optional explicit horizontal launch direction |

Minimum throttle is read from telemetry. Commands below that minimum use
frame-based pulses; the controller rejects a minimum-throttle acceleration
that would exceed the configured acceleration limit. Throttle is suppressed
when the craft points more than 45 degrees away from the requested direction.
Engine spool dynamics, atmospheric heating, aerodynamic load limits, launch
restraint release, and automatic staging are not modeled by this controller.

## Failure handling and verification

Preflight checks require controllability, body parameters, CCI position and
velocity, orientation, mass, propulsion limits, ground contact, and an initial
available thrust-to-weight ratio of at least 1.1. Unknown values are errors;
there are no substitute body parameters or invented engine limits. A reported
unavailable engine restart is rejected. Unknown restart counts still require
the user to prepare a suitable restartable engine.

During a connected failure, the helper attempts to confirm an engine-off,
zero-throttle command before releasing the lease. If confirmation fails, it
preserves the original error and adds a note identifying the unconfirmed cutoff.
Disconnects rely on the existing lease/dead-man release and cannot guarantee an
engine-off command was applied. User/manual inputs take over after release.

Automated coverage includes orbital-element math, complete idealized Earth-scale
ascent/coast/insertion with drag and mass flow, pointing lag, and an entire
trajectory through the real Python runtime and authenticated loopback WebSocket.
It also covers weak rockets, unavailable dynamics/restarts, clock/vehicle changes,
high warp, fuel failure, and applied-cutoff confirmation. These model checks
do not represent actual KSA physics, a validated Starship flight, or a live smoke
run. The first real flight must establish compatibility, vehicle tuning,
staging behavior, and the native flight-computer response.

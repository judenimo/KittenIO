# Writing KSA scripts in Python

Use ordinary Python for variables, loops, functions, and calculations. A small
`mission(ksa)` function is enough to read telemetry and run a sequence of flight
tasks. KittenIO supplies the connection, vehicle handles, waits, and control
commands.

Wait calls handle telemetry and connection events automatically. Write the
mission as a normal sequence of calls; no `yield` or `async` is required.

## Start with telemetry

Install and pair the mod as described in the [README](../README.md), launch KSA
through StarMap, and load a flight. Then, from Git Bash:

```bash
kittenio doctor
kittenio new mission.py
kittenio mission.py
```

From a source checkout, substitute `./scripts/kittenio` for `kittenio`. This
uses the current source and its locked Python environment. A previously
installed release will not acquire these development features until updated.

`new` creates a telemetry script that prints ten samples and exits. It creates
parent directories as needed and refuses to overwrite an existing file. Edit
the file, rerun it, and stop it at any time with Ctrl+C.

The runner waits up to 15 real seconds for the first flight telemetry. Change
that deadline when loading a slow save:

```bash
kittenio mission.py --connect-timeout 60
```

Python runs the function after the first valid telemetry frame. A normal
function can read once and return or use KittenIO wait calls for a longer
sequence. The mission stops when the function returns.

## Read a fresh state

```python
from kittenio import KsaApi


def mission(ksa: KsaApi):
    while True:
        state = ksa.next_tick()
        if state.vehicle is not None:
            print(state.player_time_s, state.vehicle.name)
            print("Radar altitude, metres:", state.vehicle.radar_altitude_m)
            print("Surface speed, m/s:", state.vehicle.surface_speed_mps)
```

Each `FlightState` is an immutable snapshot. Wait calls return the snapshot
that completed the wait. You can also read `ksa.data` for the latest snapshot.
Missing telemetry fields are `None`; do not replace
missing altitude, velocity, or orientation with an invented zero.

`state.vehicle` is a snapshot. `ksa.active_vehicle` is the session-bound handle
used for control. Keep that handle when controlling a particular vehicle so a
later selection change cannot silently redirect your commands to another craft.

## Wait with ordinary Python calls

| Expression | Resume when |
| --- | --- |
| `ksa.next_tick()` | A distinct telemetry frame arrives |
| `ksa.wait(2)` | KSA's simulation clock advances two seconds |
| `ksa.wait_real_seconds(2)` | A telemetry frame arrives after two real seconds |
| `ksa.wait_until(predicate, timeout=30)` | The predicate becomes true, or a real-time timeout raises `WaitTimeoutError` |

These methods are available on `ksa` without extra imports. Simulation waits
pause with the game and advance faster under time warp.
Real-time waits still resume on a telemetry frame. A predicate timeout starts
when the call begins and continues through pauses and telemetry gaps.

Catch `WaitTimeoutError` around the call to recover, or let it end the mission.
A timeout handler may run during a telemetry gap; inspect `ksa.state_age_s`
before making another decision from the last snapshot.

For example, wait for radar altitude to become available and exceed a threshold:

```python
from kittenio import WaitTimeoutError


def mission(ksa):
    try:
        ksa.wait_until(
            lambda state: (
                state.vehicle is not None
                and state.vehicle.radar_altitude_m is not None
                and state.vehicle.radar_altitude_m >= 1000
            ),
            timeout=120,
            description="radar altitude to reach 1,000 metres",
        )
        print("Altitude reached.")
    except WaitTimeoutError:
        print("Altitude was not reached within two real minutes.")
```

This snippet observes a condition; it does not launch the vehicle. Keep loop
bodies and predicates short. Use KittenIO waits instead of `time.sleep()` or a
busy loop. A stalled mission can lose its control lease.

## Hold the current direction and change throttle

Control requires the advertised capability and an explicit vehicle lease.
This short burn holds the vehicle's current thrust direction, requests 20%
throttle, then commands zero throttle after two simulation seconds:

```python
from kittenio import UnsupportedCapabilityError


def mission(ksa):
    ksa.wait_until(
        lambda state: state.vehicle is not None and state.vehicle.controllable,
        timeout=30,
        description="a controllable active vehicle",
    )
    capabilities = ksa.capabilities.control
    if not (capabilities.thrust_vector_target and capabilities.exact_throttle and capabilities.engine_state):
        raise UnsupportedCapabilityError("This vehicle needs direction, throttle, and engine control.")

    vehicle = ksa.active_vehicle
    if vehicle is None:
        raise RuntimeError("The active vehicle changed; select the intended craft and rerun.")

    with vehicle.control.leased() as control:
        control.hold_current_direction(engine_on=True, throttle=0.2)
        ksa.wait(2)
        control.set_throttle(0.0)
        ksa.wait(0.2)
```

`hold_current_direction` captures the vehicle's +X thrust direction in inertial
CCI space. It initializes the complete control frame; later `set_throttle` or
`set_engine` calls update that frame. It does not hold roll or follow the local
horizon. To supply a computed unit direction, use `set_thrust_vector` instead.

The lease is released when the `with` block exits, including on an exception,
Ctrl+C, or disconnect. Release returns control to KSA/player inputs; it is not
an engine-shutdown command. The snippet's zero-throttle request happens only
when the sequence reaches that line. A game pause also pauses the burn's
simulation wait. KSA's native limitations still apply, including controls at
high warp rates.

## Stage, target, and time warp

Inside an acquired vehicle lease, use `control.stage()` or
`control.set_target(target_id)` after checking `capabilities.control.stage` or
`capabilities.control.set_target`. These return a `CommandResult`. Check
`result.ok` before proceeding; a rejected command carries `result.error.code`
and `result.error.message`. A command ID prevents duplicate execution when
retrying an uncertain request within the same session. Do not retry a rejected
stage blindly.

Time warp is available through `ksa.time_warp`: inspect `available_rates`, then
use `set_rate`, `increase`, `decrease`, or `cancel`. Check
`ksa.capabilities.time_warp.control` first. Warp is game-global and persistent;
ending the script does not reset it. See [time-warp.md](time-warp.md).

## Split scripts into functions and files

Put ordinary helpers next to the mission file and import them normally. File
missions can import their siblings even when launched from another directory.
Pass `ksa` to helper functions and call them normally:

```python
def countdown(ksa):
    for remaining in (3, 2, 1):
        print(remaining)
        ksa.wait(1)


def mission(ksa):
    countdown(ksa)
    print("Countdown complete.")
```

Run a named function with `kittenio mission.py --program descent`. Importable
Python modules work too: `kittenio my_package.mission`. For a standard Python
entry point, call `run(mission)` inside `if __name__ == "__main__":`.

The CLI discovers a local function named `mission` or a local `KittenProgram`
subclass. A class named `Program` retains precedence for existing scripts.
Other ambiguous definitions require `--program NAME`; imported helpers are not
automatically selected as the mission.

## Larger missions and connection loss

For an experimental launch example with a 200 km target, see
[orbital-launch.md](orbital-launch.md). Generate it with
`kittenio new orbit.py --template orbit`; it uses the ordinary-call API and
requires a prepared engine stage and a compatible bridge.

Existing `KittenProgram` subclasses provide `on_start`, `on_tick`,
`on_vehicle_changed`, `on_disconnect`, and `on_stop` callbacks, plus a generator
`script` method. Use them when a callback-driven controller is clearer than a
sequence. Their generator `script()` methods can still yield the imported
`next_tick`, `wait_sim_seconds`, `wait_real_seconds`, and `wait_until` conditions.
The direct `ksa` wait methods are for function missions and their ordinary
helpers. Callback methods must stay short; direct waits from class callbacks,
another thread, or inside a wait predicate are rejected to avoid reentrant
mission execution. All user code runs on the mission thread.

A KittenIO wait raises `MissionInterruptedError` if the authenticated game
connection or runtime stops. Ordinary Python unwinding runs `finally` blocks
and exits lease contexts. Generator missions are also closed during cleanup.
Reconnecting never restarts a function mission. Inspect
the game, run `kittenio doctor`, and explicitly rerun the script when ready.
Existing class programs retain their session-restart lifecycle; their leases
and handles still become invalid on disconnect.

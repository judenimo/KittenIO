# Powered-descent MPC and OpenSCvx architecture

KittenIO supplies state, authority, safety boundaries, recording, and worker
plumbing. It does not supply a flight-qualified KSA lander model. OpenSCvx is a
general successive-convexification framework; users must formulate and validate
their own dynamics, scaling, trust regions, discretization, constraints, and
abort logic.

The optional SDK group pins `openscvx==0.5.2`, the stable PyPI release published
for Python 3 in May 2026. Its upstream package currently requires Python 3.11+
and brings CVXPY, QOCO, NumPy, JAX and related numerical dependencies. Those
JAX, CVXPY, and solver packages are intentionally absent from ordinary KittenIO
installs; NumPy is a core dependency. See the
[OpenSCvx 0.5.2 documentation](https://openscvx.github.io/OpenSCvx/v0.5.2/),
[PyPI release](https://pypi.org/project/openscvx/0.5.2/), and
[upstream package metadata](https://raw.githubusercontent.com/OpenSCvx/OpenSCvx/main/pyproject.toml).

Install only in a research environment:

```bash
uv sync --extra research --group dev
```

Recommended scheduling:

```text
KSA game thread (50–60 Hz)
  capture immutable state -> capacity-one socket telemetry
                                      |
Python mission/control loop (50–60 Hz)
  validate state/authority -> capacity-one latest optimizer input
  refresh only a previously validated plan -> complete control frame
                                      |
optimizer worker (approximately 2–5 Hz)
  warm compile before lease -> solve newest state only
  capacity-one latest plan -> feasibility, solve age, source-state age
```

Never compile JAX or call an optimizer from a KittenProgram callback that must
refresh a lease. `LatestOptimizerWorker` overwrites queued obsolete inputs and
keeps only the latest plan. Call `start()` and finish warmup before acquiring
control. A plan is usable only when it is feasible, has no error, its Python
monotonic age is within the solve deadline, and its source simulation-time age
is within the tracking horizon. There is no backlog to “catch up”.

The 3-DoF model should use ENU position/velocity/mass and thrust vector control,
with authoritative local gravity, dry mass, minimum/maximum thrust, tilt, glide
slope, and terminal position/velocity constraints. Atmospheric work may use
KSA density/dynamic pressure and air-relative velocity, but lift/drag
coefficients are not authoritative on the pinned build and must be separately
identified and labelled estimates.

The 6-DoF model adds normalized body-to-CCI quaternion, body rates, current
inertia, centre of mass, engine locations, gimbal angles/limits, moments, and
attitude/rate/touchdown constraints. This KSA build does not expose gimbal-rate
limits, positive/negative torque authority, aerodynamic/RCS moments, or direct
gimbal control. A 6-DoF controller must therefore fail closed instead of
substituting invented zeros or generic actuator constants.

For every live controller, verify the exact active vehicle and capabilities,
an explicitly reviewed simulation-rate envelope, fresh state, fresh feasible
plan, continuing lease, engine/propellant health, and controllability on every
update. Non-1x rates are supported, but KSA locks active vehicle controls above
30x. Never stage or switch vehicles implicitly. Log state transitions and abort
reasons. In a `finally` block, clear overrides and release the lease. The 750 ms
dead-man only hands control back to the player; it is not a safe landing
trajectory.

Develop in this order: offline plant tests, replay, telemetry-only live logging,
dry-run live plans, non-igniting lease/authority validation, then any powered
test only with separate explicit authorization and a reviewed vehicle-specific
abort plan.

# Coordinate frames and quaternion conventions

KittenIO never treats frame labels as interchangeable. All vectors are
right-handed KSA-native values and are serialized in `{x,y,z}` order. Angles
are radians and all dimensional values are SI.

| Suffix | Frame |
| --- | --- |
| `cci` | Celestial-centred inertial. The authoritative inertial dynamics frame. |
| `cce` | Celestial-centred equatorial. It rotates with the parent-body convention provided by KSA. |
| `ccf` | Celestial-centred body-fixed. Landing sites are stored here so they rotate with the surface. |
| `body` | Vehicle body axes. KSA/KittenIO use `+X` as the craft forward/thrust pointing axis. |
| `vehicle_assembly` | KSA part-tree assembly coordinates; not interchangeable with body coordinates. |
| `enu` | Landing-site tangent frame ordered `(east,north,up)`. `x=east`, `y=north`, `z=up`. |

The landing reference is evaluated from one game-thread snapshot at
`simulation_time_s`. Its site CCF position is rotated by KSA's current
`GetCcf2Cci`/`GetCcf2Cce`. The site inertial velocity is
`parent_angular_velocity_cci × site_position_cci`. Vehicle position and
velocity relative to that moving site are inverse-rotated through KSA's ENU
quaternion. Do not subtract a CCE position from a CCI position or combine an
ENU velocity from one timestamp with a basis from another.

Quaternions use `{x,y,z,w}`. `body_to_cci` actively rotates a body-frame vector
into CCI. Python's `rotate_vector(v,q)` computes `q (v,0) q*`; the inverse helper
uses the normalized conjugate. Incoming body-to-CCI quaternions are rejected
unless normalized within strict tolerance. `normalize_quaternion` canonicalizes
the double-cover sign into a stable hemisphere (`w >= 0`, with a lexicographic
tie-break when `w == 0`) so logs and optimizers do not see artificial `q/-q`
jumps.

The symmetric body inertia wire order is `xx,xy,xz,yy,yz,zz`. KSA stores the
off-diagonal entries as `YX,ZX,ZY`; symmetry maps those to `xy,xz,yz`.
Centre-of-mass and inertia values are recaptured every telemetry frame and can
therefore change with propellant depletion, staging, or vehicle reconfiguration.
The total body force is the same-frame KSA total mass multiplied by KSA's body
acceleration; the CCI value rotates that result through `body_to_cci`. Total
body moment follows Euler's rigid-body relation `I α + ω × (I ω)` using the
same-frame symmetric inertia, body rates, and angular acceleration. These
derived totals include all physics effects but do not pretend to separate
aerodynamic or RCS loads; those decomposition fields remain `None` here.
`propulsive_moment_body_nm` is summed about the current centre of mass as
`(nozzle_position_body - center_of_mass_body) × nozzle_force_body`.

Ground normal and surface slope are available only when KSA reports a contact
normal. `surface_slope_rad = acos(dot(normal_enu, up_enu))`. KittenIO never
invents a flat normal. Test-only dry-run atmosphere helpers are explicitly
approximations and are never mixed into live state.

Use `project_onto_enu`, `rotate_vector`, `inverse_rotate_vector`,
`normalize_vector`, and `normalize_quaternion` for deterministic core-only
math. `as_numpy` imports NumPy on demand and raises a clear error if the
optional dependency is absent.

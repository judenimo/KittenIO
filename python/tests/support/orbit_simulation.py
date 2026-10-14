"""Idealized Earth-scale plants for tests, not KSA physics or flight assets."""

from __future__ import annotations

import math
from dataclasses import replace

from kittenio import Quaternion, Vector3

MU = 3.986004418e14
RADIUS = 6_371_000.0
OMEGA = 7.292115e-5


def prepared_state(flight_state):
    vehicle = replace(
        flight_state.vehicle,
        position_cci_m=Vector3(RADIUS + 10, 0, 0),
        velocity_cci_mps=Vector3(0, OMEGA * (RADIUS + 10), 0),
        surface_velocity_cci_mps=Vector3(0, 0, 0),
        body_to_cci=Quaternion(0, 0, 0, 1),
        total_mass_kg=100_000.0,
        parent_body=replace(flight_state.vehicle.parent_body, mean_radius_m=RADIUS, gravitational_parameter_m3ps2=MU),
    )
    propulsion = replace(
        flight_state.propulsion,
        maximum_available_thrust_n=2_000_000.0,
        minimum_throttle=0.05,
        engines=(),
    )
    return replace(
        flight_state,
        vehicle=vehicle,
        propulsion=propulsion,
        surface=replace(flight_state.surface, ground_contact=True, landed=True),
    )


class IdealLaunchPlant:
    """Point-mass gravity, exponential drag, mass flow, and optional pointing lag."""

    def __init__(self, state, *, attitude_lag_s=0.0, maximum_thrust_n=2_000_000.0):
        self.template = state
        self.position = [state.vehicle.position_cci_m.x, 0.0]
        self.velocity = [0.0, state.vehicle.velocity_cci_mps.y]
        self.mass = state.vehicle.total_mass_kg
        self.dry_mass = 4_000.0
        self.time = state.player_time_s
        self.frame = state.frame_number
        self.angle = 0.0
        self.attitude_lag_s = attitude_lag_s
        self.maximum_thrust_n = maximum_thrust_n

    def state(self):
        radius = math.hypot(*self.position)
        position = Vector3(*self.position, 0)
        velocity = Vector3(*self.velocity, 0)
        rotation_velocity = (-OMEGA * self.position[1], OMEGA * self.position[0])
        vehicle = replace(
            self.template.vehicle,
            position_cci_m=position,
            velocity_cci_mps=velocity,
            surface_velocity_cci_mps=Vector3(
                self.velocity[0] - rotation_velocity[0],
                self.velocity[1] - rotation_velocity[1],
                0,
            ),
            body_to_cci=Quaternion(0, 0, math.sin(self.angle / 2), math.cos(self.angle / 2)),
            total_mass_kg=self.mass,
        )
        propulsion = replace(
            self.template.propulsion,
            maximum_available_thrust_n=self.maximum_thrust_n if self.mass > self.dry_mass else 0,
        )
        return replace(
            self.template,
            vehicle=vehicle,
            propulsion=propulsion,
            player_time_s=self.time,
            frame_number=self.frame,
            surface=replace(self.template.surface, landed=radius < RADIUS + 20, ground_contact=radius < RADIUS + 20),
        )

    def step(self, command, dt=0.2):
        desired = math.atan2(command.direction.y, command.direction.x)
        error = (desired - self.angle + math.pi) % math.tau - math.pi
        self.angle += error if self.attitude_lag_s == 0 else error * min(1, dt / self.attitude_lag_s)
        radius = math.hypot(*self.position)
        altitude = radius - RADIUS
        air = [self.velocity[0] + OMEGA * self.position[1], self.velocity[1] - OMEGA * self.position[0]]
        airspeed = math.hypot(*air)
        density = 1.225 * math.exp(-max(0, altitude) / 8500)
        drag_factor = 0.5 * density * 5.0 * airspeed / self.mass
        thrust = command.throttle * self.maximum_thrust_n if command.engine_on and self.mass > self.dry_mass else 0.0
        force_direction = (math.cos(self.angle), math.sin(self.angle))
        acceleration = [
            -MU * self.position[i] / radius**3 + thrust * force_direction[i] / self.mass - drag_factor * air[i]
            for i in range(2)
        ]
        # Second-order position step; velocity uses the current acceleration.
        for i in range(2):
            self.position[i] += self.velocity[i] * dt + 0.5 * acceleration[i] * dt**2
            self.velocity[i] += acceleration[i] * dt
        self.mass = max(self.dry_mass, self.mass - thrust / (450 * 9.80665) * dt)
        self.time += dt
        self.frame += 1

"""Dependency-free approximation plants used only by offline tests.

These are explicit test fixtures, not models of KSA physics.
"""

from __future__ import annotations

import math
from dataclasses import dataclass


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def exponential_atmosphere(
    altitude_m: float,
    *,
    sea_level_density_kgpm3: float = 1.225,
    sea_level_pressure_pa: float = 101_325.0,
    scale_height_m: float = 8_500.0,
) -> tuple[float, float]:
    if scale_height_m <= 0:
        raise ValueError("scale_height_m must be positive")
    ratio = math.exp(-max(0.0, altitude_m) / scale_height_m)
    return sea_level_density_kgpm3 * ratio, sea_level_pressure_pa * ratio


@dataclass(slots=True)
class VerticalPlant:
    altitude_m: float
    velocity_mps: float
    mass_kg: float
    dry_mass_kg: float
    maximum_thrust_n: float
    gravity_mps2: float = 9.80665
    isp_s: float = 285.0
    engine_available: bool = True

    def step(self, throttle: float, dt_s: float) -> float:
        throttle = clamp(throttle, 0.0, 1.0)
        thrust = (
            throttle * self.maximum_thrust_n
            if self.engine_available and self.mass_kg > self.dry_mass_kg
            else 0.0
        )
        acceleration = thrust / self.mass_kg - self.gravity_mps2
        self.velocity_mps += acceleration * dt_s
        self.altitude_m = max(0.0, self.altitude_m + self.velocity_mps * dt_s)
        if thrust > 0:
            self.mass_kg = max(
                self.dry_mass_kg,
                self.mass_kg - thrust / (self.isp_s * 9.80665) * dt_s,
            )
        return thrust


def vertical_throttle(
    altitude_m: float,
    velocity_mps: float,
    mass_kg: float,
    maximum_thrust_n: float,
    gravity_mps2: float,
) -> float:
    if mass_kg <= 0 or maximum_thrust_n <= 0:
        raise ValueError("positive mass and thrust authority are required")
    target_velocity = -min(10.0, max(0.5, 0.5 * math.sqrt(max(0.0, altitude_m))))
    desired_acceleration = clamp(2.0 * (target_velocity - velocity_mps), -3.0, 8.0)
    return clamp(mass_kg * (gravity_mps2 + desired_acceleration) / maximum_thrust_n, 0.0, 1.0)


@dataclass(slots=True)
class TranslationalPlant:
    position_m: list[float]
    velocity_mps: list[float]
    mass_kg: float
    maximum_thrust_n: float
    gravity_mps2: float = 9.80665

    def step(self, thrust_n: tuple[float, float, float], dt_s: float) -> None:
        acceleration = [
            thrust_n[0] / self.mass_kg,
            thrust_n[1] / self.mass_kg,
            thrust_n[2] / self.mass_kg - self.gravity_mps2,
        ]
        for axis in range(3):
            self.velocity_mps[axis] += acceleration[axis] * dt_s
            self.position_m[axis] += self.velocity_mps[axis] * dt_s


def three_dof_guidance(
    position_m: list[float],
    velocity_mps: list[float],
    mass_kg: float,
    maximum_thrust_n: float,
    gravity_mps2: float = 9.80665,
    tilt_limit_rad: float = math.radians(30.0),
) -> tuple[float, float, float]:
    acceleration = [
        -0.35 * position_m[0] - 0.9 * velocity_mps[0],
        -0.35 * position_m[1] - 0.9 * velocity_mps[1],
        -0.18 * position_m[2] - 0.9 * velocity_mps[2] + gravity_mps2,
    ]
    horizontal = math.hypot(acceleration[0], acceleration[1])
    max_horizontal = max(0.0, acceleration[2]) * math.tan(tilt_limit_rad)
    if horizontal > max_horizontal > 0:
        scale = max_horizontal / horizontal
        acceleration[0] *= scale
        acceleration[1] *= scale
    force = [value * mass_kg for value in acceleration]
    magnitude = math.sqrt(sum(value * value for value in force))
    if magnitude > maximum_thrust_n:
        force = [value * maximum_thrust_n / magnitude for value in force]
    return force[0], force[1], force[2]


@dataclass(slots=True)
class AttitudePlant:
    angle_rad: list[float]
    rate_radps: list[float]
    inertia_kg_m2: tuple[float, float, float]

    def step(self, torque_nm: tuple[float, float, float], dt_s: float) -> None:
        for axis in range(3):
            self.rate_radps[axis] += torque_nm[axis] / self.inertia_kg_m2[axis] * dt_s
            self.angle_rad[axis] += self.rate_radps[axis] * dt_s


def attitude_torque(
    angle_rad: list[float],
    rate_radps: list[float],
    limit_nm: tuple[float, float, float],
) -> tuple[float, float, float]:
    return tuple(
        clamp(-4.0 * angle_rad[index] - 2.5 * rate_radps[index], -limit_nm[index], limit_nm[index])
        for index in range(3)
    )

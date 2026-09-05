from __future__ import annotations

import math
import sys
from pathlib import Path

import pytest

SUPPORT = Path(__file__).resolve().parents[1] / "support"
sys.path.insert(0, str(SUPPORT))

from landing_simulation import (  # noqa: E402
    AttitudePlant,
    TranslationalPlant,
    VerticalPlant,
    attitude_torque,
    exponential_atmosphere,
    three_dof_guidance,
    vertical_throttle,
)


def test_simulated_1d_closed_loop_soft_landing() -> None:
    plant = VerticalPlant(altitude_m=120.0, velocity_mps=-8.0, mass_kg=1_000.0,
                          dry_mass_kg=700.0, maximum_thrust_n=20_000.0)
    for _ in range(4_000):
        throttle = vertical_throttle(plant.altitude_m, plant.velocity_mps, plant.mass_kg,
                                     plant.maximum_thrust_n, plant.gravity_mps2)
        plant.step(throttle, 0.02)
        if plant.altitude_m <= 0:
            break
    assert plant.altitude_m == 0
    assert abs(plant.velocity_mps) < 1.5
    assert plant.mass_kg >= plant.dry_mass_kg


def test_1d_engine_saturation_and_failure_are_not_hidden() -> None:
    assert vertical_throttle(100.0, -100.0, 1_000.0, 1_000.0, 9.8) == 1.0
    plant = VerticalPlant(10.0, -1.0, 1_000.0, 700.0, 20_000.0, engine_available=False)
    thrust = plant.step(1.0, 0.1)
    assert thrust == 0.0
    assert plant.velocity_mps < -1.0


def test_simulated_3d_closed_loop_reaches_terminal_neighborhood() -> None:
    plant = TranslationalPlant([30.0, -20.0, 120.0], [-2.0, 1.0, -8.0], 1_000.0, 20_000.0)
    for _ in range(2_000):
        force = three_dof_guidance(plant.position_m, plant.velocity_mps, plant.mass_kg,
                                   plant.maximum_thrust_n, plant.gravity_mps2)
        plant.step(force, 0.02)
    assert math.sqrt(sum(value * value for value in plant.position_m)) < 1.0
    assert math.sqrt(sum(value * value for value in plant.velocity_mps)) < 1.0


def test_simulated_6d_attitude_loop_respects_torque_limits() -> None:
    plant = AttitudePlant([0.4, -0.3, 0.2], [0.0, 0.0, 0.0], (1.0, 2.0, 3.0))
    limits = (0.5, 0.5, 0.5)
    for _ in range(3_000):
        torque = attitude_torque(plant.angle_rad, plant.rate_radps, limits)
        assert all(abs(torque[i]) <= limits[i] for i in range(3))
        plant.step(torque, 0.01)
    assert max(abs(value) for value in plant.angle_rad) < 0.01
    assert max(abs(value) for value in plant.rate_radps) < 0.01


def test_atmosphere_approximation_is_monotonic_and_explicit() -> None:
    sea_density, sea_pressure = exponential_atmosphere(0.0)
    high_density, high_pressure = exponential_atmosphere(10_000.0)
    assert sea_density == pytest.approx(1.225)
    assert sea_pressure == pytest.approx(101_325.0)
    assert 0 < high_density < sea_density and 0 < high_pressure < sea_pressure
    with pytest.raises(ValueError):
        exponential_atmosphere(0.0, scale_height_m=0.0)

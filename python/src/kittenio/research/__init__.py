"""Optional recording, identification, transforms, and optimisation helpers."""

from .identification import (
    acceleration_prediction_errors_mps2,
    command_application_delays_s,
    estimate_effective_isp_s,
    estimate_effective_mass_flow_kgps,
    estimate_ignition_delays_s,
    estimate_spool_time_constants_s,
    estimate_throttle_to_thrust,
    validate_coordinate_conventions,
)
from .optimizer import LatestOptimizerWorker, OptimizerInput, SolverPlan
from .recording import TelemetryRecorder, TelemetryReplay, export_csv
from .transforms import (
    as_numpy,
    inverse_rotate_vector,
    normalize_quaternion,
    normalize_vector,
    project_onto_enu,
    rotate_vector,
)

__all__ = [
    "LatestOptimizerWorker",
    "OptimizerInput",
    "SolverPlan",
    "TelemetryRecorder",
    "TelemetryReplay",
    "acceleration_prediction_errors_mps2",
    "as_numpy",
    "command_application_delays_s",
    "estimate_effective_isp_s",
    "estimate_effective_mass_flow_kgps",
    "estimate_ignition_delays_s",
    "estimate_spool_time_constants_s",
    "estimate_throttle_to_thrust",
    "export_csv",
    "inverse_rotate_vector",
    "normalize_quaternion",
    "normalize_vector",
    "project_onto_enu",
    "rotate_vector",
    "validate_coordinate_conventions",
]

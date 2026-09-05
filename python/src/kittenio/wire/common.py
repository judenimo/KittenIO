"""Frozen public models and strict telemetry decoding."""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from enum import Enum
from types import MappingProxyType
from typing import AbstractSet, Any, Mapping, TypeVar


class ModelError(ValueError):
    """Raised when a protocol payload cannot be represented safely."""


class AxisCommand(str, Enum):
    """The native ternary resolution of KSA manual directional controls."""

    NEGATIVE = "negative"
    NEUTRAL = "neutral"
    POSITIVE = "positive"


T = TypeVar("T")

_VECTOR_FIELDS = frozenset({"x", "y", "z"})
_QUATERNION_FIELDS = frozenset({"x", "y", "z", "w"})
_ORBIT_FIELD_NAMES = (
    "eccentricity",
    "semi_major_axis_m",
    "inclination_rad",
    "longitude_ascending_node_rad",
    "argument_periapsis_rad",
    "periapsis_m",
    "apoapsis_m",
    "period_s",
)
_ORBIT_FIELDS = frozenset(_ORBIT_FIELD_NAMES)
_TARGET_FIELDS = frozenset({"id", "name", "kind", "position_cci_m", "velocity_cci_mps", "orbit"})
_CONTROL_AXIS_FIELDS = (
    "pitch",
    "yaw",
    "roll",
    "translate_forward",
    "translate_right",
    "translate_up",
)
_MANUAL_CONTROL_FIELDS = frozenset({"engine_on", "throttle", *_CONTROL_AXIS_FIELDS})
_CATALOG_FIELDS = frozenset({"bodies", "vehicles"})
_CATALOG_ENTRY_FIELDS = frozenset({"id", "name", "kind"})


def _mapping(value: object, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise ModelError(f"{name} must be an object")
    if any(not isinstance(key, str) for key in value):
        raise ModelError(f"{name} contains a non-string key")
    return value


def _no_unknown(data: Mapping[str, Any], allowed: AbstractSet[str], name: str) -> None:
    unknown = set(data) - allowed
    if unknown:
        raise ModelError(f"{name} has unknown properties: {', '.join(sorted(unknown))}")


def _number(value: object, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ModelError(f"{name} must be numeric")
    try:
        result = float(value)
    except OverflowError as exc:
        raise ModelError(f"{name} must be finite") from exc
    if not math.isfinite(result):
        raise ModelError(f"{name} must be finite")
    return result


def _optional_number(value: object, name: str) -> float | None:
    return None if value is None else _number(value, name)


def _optional_nonnegative_number(value: object, name: str) -> float | None:
    result = _optional_number(value, name)
    if result is not None and result < 0:
        raise ModelError(f"{name} must be non-negative")
    return result


def _optional_str(value: object, name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise ModelError(f"{name} must be a string or null")
    return value


def _optional_axis(value: object, name: str) -> AxisCommand | None:
    if value is None:
        return None
    try:
        return AxisCommand(value)
    except (TypeError, ValueError) as exc:
        raise ModelError(f"{name} must be negative, neutral, positive, or null") from exc


def _boolean(value: object, name: str) -> bool:
    if not isinstance(value, bool):
        raise ModelError(f"{name} must be a boolean")
    return value


@dataclass(frozen=True, slots=True)
class Vector3:
    x: float
    y: float
    z: float

    @classmethod
    def from_dict(cls, value: object, name: str = "vector") -> "Vector3":
        data = _mapping(value, name)
        _no_unknown(data, _VECTOR_FIELDS, name)
        return cls(*(_number(data.get(axis), f"{name}.{axis}") for axis in ("x", "y", "z")))


@dataclass(frozen=True, slots=True)
class Quaternion:
    x: float
    y: float
    z: float
    w: float

    @classmethod
    def from_dict(cls, value: object, name: str = "quaternion") -> "Quaternion":
        data = _mapping(value, name)
        _no_unknown(data, _QUATERNION_FIELDS, name)
        return cls(*(_number(data.get(axis), f"{name}.{axis}") for axis in ("x", "y", "z", "w")))


@dataclass(frozen=True, slots=True)
class OrbitState:
    eccentricity: float | None = None
    semi_major_axis_m: float | None = None
    inclination_rad: float | None = None
    longitude_ascending_node_rad: float | None = None
    argument_periapsis_rad: float | None = None
    periapsis_m: float | None = None
    apoapsis_m: float | None = None
    period_s: float | None = None

    @classmethod
    def from_dict(cls, value: object) -> "OrbitState":
        data = _mapping(value, "orbit")
        _no_unknown(data, _ORBIT_FIELDS, "orbit")
        return cls(**{name: _optional_number(data.get(name), f"orbit.{name}") for name in _ORBIT_FIELD_NAMES})


@dataclass(frozen=True, slots=True)
class TargetState:
    id: str
    name: str
    kind: str
    position_cci_m: Vector3 | None = None
    velocity_cci_mps: Vector3 | None = None
    orbit: OrbitState | None = None

    @classmethod
    def from_dict(cls, value: object) -> "TargetState":
        data = _mapping(value, "target")
        _no_unknown(data, _TARGET_FIELDS, "target")
        for name in ("id", "name", "kind"):
            if not isinstance(data.get(name), str) or not data[name]:
                raise ModelError(f"target.{name} must be a non-empty string")
        return cls(
            id=data["id"],
            name=data["name"],
            kind=data["kind"],
            position_cci_m=None
            if data.get("position_cci_m") is None
            else Vector3.from_dict(data["position_cci_m"], "target.position_cci_m"),
            velocity_cci_mps=None
            if data.get("velocity_cci_mps") is None
            else Vector3.from_dict(data["velocity_cci_mps"], "target.velocity_cci_mps"),
            orbit=None if data.get("orbit") is None else OrbitState.from_dict(data["orbit"]),
        )


@dataclass(frozen=True, slots=True)
class ManualControlState:
    engine_on: bool | None = None
    throttle: float | None = None
    pitch: AxisCommand | None = None
    yaw: AxisCommand | None = None
    roll: AxisCommand | None = None
    translate_forward: AxisCommand | None = None
    translate_right: AxisCommand | None = None
    translate_up: AxisCommand | None = None

    @classmethod
    def from_dict(cls, value: object, name: str = "manual_control") -> "ManualControlState":
        data = _mapping(value, name)
        _no_unknown(data, _MANUAL_CONTROL_FIELDS, name)
        engine_on = data.get("engine_on")
        if engine_on is not None:
            engine_on = _boolean(engine_on, f"{name}.engine_on")
        throttle = _optional_number(data.get("throttle"), f"{name}.throttle")
        if throttle is not None and not 0.0 <= throttle <= 1.0:
            raise ModelError(f"{name}.throttle must be in [0, 1]")
        return cls(
            engine_on=engine_on,
            throttle=throttle,
            **{axis: _optional_axis(data.get(axis), f"{name}.{axis}") for axis in _CONTROL_AXIS_FIELDS},
        )


def _nonnegative_int(value: object, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ModelError(f"{name} must be a non-negative integer")
    return value


@dataclass(frozen=True, slots=True)
class BodyInfo:
    id: str
    name: str
    kind: str


@dataclass(frozen=True, slots=True)
class VehicleInfo:
    id: str
    name: str
    kind: str


@dataclass(frozen=True, slots=True)
class Catalog:
    bodies: tuple[BodyInfo, ...] = ()
    vehicles: tuple[VehicleInfo, ...] = ()

    @classmethod
    def from_payload(cls, value: object) -> "Catalog":
        data = _mapping(value, "catalog")
        _no_unknown(data, _CATALOG_FIELDS, "catalog")

        def decode(items: object, item_type: type[T], name: str) -> tuple[T, ...]:
            if not isinstance(items, list):
                raise ModelError(f"catalog.{name} must be an array")
            result = []
            seen_ids: set[str] = set()
            for index, item in enumerate(items):
                entry = _mapping(item, f"catalog.{name}[{index}]")
                _no_unknown(entry, _CATALOG_ENTRY_FIELDS, f"catalog.{name}[{index}]")
                if any(not isinstance(entry.get(key), str) for key in ("id", "name", "kind")):
                    raise ModelError(f"catalog.{name}[{index}] requires string id, name, and kind")
                if entry["id"] in seen_ids:
                    raise ModelError(f"catalog.{name} contains duplicate id {entry['id']!r}")
                seen_ids.add(entry["id"])
                result.append(item_type(id=entry["id"], name=entry["name"], kind=entry["kind"]))
            return tuple(result)

        return cls(
            bodies=decode(data.get("bodies", []), BodyInfo, "bodies"),
            vehicles=decode(data.get("vehicles", []), VehicleInfo, "vehicles"),
        )


@dataclass(frozen=True, slots=True)
class ConnectionInfo:
    connected: bool
    game_instance_id: str = ""
    session_id: str = ""
    game_version: str = ""
    mod_version: str = ""
    starmap_version: str = ""
    protocol_version: int = 1


@dataclass(frozen=True, slots=True)
class CommandResult:
    ok: bool
    result: Mapping[str, Any] = field(default_factory=dict)
    error: "CommandError | None" = None

    def __post_init__(self) -> None:
        object.__setattr__(self, "result", MappingProxyType(dict(self.result)))


@dataclass(frozen=True, slots=True)
class CommandError:
    code: str
    message: str

"""Pure-Python frame and quaternion helpers; NumPy is imported only on demand."""

from __future__ import annotations

import math
from typing import Iterable

from ..wire.common import Quaternion, Vector3


def dot(a: Vector3, b: Vector3) -> float:
    return a.x * b.x + a.y * b.y + a.z * b.z


def cross(a: Vector3, b: Vector3) -> Vector3:
    return Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x)


def norm(value: Vector3) -> float:
    return math.sqrt(dot(value, value))


def normalize_vector(value: Vector3, *, tolerance: float = 1e-12) -> Vector3:
    length = norm(value)
    if not math.isfinite(length) or length <= tolerance:
        raise ValueError("cannot normalize a zero or non-finite vector")
    return Vector3(value.x / length, value.y / length, value.z / length)


def normalize_quaternion(value: Quaternion, *, tolerance: float = 1e-12) -> Quaternion:
    length = math.sqrt(value.x**2 + value.y**2 + value.z**2 + value.w**2)
    if not math.isfinite(length) or length <= tolerance:
        raise ValueError("cannot normalize a zero or non-finite quaternion")
    result = Quaternion(value.x / length, value.y / length, value.z / length, value.w / length)
    # q and -q encode the same rotation. A stable hemisphere prevents sign jumps in logs/optimizers.
    if result.w < 0 or (result.w == 0 and (result.x, result.y, result.z) < (0.0, 0.0, 0.0)):
        return Quaternion(-result.x, -result.y, -result.z, -result.w)
    return result


def quaternion_conjugate(value: Quaternion) -> Quaternion:
    return Quaternion(-value.x, -value.y, -value.z, value.w)


def quaternion_multiply(left: Quaternion, right: Quaternion) -> Quaternion:
    return Quaternion(
        left.w * right.x + left.x * right.w + left.y * right.z - left.z * right.y,
        left.w * right.y - left.x * right.z + left.y * right.w + left.z * right.x,
        left.w * right.z + left.x * right.y - left.y * right.x + left.z * right.w,
        left.w * right.w - left.x * right.x - left.y * right.y - left.z * right.z,
    )


def rotate_vector(value: Vector3, frame_to_parent: Quaternion) -> Vector3:
    q = normalize_quaternion(frame_to_parent)
    pure = Quaternion(value.x, value.y, value.z, 0.0)
    rotated = quaternion_multiply(quaternion_multiply(q, pure), quaternion_conjugate(q))
    return Vector3(rotated.x, rotated.y, rotated.z)


def inverse_rotate_vector(value: Vector3, frame_to_parent: Quaternion) -> Vector3:
    return rotate_vector(value, quaternion_conjugate(normalize_quaternion(frame_to_parent)))


def project_onto_enu(value_cci: Vector3, east_cci: Vector3, north_cci: Vector3, up_cci: Vector3) -> Vector3:
    """Project a CCI vector into ordered (east, north, up) components."""

    east, north, up = (normalize_vector(axis) for axis in (east_cci, north_cci, up_cci))
    return Vector3(dot(value_cci, east), dot(value_cci, north), dot(value_cci, up))


def vector_from_iterable(values: Iterable[float]) -> Vector3:
    x, y, z = values
    return Vector3(float(x), float(y), float(z))


def as_numpy(value: Vector3 | Quaternion):
    """Return a NumPy float64 array without making NumPy a core dependency."""

    try:
        import numpy as np
    except ImportError as exc:  # pragma: no cover - depends on optional environment
        raise RuntimeError("NumPy helpers require installing kittenio[research] or numpy") from exc
    if isinstance(value, Vector3):
        return np.asarray((value.x, value.y, value.z), dtype=np.float64)
    if isinstance(value, Quaternion):
        return np.asarray((value.x, value.y, value.z, value.w), dtype=np.float64)
    raise TypeError("value must be Vector3 or Quaternion")

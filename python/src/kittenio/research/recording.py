"""Non-blocking JSONL recording and typed canonical replay."""

from __future__ import annotations

import csv
import json
import queue
import threading
import time
from dataclasses import asdict
from pathlib import Path
from typing import Iterator

from ..wire.models import FlightState

FORMAT_NAME = "kittenio.telemetry.jsonl"
FORMAT_VERSION = 1


def _state_payload(state: FlightState) -> dict:
    """Convert the typed state back to the canonical live wire shape."""

    payload = asdict(state)
    feedback = payload["applied_control"]
    for field in ("latest_requested", "accepted", "applied"):
        command = feedback[field]
        if command is not None and command["thrust_vector_target"] is not None:
            command["thrust_vector_target"] = {"direction_cci": command["thrust_vector_target"]}
    return payload


class TelemetryRecorder:
    """Write canonical frames on a worker; ``submit`` never waits for disk I/O."""

    def __init__(self, path: str | Path, *, capacity: int = 1024) -> None:
        if capacity < 1:
            raise ValueError("capacity must be positive")
        self.path = Path(path)
        self._queue: queue.Queue[dict | None] = queue.Queue(maxsize=capacity)
        self._thread: threading.Thread | None = None
        self._error: BaseException | None = None
        self.dropped_frames = 0

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._write_loop, name="kittenio-recorder", daemon=True)
        self._thread.start()

    def submit(self, state: FlightState, *, received_monotonic_s: float | None = None) -> bool:
        if not isinstance(state, FlightState):
            raise TypeError("recording requires FlightState")
        if self._thread is None:
            self.start()
        if self._error is not None:
            raise RuntimeError("recording worker failed") from self._error
        record = {
            "kind": "telemetry",
            "protocol_version": 1,
            "received_monotonic_s": time.monotonic() if received_monotonic_s is None else float(received_monotonic_s),
            "payload": _state_payload(state),
        }
        try:
            self._queue.put_nowait(record)
            return True
        except queue.Full:
            self.dropped_frames += 1
            return False

    def close(self, timeout: float = 5.0) -> None:
        thread = self._thread
        if thread is None:
            return
        try:
            self._queue.put_nowait(None)
        except queue.Full:
            # Make shutdown bounded and explicit: discard one queued old frame, not the caller.
            self._queue.get_nowait()
            self.dropped_frames += 1
            self._queue.put_nowait(None)
        thread.join(timeout)
        if thread.is_alive():
            raise TimeoutError("recording worker did not stop before the deadline")
        if self._error is not None:
            raise RuntimeError("recording worker failed") from self._error

    def __enter__(self) -> "TelemetryRecorder":
        self.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def _write_loop(self) -> None:
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write(
                    json.dumps(
                        {"kind": "header", "format": FORMAT_NAME, "format_version": FORMAT_VERSION},
                        separators=(",", ":"),
                    )
                    + "\n"
                )
                while True:
                    record = self._queue.get()
                    if record is None:
                        return
                    stream.write(json.dumps(record, separators=(",", ":"), allow_nan=False) + "\n")
        except BaseException as exc:  # retained and raised by close/next submit
            self._error = exc


class TelemetryReplay:
    """Iterate recorded frames through the same strict model used live."""

    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)

    def __iter__(self) -> Iterator[FlightState]:
        with self.path.open("r", encoding="utf-8") as stream:
            first = stream.readline()
            if not first:
                raise ValueError("recording is empty")
            header = json.loads(first)
            if header != {"kind": "header", "format": FORMAT_NAME, "format_version": FORMAT_VERSION}:
                raise ValueError("unsupported KittenIO recording header")
            for line_number, line in enumerate(stream, 2):
                if not line.strip():
                    continue
                record = json.loads(line)
                if (
                    set(record) != {"kind", "protocol_version", "received_monotonic_s", "payload"}
                    or record["kind"] != "telemetry"
                    or record["protocol_version"] != 1
                ):
                    raise ValueError(f"invalid recording record at line {line_number}")
                yield FlightState.from_payload(record["payload"])


def export_csv(recording: str | Path, destination: str | Path) -> int:
    """Export the common landing-identification scalars; blanks mean unavailable."""

    fields = [
        "frame_number",
        "player_time_s",
        "radar_altitude_m",
        "vertical_velocity_mps",
        "total_mass_kg",
        "applied_throttle",
        "current_thrust_n",
        "mass_flow_rate_kgps",
    ]
    count = 0
    with Path(destination).open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for state in TelemetryReplay(recording):
            vehicle, propulsion, landing = state.vehicle, state.propulsion, state.landing_reference
            writer.writerow(
                {
                    "frame_number": state.frame_number,
                    "player_time_s": state.player_time_s,
                    "radar_altitude_m": None if vehicle is None else vehicle.radar_altitude_m,
                    "vertical_velocity_mps": None if landing is None else landing.vertical_velocity_mps,
                    "total_mass_kg": None if vehicle is None else vehicle.total_mass_kg,
                    "applied_throttle": None if propulsion is None else propulsion.applied_throttle,
                    "current_thrust_n": None
                    if propulsion is None or propulsion.current_thrust_body_n is None
                    else (
                        propulsion.current_thrust_body_n.x**2
                        + propulsion.current_thrust_body_n.y**2
                        + propulsion.current_thrust_body_n.z**2
                    )
                    ** 0.5,
                    "mass_flow_rate_kgps": None if propulsion is None else propulsion.mass_flow_rate_kgps,
                }
            )
            count += 1
    return count

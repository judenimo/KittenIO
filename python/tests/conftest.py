"""Shared pytest configuration for the canonical SDK test suites."""

import json
from pathlib import Path

import pytest
from kittenio import CapabilitySet, FlightState

_FIXTURE_ROOT = Path(__file__).resolve().parents[2] / "protocol" / "fixtures" / "v1"

_SUITE_MARKERS = {
    "unit": pytest.mark.unit,
    "integration": pytest.mark.integration,
    "contract": pytest.mark.contract,
}


def pytest_collection_modifyitems(items: list[pytest.Item]) -> None:
    """Mark tests from their suite directory so subsets are easy to run."""

    for item in items:
        parts = Path(str(item.path)).parts
        for directory, marker in _SUITE_MARKERS.items():
            if directory in parts:
                item.add_marker(marker)
                break


@pytest.fixture
def telemetry_payload() -> dict[str, object]:
    return json.loads((_FIXTURE_ROOT / "03-telemetry.json").read_text(encoding="utf-8"))["payload"]


@pytest.fixture
def flight_state(telemetry_payload: dict[str, object]) -> FlightState:
    return FlightState.from_payload(telemetry_payload)


@pytest.fixture
def capabilities(telemetry_payload: dict[str, object]) -> CapabilitySet:
    return CapabilitySet.from_dict(telemetry_payload["capabilities"])

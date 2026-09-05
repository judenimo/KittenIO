"""A deliberately small deterministic finite-state-machine helper."""

from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Generic, TypeVar

from ..wire.models import FlightState

StateName = TypeVar("StateName", bound=str)
Transition = Callable[[FlightState], StateName | None]


@dataclass(slots=True)
class StateMachine(Generic[StateName]):
    state: StateName
    transitions: Mapping[StateName, Transition[StateName]]

    def tick(self, flight_state: FlightState) -> StateName:
        try:
            transition = self.transitions[self.state]
        except KeyError as exc:
            raise KeyError(f"no transition function registered for state {self.state!r}") from exc
        next_state = transition(flight_state)
        if next_state is not None:
            if next_state not in self.transitions:
                raise KeyError(f"transition selected unknown state {next_state!r}")
            self.state = next_state
        return self.state

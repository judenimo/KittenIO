"""Canonical wire envelopes and immutable telemetry models."""

from .protocol import Envelope, ProtocolError, decode_envelope, encode_envelope

__all__ = ["Envelope", "ProtocolError", "decode_envelope", "encode_envelope"]

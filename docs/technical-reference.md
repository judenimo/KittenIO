# Technical reference

Protocol version 1 is the sole accepted wire version. Envelopes and payloads
use strict property names, reject unknown fields, require finite numeric values,
and cap messages at 256 KiB.

Telemetry is captured after the KSA frame and contains the authoritative nested
sections described in [the protocol specification](../protocol/README.md).
The SDK publishes immutable frames to user callbacks on the mission thread.

Control frames carry a monotonically increasing sequence and one normalized CCI
thrust vector. The game-thread coordinator validates the current session,
vehicle identity, authority epoch, lease, sequence, and freshness before the
flight-computer patch applies engine/throttle and the target attitude. Loss of
fresh frames releases control through the dead-man path. Camera changes do not
change the exact selected-vehicle route. The SDK renews a previously validated
command independently of telemetry delivery during a bounded two-second UI
transition grace period; after that grace, the server dead-man releases it.

The endpoint remains loopback-only, pairing-token authenticated, and origin
restricted. Reconnect creates a new session and invalidates handles and leases.

# Time-warp control

Time warp is part of the canonical protocol and is available when
`ksa.capabilities.time_warp.control` is true. Supported rates are in
`available_rates`; an unavailable reason is provided when control is false.

`ksa.time_warp.set_rate`, `increase`, `decrease`, and `cancel` are acknowledged
commands. Success means a later authoritative KSA frame confirmed the new rate.
Warp is game-global and persistent: releasing vehicle control, disconnecting,
or stopping the SDK does not cancel it.

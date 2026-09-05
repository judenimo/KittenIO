# KittenIO

KittenIO connects Python flight software to Kitten Space Agency (KSA) through a
loopback-only StarMap mod. Version 0.1.0 provides authenticated telemetry,
mission scheduling, recording and replay, time-warp requests, and guarded thrust
vector control over protocol v1.

KittenIO is early software. Keep manual control available, test missions on
disposable vehicles, and never rely on it for real-world systems.

## Requirements and compatibility

- Windows with Kitten Space Agency `2026.9.7.5402`
- StarMap `0.4.6` (the only verified loader build)
- Python 3.11, 3.12, 3.13, or 3.14
- `uv` for the supported command-line installation workflow

Other KSA builds fail closed at the mod boundary. Telemetry and recording may
remain available when a particular control capability is unavailable, but an
unsupported game or loader version is not considered a healthy installation.
See the [compatibility matrix](docs/compatibility.md) for details.

## Install

Download the mod ZIP, Python wheel, and checksum file from the
[KittenIO v0.1.0 GitHub Release](https://github.com/judenimo/KittenIO/releases).
Verify the downloaded files against `SHA256SUMS`, then install the wheel and mod:

```text
uv tool install ./kittenio-0.1.0-py3-none-any.whl
kittenio install-mod ./KittenIO-0.1.0.zip
kittenio pair
```

`pair` creates a random local token and writes the matching configuration for
the Python client and mod. Treat this token as a password even though the bridge
only listens on `127.0.0.1`.

Launch KSA through StarMap, load a flight, and verify the complete authenticated
connection:

```text
kittenio doctor
```

`doctor` performs a bounded protocol-v1 handshake and waits for one valid
telemetry frame. It reports component versions and capability availability. It
does not send control or game-changing commands and never prints the token.

## Using KittenIO

The supported Python surface is documented in the
[Python API reference](docs/python-api.md). The repository intentionally ships
without example flight programs; mission code must explicitly check advertised
capabilities and acquire control authority before actuation.

Control is guarded by an exact vehicle/session lease and a dead-man timeout.
Leases are not restored after reconnection. Time-warp requests are capability
checked, and KSA itself limits controls at high simulation rates.

## Update

Download and verify the newer release assets, then run:

```text
kittenio install-mod ./KittenIO-VERSION.zip
uv tool install --force ./kittenio-VERSION-py3-none-any.whl
kittenio doctor
```

Review [CHANGELOG.md](CHANGELOG.md) and the compatibility matrix before updating.

## Uninstall

Remove the Python tool with `uv tool uninstall kittenio`. Remove the installed
KittenIO mod directory from the StarMap mods folder, or use StarMap's mod
management interface if available. The pairing files may then be deleted from
the paths shown by `kittenio pair --help`; deleting either copy invalidates the
old token.

## Security and privacy

KittenIO accepts connections only on loopback and requires an authentication
token. Telemetry and recordings may reveal gameplay state and local metadata;
share them deliberately. Do not publish pairing configuration. If it may have
leaked, run `kittenio pair` again before reconnecting.

Report security issues privately using [SECURITY.md](SECURITY.md). For ordinary
installation and usage help, see [SUPPORT.md](SUPPORT.md).

## Known limitations

- Protocol v1 has no compatibility negotiation with retired protocol versions.
- KSA `2026.9.7.5402` and StarMap `0.4.6` are the sole verified game/loader pair.
- Continuous actuation is limited to the advertised thrust-vector route.
- No example mission programs are distributed.
- Automated contract and packaging tests passed for v0.1.0; a live KSA
  acceptance run was explicitly waived and is not claimed.

KittenIO is provided under the [MIT License](LICENSE). Third-party notices are
listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

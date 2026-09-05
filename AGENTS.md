# KittenIO agent guide

Read [`docs/engineering-guide.md`](docs/engineering-guide.md) before changing behaviour. It is the authoritative architecture, protocol, safety, build, and debugging reference. This short file is the repository entry point; update the detailed guide whenever an invariant, workflow, or supported capability changes.

## Repository map

```text
src/                         C# protocol and StarMap/KSA mod
python/src/kittenio/api/     vehicle, control, and time-warp APIs
python/src/kittenio/runtime/ mission lifecycle, scheduling, waits, and FSM
python/src/kittenio/transport/ authenticated loopback client and configuration
python/src/kittenio/wire/    canonical envelopes and immutable models
python/src/kittenio/research/ recording, transforms, identification, optimisation
python/tests/unit/            isolated Python tests
python/tests/integration/     component and real-loopback integration tests
python/tests/contract/        cross-language fixture and packaging contracts
protocol/                     normative prose, schemas, and golden fixtures
tests/                        executable C# protocol, mod, and KSA contract tests
docs/                         focused references, runbooks, and architecture
scripts/                      Git Bash entry points and uv orchestration
tools/                        standalone engineering utilities
```

Compatibility modules at `python/src/kittenio/*.py` preserve established imports. New implementation code belongs in the responsibility-based packages above; keep the root `kittenio` package as the stable public facade.

## Non-negotiable boundaries

- Proprietary game and loader assemblies are local build inputs, not KittenIO source. Never stage, package, or commit them.
- KSA objects stay on the game thread. Networking receives immutable DTOs only.
- Game-frame callbacks never wait for Python or socket I/O.
- Continuous control remains complete, bounded, exact-vehicle/session scoped, and protected by the lease and dead-man timeout.
- Harmony code stays short, allocation-conscious, fail-closed, and pinned to the verified KSA contract.
- Wire changes update C#, Python, protocol prose, schemas, fixtures, and both test suites together. Protocol v1 has no compatibility aliases; a schema change requires an explicit protocol-version decision.
- Pairing tokens and other credentials must never be printed or committed.

## Source-of-truth order

1. Tests exercising verified behaviour and the pinned KSA contract.
2. `src/` and `python/src/kittenio/`.
3. `protocol/README.md`, schemas, and golden fixtures.
4. `docs/engineering-guide.md` and focused documentation.
5. Historical plans and JunoIO behaviour.

## Development workflow

Use Git Bash and the repository wrappers:

```bash
./scripts/dev check
./scripts/dev test
./scripts/dev package   # release, installer, or packaging changes
./scripts/dev public-audit
```

`check` and `public-audit` are portable. Full mod commands require legally
obtained local KSA and StarMap reference assemblies. Inspect `git status` before
editing, preserve unrelated work, and report automated and live checks
separately. Never infer a live-game result from contract tests.

Public Python APIs are exported through `kittenio.__init__`; do not add deprecated aliases or compatibility modules. For wire or runtime changes, read the focused change procedures in the engineering guide before implementation.

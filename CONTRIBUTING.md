# Contributing to KittenIO

Thank you for helping improve KittenIO. Start with the [engineering guide](docs/engineering-guide.md), which records the thread, protocol, and safety invariants that changes must preserve.

## Development setup

Install Python 3.11 or newer, `uv`, the .NET SDK pinned by `global.json`, and Git Bash. A full mod build also needs the locally installed KSA and StarMap reference assemblies described in the engineering guide.

From Git Bash:

```bash
./scripts/dev check
./scripts/dev test
./scripts/dev public-audit
```

`check` is portable and validates the lockfile, Python imports/style, and protocol fixtures. `test` additionally builds the mod and runs the locally pinned KSA contract tests.

## Pull requests

- Keep changes focused and preserve unrelated work.
- Add or update tests for changed behaviour.
- Update both language implementations, schemas, fixtures, and protocol prose for wire changes.
- State which automated checks ran and which live KSA scenarios remain.
- Never commit KSA, StarMap, pairing tokens, generated packages, or local game inputs.
- Keep public documentation free of machine-specific paths and private release notes.

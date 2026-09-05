# Third-party notices

KittenIO is an independent community project and is not affiliated with or
endorsed by Ahwoo, Kitten Space Agency, or the projects named below.

## JunoIO

KittenIO's external-flight-software programming model was informed by
[JunoIO](https://github.com/judenimo/JunoIO), pinned during development at
commit `678e5b83a946966e1fa3a870e8a6610599a1a564`.

JunoIO is distributed under the MIT License. Copyright remains with its
respective author(s). KittenIO does not redistribute JunoIO binaries, game
binaries, or Juno: New Origins assemblies.

## StarMap

The KittenIO game mod integrates with the
[StarMap mod loader](https://github.com/StarMapLoader/StarMap), which is
distributed under the MIT License. Copyright remains with the StarMap
contributors. StarMap is a runtime prerequisite and is not included in the
KittenIO release archive.

## Lib.Harmony

KittenIO redistributes Lib.Harmony 2.4.2 (`0Harmony.dll`) to install its
fail-closed KSA control patch. Lib.Harmony is copyright (c) 2017 Andreas
Pardeike and is distributed under the MIT License. The complete license is
included as `licenses/Lib.Harmony-LICENSE.txt`.

## Kitten Space Agency

Kitten Space Agency and its runtime assemblies are proprietary third-party
software. `KSA.dll` and `Brutal.*` assemblies may be referenced locally while
building the mod, but are never copied into a KittenIO package.

## Python dependencies

The Python distribution declares its dependencies in `pyproject.toml`.
Those packages retain their own copyright and license terms and are resolved
by the user's Python package installer; they are not vendored into KittenIO.

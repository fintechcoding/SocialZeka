"""
What the worker is allowed to need before anything has been installed.

The probe and download commands run BEFORE the setup wizard has installed a single package: that
is how the wizard finds out what is missing and offers to fetch it. ``vt_worker.__main__``
imports every module in the package unconditionally, so one heavy import at a module top turns
"numpy is not there yet" into a worker that will not start and a setup screen that cannot say
why — on precisely the machine that needed the screen.

Every module in the package already followed the rule (prosody.py and speaker.py import numpy
inside the functions that use it); nothing said so out loud, and ringback.py was written with the
import at the top and broke it. A source scan rather than a behavioural test, which is the shape
this repository already uses when the thing to protect is a rule about the code itself.
"""

from __future__ import annotations

import ast
from pathlib import Path

import pytest

PACKAGE = Path(__file__).resolve().parent.parent / "vt_worker"

#: Packages the worker may not import before the setup wizard has run.
#:
#: Everything the wizard installs. A module may still use them — inside the function that needs
#: them, so importing the module costs nothing and calling it fails with a message somebody can
#: act on.
HEAVY = {"numpy", "av", "faster_whisper", "ctranslate2", "torch", "huggingface_hub"}


def _modules() -> list[Path]:
    return sorted(p for p in PACKAGE.rglob("*.py") if p.name != "__init__.py")


def test_the_package_has_modules_to_check():
    """A scan over nothing passes for the wrong reason."""
    assert len(_modules()) >= 5


@pytest.mark.parametrize("path", _modules(), ids=lambda p: p.name)
def test_no_module_needs_a_wizard_installed_package_to_import(path: Path):
    tree = ast.parse(path.read_text(encoding="utf-8"))

    offending = []

    for node in tree.body:
        if isinstance(node, ast.Import):
            offending += [alias.name for alias in node.names]
        elif isinstance(node, ast.ImportFrom) and node.module and node.level == 0:
            offending.append(node.module)

    heavy = sorted({name for name in offending if name.split(".")[0] in HEAVY})

    assert not heavy, (
        f"{path.name} modul basinda {', '.join(heavy)} import ediyor. "
        "Kurulum sihirbazi calismadan once probe bu paketi import ediyor; "
        "agir import'u kullanan fonksiyonun icine al."
    )

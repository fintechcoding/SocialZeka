"""
Every module a name is used from is actually imported.

Written because of a crash that reached a user's screen: ``faster_whisper_engine.load`` computed
``os.cpu_count()`` without ``import os``, so every transcription on the processor died with
``NameError: name 'os' is not defined`` — at load time, after the recording was already made.

Nothing caught it. The engine needs Whisper weights, so its tests are skipped on machines without
them, and the line only runs when the device resolves to ``cpu``; a laptop with a working card
never reaches it. It is a class of fault a linter finds instantly and this project has none, so the
check lives here: cheap, dependency-free, and pointed at exactly the mistake that happened.
"""

from __future__ import annotations

import ast
import pathlib

import pytest

# The standard-library modules the worker uses. A name from this set appearing as ``name.attr``
# without a matching import is the bug above; anything else is a local variable and not our
# business.
STDLIB = {
    "argparse", "array", "ast", "asyncio", "base64", "collections", "contextlib", "ctypes",
    "dataclasses", "datetime", "functools", "glob", "hashlib", "importlib", "io", "itertools",
    "json", "logging", "math", "os", "pathlib", "platform", "re", "shutil", "socket", "struct",
    "subprocess", "sys", "tempfile", "threading", "time", "traceback", "types", "typing",
    "urllib", "uuid", "wave",
}

MODULES = sorted((pathlib.Path(__file__).resolve().parents[1] / "vt_worker").rglob("*.py"))


def _imported(tree: ast.Module) -> set[str]:
    """Every name this module binds by importing, at any level."""
    names: set[str] = set()

    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names:
                names.add(alias.asname or alias.name.split(".")[0])
        elif isinstance(node, ast.ImportFrom):
            for alias in node.names:
                names.add(alias.asname or alias.name)

    return names


def _assigned(tree: ast.Module) -> set[str]:
    """Names bound some other way, so a local called `time` is not mistaken for the module."""
    names: set[str] = set()

    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            names.add(node.name)
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                args = node.args
                for arg in [*args.posonlyargs, *args.args, *args.kwonlyargs]:
                    names.add(arg.arg)
        elif isinstance(node, ast.Name) and isinstance(node.ctx, (ast.Store, ast.Del)):
            names.add(node.id)

    return names


@pytest.mark.parametrize("path", MODULES, ids=lambda p: p.name)
def test_every_standard_library_name_used_is_imported(path: pathlib.Path):
    tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    known = _imported(tree) | _assigned(tree)

    used = {
        node.value.id
        for node in ast.walk(tree)
        if isinstance(node, ast.Attribute)
        and isinstance(node.value, ast.Name)
        and node.value.id in STDLIB
    }

    missing = sorted(used - known)

    assert not missing, f"{path.name} uses {', '.join(missing)} without importing it"

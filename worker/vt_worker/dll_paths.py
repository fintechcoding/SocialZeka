"""Make the pip-installed NVIDIA runtime DLLs findable on Windows.

Since Python 3.8 the Windows loader no longer searches PATH when resolving dependencies of C
extension modules. `pip install nvidia-cublas-cu12` therefore puts cublas64_12.dll somewhere
perfectly valid that ctranslate2.dll will never look:

    <venv>/Lib/site-packages/nvidia/cublas/bin/cublas64_12.dll

The fix is os.add_dll_directory(). The LD_LIBRARY_PATH workaround printed in the faster-whisper
README is Linux-only and silently does nothing here, which is why this failure is usually
misdiagnosed as a broken CUDA install.

Import this before importing ctranslate2 or faster_whisper. Calling it twice is harmless.
"""

from __future__ import annotations

import glob
import os
import site
import sys

_applied = False
_registered: list[str] = []


def register_nvidia_dll_directories() -> list[str]:
    """Register every ``nvidia/*/bin`` directory found in site-packages. Returns what was added."""
    global _applied

    if _applied or sys.platform != "win32":
        return _registered

    roots: list[str] = []
    try:
        roots.extend(site.getsitepackages())
    except AttributeError:  # pragma: no cover - only in unusual embedded interpreters
        pass

    user_site = site.getusersitepackages()
    if isinstance(user_site, str):
        roots.append(user_site)

    seen: set[str] = set()
    for root in roots:
        for directory in glob.glob(os.path.join(root, "nvidia", "*", "bin")):
            real = os.path.realpath(directory)
            if real in seen or not os.path.isdir(real):
                continue
            seen.add(real)
            try:
                os.add_dll_directory(real)
                _registered.append(real)
            except OSError:
                # A directory that vanished between glob and here is not worth failing over.
                continue

    _applied = True
    return _registered


def missing_cuda_dlls() -> list[str]:
    """Names of CUDA runtime DLLs that could not be located after registration.

    Only cuBLAS is required: CTranslate2 4.6.3 and later moved conv1d to pure CUDA, which
    dropped cuDNN as a dependency altogether. Any advice to install cuDNN 9, including the
    text still shipped in the faster-whisper README, applies to older releases.
    """
    if sys.platform != "win32":
        return []

    register_nvidia_dll_directories()

    import ctypes

    missing: list[str] = []
    for name in ("cublas64_12.dll",):
        try:
            ctypes.CDLL(name)
        except OSError:
            missing.append(name)
    return missing

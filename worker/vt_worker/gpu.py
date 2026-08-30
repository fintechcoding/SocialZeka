"""Chooses which graphics card does the work, and names it.

Two things people reasonably worry about on a laptop, one of which turns out not to be a problem:

*Picking the integrated Intel or AMD chip by mistake.* This cannot happen. CTranslate2 enumerates
CUDA devices, and an Intel or AMD integrated GPU is not one — it is invisible to the count, so
device 0 is always an NVIDIA card. The worry is real on frameworks that enumerate every adapter;
it is not real here.

*Picking the weaker of two NVIDIA cards.* This can happen, and index 0 is not reliably the better
one — the order comes from PCI enumeration, not from capability. So the cards are enumerated and
the one with the most memory is chosen, because memory is what decides which model fits.

Enumeration goes through nvidia-smi, which ships with the driver itself: if there is a working
NVIDIA GPU at all then nvidia-smi is present, and no extra dependency is introduced to ask a
question this small. When it is missing or unreadable the answer is device 0 with no name, which
is exactly what the behaviour was before this module existed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass


@dataclass(frozen=True)
class GpuDevice:
    index: int
    name: str
    total_memory_mb: int

    @property
    def label(self) -> str:
        if self.total_memory_mb <= 0:
            return self.name
        return f"{self.name} ({self.total_memory_mb / 1024:.0f} GB)"


def _nvidia_smi() -> str | None:
    """Full path to nvidia-smi, or None.

    PATH first; on Windows the driver also drops a copy in System32 which is present even when
    PATH has been trimmed by a launcher.
    """
    found = shutil.which("nvidia-smi")
    if found:
        return found

    if sys.platform == "win32":
        fallback = os.path.join(
            os.environ.get("SystemRoot", r"C:\Windows"), "System32", "nvidia-smi.exe"
        )
        if os.path.isfile(fallback):
            return fallback

    return None


def enumerate_devices(timeout: float = 5.0) -> list[GpuDevice]:
    """Every CUDA-capable NVIDIA card, in the driver's own order."""
    exe = _nvidia_smi()
    if exe is None:
        return []

    try:
        completed = subprocess.run(
            [exe, "--query-gpu=index,name,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=timeout,
            # Without this a console window flashes on screen every time the worker starts, which
            # on an application that runs unattended in the tray is unacceptable.
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return []

    if completed.returncode != 0:
        return []

    devices: list[GpuDevice] = []
    for line in completed.stdout.splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) < 3:
            continue
        try:
            devices.append(GpuDevice(int(parts[0]), parts[1], int(float(parts[2]))))
        except ValueError:
            continue

    return devices


def select_device(devices: list[GpuDevice] | None = None) -> GpuDevice | None:
    """The card the work should run on: most memory wins, lowest index breaks a tie.

    Memory rather than a name-based guess at model tiers. Marketing names do not order
    themselves — an RTX 4050 is not obviously weaker than an RTX 3060 from the string alone —
    whereas the constraint that actually decides whether a model runs is how much fits on it.
    """
    if devices is None:
        devices = enumerate_devices()

    if not devices:
        return None

    return min(devices, key=lambda d: (-d.total_memory_mb, d.index))

"""What more than one of the canary's suites builds: a verdict's readings and the repository's src/."""

from __future__ import annotations

import tempfile
from pathlib import Path


def readings(signals=("http",), **overrides) -> dict:
    """A healthy step, which the tests then break one field at a time."""
    healthy = {
        "canary": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 5000.0},
        "baseline": {"errorRate": 0.0, "latencyP99Seconds": 0.2, "requests": 90000.0},
    }
    document = {
        track: {signal: dict(values) for signal in signals}
        for track, values in healthy.items()
    }
    for track, per_signal in overrides.items():
        if per_signal is None:
            document.pop(track)
            continue
        for signal, values in per_signal.items():
            if values is None:
                document[track].pop(signal, None)
            else:
                document[track].setdefault(signal, {}).update(values)
    return document


def service_tree(files: dict[str, str]) -> Path:
    """A throwaway src/ holding one service, `Svc.Api`, and the given sources.

    Keys are paths under src/Services/Svc; the host project and Program.cs
    are always written, because the scans find a service by its host.
    """
    root = Path(tempfile.mkdtemp())
    service = root / "src" / "Services" / "Svc"
    host = service / "Svc.Api"
    host.mkdir(parents=True)
    (host / "Svc.Api.csproj").write_text("<Project />\n", encoding="utf-8")
    (host / "Program.cs").write_text("// host\n", encoding="utf-8")
    for relative, text in files.items():
        path = service / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    return root

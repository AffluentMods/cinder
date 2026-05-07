"""Cinder echo sidecar.

Round-trip ping for the JSON-RPC protocol — used by Phase 0 acceptance tests
and as the reference implementation for new sidecars.
"""

from __future__ import annotations

import asyncio
import sys
from typing import Any

from shared.protocol import Sidecar

sidecar = Sidecar()


@sidecar.method("ping")
def ping(params: Any) -> dict[str, Any]:
    return {"pong": params}


@sidecar.method("info")
def info(_params: Any) -> dict[str, str]:
    return {
        "name": "cinder-echo",
        "version": "0.0.1",
        "python": sys.version,
    }


def main() -> None:
    asyncio.run(sidecar.serve())


if __name__ == "__main__":
    main()

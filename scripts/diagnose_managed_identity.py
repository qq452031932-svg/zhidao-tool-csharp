"""Run the packaged identity observer against an already-owned test browser.

Prints only the typed result kind so live diagnostics never disclose account IDs.
"""

from __future__ import annotations

import json
import pathlib
import sys

sys.dont_write_bytecode = True


def main() -> int:
    if len(sys.argv) != 5:
        print("usage: diagnose_managed_identity.py HELPER_ROOT PORT BROWSER_ID TARGET_ID")
        return 2
    helper_root = pathlib.Path(sys.argv[1]).resolve()
    sys.path.insert(0, str(helper_root))
    import main as helper_main

    payload = {
        "browserId": sys.argv[3],
        "targetId": sys.argv[4],
        "devToolsEndpoint": f"http://127.0.0.1:{int(sys.argv[2])}/",
        "timeoutMilliseconds": 5000,
    }
    result = helper_main.observe_account_identity(payload)
    print(json.dumps({"kind": result.get("kind")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

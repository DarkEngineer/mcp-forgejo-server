#!/usr/bin/env python3
"""MCP stdio smoke test for forgejo-mcp-server.

Runs the built server binary (src/ForgejoMcp/bin/Release/net10.0/ForgejoMcp),
performs an `initialize` handshake, then lists tools and resources over the
stdio protocol. Exits non-zero if the handshake or tool listing fails.

The server's stdin must stay OPEN while it works (an EOF on stdin makes the
MCP host shut down before it can flush its replies), so this script keeps the
pipe open until the expected responses arrive — unlike a plain
`server < file` redirect, which closes stdin immediately.

Usage:
    export FORGEJO_URL=https://your-forgejo   # required (any value)
    dotnet build ForgejoMcp.sln -c Release
    python3 scripts/mcp_smoke.py
"""
import json
import os
import shutil
import subprocess
import sys
import time


def find_dotnet_root() -> str | None:
    """Locate a .NET runtime root for the apphost binary.

    The apphost (``ForgejoMcp``) searches DOTNET_ROOT, then a system
    default. On machines where the SDK was installed in a user directory
    (e.g. ``~/.dotnet``) we discover that here so the smoke test works
    without exporting DOTNET_ROOT manually.
    """
    if os.environ.get("DOTNET_ROOT"):
        return os.environ["DOTNET_ROOT"]
    if shutil.which("dotnet"):
        p = os.path.realpath(shutil.which("dotnet"))
        parent = os.path.dirname(p)
        if os.path.basename(parent) == "dotnet":
            return parent
    candidates = [os.path.expanduser("~/.dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet"]
    for c in candidates:
        if os.path.isdir(c):
            return c
    return None

def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    bin_path = os.path.join(root, "src", "ForgejoMcp", "bin", "Release",
                            "net10.0", "ForgejoMcp")
    if not os.path.exists(bin_path):
        print(f"server binary not found: {bin_path}\n"
              f"build first: dotnet build ForgejoMcp.sln -c Release", file=sys.stderr)
        return 2

    env = dict(os.environ)
    env.setdefault("FORGEJO_URL", "https://forgejo.invalid")  # only needed so startup validates
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    dotnet_root = find_dotnet_root()
    if dotnet_root:
        env["DOTNET_ROOT"] = dotnet_root

    proc = subprocess.Popen([bin_path], stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=env, text=True, bufsize=1)
    assert proc.stdin is not None and proc.stdout is not None and proc.stderr is not None

    def send(obj: dict) -> None:
        proc.stdin.write(json.dumps(obj) + "\n")
        proc.stdin.flush()

    def expect_response(want_id: int, timeout: float = 15.0):
        t0 = time.time()
        while time.time() - t0 < timeout:
            line = proc.stdout.readline()
            if not line:
                err = (proc.stderr.read() if proc.stderr and not proc.stderr.closed else "")
                raise SystemExit(f"server closed stdout early; stderr={err!r}")
            msg = json.loads(line)
            if msg.get("id") == want_id:
                if "error" in msg:
                    raise SystemExit(f"{want_id} returned an error: {msg['error']}")
                return msg
        raise SystemExit(f"no response to request {want_id} within {timeout}s")

    failures = 0
    try:
        send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
              "params": {"protocolVersion": "2025-06-18",
                         "capabilities": {},
                         "clientInfo": {"name": "smoke", "version": "0.0.0"}}})
        init = expect_response(1)
        info = init["result"]["serverInfo"]
        print(f"initialize OK: {info['name']} v{info.get('version', '?')} "
              f"(" + init["result"].get("protocolVersion", "?") + ")")

        send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        tools = [t["name"] for t in expect_response(2)["result"]["tools"]]
        print(f"tools ({len(tools)}): {', '.join(tools)}")
        expected = {"list_repos", "get_repo", "create_issue", "list_issues",
                    "list_pull_requests", "list_commits", "get_file"}
        if not expected.issubset(set(tools)):
            print(f"MISSING expected tools: {sorted(expected - set(tools))}",
                  file=sys.stderr)
            failures += 1

        send({"jsonrpc": "2.0", "id": 3, "method": "resources/list", "params": {}})
        res = [r["uri"] for r in expect_response(3)["result"]["resources"]]
        print(f"resources: {', '.join(res)}")

        send({"jsonrpc": "2.0", "id": 4, "method": "resources/templates/list",
              "params": {}})
        tpl = [r["uriTemplate"] for r in
               expect_response(4)["result"]["resourceTemplates"]]
        print(f"resource templates: {', '.join(tpl)}")
    finally:
        try:
            proc.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()

    return failures


if __name__ == "__main__":
    sys.exit(main())

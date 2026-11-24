#!/usr/bin/env python3
"""Live acceptance round-trip for `search_repos` + `search_code` (issue #38).

Boots the worktree's built MCP server over stdio with real Forgejo
credentials and verifies, against the LIVE instance:
  1. `search_repos` is present in tools/list and returns the list
     envelope (items/count) for a known-repo query — a hit, not an
     error envelope.
  2. `search_repos` with a query that matches nothing returns an EMPTY
     items list, not an error (empty-result acceptance criterion).
  3. `search_code` is present and answers the typed `search_disabled`
     error — the acceptance instance (git.home.internal,
     Forgejo 16.0.3+gitea-1.22.0) does not serve any code-search route
     (verified: 404 on /repos/search/code and no code-search path in
     the instance's swagger), so a crash/protocol error here would
     fail the issue #38 acceptance criterion.

Read-only: posts nothing. Run after `dotnet build ForgejoMcp.sln
-c Release` in the worktree; no arguments required.
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "src", "ForgejoMcp", "bin", "Release", "net10.0", "ForgejoMcp")

def main() -> int:
    pass_file = os.path.expanduser("~/.hermes/git-pass-emberfall")
    ca = os.path.expanduser("~/.hermes/forgejo-ca.crt")
    with open(pass_file) as f:
        pwd = f.read().strip()

    env = dict(os.environ)
    env["FORGEJO_URL"] = "https://git.home.internal"
    env["FORGEJO_USERNAME"] = "hermes-agent"
    env["FORGEJO_PASSWORD"] = pwd
    env["SSL_CERT_FILE"] = ca
    env["DOTNET_ROOT"] = os.path.expanduser("~/.dotnet")
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"

    proc = subprocess.Popen([BIN], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, text=True, bufsize=1)
    stdin, stdout = proc.stdin, proc.stdout
    if stdin is None or stdout is None:
        print("failed to set up pipes", file=sys.stderr); return 2

    def send(obj):
        stdin.write(json.dumps(obj) + "\n"); stdin.flush()

    def expect(want_id, timeout=30.0):
        t0 = time.time()
        while time.time() - t0 < timeout:
            line = stdout.readline()
            if not line:
                raise SystemExit(f"server closed stdout early; stderr={proc.stderr.read()!r}")
            msg = json.loads(line)
            if msg.get("id") == want_id:
                if "error" in msg: raise SystemExit(f"{want_id} error: {msg['error']}")
                return msg
        raise SystemExit(f"no response to {want_id} in {timeout}s")

    try:
        send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"live-acceptance","version":"0"}}})
        init = expect(1)["result"]
        print(f"initialize OK: {init['serverInfo']}")
        send({"jsonrpc":"2.0","method":"notifications/initialized"})

        send({"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}})
        tools = [t["name"] for t in expect(2)["result"]["tools"]]
        print(f"tools ({len(tools)}): {', '.join(tools)}")
        for want in ("search_repos", "search_code"):
            if want not in tools:
                print(f"FAIL: {want} missing from tools/list", file=sys.stderr); return 1

        # 1. search_repos hit — a repo we know the token can see.
        send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
            "name":"search_repos",
            "arguments":{"query":"mcp","limit":10}}})
        doc = json.loads(expect(3)["result"]["content"][0]["text"])
        print(f"search_repos(hit) OK -> count={doc.get('count')}")
        if "error" in doc:
            print(f"FAIL: error envelope on hit: {doc['error']}", file=sys.stderr); return 1
        items = doc.get("items") or []
        if not items:
            print("FAIL: expected at least one repo for query 'mcp'", file=sys.stderr); return 1
        full_names = [r.get("full_name") for r in items]
        if "dark-eternity/mcp-forgejo-server" not in full_names:
            print(f"FAIL: mcp-forgejo-server not in results: {full_names}", file=sys.stderr); return 1
        for r in items:
            if "full_name" not in r or "id" not in r:
                print(f"FAIL: repo entry missing identity fields: {r}", file=sys.stderr); return 1
        print(f"  matched: {full_names}")

        # 2. search_repos empty — must be an empty items list, not an error.
        send({"jsonrpc":"2.0","id":4,"method":"tools/call","params":{
            "name":"search_repos",
            "arguments":{"query":"zzz-no-match-xyz-98765"}}})
        doc = json.loads(expect(4)["result"]["content"][0]["text"])
        print(f"search_repos(empty) OK -> count={doc.get('count')}")
        if "error" in doc:
            print(f"FAIL: error envelope on empty result: {doc['error']}", file=sys.stderr); return 1
        if doc.get("count") != 0 or doc.get("items") != []:
            print(f"FAIL: expected empty items, got: {doc}", file=sys.stderr); return 1

        # 3. search_code — the acceptance instance does not serve the
        # code-search route: the typed search_disabled error is the
        # correct, expected answer (issue #38).
        send({"jsonrpc":"2.0","id":5,"method":"tools/call","params":{
            "name":"search_code",
            "arguments":{"q":"ForgejoClient","owner":"dark-eternity","repo":"mcp-forgejo-server"}}})
        doc = json.loads(expect(5)["result"]["content"][0]["text"])
        print(f"search_code OK -> {json.dumps(doc)}")
        err = doc.get("error") or {}
        if err.get("code") != "search_disabled":
            print(f"FAIL: expected typed search_disabled error, got: {doc}", file=sys.stderr); return 1
    finally:
        try:
            if stdin is not None:
                stdin.close()
        except Exception: pass
        proc.terminate()
        try: proc.wait(timeout=5)
        except Exception: proc.kill()
        try:
            if stdout is not None:
                stdout.close()
        except Exception: pass
    print("LIVE ACCEPTANCE OK")
    return 0

if __name__ == "__main__":
    sys.exit(main())

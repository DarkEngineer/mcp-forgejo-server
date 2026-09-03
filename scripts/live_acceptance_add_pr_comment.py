#!/usr/bin/env python3
"""Live acceptance round-trip for `add_pr_comment` (issue #17).

Boots the worktree's built MCP server over stdio with real Forgejo
credentials, verifies the tool is present, then posts a comment to the
live PR for the feature branch and asserts the created-comment identity
comes back. Run after `dotnet build ForgejoMcp.sln -c Release` in the
worktree; the PR number is passed as argv[1].
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BIN = os.path.join(ROOT, "src", "ForgejoMcp", "bin", "Release", "net10.0", "ForgejoMcp")

def main(pr_number: int) -> int:
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
    if proc.stdin is None or proc.stdout is None:
        print("failed to set up pipes", file=sys.stderr); return 2

    def send(obj):
        proc.stdin.write(json.dumps(obj) + "\n"); proc.stdin.flush()

    def expect(want_id, timeout=30.0):
        t0 = time.time()
        while time.time() - t0 < timeout:
            line = proc.stdout.readline()
            if not line:
                raise SystemExit(f"server closed stdout early; stderr={proc.stderr.read()!r}")
            msg = json.loads(line)
            if msg.get("id") == want_id:
                if "error" in msg: raise SystemExit(f"{want_id} error: {msg['error']}")
                return msg
        raise SystemExit(f"no response to {want_id} in {timeout}s")

    failures = 0
    try:
        send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"live-acceptance","version":"0"}}})
        init = expect(1)["result"]
        print(f"initialize OK: {init['serverInfo']}")
        send({"jsonrpc":"2.0","method":"notifications/initialized"})

        send({"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}})
        tools = [t["name"] for t in expect(2)["result"]["tools"]]
        print(f"tools ({len(tools)}): {', '.join(tools)}")
        if "add_pr_comment" not in tools:
            print("FAIL: add_pr_comment missing from tools/list", file=sys.stderr); return 1

        body = (f"acceptance round-trip: add_pr_comment posted this comment "
                f"against live PR #{pr_number} via the MCP stdio protocol.")
        send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
            "name":"add_pr_comment",
            "arguments":{"owner":"dark-eternity","name":"mcp-forgejo-server",
                         "index":pr_number,"content":body}}})
        r = expect(3)["result"]
        text = r["content"][0]["text"]
        doc = json.loads(text)
        print(f"add_pr_comment OK -> {text}")
        if "error" in doc:
            print("FAIL: error envelope returned", file=sys.stderr); return 1
        ok = (isinstance(doc.get("id"), int)
              and doc.get("user", {}).get("login") in ("hermes-agent", None)
              and "created_at" in doc and "html_url" in doc)
        if not ok:
            print("FAIL: comment identity fields missing", file=sys.stderr); return 1
        print(f"comment id={doc['id']} url={doc['html_url']}")
    finally:
        try: proc.stdin.close()
        except Exception: pass
        proc.terminate()
        try: proc.wait(timeout=5)
        except Exception: proc.kill()
        try: proc.stdout.close()
        except Exception: pass
    return failures

if __name__ == "__main__":
    sys.exit(main(int(sys.argv[1])))

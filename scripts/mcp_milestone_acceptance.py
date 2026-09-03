#!/usr/bin/env python3
"""MCP stdio acceptance driver for the milestone tools (issue #20).

Drives the built forgejo-mcp-server over stdio (same NDJSON handshake as
scripts/mcp_smoke.py) and performs the acceptance flow:

  1. initialize handshake + `notifications/initialized`
  2. tools/list — assert `list_milestones`, `get_milestone`,
     `create_milestone` are present and carry the [McpServerTool]
     annotation hints (readOnly/destructive) the tools advertise
  3. tools/call create_milestone (title from MILESTONE_TITLE, default "v3.1")
     — a duplicate-title create (HTTP 422) is an acceptable repeat per the
     issue's idempotency note; it is recorded and NOT a failure
  4. tools/call list_milestones — the milestone must appear with an `id`
  5. tools/call get_milestone (by that id) — fields round-trip

Credentials come from the environment (FORGEJO_URL / FORGEJO_USERNAME /
FORGEJO_PASSWORD / SSL_CERT_FILE — the production launcher env names) and the
repo from FORGEJO_OWNER / FORGEJO_REPO; nothing sensitive is hardcoded.

Usage:
    export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
    export FORGEJO_URL=https://git.home.internal
    export FORGEJO_USERNAME=hermes-agent
    export FORGEJO_PASSWORD="***"        # from ~/.hermes/git-pass-emberfall
    export SSL_CERT_FILE=$HOME/.hermes/forgejo-ca.crt
    export FORGEJO_OWNER=dark-eternity FORGEJO_REPO=mcp-forgejo-server
    python3 scripts/mcp_milestone_acceptance.py    # → results JSON on stdout
"""
import json
import os
import subprocess
import sys
import time


def find_dotnet_root() -> str | None:
    """Locate the .NET runtime root for the apphost binary (mirrors mcp_smoke.py)."""
    if os.environ.get("DOTNET_ROOT"):
        return os.environ["DOTNET_ROOT"]
    for c in [os.path.expanduser("~/.dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet"]:
        if os.path.isdir(c):
            return c
    return None


class Driver:
    def __init__(self, proc: subprocess.Popen):
        self.proc = proc
        self._rid = 0

    def _next_id(self) -> int:
        self._rid += 1
        return self._rid

    def send(self, obj: dict) -> None:
        assert self.proc.stdin is not None
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def expect(self, want_id: int, timeout: float = 30.0) -> dict:
        assert self.proc.stdout is not None
        t0 = time.time()
        while time.time() - t0 < timeout:
            line = self.proc.stdout.readline()
            if not line:
                err = self.proc.stderr.read() if self.proc.stderr and not self.proc.stderr.closed else ""
                raise SystemExit(f"server closed stdout early; stderr={err!r}")
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == want_id:
                if "error" in msg:
                    raise SystemExit(f"JSON-RPC error on request {want_id}: {msg['error']}")
                return msg
        raise SystemExit(f"no response to request {want_id} within {timeout}s")

    def call_tool(self, name: str, arguments: dict) -> dict:
        """tools/call → the decoded text payload of the result."""
        rid = self._next_id()
        self.send({"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                   "params": {"name": name, "arguments": arguments}})
        return tool_result_payload(self.expect(rid))


def tool_result_payload(msg: dict):
    res = msg.get("result") or {}
    text = ""
    for c in res.get("content") or []:
        if c.get("type") == "text" and "text" in c:
            text = c["text"]
            break
    if not text:
        if res.get("isError"):
            return {"error": {"code": "tool_error", "message": "<no text content>"}}
        raise SystemExit(f"unexpected tools/call result shape: {json.dumps(msg)[:400]}")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return {"raw": text}


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    bin_path = os.path.join(root, "src", "ForgejoMcp", "bin", "Release", "net10.0", "ForgejoMcp")
    if not os.path.exists(bin_path):
        print("server binary not found — build first: dotnet build ForgejoMcp.sln -c Release",
              file=sys.stderr)
        return 2
    for var in ("FORGEJO_URL", "FORGEJO_USERNAME", "FORGEJO_PASSWORD"):
        if not os.environ.get(var):
            print(f"missing required env: {var}", file=sys.stderr)
            return 2

    owner = os.environ.get("FORGEJO_OWNER", "dark-eternity")
    repo = os.environ.get("FORGEJO_REPO", "mcp-forgejo-server")
    title = os.environ.get("MILESTONE_TITLE", "v3.1")
    description = os.environ.get(
        "MILESTONE_DESCRIPTION", "v3.1.0 Tier-1 batch — milestone tools (issue #20).")

    env = dict(os.environ)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    dotnet_root = find_dotnet_root()
    if dotnet_root:
        env["DOTNET_ROOT"] = dotnet_root

    proc = subprocess.Popen([bin_path], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env, text=True, bufsize=1)
    failures: list[str] = []
    results: dict = {
        "server": None, "tool_names": None, "annotations": {},
        "create": None, "duplicate_status": None,
        "list_contains_title": False, "milestone": None,
        "get_round_trip": None,
    }
    rid = Driver(proc)
    milestone_id = None
    try:
        # ---- 1. initialize ----------------------------------------------------
        init_rid = rid._next_id()
        rid.send({"jsonrpc": "2.0", "id": init_rid, "method": "initialize",
                  "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                             "clientInfo": {"name": "w20-acceptance", "version": "0.0.0"}}})
        init = rid.expect(init_rid)
        results["server"] = init["result"]["serverInfo"]
        print(f"initialize OK: {results['server']['name']} v{results['server'].get('version')}")
        rid.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

        # ---- 2. tools/list -----------------------------------------------------
        list_rid = rid._next_id()
        rid.send({"jsonrpc": "2.0", "id": list_rid, "method": "tools/list", "params": {}})
        tools = {t["name"]: t for t in rid.expect(list_rid)["result"]["tools"]}
        results["tool_names"] = sorted(tools)
        expectations = [
            ("list_milestones", "readOnlyHint", True),
            ("get_milestone", "readOnlyHint", True),
            ("create_milestone", "destructiveHint", True),
            ("create_milestone", "readOnlyHint", False),
        ]
        for name, key, want in [e for e in expectations if e[1]]:
            ann = (tools.get(name) or {}).get("annotations") or {}
            results["annotations"][name] = {
                "readOnlyHint": ann.get("readOnlyHint"),
                "destructiveHint": ann.get("destructiveHint"),
                "idempotentHint": ann.get("idempotentHint"),
            }
            got = ann.get(key)
            print(f"{name} {key}: {got!r}")
            if got is not want:
                failures.append(f"{name} annotation {key}: got {got!r}, want {want!r}")
        for name in ("list_milestones", "get_milestone", "create_milestone"):
            if name not in tools:
                failures.append(f"tool missing from tools/list: {name}")

        # ---- 3. create_milestone ------------------------------------------------
        created = rid.call_tool("create_milestone", {
            "owner": owner, "name": repo, "title": title, "description": description})
        if isinstance(created, dict) and "error" in created:
            code = created["error"].get("code")
            results["create"] = created
            if code == "http_422":
                # Duplicate-title repeat: acceptable per the issue's idempotency
                # note — record which case occurred and continue; the milestone
                # already exists and steps 4–5 will find it.
                results["duplicate_status"] = ("422 duplicate-title (acceptable per issue "
                                               "idempotency note): milestone already exists")
                print(f"create: 422 duplicate (acceptable): {created['error'].get('message','')[:200]}")
            else:
                failures.append(f"create_milestone failed: {created['error']}")
        else:
            results["create"] = created
            milestone_id = (created or {}).get("id")
            print(f"create OK: id={milestone_id} title={created.get('title')!r} "
                  f"state={created.get('state')!r} due_on={created.get('due_on')!r}")

        # ---- 4. list_milestones ---------------------------------------------------
        items = rid.call_tool("list_milestones", {"owner": owner, "name": repo})
        if isinstance(items, dict) and "error" in items:
            failures.append(f"list_milestones failed: {items['error']}")
            items = []
        match = next((m for m in items if isinstance(m, dict) and m.get("title") == title), None)
        results["list_contains_title"] = match is not None
        if match is None:
            failures.append(f"milestone title {title!r} not present in list_milestones result")
        else:
            results["milestone"] = match
            if isinstance(match.get("id"), int) and match["id"] >= 1:
                milestone_id = match["id"]
            print(f"list OK: {len(items)} milestone(s); matched {title!r} id={match.get('id')} "
                  f"state={match.get('state')}")

        # ---- 5. get_milestone by id ---------------------------------------------
        if milestone_id is None:
            failures.append("no milestone id available to call get_milestone with")
        else:
            got = rid.call_tool("get_milestone", {"owner": owner, "name": repo, "id": milestone_id})
            if isinstance(got, dict) and "error" in got:
                failures.append(f"get_milestone failed: {got['error']}")
            else:
                base = results["milestone"] or results.get("create") or {}
                checks = {}
                for field in ("title", "state", "open_issues", "closed_issues",
                              "created_at", "updated_at", "description", "due_on"):
                    if field in base:
                        checks[field] = (got.get(field) == base[field])
                results["get_round_trip"] = checks
                if not all(checks.values()):
                    failures.append(f"get_milestone field mismatch: {checks}")
                print(f"get OK: title={got.get('title')!r} state={got.get('state')!r} "
                      f"open/closed={got.get('open_issues')}/{got.get('closed_issues')} "
                      f"roundtrip={checks}")
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

    results["failures"] = failures
    print("\n=== ACCEPTANCE RESULT ===")
    print(json.dumps(results, indent=2, ensure_ascii=False))
    print("ACCEPTANCE " + ("FAILED" if failures else "PASSED"), file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

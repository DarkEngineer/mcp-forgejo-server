#!/usr/bin/env bash
# install.sh — install the Forgejo MCP server onto this machine.
#
#   1. Copies the self-contained runtime (binary + .NET runtime files) into
#      <PREFIX>/lib/forgejo-mcp-server  (default PREFIX: /usr/local,
#      falling back to ~/.local when /usr/local is not writable and ! root).
#   2. Creates a `forgejo-mcp-server` launcher on PATH in <PREFIX>/bin.
#   3. Runs a live MCP handshake (initialize + tools/list over stdio) to
#      prove the server starts and exposes its tools.
#   4. If Hermes Agent (`hermes`) is on PATH, registers the server in its
#      MCP config as `forgejo` using FORGEJO_URL / FORGEJO_TOKEN if set.
#
# Self-contained: no .NET SDK or runtime is required on the target machine.
#
# Environment:
#   PREFIX           installation root (default /usr/local)
#   FORGEJO_URL      required for the handshake step (dummy value if unset)
#   FORGEJO_TOKEN    used for the `hermes mcp` registration (optional)

set -euo pipefail

BIN_NAME="forgejo-mcp-server"
SRC="$(cd "$(dirname "$0")" && pwd)"

if [ ! -x "$SRC/$BIN_NAME" ]; then
  echo "ERROR: $SRC/$BIN_NAME not found or not executable." >&2
  exit 1
fi

# ---- resolve installation root -------------------------------------------
PREFIX="${PREFIX:-/usr/local}"
if [ "$(id -u)" -ne 0 ] && [ ! -w "$PREFIX" ]; then
  PREFIX="$HOME/.local"
  echo "NOTE: /usr/local not writable and not root — using $PREFIX"
fi
LIBDIR="$PREFIX/lib/$BIN_NAME"
BINDIR="$PREFIX/bin"

mkdir -p "$LIBDIR" "$BINDIR"

echo "==> copying runtime files -> $LIBDIR"
cp -a "$SRC/." "$LIBDIR/"
chmod +x "$LIBDIR/$BIN_NAME"

echo "==> creating launcher -> $BINDIR/$BIN_NAME"
cat > "$BINDIR/$BIN_NAME" <<LAUNCH
#!/usr/bin/env bash
exec "$LIBDIR/$BIN_NAME" "\$@"
LAUNCH
chmod +x "$BINDIR/$BIN_NAME"

case ":$PATH:" in
  *":$BINDIR:"*) : ;;
  *) echo "NOTE: $BINDIR is not on PATH. Add it to your shell profile:"
     echo "       export PATH=\"$BINDIR:\$PATH\"   # add to ~/.bashrc" ;;
esac

# ---- 3. live MCP handshake (proves the installed binary works) ------------
echo "==> running MCP handshake smoke test (initialize + tools/list)"
HANDSHAKE_URL="${FORGEJO_URL:-https://handshake.invalid}"
if command -v python3 >/dev/null 2>&1; then
  FORGEJO_URL="$HANDSHAKE_URL" python3 - "$LIBDIR/$BIN_NAME" <<'PY'
import json, subprocess, sys

bin_path = sys.argv[1]
p = subprocess.Popen([bin_path], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, text=True, bufsize=1)

def send(obj):
    p.stdin.write(json.dumps(obj) + "\n"); p.stdin.flush()

def expect(want_id, timeout=20):
    import time; t0 = time.time()
    while time.time() - t0 < timeout:
        line = p.stdout.readline()
        if not line:
            raise SystemExit("server closed early; stderr=" + repr(p.stderr.read()))
        msg = json.loads(line)
        if msg.get("id") == want_id:
            if "error" in msg: raise SystemExit(json.dumps(msg["error"]))
            return msg
    raise SystemExit("timeout waiting for id " + str(want_id))

try:
    send({"jsonrpc": "2.0", "id": 1, "method": "initialize",
          "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                     "clientInfo": {"name": "install-smoke", "version": "0"}}})
    info = expect(1)["result"]["serverInfo"]
    print(f"    initialize OK: {info['name']} v{info.get('version', '?')}")
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    tools = [t["name"] for t in expect(2)["result"]["tools"]]
    print(f"    tools ({len(tools)}): {', '.join(tools)}")
finally:
    p.stdin.close(); p.terminate()
    try: p.wait(timeout=5)
    except subprocess.TimeoutExpired: p.kill()
PY
else
  echo "NOTE: python3 not found — skipping handshake (binary --help check only)."
  env -u FORGEJO_URL "$BINDIR/$BIN_NAME" --help >/dev/null
fi

# ---- 4. Hermes Agent registration (optional, best-effort) ------------------
if command -v hermes >/dev/null 2>&1; then
  if [ -n "${FORGEJO_URL:-}" ]; then
    echo "==> registering MCP server in Hermes Agent (hermes mcp)"
    hermes mcp remove forgejo >/dev/null 2>&1 || true
    if [ -n "${FORGEJO_TOKEN:-}" ]; then
      hermes mcp add forgejo --command "$LIBDIR/$BIN_NAME" \
        --env "FORGEJO_URL=$FORGEJO_URL" "FORGEJO_TOKEN=$FORGEJO_TOKEN" \
        || echo "NOTE: `hermes mcp add` failed — register manually (see README.md)."
    else
      hermes mcp add forgejo --command "$LIBDIR/$BIN_NAME" \
        --env "FORGEJO_URL=$FORGEJO_URL" \
        || echo "NOTE: `hermes mcp add` failed — register manually (see README.md)."
    fi
  else
    echo "NOTE: FORGEJO_URL not set — skipping Hermes Agent registration."
    echo "      After setting FORGEJO_URL, run: bash install.sh  (re-run is idempotent)"
  fi
fi

echo "==> install complete"
echo "    server binary: $LIBDIR/$BIN_NAME"
echo "    PATH launcher: $BINDIR/$BIN_NAME"
echo "    next steps:    export FORGEJO_URL=... FORGEJO_TOKEN=...   (see README.md)"

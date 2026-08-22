#!/usr/bin/env bash
# package.sh — build a self-contained, installable Forgejo MCP server package.
#
# Usage:
#   scripts/package.sh [VERSION]          # VERSION defaults to YYYYMMDD
#
# Outputs (in dist/, the only artifacts that remain):
#   forgejo-mcp-server-<VERSION>-linux-x64.tar.gz
#   forgejo-mcp-server-<VERSION>-linux-x64.tar.gz.sha256
#
# The package is fully self-contained: the .NET 10 runtime, all NuGet
# dependencies, appsettings.json, install.sh, README.md, LICENSE and a
# manifest.json are inside the tarball — the target machine needs no
# .NET SDK or runtime at all.
#
# Environment:
#   RID          override the target runtime id (default: linux-x64)
#   NUGET_SOURCES  extra NuGet package source (default: nuget.org)
#   DOTNET_ROOT  dotnet SDK location (auto-discovered if unset)

set -euo pipefail

cd "$(dirname "$0")/.."
REPO_ROOT="$(pwd)"

# ---------- 0. Environment --------------------------------------------------
# Discover the dotnet SDK (~/.dotnet in this project's standard layout).
if [ -z "${DOTNET_ROOT:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
fi
if [ -z "${DOTNET_ROOT:-}" ] && command -v dotnet >/dev/null 2>&1; then
  DOTNET_ROOT_BIN="$(command -v dotnet)"
  export DOTNET_ROOT="$(cd "$(dirname "$DOTNET_ROOT_BIN")" && pwd)"
fi
if [ -n "${DOTNET_ROOT:-}" ]; then
  case ":$PATH:" in
    *":$DOTNET_ROOT:"*) : ;;
    *) export PATH="$DOTNET_ROOT:$PATH" ;;
  esac
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet SDK not found on PATH or in ~/.dotnet." >&2
  echo "Install the .NET 10 SDK (https://dotnet.microsoft.com/download) or set DOTNET_ROOT." >&2
  exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Target RID must match the build host architecture (the package is
# self-contained and NOT portable across archs).
ARCH="$(uname -m)"
case "$ARCH" in
  x86_64|amd64)  RID="${RID:-linux-x64}" ;;
  aarch64|arm64) RID="${RID:-linux-arm64}" ;;
  *) echo "ERROR: unsupported architecture '$ARCH' (only x86_64 and aarch64 are supported)." >&2; exit 1 ;;
esac

NAME="forgejo-mcp-server"
VERSION="${1:-$(date +%Y%m%d)}"
OUT="$REPO_ROOT/dist"
PKG="$NAME-$VERSION-$RID"   # the top-level dir inside the tarball

GIT_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo 'unknown')"
GIT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo 'unknown')"

echo "==> .NET SDK:      $(dotnet --version)"
echo "==> Target RID:    $RID"
echo "==> Package:       $PKG"
echo "==> git sha:       $GIT_SHA ($GIT_BRANCH)"

# ---------- 1. Build & test (Release) --------------------------------------
echo "==> dotnet build (Release)"
dotnet build "$REPO_ROOT/ForgejoMcp.sln" -c Release --nologo

echo "==> dotnet test (Release)"
dotnet test  "$REPO_ROOT/ForgejoMcp.sln" -c Release --no-build --nologo --verbosity quiet

# ---------- 2. Publish self-contained -------------------------------------
PUB="$OUT/.publish-$PKG"
STAGE="$OUT/.stage-$PKG"
rm -rf "$PUB" "$STAGE"
mkdir -p "$PUB" "$STAGE/$PKG"

echo "==> dotnet publish --self-contained ($RID)"
dotnet publish "$REPO_ROOT/src/ForgejoMcp/ForgejoMcp.csproj" \
  -c Release -o "$PUB" \
  --self-contained true \
  -r "$RID" \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  --nologo

# Rename the apphost from its default (ForgejoMcp) to the canonical CLI name
# used by install.sh, the README examples, and the MCP client registration.
mv "$PUB/ForgejoMcp" "$PUB/$NAME"
chmod +x "$PUB/$NAME"

# ---------- 3. Assemble the stage root -------------------------------------
DEST="$STAGE/$PKG"
echo "==> assembling package tree -> $DEST"
cp -a "$PUB/." "$DEST/"

# Drop symbols and dev artifacts that publish may have copied — not needed
# at runtime, pure weight.
find "$DEST" -type f \( -name '*.pdb' -o -name '*.xml' \) -not -name 'appsettings*.json' -delete 2>/dev/null || true

# Ship the installer + a package-local README + the license.
install_tpl="$REPO_ROOT/scripts/package"
cp "$install_tpl/install.sh" "$DEST/install.sh"
chmod +x "$DEST/install.sh"
cp "$install_tpl/README.md"  "$DEST/README.md"
cp "$REPO_ROOT/LICENSE"      "$DEST/LICENSE"

# ---------- 4. manifest.json + sha256 (per-file audit for the bundle) -----
echo "==> writing manifest.json (per-file sha256 for every shipped file)"
python3 - "$DEST" "$PKG" "$NAME" "$VERSION" "$RID" "$ARCH" "$GIT_SHA" "$GIT_BRANCH" <<'PY'
import hashlib, json, os, sys, time

dest, pkg, name, ver, rid, arch, sha, branch = sys.argv[1:9]
files = []
for root, dirs, fs in os.walk(dest):
    for f in sorted(fs):
        p = os.path.join(root, f)
        rel = os.path.relpath(p, dest)
        h = hashlib.sha256()
        with open(p, "rb") as fp:
            for chunk in iter(lambda: fp.read(1024 * 1024), b""):
                h.update(chunk)
        files.append({"path": rel, "sha256": h.hexdigest(), "size": os.path.getsize(p)})
files.sort(key=lambda x: x["path"])

manifest = {
    "name": name,
    "version": ver,
    "description": "MCP server exposing Forgejo/Gitea operations over stdio.",
    "runtime": {"rid": rid, "arch": arch, "os": "linux",
                "dotnet": "10", "self_contained": True},
    "entrypoint": name,
    "source": {"git_sha": sha, "git_branch": branch,
               "repo": "dark-eternity/mcp-forgejo-server"},
    "built_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "file_count": len(files),
    "files": files,
}
with open(os.path.join(dest, "manifest.json"), "w") as fp:
    json.dump(manifest, fp, indent=2)
    fp.write("\n")
print(f"    {len(files)} files, total {sum(f['size'] for f in files):,} bytes")
PY

# ---------- 5. Pack the tar.gz ---------------------------------------------
TARBALL="$OUT/$PKG.tar.gz"
rm -f "$TARBALL" "$TARBALL.sha256"
echo "==> creating tar.gz -> $TARBALL"
tar -C "$STAGE" -czf "$TARBALL" "$PKG"

echo "==> generating sha256"
( cd "$OUT" && sha256sum "$PKG.tar.gz" > "$PKG.tar.gz.sha256" )

# ---------- 6. Clean scratch, report ---------------------------------------
rm -rf "$PUB" "$STAGE"
# Leave any stale stage dirs from previous runs behind (best-effort prune).
find "$OUT" -maxdepth 1 -type d -name '.stage-*' -exec rm -rf {} + 2>/dev/null || true
find "$OUT" -maxdepth 1 -type d -name '.publish-*' -exec rm -rf {} + 2>/dev/null || true

SIZE="$(du -h "$TARBALL" | awk '{print $1}')"
echo
echo "==> done"
echo "    artifact:      $TARBALL  ($SIZE)"
echo "    checksum:      $(cat "$TARBALL.sha256")"
echo
echo "    install:       tar -xzf $PKG.tar.gz && cd $PKG && bash install.sh"
echo "    smoke test:    FORGEJO_URL=*** forgejo-mcp-server  (then MCP handshake)"

#!/usr/bin/env bash
# publish_release.sh — create a Forgejo release for the current tag and attach
# the dist/*.tar.gz and its *.sha256 sidecar as assets.

set -euo pipefail

: "${FORGEJO_TOKEN:?publish_release.sh: FORGEJO_TOKEN not set}"

# Jeśli FORGEJO_API_URL nie jest ustawiony, spróbuj zbudować go ze standardowych zmiennych Actions
if [ -z "${FORGEJO_API_URL:-}" ]; then
  SERVER_URL="${GITHUB_SERVER_URL:-https://forgejo.example.com}"
  FORGEJO_API_URL="${SERVER_URL%/}/api/v1"
fi

REPO="${GITHUB_REPOSITORY:-dark-eternity/mcp-forgejo-server}"

# Pobieranie tagu z kontekstu runnera
TAG="${RELEASE_TAG:-${GITHUB_REF_NAME:-}}"
[ -n "$TAG" ] || { TAG="${GITHUB_REF#refs/tags/}"; : "${TAG:=$GITHUB_REF}"; }
[ -n "$TAG" ] || { echo "publish_release.sh: no tag (RELEASE_TAG/GITHUB_REF_NAME unset)" >&2; exit 1; }

API="${FORGEJO_API_URL%/}"
# NAPRAWIONE: Użycie właściwej zmiennej $FORGEJO_TOKEN zamiast maskowanego tekstu "***"
AUTH="Authorization: token ${FORGEJO_TOKEN}"
REPO_API="$API/repos/$REPO"

TARBALL="$(ls dist/*.tar.gz 2>/dev/null | head -n1 || true)"
[ -n "$TARBALL" ] || { echo "publish_release.sh: no dist/*.tar.gz; run scripts/package.sh first" >&2; exit 1; }
SHAFILE="$TARBALL.sha256"
[ -f "$SHAFILE" ] || { echo "publish_release.sh: missing sha256 sidecar $SHAFILE" >&2; exit 1; }
BNAME="$(basename "$TARBALL")"
SIZE="$(du -h "$TARBALL" | awk '{print $1}')"

echo "=== publishing tag: $TAG" >&2
echo "=== artifact: $BNAME ($SIZE)" >&2

json_field() {
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); v=d["id" if sys.argv[2]=="id" else sys.argv[2]]; print(v)' "$1" "$2" 2>/dev/null || true
}
asset_ids() {
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(" ".join(str(a["id"]) for a in d.get("assets",[])))' "$1" 2>/dev/null || true
}

# --- 0. Idempotency: drop any pre-existing release for this tag ------------
EXISTS_JSON="$(mktemp)"
curl -fsS -H "$AUTH" "$REPO_API/releases/tags/$TAG" -o "$EXISTS_JSON" 2>/dev/null || true
if [ -s "$EXISTS_JSON" ] && python3 -c 'import json,sys; sys.exit(0 if "id" in json.load(open(sys.argv[1])) else 1)' "$EXISTS_JSON" 2>/dev/null; then
  OLDRID="$(json_field "$EXISTS_JSON" id)"
  echo "=== pre-existing release $OLDRID for $TAG — detaching assets" >&2
  for AID in $(asset_ids "$EXISTS_JSON"); do
    curl -sS -o /dev/null -w "  del asset $AID: %{http_code}\n" \
      -X DELETE -H "$AUTH" "$REPO_API/releases/$OLDRID/assets/$AID"
  done
  curl -sS -o /dev/null -w "  del release $OLDRID: %{http_code}\n" \
    -X DELETE -H "$AUTH" "$REPO_API/releases/$OLDRID"
fi
rm -f "$EXISTS_JSON"

# --- 1. Create the release --------------------------------------------------
BODY_FILE="$(mktemp)"
python3 -c '
import json, sys
tag = sys.argv[1]
body = {
    "tag_name": tag,
    "name": tag,
    "body": (
        "Built by Forgejo Actions.\n\n"
        "Self-contained Linux package: bundles the .NET runtime, so the "
        "target machine needs no .NET install.\n\n"
        "Install:\n"
        "    tar -xzf <package>.tar.gz\n"
        "    cd <package>\n"
        "    ./install.sh\n\n"
        "SHA-256: see the .sha256 sidecar attachment."
    ),
    "draft": False,
}
open(sys.argv[2], "w").write(json.dumps(body))
' "$TAG" "$BODY_FILE"

CREATE_JSON="$(mktemp)"
CREATE_CODE="$(curl -sS -o "$CREATE_JSON" -w "%{http_code}" \
  -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  --data-binary "@$BODY_FILE" "$REPO_API/releases")"
rm -f "$BODY_FILE"

if [ "$CREATE_CODE" != "201" ]; then
  echo "release create: HTTP $CREATE_CODE (want 201)" >&2
  head -c 400 "$CREATE_JSON"; echo >&2
  rm -f "$CREATE_JSON"
  exit 1
fi

RID="$(json_field "$CREATE_JSON" id)"
rm -f "$CREATE_JSON"
[ -n "$RID" ] || { echo "no release id in create response" >&2; exit 1; }
echo "=== release id: $RID" >&2

# --- 2. Attach the artifacts ------------------------------------------------
for pair in "$BNAME:$TARBALL" "${BNAME}.sha256:$SHAFILE"; do
  name="${pair%%:*}"
  file="${pair#*:}"
  ATT_JSON="$(mktemp)"
  ATT_CODE="$(curl -sS -o "$ATT_JSON" -w "%{http_code}" \
    -X POST -H "$AUTH" \
    -F "attachment=@$file" \
    "$REPO_API/releases/$RID/assets?name=$name")"
  if [ "$ATT_CODE" != "201" ]; then
    echo "attach $name: HTTP $ATT_CODE (want 201)" >&2
    head -c 400 "$ATT_JSON"; echo >&2
    rm -f "$ATT_JSON"
    exit 1
  fi
  echo "=== attached: $name" >&2
  rm -f "$ATT_JSON"
done

# --- 3. Verify the release as the Forgejo UI sees it -------------------------
FINAL_JSON="$(mktemp)"
if ! curl -fsS -H "$AUTH" "$REPO_API/releases/tags/$TAG" -o "$FINAL_JSON"; then
  echo "verify: GET releases/tags/$TAG failed" >&2
  rm -f "$FINAL_JSON"
  exit 1
fi

python3 -c '
import json, sys
d = json.load(open(sys.argv[1]))
print(f"release: {d[\"id\"]} {d[\"tag_name\"]} -> {d.get(\"html_url\")}")
for a in d.get("assets", []):
    print(f"  asset: {a[\"name\"]} ({a[\"size\"]} bytes) -> {a.get(\"browser_download_url\")}")
' "$FINAL_JSON"
rm -f "$FINAL_JSON"

echo "=== done: release $TAG is live with its artifacts" >&2
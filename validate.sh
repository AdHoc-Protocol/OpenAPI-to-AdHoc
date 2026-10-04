#!/usr/bin/env bash
# Validates every AdHoc protocol description (.cs) in a folder with AdHocAgent's local parse-only mode.
# Nothing is uploaded. Each file is copied to a temp folder first because the agent rewrites the file in place.
#
#   ./validate.sh <folder with .cs files>          # exit 0 when every file is clean
#   AGENT=/path/to/AdHocAgent.exe ./validate.sh …  # override the agent binary
#
# The agent reads your personal UUID from the AdHocAgent.toml next to it (AdHoc manual, "UUID"): point AGENT
# at a build that has one.

set -u
DIR="${1:?folder with .cs files}"
# The agent binary: $AGENT, else the first hit on PATH, else a local build.
AGENT="${AGENT:-$(command -v AdHocAgent.exe || command -v AdHocAgent || echo "")}"
if [ -z "$AGENT" ]; then
    for c in ./AdHocAgent.exe ../../AdHocAgent/bin/Debug/net10.0/AdHocAgent.exe ../AdHocAgent/bin/Release/net10.0/AdHocAgent.exe ../AdHocAgent/bin/Debug/net10.0/AdHocAgent.exe; do
        [ -x "$c" ] && AGENT="$(cd "$(dirname "$c")" && pwd)/$(basename "$c")" && break
    done
fi
if [ -z "$AGENT" ]; then
    echo "AdHocAgent not found. Build it from https://github.com/AdHoc-Protocol/AdHoc-protocol and set AGENT=/path/to/AdHocAgent.exe" >&2
    exit 127
fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

status=0
shopt -s nullglob
for f in "$DIR"/*.cs; do
    n="$(basename "$f" .cs)"
    cp "$f" "$TMP/$n.cs"
    ( cd "$(dirname "$AGENT")" && ADHOC_PARSE_ONLY=1 timeout 600 "$AGENT" "$TMP/$n.cs" > "$TMP/$n.log" 2>&1 < /dev/null )
    rc=$?
    bad="$(grep -c ' ERR\]\| WRN\]\| FTL\]\|Unhandled exception' "$TMP/$n.log")"
    if [ "$rc" -eq 0 ] && [ "$bad" -eq 0 ]; then
        printf '%-32s OK\n' "$n"
    else
        status=1
        printf '%-32s FAILED (exit %s)\n' "$n" "$rc"
        grep ' ERR\]\| WRN\]\| FTL\]\|Unhandled exception\|   at ' "$TMP/$n.log" | head -20 | sed 's/^/    /'
    fi
done
exit $status

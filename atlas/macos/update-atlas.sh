#!/bin/bash
# Updates Atlas Server on this Mac from the GitHub releases. Works over SSH.
#
#   update-atlas.sh            the latest release
#   update-atlas.sh 1.0.7      that release instead (ATLAS_VERSION=1.0.7 works too)
#   FORCE=1 update-atlas.sh    reinstall even if it's already that version
#
# "Latest" is the newest published release: a draft from the build workflow doesn't count until
# it's published (or pass its tag).
set -euo pipefail

REPO="besupernow/jellyfin-atlas"
APP="/Applications/Atlas Server.app"
PORT=8096

case "$(uname -m)" in
  arm64) ASSET="atlas-server-macos-arm64.dmg" ;;
  *) ASSET="atlas-server-macos-x64.dmg" ;;
esac

VERSION="${1:-${ATLAS_VERSION:-}}"
if [ -z "$VERSION" ]; then
  VERSION=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
    | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)
  if [ -z "$VERSION" ]; then
    echo "Couldn't find the latest release. Is it still a draft? Publish it, or pass the tag." >&2
    exit 1
  fi
fi

CURRENT=$(sed -n 's/^tag=//p' "$APP/Contents/Resources/ATLAS_BUILD" 2>/dev/null || true)
if [ "$CURRENT" = "$VERSION" ] && [ "${FORCE:-0}" != 1 ]; then
  echo "Already on $VERSION (FORCE=1 to reinstall)."
  exit 0
fi
echo "Updating Atlas Server ${CURRENT:-?} -> $VERSION"

WORK=$(mktemp -d /tmp/atlas-update.XXXXXX)
MOUNT="$WORK/mnt"
cleanup() {
  hdiutil detach "$MOUNT" -quiet 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

# Download and check it before touching the running server.
curl -fL --progress-bar -o "$WORK/atlas.dmg" "https://github.com/$REPO/releases/download/$VERSION/$ASSET"
mkdir -p "$MOUNT"
hdiutil attach "$WORK/atlas.dmg" -nobrowse -readonly -noautoopen -mountpoint "$MOUNT" -quiet
if [ ! -d "$MOUNT/Atlas Server.app" ]; then
  echo "No Atlas Server.app in that DMG." >&2
  exit 1
fi

server_listening() { lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; }

# The menu bar app first (it would restart the server), then the server itself. No AppleScript
# "quit": over SSH that asks for Automation permission and hangs on the prompt.
echo "Stopping Atlas Server..."
pkill -x AtlasServer 2>/dev/null || true
pkill -f "Atlas Server.app/Contents/Resources/server/jellyfin" 2>/dev/null || true
for _ in $(seq 1 30); do
  server_listening || break
  sleep 1
done
if server_listening; then
  echo "Still on port $PORT after 30s, forcing it."
  pkill -9 -f "Atlas Server.app/Contents/Resources/server/jellyfin" 2>/dev/null || true
  sleep 2
fi
if server_listening; then
  echo "Something else is on port $PORT:" >&2
  lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >&2 || true
  exit 1
fi

echo "Installing..."
rm -rf "$APP.old"
if [ -d "$APP" ]; then mv "$APP" "$APP.old"; fi
if ! ditto "$MOUNT/Atlas Server.app" "$APP"; then
  echo "Install failed, putting the old version back." >&2
  rm -rf "$APP"
  if [ -d "$APP.old" ]; then mv "$APP.old" "$APP"; fi
  exit 1
fi
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
rm -rf "$APP.old"

# Whether macOS asks for Removable Volumes / Full Disk Access again depends on the build's
# signature, not on this script: a build signed with the Atlas identity keeps the grants.
SIGNATURE=$(codesign -dvv "$APP" 2>&1 || true)
if echo "$SIGNATURE" | grep -q "Signature=adhoc"; then
  echo "Note: this build is ad-hoc signed, so macOS may ask for drive access again (see atlas/macos/README.md)."
fi

# Start it in the logged-in GUI session (a menu bar app needs one). Plain `open` works over SSH
# when this user is logged in at the Mac; otherwise launch through launchd as this user.
echo "Starting $VERSION..."
started() { pgrep -x AtlasServer >/dev/null 2>&1; }
open -a "$APP" 2>/dev/null || true
sleep 3
if ! started; then
  sudo launchctl asuser "$(id -u)" sudo -u "$(id -un)" /usr/bin/open -a "$APP" 2>/dev/null || true
  sleep 3
fi
if ! started; then
  echo "Installed $VERSION, but couldn't start the menu bar app from here. Is $(id -un) logged in at the Mac?" >&2
  echo "Open Atlas Server there (or over Screen Sharing) and it'll pick up the new version." >&2
  exit 1
fi

# Wait until it answers (a first start after an update can run database migrations).
for _ in $(seq 1 120); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/health" || true)" = "200" ]; then
    echo "Atlas Server $VERSION is up."
    exit 0
  fi
  sleep 1
done
echo "Installed $VERSION, but it isn't answering on port $PORT yet. Check the menu bar app or the logs." >&2
exit 1
#!/bin/sh
# Runs the bundled Jellyfin server with the bundled jellyfin-ffmpeg and Atlas ABR on.
DIR="$(cd "$(dirname "$0")" && pwd)"
export ATLAS_ABR_ENABLED="${ATLAS_ABR_ENABLED:-true}"
export ATLAS_ABR_GRID_SECONDS="${ATLAS_ABR_GRID_SECONDS:-2}"
export ATLAS_ABR_IDLE_LEVEL_SECONDS="${ATLAS_ABR_IDLE_LEVEL_SECONDS:-20}"
exec "$DIR/jellyfin/jellyfin" --ffmpeg "$DIR/ffmpeg/ffmpeg" "$@"
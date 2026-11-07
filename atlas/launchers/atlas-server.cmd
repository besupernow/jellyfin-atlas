@echo off
rem Runs the bundled Jellyfin server with the bundled jellyfin-ffmpeg and Atlas ABR on.
set "DIR=%~dp0"
if not defined ATLAS_ABR_ENABLED set ATLAS_ABR_ENABLED=true
if not defined ATLAS_ABR_GRID_SECONDS set ATLAS_ABR_GRID_SECONDS=2
if not defined ATLAS_ABR_IDLE_LEVEL_SECONDS set ATLAS_ABR_IDLE_LEVEL_SECONDS=20
"%DIR%jellyfin\jellyfin.exe" --ffmpeg "%DIR%ffmpeg\ffmpeg.exe" %*
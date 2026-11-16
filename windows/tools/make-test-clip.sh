#!/bin/sh
# Makes src/TylerCast.Core/Resources/TestPattern.h264: 4 seconds (100 frames) of the
# test pattern, pre-encoded, which TylerCast loops for --test-pattern when there is no
# H.264 encoder (the Mac build of the .NET code; a Windows PC whose encoder fails).
# The box crosses the screen once in 100 frames, so the loop is seamless.
#
# Needs ffmpeg with libx264. The frames come from TestPattern itself (tylercast
# dump-pattern), so the clip is exactly what a live encoder would be fed.
# Settings match the TV's profile and the Mac app's VideoToolbox session:
# H.264 Main 4.1, 1920x1080, 25fps, no B-frames, IDR every 2s (frames 0 and 50) with
# SPS/PPS before each, AUD on every access unit, BT.709, 6 Mbit/s capped over 0.5s.
set -eu
cd "$(dirname "$0")/.."
out=src/TylerCast.Core/Resources/TestPattern.h264
mkdir -p "$(dirname "$out")"
dotnet run --project src/TylerCast.Cli -c Release -- dump-pattern 100 |
  ffmpeg -hide_banner -loglevel error -y -f rawvideo -pix_fmt nv12 -s 1920x1080 -r 25 -i - \
    -c:v libx264 -preset slow -profile:v main -level:v 4.1 -pix_fmt yuv420p \
    -b:v 6M -maxrate 6M -bufsize 3M -g 50 -keyint_min 50 -sc_threshold 0 -bf 0 \
    -x264-params aud=1:repeat-headers=1:colorprim=bt709:transfer=bt709:colormatrix=bt709:range=tv \
    -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv \
    -f h264 "$out"
ls -l "$out"

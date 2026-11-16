#!/bin/sh
# Packages one Windows test round and serves its page on the LAN until the results arrive.
#   tools/make-round.sh 1        -> out/round1 (page, script, fresh tylercast.exe), results in results/round1
# The page URL is printed (ROUND PAGE ...); open it on the Windows laptop.
set -eu
cd "$(dirname "$0")/.."
n=${1:?round number}
src=rounds/round$n
out=out/round$n
[ -f "$src/round.ps1" ] || { echo "no $src/round.ps1"; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
PATH="$HOME/.dotnet:$PATH"
rm -rf "$out"
mkdir -p "$out" "results/round$n"
dotnet publish src/TylerCast.Cli -f net10.0-windows -r win-x64 -c Release --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -o "$out/bin" >/dev/null
mv "$out/bin/tylercast.exe" "$out/tylercast.exe"
rm -rf "$out/bin"
cp "$src"/* "$out/"
echo "Round $n: $(ls "$out" | tr '\n' ' ')"
exec python3 tools/round-server.py "$out" "results/round$n" --port "${PORT:-8765}"

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
# rounds/roundN/builds names what to ship: "cli" (tylercast.exe) and/or "app" (TylerCast.exe,
# the tray app). Not both: the Mac's disk ignores case, so the two names are one file here.
builds=$(cat "$src/builds" 2>/dev/null || echo cli)
pub() { # project, exe
  dotnet publish "src/$1" -r win-x64 -c Release --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$out/bin" $3 >/dev/null
  mv "$out/bin/$2" "$out/$2"
  rm -rf "$out/bin"
}
case "$builds" in *cli*) pub TylerCast.Cli tylercast.exe "-f net10.0-windows" ;; esac
case "$builds" in *app*) pub TylerCast.App TylerCast.exe ;; esac
cp "$src"/*.html "$src"/*.ps1 "$out/"
echo "Round $n: $(ls "$out" | tr '\n' ' ')"
exec python3 tools/round-server.py "$out" "results/round$n" --port "${PORT:-8765}"

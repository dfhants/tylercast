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
# rounds/roundN/builds names what to ship: "cli" (tylercast.exe), "app" (TylerCast.exe, the tray
# app) or "installer" (TylerCast-Setup-*.exe, from CI). Not cli and app together: the Mac's disk
# ignores case, so the two names are one file here.
cp "$src"/*.html "$src"/*.ps1 "$out/"
builds=$(cat "$src/builds" 2>/dev/null || echo cli)
pub() { # project, exe
  dotnet publish "src/$1" -r win-x64 -c Release --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$out/bin" ${3:-} >/dev/null
  mv "$out/bin/$2" "$out/$2"
  rm -rf "$out/bin"
}
case "$builds" in *cli*) pub TylerCast.Cli tylercast.exe "-f net10.0-windows" ;; esac
case "$builds" in *app*) pub TylerCast.App TylerCast.exe ;; esac
# The installer is built by CI on Windows (the Mac can't run Inno Setup): take it from the
# CI run for this commit, which must have passed.
case "$builds" in *installer*)
  # RUN=<id> takes a given CI run's installer (e.g. when only the round's own files changed since).
  sha=$(git rev-parse HEAD)
  id=${RUN:-}
  [ -n "$id" ] || id=$(gh run list --branch "$(git branch --show-current)" --commit "$sha" --status success --limit 1 --json databaseId --jq '.[0].databaseId')
  [ -n "$id" ] || { echo "no passing CI run for $sha yet: push, wait for CI, retry"; exit 1; }
  gh run download "$id" -n installer -D "$out"
  setup=$(cd "$out" && ls TylerCast-Setup-*.exe | head -1)
  sed -i '' "s/__SETUP__/$setup/" "$out/round.ps1" 2>/dev/null || true ;;
esac
echo "Round $n: $(ls "$out" | tr '\n' ' ')"
# TOKEN=... reuses an earlier round's page address, so the same link keeps working.
exec python3 tools/round-server.py "$out" "results/round$n" --port "${PORT:-8765}" ${TOKEN:+--token "$TOKEN"}

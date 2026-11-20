#!/usr/bin/env bash
# Live patch check for the cloud threads (same as check.ps1): builds the server from BASE (a commit or full release tag)
# with Live patches on, then has it try to put this checkout's server code into itself. Needs the generator inputs in
# submodule/Imcodec/src/*/GeneratorInput (see the memory note on building the server in the cloud).
#   tools/live-patch-check/check.sh v0.10.933
set -euo pipefail
base="$1"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'git -C "$repo" worktree remove --force "$work/base" >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
git -C "$repo" worktree add --detach "$work/base" "$base" >/dev/null
git -C "$work/base" submodule update --init --recursive >/dev/null
for part in Imcodec.ObjectProperty Imcodec.MessageLayer; do
  mkdir -p "$work/base/submodule/Imcodec/src/$part/GeneratorInput"
  cp "$repo/submodule/Imcodec/src/$part/GeneratorInput/"* "$work/base/submodule/Imcodec/src/$part/GeneratorInput/"
done
dotnet publish "$work/base/src/Imlight.Director/Imlight.Director.csproj" -c Release -r linux-x64 --self-contained true \
  -o "$work/server" -nologo -v q -p:LivePatches=true >/dev/null
if [ ! -f "$work/server/live-patches/baseline/manifest.txt" ]; then
  echo "$base was released before live patches; nothing to check against yet."; exit 0
fi
set +e
DOTNET_MODIFIABLE_ASSEMBLIES=debug "$work/server/Imlight.Director" --live-patch-check "$repo" 2>&1 | grep -E '^(CAN GO LIVE|NEEDS A FULL UPDATE|NOTE|CHECK FAILED)'
exit "${PIPESTATUS[0]}"

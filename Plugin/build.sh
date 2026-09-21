#!/bin/sh
#
#   Build and run the conformance check.
#
#       ./build.sh              builds and runs it
#       ./build.sh --no-run     builds only
#
set -e

root=$(cd "$(dirname "$0")" && pwd)
out="${TMPDIR:-/tmp}/s950build"
run=1

for arg in "$@"; do
    case "$arg" in
        --no-run) run=0 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

mkdir -p "$out"
exe="$out/ConformanceCheck"

echo "building 3 files -> $exe"

c++ -std=c++17 -O2 -Wall -Wextra \
    -I "$root/Source/S950" \
    "$root/Tests/ConformanceCheck.cpp" \
    "$root/Source/S950/Voice.cpp" \
    "$root/Source/S950/Engine.cpp" \
    -o "$exe"

echo "ok  -  $(( $(wc -c < "$exe") / 1024 )) KB"

[ "$run" -eq 1 ] || exit 0

"$exe"

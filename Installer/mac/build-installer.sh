#!/bin/sh
#
#   Build the plugin and package it as a .pkg.
#
#       ./build-installer.sh              build everything, then the installer
#       ./build-installer.sh --skip-build package whatever is already built
#       ./build-installer.sh --check-only just say whether the pieces are there
#
set -e

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)

artefacts="$repo/Plugin/build/VirtualS950_artefacts/Release"
output="$repo/Installer/Output"
staging="$repo/Installer/Output/staging"

version=$(sed -n 's/^#define AppVersion  *"\(.*\)"/\1/p' "$repo/Installer/VirtualS950.iss")
[ -n "$version" ] || { echo "no AppVersion in Installer/VirtualS950.iss" >&2; exit 1; }

skip_build=0
check_only=0

for arg in "$@"; do
    case "$arg" in
        --skip-build) skip_build=1 ;;
        --check-only) check_only=1 ;;
        *) echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

if [ "$skip_build" -eq 0 ] && [ "$check_only" -eq 0 ]; then
    echo
    echo "  building the plugin"
    cmake -S "$repo/Plugin" -B "$repo/Plugin/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
    cmake --build "$repo/Plugin/build" --parallel
fi

echo
echo "  what would be packaged"
echo

missing=""
for pair in \
    "the VST3 bundle|$artefacts/VST3/VirtualS950.vst3" \
    "the AU bundle|$artefacts/AU/VirtualS950.component" \
    "the standalone|$artefacts/Standalone/VirtualS950.app" \
    "the licence|$repo/LICENSE" \
    "the plugin readme|$repo/Plugin/README.md"
do
    what=${pair%%|*}
    path=${pair#*|}
    if [ -e "$path" ]; then
        printf "    %-20s %s  %s\n" "$what" "$(date -r "$path" '+%Y-%m-%d %H:%M')" "$path"
    else
        printf "    %-20s MISSING  %s\n" "$what" "$path"
        missing="$missing $what,"
    fi
done

if [ -n "$missing" ]; then
    echo
    echo "not ready to package:${missing%,}" >&2
    exit 1
fi

if [ "$check_only" -eq 1 ]; then
    echo
    echo "  everything the installer needs is present"
    echo
    exit 0
fi

rm -rf "$staging"
mkdir -p "$staging/vst3" "$staging/au" "$staging/standalone" "$staging/resources" "$staging/pkgs"

ditto "$artefacts/VST3/VirtualS950.vst3"        "$staging/vst3/VirtualS950.vst3"
ditto "$artefacts/AU/VirtualS950.component"     "$staging/au/VirtualS950.component"
ditto "$artefacts/Standalone/VirtualS950.app"   "$staging/standalone/VirtualS950.app"

cp "$repo/LICENSE"           "$staging/resources/LICENSE"
cp "$repo/Plugin/README.md"  "$staging/resources/README-plugin.md"

echo
echo "  packaging $version"

pkgbuild --quiet --root "$staging/vst3" \
         --identifier com.simozzer.VirtualS950.vst3 --version "$version" \
         --install-location /Library/Audio/Plug-Ins/VST3 \
         "$staging/pkgs/VirtualS950-VST3.pkg"

pkgbuild --quiet --root "$staging/au" \
         --identifier com.simozzer.VirtualS950.au --version "$version" \
         --install-location /Library/Audio/Plug-Ins/Components \
         "$staging/pkgs/VirtualS950-AU.pkg"

pkgbuild --quiet --root "$staging/standalone" \
         --identifier com.simozzer.VirtualS950.standalone --version "$version" \
         --install-location /Applications \
         "$staging/pkgs/VirtualS950-Standalone.pkg"

mkdir -p "$output"
pkg="$output/VirtualS950-$version-macos.pkg"

productbuild --quiet \
             --distribution "$here/distribution.xml" \
             --package-path "$staging/pkgs" \
             --resources "$staging/resources" \
             "$pkg"

rm -rf "$staging"

echo
echo "  $pkg  -  $(( $(stat -f%z "$pkg") / 1024 )) KB"
echo

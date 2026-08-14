#!/usr/bin/env bash
#
# The version every artifact is named after, read from the one place it is
# written down.
#
# <Version> in GitGui.csproj is that place. It has to be right regardless of
# what any script is told, because the Flatpak build never passes -p:Version -
# Flathub would not either - so the app reports whatever the csproj says. Making
# it the default everywhere else means the packaging commands in the README carry
# no version number at all, and a release stops being an errand of editing copies
# of it out of documentation.
#
# Source this, don't run it:
#     . "$ROOT/build/version.sh"
#     VERSION="${2:-$(project_version "$ROOT")}"

project_version() {
    local root="${1:?usage: project_version <repository-root>}"
    local csproj="$root/src/GitGui/GitGui.csproj"
    local version

    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj")"

    if [ -z "$version" ]; then
        echo "!! no <Version> in $csproj" >&2
        return 1
    fi

    printf '%s\n' "$version"
}

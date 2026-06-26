#!/usr/bin/env bash
# Build a Cinder AppImage from a published cinder-linux-x64.tar.gz.
# Run from the repo root after `dotnet publish` has produced publish/linux-x64/.
# Requires appimagetool on PATH (https://github.com/AppImage/AppImageKit/releases).

set -euo pipefail

VERSION="${1:-0.2.1}"
PUBLISH_DIR="${2:-publish/linux-x64}"

if [[ ! -x "$PUBLISH_DIR/Cinder" ]]; then
    echo "Expected $PUBLISH_DIR/Cinder (the published Linux binary)." >&2
    exit 2
fi

WORK=$(mktemp -d)
APPDIR="$WORK/Cinder.AppDir"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/512x512/apps" \
         "$APPDIR/usr/share/icons/hicolor/scalable/apps"

# Payload — the entire self-contained Linux publish output.
cp -r "$PUBLISH_DIR"/* "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Cinder"

# Icons + .desktop — copies, not symlinks, so the AppImage is self-contained.
cp packaging/linux/cinder.desktop                    "$APPDIR/cinder.desktop"
cp packaging/linux/cinder.desktop                    "$APPDIR/usr/share/applications/cinder.desktop"
cp assets/branding/png/cinder-512.png                "$APPDIR/cinder.png"
cp assets/branding/png/cinder-512.png                "$APPDIR/usr/share/icons/hicolor/512x512/apps/cinder.png"
cp assets/branding/cinder-logo.svg                   "$APPDIR/usr/share/icons/hicolor/scalable/apps/cinder.svg"

# AppRun — the launcher AppImage looks for at the root of the AppDir.
cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${0}")")"
export PATH="${HERE}/usr/bin:${PATH}"
exec "${HERE}/usr/bin/Cinder" "$@"
EOF
chmod +x "$APPDIR/AppRun"

OUTPUT="Cinder-${VERSION}-x86_64.AppImage"
appimagetool "$APPDIR" "$OUTPUT"
echo "Built $OUTPUT"

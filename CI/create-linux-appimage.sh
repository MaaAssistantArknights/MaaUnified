#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 || $# -gt 4 ]]; then
  echo "Usage: $0 <staging-dir> <release-dir> <package-name> [appimage-arch]" >&2
  exit 1
fi

staging_dir="$1"
release_dir="$2"
package_name="$3"
appimage_arch="${4:-x86_64}"

if [[ ! -x "$staging_dir/MAAUnified" ]]; then
  echo "Linux root launcher not found or not executable: $staging_dir/MAAUnified" >&2
  exit 1
fi

if [[ ! -x "$staging_dir/bin/MAAUnified" ]]; then
  echo "Managed app executable not found: $staging_dir/bin/MAAUnified" >&2
  exit 1
fi

if [[ ! -f "$staging_dir/libMaaCore.so" ]]; then
  echo "MaaCore shared library not found: $staging_dir/libMaaCore.so" >&2
  exit 1
fi

if [[ ! -d "$staging_dir/resource" ]]; then
  echo "MaaCore resource directory not found: $staging_dir/resource" >&2
  exit 1
fi

mkdir -p "$release_dir"

work_dir="$release_dir/appimage-build"
app_dir="$work_dir/MAAUnified.AppDir"
payload_dir="$app_dir/usr/share/maaunified"
output_path="$release_dir/$package_name.AppImage"

rm -rf "$work_dir" "$output_path"
mkdir -p "$payload_dir" "$app_dir/usr/share/icons/hicolor/512x512/apps" "$app_dir/usr/share/metainfo"

cp -a "$staging_dir/." "$payload_dir/"

wget -c https://raw.githubusercontent.com/MaaAssistantArknights/design/main/logo/maa-logo_512x512.png -O "$app_dir/MAAUnified.png"
cp -v "$app_dir/MAAUnified.png" "$app_dir/usr/share/icons/hicolor/512x512/apps/MAAUnified.png"

cat > "$app_dir/MAAUnified.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=MAAUnified
Icon=MAAUnified
Exec=AppRun
Terminal=false
Categories=Game;StrategyGame;
Comment=An Arknights assistant
EOF

cat > "$app_dir/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
app_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
payload_dir="$app_dir/usr/share/maaunified"
cd "$payload_dir"
exec "$payload_dir/MAAUnified" "$@"
EOF
chmod a+x "$app_dir/AppRun"

if [[ -f tools/AppImage/io.github.maaassistantarknights.maaassistantarknights.metainfo.xml ]]; then
  cp -v tools/AppImage/io.github.maaassistantarknights.maaassistantarknights.metainfo.xml "$app_dir/usr/share/metainfo/"
fi

appimagetool="$work_dir/appimagetool-x86_64.AppImage"
runtime_file="$work_dir/runtime-fuse3-$appimage_arch"
wget "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" -O "$appimagetool"
# appimagetool with embedded runtime does not support cross build, till AppImage/appimagetool 7a10b8
wget "https://github.com/AppImage/type2-runtime/releases/download/old/runtime-fuse3-$appimage_arch" -O "$runtime_file"
chmod a+x "$appimagetool"

ARCH="$appimage_arch" "$appimagetool" --runtime-file "$runtime_file" "$app_dir" "$output_path"
chmod a+x "$output_path"
test -f "$output_path"

rm -rf "$work_dir"

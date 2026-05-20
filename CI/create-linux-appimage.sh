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
brand_icon_path="src/MAAUnified/App/Assets/Brand/newlogo.ico"
desktop_id="maaunified.desktop"
icon_name="maaunified"

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
output_path="$release_dir/$package_name.AppImage"

rm -rf "$work_dir" "$output_path"
mkdir -p \
  "$app_dir/usr/share/applications" \
  "$app_dir/usr/share/icons/hicolor/256x256/apps" \
  "$app_dir/usr/share/pixmaps" \
  "$app_dir/usr/share/metainfo"

desktop_entry_path="$app_dir/usr/share/applications/$desktop_id"
root_desktop_entry_path="$app_dir/$desktop_id"
icon_theme_path="$app_dir/usr/share/icons/hicolor/256x256/apps/$icon_name.png"
pixmaps_icon_path="$app_dir/usr/share/pixmaps/$icon_name.png"
root_icon_path="$app_dir/$icon_name.png"

if [[ ! -f "$brand_icon_path" ]]; then
  echo "App icon source not found: $brand_icon_path" >&2
  exit 1
fi

python3 - "$brand_icon_path" "$icon_theme_path" <<'PY'
import struct
import sys

source_path, output_path = sys.argv[1], sys.argv[2]
data = open(source_path, "rb").read()

if len(data) < 6:
    raise SystemExit("ICO source is too small.")

reserved, icon_type, count = struct.unpack_from("<HHH", data, 0)
if reserved != 0 or icon_type != 1 or count == 0:
    raise SystemExit("Icon source is not a valid ICO file.")

best_entry = None
for index in range(count):
    entry_offset = 6 + index * 16
    if entry_offset + 16 > len(data):
        raise SystemExit("ICO directory is truncated.")

    width, height, _, _, _, bit_count, image_size, image_offset = struct.unpack_from("<BBBBHHII", data, entry_offset)
    width = 256 if width == 0 else width
    height = 256 if height == 0 else height
    image_end = image_offset + image_size
    if image_offset >= len(data) or image_end > len(data):
        raise SystemExit("ICO image payload is out of range.")

    payload = data[image_offset:image_end]
    if not payload.startswith(b"\x89PNG\r\n\x1a\n"):
        continue

    score = (width * height, bit_count)
    if best_entry is None or score > best_entry[0]:
        best_entry = (score, payload)

if best_entry is None:
    raise SystemExit("ICO source does not contain a PNG icon payload.")

with open(output_path, "wb") as output:
    output.write(best_entry[1])
PY

cp -v "$icon_theme_path" "$pixmaps_icon_path"
ln -s "usr/share/icons/hicolor/256x256/apps/$icon_name.png" "$root_icon_path"
ln -s "$icon_name.png" "$app_dir/.DirIcon"

cat > "$desktop_entry_path" <<EOF
[Desktop Entry]
Type=Application
Name=MAAUnified
Icon=$icon_name
Exec=AppRun
Terminal=false
Categories=Game;StrategyGame;
Comment=An Arknights assistant
X-AppImage-Name=MAAUnified
X-AppImage-Arch=$appimage_arch
EOF

ln -s "usr/share/applications/$desktop_id" "$root_desktop_entry_path"

cat > "$app_dir/AppRun" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

show_error() {
  local title="$1"
  local message="$2"
  if command -v zenity >/dev/null 2>&1; then
    zenity --error --title="$title" --text="$message" --no-wrap >/dev/null 2>&1 && return 0
  fi
  if command -v kdialog >/dev/null 2>&1; then
    kdialog --title "$title" --error "$message" >/dev/null 2>&1 && return 0
  fi
  if command -v xmessage >/dev/null 2>&1; then
    xmessage -center "$message" >/dev/null 2>&1 && return 0
  fi
  printf '%s: %s\n' "$title" "$message" >&2
}

package_root="$(cd -- "$(dirname -- "${APPIMAGE:-$0}")" && pwd)"

has_core_library=0
if compgen -G "$package_root/libMaaCore.so*" >/dev/null; then
  has_core_library=1
fi

if [[ ! -x "$package_root/bin/MAAUnified" || ! -d "$package_root/resource" || "$has_core_library" -ne 1 ]]; then
  show_error \
    "MAAUnified" \
    "The Linux portable package is incomplete. Please extract the full package and launch MAAUnified.AppImage from that directory."
  exit 1
fi

cd "$package_root"
exec "$package_root/bin/MAAUnified" "$@"
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

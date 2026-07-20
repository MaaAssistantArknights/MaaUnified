#!/usr/bin/env bash
set -euo pipefail

brand_icon_path="src/MAAUnified/App/Assets/Brand/newlogo.ico"
app_id="io.github.maaassistantarknights.maaassistantarknights"
desktop_id="$app_id.desktop"
icon_name="$app_id"

mkdir -p \
  "$FLATPAK_DEST/share/applications" \
  "$FLATPAK_DEST/share/icons/hicolor/256x256/apps" \
  "$FLATPAK_DEST/share/pixmaps" \
  "$FLATPAK_DEST/share/metainfo"

desktop_entry_path="$FLATPAK_DEST/share/applications/$desktop_id"
icon_theme_path="$FLATPAK_DEST/share/icons/hicolor/256x256/apps/$icon_name.png"
pixmaps_icon_path="$FLATPAK_DEST/share/pixmaps/$icon_name.png"

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

cat > "$desktop_entry_path" <<EOF
[Desktop Entry]
Type=Application
Name=MAAUnified
Icon=$icon_name
Exec=MAAUnified
Terminal=false
Categories=Game;StrategyGame;
Comment=An Arknights assistant
EOF

if [[ -f tools/AppImage/io.github.maaassistantarknights.maaassistantarknights.metainfo.xml ]]; then
  cp -v tools/AppImage/io.github.maaassistantarknights.maaassistantarknights.metainfo.xml "$FLATPAK_DEST/share/metainfo/"
fi

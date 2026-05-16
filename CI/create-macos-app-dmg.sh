#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 <staging-dir> <release-dir> <package-name> <version> <informational-version>" >&2
  exit 1
fi

staging_dir="$1"
release_dir="$2"
package_name="$3"
version="$4"
informational_version="$5"

bundle_id="com.MaaAssistantArknights.MaaUnified"
app_name="MAAUnified"
app_dir="$release_dir/$app_name.app"
contents_dir="$app_dir/Contents"
macos_dir="$contents_dir/MacOS"
resources_dir="$contents_dir/Resources"
entitlements_path="$contents_dir/entitlements.plist"
dmg_root="$release_dir/dmg-root"
dmg_path="$release_dir/$package_name.dmg"
dmg_rw_path="$release_dir/.$package_name.rw.dmg"
dmg_mount_dir="$release_dir/.$package_name.mount"
dmg_background_dir="$dmg_root/.background"
dmg_background_png_path="$dmg_background_dir/installer-background.png"
dmg_background_jpeg_path="$dmg_background_dir/installer-background.jpg"
dmg_background_tiff_path="$dmg_background_dir/installer-background.tiff"
dmg_note_name="Installation Notes.txt"
app_icon_name="MAAUnified.icns"
brand_icon_path="src/MAAUnified/App/Assets/Brand/newlogo.ico"
signing_status_path="$release_dir/.$package_name.signing-status"
dmg_settings_path="$release_dir/.$package_name.dmg-settings.py"
dmgbuild_log_path="$release_dir/.$package_name.dmgbuild.log"
dmg_volume_name="$package_name"

if [[ ! -x "$staging_dir/bin/MAAUnified" ]]; then
  echo "Managed app executable not found: $staging_dir/bin/MAAUnified" >&2
  exit 1
fi

if [[ ! -f "$staging_dir/libMaaCore.dylib" ]]; then
  echo "MaaCore dylib not found: $staging_dir/libMaaCore.dylib" >&2
  exit 1
fi

if [[ ! -d "$staging_dir/resource" ]]; then
  echo "MaaCore resource directory not found: $staging_dir/resource" >&2
  exit 1
fi

write_dmg_install_note() {
  local note_path="$1"

  cat > "$note_path" <<'NOTE'
MAAUnified macOS 安装说明

1. 把 MAAUnified.app 拖到 Applications 文件夹。
2. 如果打开时提示“MAAUnified.app 已损坏，无法打开”，通常并不是真的文件损坏，而是 Gatekeeper 的 quarantine 隔离属性在拦截。
3. 打开“终端”，复制并运行下面这条命令：

xattr -dr com.apple.quarantine "/Applications/MAAUnified.app"

4. 再重新打开 /Applications/MAAUnified.app。

MAAUnified macOS Install Notes

1. Drag MAAUnified.app to the Applications folder.
2. If macOS says "MAAUnified.app is damaged and can't be opened", the app is usually being blocked by the Gatekeeper quarantine attribute rather than being actually damaged.
3. Open Terminal, then copy and run:

xattr -dr com.apple.quarantine "/Applications/MAAUnified.app"

4. Reopen /Applications/MAAUnified.app.

MAAUnified macOS インストールメモ

1. MAAUnified.app を Applications フォルダへドラッグします。
2. macOS で「MAAUnified.app は壊れているため開けません」と表示される場合、通常はファイルの破損ではなく、Gatekeeper の quarantine 属性によってブロックされています。
3. ターミナルを開き、次のコマンドをコピーして実行します：

xattr -dr com.apple.quarantine "/Applications/MAAUnified.app"

4. /Applications/MAAUnified.app をもう一度開きます。

MAAUnified macOS 설치 안내

1. MAAUnified.app을 Applications 폴더로 드래그합니다.
2. macOS에서 "MAAUnified.app이 손상되어 열 수 없습니다"라고 표시되면 실제 파일 손상이 아니라 Gatekeeper의 quarantine 속성 때문에 차단된 경우가 많습니다.
3. 터미널을 열고 아래 명령을 복사해 실행합니다:

xattr -dr com.apple.quarantine "/Applications/MAAUnified.app"

4. /Applications/MAAUnified.app을 다시 엽니다.
NOTE
}

resolve_dmgbuild_python() {
  if [[ -n "${DMGBUILD_PYTHON:-}" && -x "${DMGBUILD_PYTHON}" ]]; then
    printf '%s\n' "${DMGBUILD_PYTHON}"
    return 0
  fi

  if python3 -m dmgbuild -h >/dev/null 2>&1; then
    printf '%s\n' "python3"
    return 0
  fi

  if [[ -x "/private/tmp/maaunified-dmgbuild-venv/bin/python" ]] &&
    /private/tmp/maaunified-dmgbuild-venv/bin/python -m dmgbuild -h >/dev/null 2>&1; then
    printf '%s\n' "/private/tmp/maaunified-dmgbuild-venv/bin/python"
    return 0
  fi

  return 1
}

write_dmgbuild_settings() {
  cat > "$dmg_settings_path" <<'PY'
import os

app_path = os.environ["MAA_DMG_APP_PATH"]
note_path = os.environ["MAA_DMG_NOTE_PATH"]
volume_icon = os.environ["MAA_DMG_VOLUME_ICON_PATH"]

files = [
    (app_path, "MAAUnified.app"),
    (note_path, "Installation Notes.txt"),
]

symlinks = {
    "Applications": "/Applications",
}

hide = []

badge_icon = None
icon = volume_icon if volume_icon and os.path.exists(volume_icon) else None
format = "UDRW"
filesystem = "HFS+"
compression_level = 9
window_rect = ((100, 100), (640, 420))
default_view = "icon-view"
show_toolbar = False
show_status_bar = False
show_tab_view = False
show_pathbar = False
show_sidebar = False
icon_size = 80
text_size = 12
background = "#ffffff"

icon_locations = {
    "MAAUnified.app": (150, 168),
    "Applications": (490, 168),
    "Installation Notes.txt": (535, 295),
}
PY
}

create_dmg_with_dmgbuild() {
  local dmgbuild_python

  if ! dmgbuild_python="$(resolve_dmgbuild_python)"; then
    echo "dmgbuild is required to create the macOS installer image. Install it with 'python3 -m pip install dmgbuild' or set DMGBUILD_PYTHON." >&2
    return 1
  fi

  write_dmgbuild_settings
  rm -rf "$dmg_mount_dir"
  rm -f "$dmg_path" "$dmg_rw_path" "$dmgbuild_log_path"

  if ! MAA_DMG_APP_PATH="$app_dir" \
    MAA_DMG_NOTE_PATH="$dmg_root/$dmg_note_name" \
    MAA_DMG_BACKGROUND_PATH="$dmg_background_path" \
    MAA_DMG_VOLUME_ICON_PATH="$resources_dir/$app_icon_name" \
    "$dmgbuild_python" -m dmgbuild \
    -s "$dmg_settings_path" \
    --no-hidpi \
    "$dmg_volume_name" \
    "$dmg_rw_path" >"$dmgbuild_log_path" 2>&1; then
    echo "dmgbuild failed. See $dmgbuild_log_path for details." >&2
    sed -n '1,120p' "$dmgbuild_log_path" >&2 || true
    return 1
  fi

  patch_dmg_background "$dmgbuild_python"
  hdiutil convert "$dmg_rw_path" -format UDZO -imagekey zlib-level=9 -o "$dmg_path" >/dev/null
  rm -f "$dmg_rw_path"
  hdiutil verify "$dmg_path" >/dev/null
}

assert_volume_not_mounted() {
  local mounts

  mounts="$(hdiutil info | awk -v path="/Volumes/$dmg_volume_name" '
    $1 ~ /^\/dev\/disk/ && index($0, path) {
      device = $1
      sub(/^\/dev\//, "", device)
      sub(/s[0-9]+$/, "", device)
      mount = $0
      sub(/^([^\t]*\t){2}/, "", mount)
      printf "/dev/%s\t%s\n", device, mount
    }
  ')"

  if [[ -z "$mounts" ]]; then
    return 0
  fi

  echo "The DMG volume '$dmg_volume_name' is already mounted. Please eject it before rebuilding." >&2
  echo "在重新构建前，请先在 Finder 侧边栏推出已打开的 '$dmg_volume_name' 磁盘。" >&2
  echo "Or run one of these commands, then retry:" >&2
  while IFS=$'\t' read -r device mount_point; do
    [[ -n "$device" ]] || continue
    echo "  hdiutil detach \"$device\"  # $mount_point" >&2
  done <<< "$mounts"
  return 1
}

patch_dmg_background() {
  local dmgbuild_python="$1"
  local attach_output
  local device
  local mount_point
  local mounted_background_dir
  local mounted_background_path

  hdiutil resize -size 512m "$dmg_rw_path" >/dev/null
  attach_output="$(hdiutil attach "$dmg_rw_path" -readwrite -noverify -noautoopen)"
  device="$(awk '/Apple_HFS|Apple_APFS/ {print $1; exit}' <<< "$attach_output")"
  mount_point="$(awk -F '\t' '/Apple_HFS|Apple_APFS/ {print $3; exit}' <<< "$attach_output")"
  if [[ -z "$device" || -z "$mount_point" ]]; then
    echo "Failed to mount temporary dmg image: $dmg_rw_path" >&2
    return 1
  fi
  if [[ ! -w "$mount_point" ]]; then
    echo "Temporary dmg mounted read-only at $mount_point; expected writable image $dmg_rw_path." >&2
    hdiutil detach "$device" >/dev/null 2>&1 || true
    return 1
  fi

  mounted_background_dir="$mount_point/.background"
  mounted_background_path="$mounted_background_dir/installer-background.jpg"
  mkdir -p "$mounted_background_dir"
  cp "$dmg_background_path" "$mounted_background_path"

  "$dmgbuild_python" - "$mount_point/.DS_Store" "$mounted_background_path" <<'PY'
import sys

from ds_store import DSStore
from mac_alias import Alias, Bookmark

ds_store_path, background_path = sys.argv[1], sys.argv[2]

with DSStore.open(ds_store_path, "r+") as store:
    icvp = store["."]["icvp"]
    icvp["backgroundType"] = 2
    icvp["backgroundImageAlias"] = Alias.for_file(background_path).to_bytes()
    store["."]["icvp"] = icvp
    store["."]["pBBk"] = Bookmark.for_file(background_path)
PY

  if command -v osascript >/dev/null 2>&1; then
    osascript >/dev/null 2>&1 <<APPLESCRIPT || true
tell application "Finder"
  tell disk "$dmg_volume_name"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {100, 100, 740, 520}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 80
    set text size of opts to 12
    set background picture of opts to file ".background:installer-background.jpg"
    set position of item "$app_name.app" of container window to {150, 168}
    set position of item "Applications" of container window to {490, 168}
    set position of item "$dmg_note_name" of container window to {535, 295}
    update without registering applications
    delay 1
    close
  end tell
end tell
APPLESCRIPT
  fi

  sync
  hdiutil detach "$device" >/dev/null
}

create_app_icon() {
  local icon_work_dir="$release_dir/icon-work"
  local icon_png="$icon_work_dir/source.png"
  local iconset_dir="$icon_work_dir/$app_name.iconset"

  if [[ ! -f "$brand_icon_path" ]]; then
    echo "App icon source not found: $brand_icon_path" >&2
    exit 1
  fi

  rm -rf "$icon_work_dir"
  mkdir -p "$iconset_dir"

  python3 - "$brand_icon_path" "$icon_png" <<'PY'
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

  sips -z 16 16 "$icon_png" --out "$iconset_dir/icon_16x16.png" >/dev/null
  sips -z 32 32 "$icon_png" --out "$iconset_dir/icon_16x16@2x.png" >/dev/null
  sips -z 32 32 "$icon_png" --out "$iconset_dir/icon_32x32.png" >/dev/null
  sips -z 64 64 "$icon_png" --out "$iconset_dir/icon_32x32@2x.png" >/dev/null
  sips -z 128 128 "$icon_png" --out "$iconset_dir/icon_128x128.png" >/dev/null
  sips -z 256 256 "$icon_png" --out "$iconset_dir/icon_128x128@2x.png" >/dev/null
  sips -z 256 256 "$icon_png" --out "$iconset_dir/icon_256x256.png" >/dev/null
  sips -z 512 512 "$icon_png" --out "$iconset_dir/icon_256x256@2x.png" >/dev/null
  sips -z 512 512 "$icon_png" --out "$iconset_dir/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$icon_png" --out "$iconset_dir/icon_512x512@2x.png" >/dev/null
  if ! iconutil -c icns "$iconset_dir" -o "$resources_dir/$app_icon_name"; then
    echo "::warning title=macOS icon skipped::iconutil could not build $app_icon_name from $iconset_dir; continuing without a bundled app/volume icon."
    rm -f "$resources_dir/$app_icon_name"
  fi
  rm -rf "$icon_work_dir"
}

write_signing_status() {
  printf '%s\n' "$1" > "$signing_status_path"
}

should_codesign_file() {
  local file="$1"
  local file_name="${file##*/}"
  local file_type

  case "$file_name" in
    *.dylib | *.dll | *.exe)
      return 0
      ;;
  esac

  file_type="$(file "$file")"
  if grep -Eq 'Mach-O.*executable' <<< "$file_type"; then
    return 0
  fi

  grep -Eq 'dynamically linked shared library' <<< "$file_type"
}

sign_bundle_components_with_identity() {
  local identity="$1"
  local use_timestamp="$2"
  local file
  local codesign_args=(--force --options runtime)

  if [[ "$use_timestamp" == "true" ]]; then
    codesign_args+=(--timestamp)
  fi

  while IFS= read -r file; do
    if should_codesign_file "$file"; then
      if [[ "$file" == "$macos_dir/MAAUnified" ]]; then
        codesign "${codesign_args[@]}" --entitlements "$entitlements_path" --sign "$identity" "$file" || return
      else
        codesign "${codesign_args[@]}" --sign "$identity" "$file" || return
      fi
    fi
  done < <(find "$macos_dir" -type f)
}

sign_developer_id() {
  local identity="${MACOS_CODESIGN_IDENTITY:-}"

  if [[ -z "$identity" ]]; then
    identity="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p' | head -n 1)"
  fi

  if [[ -z "$identity" ]]; then
    echo "MACOS_CODESIGN_ENABLED=true but no Developer ID Application identity was found." >&2
    return 1
  fi

  echo "Signing $app_name.app with identity: $identity"
  sign_bundle_components_with_identity "$identity" true || return
  codesign --force --options runtime --timestamp --entitlements "$entitlements_path" --sign "$identity" "$app_dir" || return
  codesign --verify --deep --strict --verbose=2 "$app_dir" || return
}

sign_adhoc() {
  sign_bundle_components_with_identity "-" false || return
  codesign --force --options runtime --entitlements "$entitlements_path" --sign - "$app_dir" || return
  if ! codesign --verify --deep --strict --verbose=2 "$app_dir"; then
    echo "::warning title=macOS ad-hoc verification failed::Ad-hoc signing completed, but strict app verification failed; keeping fallback dmg without notarization."
  fi
}

fallback_to_adhoc() {
  echo "::warning title=macOS ad-hoc signing::$1; creating an ad-hoc signed app that will not pass Gatekeeper notarization."
  if sign_adhoc; then
    write_signing_status "ad-hoc"
  else
    echo "::warning title=macOS signing failed::Ad-hoc signing also failed; continuing with an unsigned fallback dmg."
    write_signing_status "unsigned"
  fi
}

assert_volume_not_mounted
rm -rf "$app_dir" "$dmg_root" "$dmg_mount_dir" "$dmg_path" "$dmg_rw_path" "$dmg_settings_path" "$dmgbuild_log_path"
mkdir -p "$macos_dir" "$resources_dir" "$dmg_root"

cp -a "$staging_dir/bin/." "$macos_dir/"
shopt -s nullglob
cp -a "$staging_dir"/*.dylib "$macos_dir/"
shopt -u nullglob
cp -a "$staging_dir/resource" "$resources_dir/resource"
create_app_icon

if [[ -f "$resources_dir/$app_icon_name" ]]; then
  icon_plist_block="  <key>CFBundleIconFile</key>
  <string>${app_icon_name}</string>"
else
  icon_plist_block=""
fi

cat > "$contents_dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>MAAUnified</string>
  <key>CFBundleIdentifier</key>
  <string>${bundle_id}</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>MAAUnified</string>
  <key>CFBundleDisplayName</key>
  <string>MAAUnified</string>
${icon_plist_block}
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundleVersion</key>
  <string>${version}</string>
  <key>CFBundleGetInfoString</key>
  <string>${informational_version}</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.3</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

cat > "$entitlements_path" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.cs.allow-jit</key>
  <true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
  <true/>
  <key>com.apple.security.cs.disable-executable-page-protection</key>
  <true/>
</dict>
</plist>
PLIST

plutil -lint "$contents_dir/Info.plist"
plutil -lint "$entitlements_path"
test -x "$macos_dir/MAAUnified"
test -f "$macos_dir/libMaaCore.dylib"
test -d "$resources_dir/resource"

if [[ "${MACOS_CODESIGN_ENABLED:-false}" == "true" ]]; then
  if sign_developer_id; then
    write_signing_status "developer-id"
  else
    fallback_to_adhoc "Developer ID signing or verification failed"
  fi
elif [[ "${MACOS_ADHOC_CODESIGN_ENABLED:-false}" == "true" ]]; then
  fallback_to_adhoc "Developer ID signing is unavailable"
elif [[ "${MACOS_CODESIGN_REQUIRED:-false}" == "true" ]]; then
  echo "MACOS_CODESIGN_REQUIRED=true but MACOS_CODESIGN_ENABLED is not true; refusing to create unsigned macOS package." >&2
  exit 1
else
  echo "macOS codesigning skipped because MACOS_CODESIGN_ENABLED is not true."
  write_signing_status "unsigned"
fi

mkdir -p "$dmg_background_dir"
python3 src/MAAUnified/CI/create-dmg-background.py "$dmg_background_png_path"
if command -v sips >/dev/null 2>&1; then
  sips -s format jpeg "$dmg_background_png_path" --out "$dmg_background_jpeg_path" >/dev/null 2>&1 || true
fi
if command -v sips >/dev/null 2>&1; then
  sips -s format tiff "$dmg_background_png_path" --out "$dmg_background_tiff_path" >/dev/null 2>&1 || true
fi
if [[ -f "$dmg_background_jpeg_path" ]]; then
  dmg_background_path="$dmg_background_jpeg_path"
elif [[ -f "$dmg_background_tiff_path" ]]; then
  dmg_background_path="$dmg_background_tiff_path"
else
  dmg_background_path="$dmg_background_png_path"
fi
write_dmg_install_note "$dmg_root/$dmg_note_name"
create_dmg_with_dmgbuild

rm -rf "$dmg_root" "$dmg_mount_dir" "$dmg_settings_path"

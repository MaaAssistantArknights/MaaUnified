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
dmg_tmp_path="$release_dir/.$package_name.tmp.dmg"
dmg_mount_dir="$release_dir/.$package_name.mount"
dmg_background_dir="$dmg_root/.background"
dmg_background_path="$dmg_background_dir/installer-background.png"
dmg_fix_script_name="Fix Damaged.command"
app_icon_name="MAAUnified.icns"
brand_icon_path="src/MAAUnified/App/Assets/Brand/newlogo.ico"
signing_status_path="$release_dir/.$package_name.signing-status"

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

write_dmg_quarantine_fix_script() {
  local script_path="$1"

  cat > "$script_path" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail

app_path="/Applications/MAAUnified.app"
quarantine_command='xattr -dr com.apple.quarantine "/Applications/MAAUnified.app"'
locale_name="$(defaults read -g AppleLocale 2>/dev/null || printf '%s' "${LANG:-en_US}")"

case "$locale_name" in
  zh_Hant*|zh_TW*|zh_HK*|zh_MO*)
    title="MAAUnified macOS 修復腳本"
    missing="找不到 $app_path"
    install_first="請先把 MAAUnified.app 拖到 Applications 資料夾，再執行本腳本。"
    damaged="如果 macOS 提示「MAAUnified.app 已損壞，無法打開」，通常是 Gatekeeper quarantine 隔離屬性攔截。"
    removing="正在移除 quarantine 屬性：$app_path"
    removed="quarantine 屬性已移除。"
    already_clean="未發現 quarantine 屬性，或該屬性已被移除。"
    done_message="處理完成，正在打開 MAAUnified。"
    command_hint="也可以在終端執行：$quarantine_command"
    press_enter="按 Return 鍵退出..."
    ;;
  zh*|zh_CN*)
    title="MAAUnified macOS 修复脚本"
    missing="未找到 $app_path"
    install_first="请先把 MAAUnified.app 拖到 Applications 文件夹，再运行本脚本。"
    damaged="如果 macOS 提示“MAAUnified.app 已损坏，无法打开”，通常是 Gatekeeper quarantine 隔离属性拦截。"
    removing="正在移除 quarantine 属性：$app_path"
    removed="quarantine 属性已移除。"
    already_clean="未发现 quarantine 属性，或该属性已被移除。"
    done_message="处理完成，正在打开 MAAUnified。"
    command_hint="也可以在终端运行：$quarantine_command"
    press_enter="按回车退出..."
    ;;
  ja*)
    title="MAAUnified macOS 修復スクリプト"
    missing="$app_path が見つかりません"
    install_first="先に MAAUnified.app を Applications フォルダへドラッグしてから、このスクリプトを実行してください。"
    damaged="macOS に「MAAUnified.app は壊れているため開けません」と表示される場合、通常は Gatekeeper の quarantine 属性によるブロックです。"
    removing="quarantine 属性を削除しています：$app_path"
    removed="quarantine 属性を削除しました。"
    already_clean="quarantine 属性は見つからないか、すでに削除されています。"
    done_message="処理が完了しました。MAAUnified を開きます。"
    command_hint="Terminal で次のコマンドを実行することもできます：$quarantine_command"
    press_enter="Return キーを押して終了..."
    ;;
  ko*)
    title="MAAUnified macOS 복구 스크립트"
    missing="$app_path 를 찾을 수 없습니다"
    install_first="먼저 MAAUnified.app을 Applications 폴더로 드래그한 뒤 이 스크립트를 실행하세요."
    damaged="macOS에서 'MAAUnified.app이 손상되어 열 수 없습니다'라고 표시되면 보통 Gatekeeper quarantine 속성 때문에 차단된 것입니다."
    removing="quarantine 속성을 제거하는 중: $app_path"
    removed="quarantine 속성을 제거했습니다."
    already_clean="quarantine 속성이 없거나 이미 제거되었습니다."
    done_message="처리가 완료되었습니다. MAAUnified를 엽니다."
    command_hint="터미널에서 다음 명령을 실행할 수도 있습니다: $quarantine_command"
    press_enter="Return 키를 눌러 종료..."
    ;;
  *)
    title="MAAUnified macOS repair script"
    missing="Could not find $app_path"
    install_first="Drag MAAUnified.app to the Applications folder first, then run this script again."
    damaged="If macOS says \"MAAUnified.app is damaged and can't be opened\", it is usually blocked by the Gatekeeper quarantine attribute."
    removing="Removing the quarantine attribute from: $app_path"
    removed="The quarantine attribute was removed."
    already_clean="The quarantine attribute was not found, or it was already removed."
    done_message="Done. Opening MAAUnified."
    command_hint="You can also run this in Terminal: $quarantine_command"
    press_enter="Press Return to exit..."
    ;;
esac

echo "$title"
echo

if [[ ! -d "$app_path" ]]; then
  echo "$missing"
  echo "$install_first"
  echo "$command_hint"
  echo
  read -r -p "$press_enter" _
  exit 1
fi

echo "$damaged"
echo "$command_hint"
echo "$removing"
if xattr -dr com.apple.quarantine "$app_path" 2>/dev/null; then
  echo "$removed"
else
  echo "$already_clean"
fi

echo
echo "$done_message"
open "$app_path"
echo
read -r -p "$press_enter" _
SCRIPT

  chmod +x "$script_path"
}

customize_mounted_dmg() {
  local mount_dir="$1"

  if ! command -v osascript >/dev/null 2>&1; then
    echo "::warning title=macOS dmg layout skipped::osascript is unavailable; dmg will keep the default Finder layout."
    return 0
  fi

  osascript <<APPLESCRIPT || {
tell application "Finder"
  tell disk "$app_name"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {100, 100, 740, 520}
    set viewOptions to the icon view options of container window
    set arrangement of viewOptions to not arranged
    set icon size of viewOptions to 96
    set background picture of viewOptions to POSIX file "$mount_dir/.background/installer-background.png"
    set position of item "$app_name.app" of container window to {150, 205}
    set position of item "Applications" of container window to {490, 205}
    set position of item "$dmg_fix_script_name" of container window to {88, 330}
    update without registering applications
    delay 1
    close
  end tell
end tell
APPLESCRIPT
    echo "::warning title=macOS dmg layout skipped::Finder did not apply the custom dmg layout; dmg will still include the installation note."
    return 0
  }
}

prepare_dmg_layout() {
  local image_path="$1"
  local device

  rm -rf "$dmg_mount_dir"
  mkdir -p "$dmg_mount_dir"

  device="$(hdiutil attach "$image_path" -readwrite -noverify -noautoopen -mountpoint "$dmg_mount_dir" | awk '/Apple_HFS|Apple_APFS/ {print $1; exit}')"
  if [[ -z "$device" ]]; then
    echo "Failed to mount temporary dmg image: $image_path" >&2
    hdiutil detach "$dmg_mount_dir" >/dev/null 2>&1 || true
    rm -rf "$dmg_mount_dir"
    return 1
  fi

  customize_mounted_dmg "$dmg_mount_dir"
  sync
  hdiutil detach "$device"
  rm -rf "$dmg_mount_dir"
}

create_verified_dmg() {
  local max_attempts=5
  local attempt
  local delay
  local status

  for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    rm -rf "$dmg_mount_dir"
    rm -f "$dmg_tmp_path" "$dmg_path"
    status=0

    if hdiutil create -volname "$app_name" -srcfolder "$dmg_root" -ov -format UDRW "$dmg_tmp_path" &&
      prepare_dmg_layout "$dmg_tmp_path" &&
      hdiutil convert "$dmg_tmp_path" -format UDZO -imagekey zlib-level=9 -o "$dmg_path"; then
      sync
      rm -f "$dmg_tmp_path"
      if hdiutil verify "$dmg_path"; then
        return 0
      else
        status=$?
      fi
    else
      status=$?
    fi

    rm -rf "$dmg_mount_dir"
    rm -f "$dmg_tmp_path" "$dmg_path"
    if ((attempt == max_attempts)); then
      echo "hdiutil create/verify failed after $max_attempts attempts." >&2
      return "$status"
    fi

    delay=$((attempt * 5))
    echo "hdiutil create/verify failed (attempt $attempt/$max_attempts, exit $status); retrying in ${delay}s." >&2
    sleep "$delay"
  done
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
  iconutil -c icns "$iconset_dir" -o "$resources_dir/$app_icon_name"
  rm -rf "$icon_work_dir"
}

write_signing_status() {
  printf '%s\n' "$1" > "$signing_status_path"
}

sign_macho_with_identity() {
  local identity="$1"
  local use_timestamp="$2"
  local file
  local file_type
  local codesign_args=(--force --options runtime)

  if [[ "$use_timestamp" == "true" ]]; then
    codesign_args+=(--timestamp)
  fi

  while IFS= read -r file; do
    file_type="$(file "$file")"
    if grep -Eq 'Mach-O|dynamically linked shared library' <<< "$file_type"; then
      if grep -Eq 'Mach-O.*executable' <<< "$file_type"; then
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
  sign_macho_with_identity "$identity" true || return
  codesign --force --options runtime --timestamp --entitlements "$entitlements_path" --sign "$identity" "$app_dir" || return
  codesign --verify --deep --strict --verbose=2 "$app_dir" || return
}

sign_adhoc() {
  sign_macho_with_identity "-" false || return
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

rm -rf "$app_dir" "$dmg_root" "$dmg_mount_dir" "$dmg_path" "$dmg_tmp_path"
mkdir -p "$macos_dir" "$resources_dir" "$dmg_root"

cp -a "$staging_dir/bin/." "$macos_dir/"
shopt -s nullglob
cp -a "$staging_dir"/*.dylib "$macos_dir/"
shopt -u nullglob
cp -a "$staging_dir/resource" "$resources_dir/resource"
create_app_icon

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
  <key>CFBundleIconFile</key>
  <string>${app_icon_name}</string>
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
test -f "$resources_dir/$app_icon_name"

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

cp -a "$app_dir" "$dmg_root/$app_name.app"
ln -s /Applications "$dmg_root/Applications"
mkdir -p "$dmg_background_dir"
python3 src/MAAUnified/CI/create-dmg-background.py "$dmg_background_path"
write_dmg_quarantine_fix_script "$dmg_root/$dmg_fix_script_name"
create_verified_dmg

rm -rf "$dmg_root" "$dmg_mount_dir"

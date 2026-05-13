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
app_icon_name="MAAUnified.icns"
brand_icon_path="src/MAAUnified/App/Assets/Brand/newlogo.ico"

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

create_verified_dmg() {
  local max_attempts=5
  local attempt
  local delay
  local status

  for ((attempt = 1; attempt <= max_attempts; attempt++)); do
    rm -f "$dmg_tmp_path" "$dmg_path"
    status=0

    if hdiutil create -volname "$app_name" -srcfolder "$dmg_root" -ov -format UDZO "$dmg_tmp_path"; then
      sync
      if mv -f "$dmg_tmp_path" "$dmg_path" && hdiutil verify "$dmg_path"; then
        return 0
      else
        status=$?
      fi
    else
      status=$?
    fi

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

rm -rf "$app_dir" "$dmg_root" "$dmg_path" "$dmg_tmp_path"
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
  identity="${MACOS_CODESIGN_IDENTITY:-}"
  if [[ -z "$identity" ]]; then
    identity="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p' | head -n 1)"
  fi

  if [[ -z "$identity" ]]; then
    echo "MACOS_CODESIGN_ENABLED=true but no Developer ID Application identity was found." >&2
    exit 1
  fi

  echo "Signing $app_name.app with identity: $identity"
  while IFS= read -r file; do
    file_type="$(file "$file")"
    if grep -Eq 'Mach-O|dynamically linked shared library' <<< "$file_type"; then
      if grep -Eq 'Mach-O.*executable' <<< "$file_type"; then
        codesign --force --options runtime --timestamp --entitlements "$entitlements_path" --sign "$identity" "$file"
      else
        codesign --force --options runtime --timestamp --sign "$identity" "$file"
      fi
    fi
  done < <(find "$macos_dir" -type f)
  codesign --force --options runtime --timestamp --entitlements "$entitlements_path" --sign "$identity" "$app_dir"
  codesign --verify --deep --strict --verbose=2 "$app_dir"
elif [[ "${MACOS_ADHOC_CODESIGN_ENABLED:-false}" == "true" ]]; then
  echo "::warning title=macOS ad-hoc signing::Developer ID signing is unavailable; creating an ad-hoc signed app that will not pass Gatekeeper notarization."
  while IFS= read -r file; do
    file_type="$(file "$file")"
    if grep -Eq 'Mach-O|dynamically linked shared library' <<< "$file_type"; then
      if grep -Eq 'Mach-O.*executable' <<< "$file_type"; then
        codesign --force --options runtime --entitlements "$entitlements_path" --sign - "$file"
      else
        codesign --force --options runtime --sign - "$file"
      fi
    fi
  done < <(find "$macos_dir" -type f)
  codesign --force --options runtime --entitlements "$entitlements_path" --sign - "$app_dir"
  codesign --verify --deep --strict --verbose=2 "$app_dir"
elif [[ "${MACOS_CODESIGN_REQUIRED:-false}" == "true" ]]; then
  echo "MACOS_CODESIGN_REQUIRED=true but MACOS_CODESIGN_ENABLED is not true; refusing to create unsigned macOS package." >&2
  exit 1
else
  echo "macOS codesigning skipped because MACOS_CODESIGN_ENABLED is not true."
fi

cp -a "$app_dir" "$dmg_root/$app_name.app"
ln -s /Applications "$dmg_root/Applications"
create_verified_dmg

rm -rf "$dmg_root"

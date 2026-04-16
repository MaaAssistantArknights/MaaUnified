#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <target-dir> <linux|macos>" >&2
  exit 1
fi

target_dir="$1"
platform="$2"

mkdir -p "$target_dir"

cat > "$target_dir/MAAUnified" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/bin/MAAUnified" "$@"
EOF
chmod +x "$target_dir/MAAUnified"

case "$platform" in
  linux)
    cp "$target_dir/MAAUnified" "$target_dir/MAAUnified.sh"
    chmod +x "$target_dir/MAAUnified.sh"
    ;;
  macos)
    cp "$target_dir/MAAUnified" "$target_dir/MAAUnified.command"
    chmod +x "$target_dir/MAAUnified.command"
    ;;
  *)
    echo "Unsupported platform: $platform" >&2
    exit 1
    ;;
esac

#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <publish-directory> <linux|windows|macos>" >&2
  exit 2
fi

publish_dir="$1"
platform="$2"

if [[ ! -d "$publish_dir" ]]; then
  echo "Publish directory does not exist: $publish_dir" >&2
  exit 1
fi

case "$platform" in
  linux)
    for required_command in head readelf ldd; do
      if ! command -v "$required_command" >/dev/null 2>&1; then
        echo "Linux artifact verification requires $required_command." >&2
        exit 1
      fi
    done

    elf_files=()
    sqlcipher_file_count=0
    dynamic_sqlcipher_count=0
    while IFS= read -r -d '' candidate; do
      if [[ "${candidate##*/}" == 'libe_sqlcipher.so' ]]; then
        ((sqlcipher_file_count += 1))
      fi

      magic="$(LC_ALL=C head -c 4 -- "$candidate")"
      if [[ "$magic" != $'\177ELF' ]]; then
        continue
      fi

      if ! dynamic_section="$(LC_ALL=C readelf -d -- "$candidate" 2>&1)"; then
        echo "Could not inspect ELF artifact: $candidate" >&2
        printf '%s\n' "$dynamic_section" >&2
        exit 1
      fi
      if [[ "$dynamic_section" != *'Dynamic section at offset'* ]]; then
        continue
      fi

      elf_files+=("$candidate")
      if [[ "${candidate##*/}" == 'libe_sqlcipher.so' ]]; then
        ((dynamic_sqlcipher_count += 1))
      fi
    done < <(find "$publish_dir" -type f -print0)

    if (( sqlcipher_file_count == 0 )); then
      echo "Linux publish is missing libe_sqlcipher.so." >&2
      exit 1
    fi
    if (( dynamic_sqlcipher_count != sqlcipher_file_count )); then
      echo "Linux publish contains a libe_sqlcipher.so that is not a dynamic ELF library." >&2
      exit 1
    fi

    for elf_file in "${elf_files[@]}"; do
      set +e
      # AppImage's AppRun puts the bundled runtime directory on LD_LIBRARY_PATH;
      # mirror that here so sibling libraries (e.g. libunwind for libc++abi) resolve.
      ldd_output="$(LC_ALL=C LD_LIBRARY_PATH="$publish_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ldd "$elf_file" 2>&1)"
      ldd_status=$?
      set -e
      if (( ldd_status != 0 )); then
        echo "ldd failed for ELF artifact: $elf_file" >&2
        printf '%s\n' "$ldd_output" >&2
        exit 1
      fi
      if [[ "$ldd_output" == *'not found'* ]]; then
        echo "ELF artifact has an unresolved native dependency: $elf_file" >&2
        printf '%s\n' "$ldd_output" >&2
        exit 1
      fi
    done
    if [[ ! -x "$publish_dir/bin/MAAUnified" ]]; then
      echo "Linux publish is missing the MAAUnified executable needed for the SQLCipher smoke test." >&2
      exit 1
    fi
    smoke_root="$(mktemp -d)"
    trap 'rm -rf "$smoke_root"' EXIT
    install -d -m 700 "$smoke_root/runtime" "$smoke_root/xdg"
    smoke_output="$(
      "$publish_dir/bin/MAAUnified" \
        --sensitive-storage-sqlcipher-smoke-test \
        "$smoke_root/runtime" \
        "$smoke_root/xdg"
    )"
    if [[ "$smoke_output" != cipher_version=* ]]; then
      echo "Published SQLCipher smoke test did not report cipher_version." >&2
      exit 1
    fi
    if [[ ! -f "$smoke_root/runtime/data/sensitive-settings.db" ]]; then
      echo "Published SQLCipher smoke test did not create its temporary database." >&2
      exit 1
    fi
    ;;
  windows)
    if find "$publish_dir" -type f \( -iname '*e_sqlcipher*.dll' -o -iname '*sqlcipher*.dll' \) | grep -q .; then
      echo "Windows publish unexpectedly contains a SQLCipher native DLL." >&2
      exit 1
    fi
    ;;
  macos)
    if find "$publish_dir" -type f \( -iname '*e_sqlcipher*.dylib' -o -iname '*sqlcipher*.dylib' \) | grep -q .; then
      echo "macOS publish unexpectedly contains a SQLCipher native dylib." >&2
      exit 1
    fi
    ;;
  *)
    echo "Unknown platform: $platform" >&2
    exit 2
    ;;
esac

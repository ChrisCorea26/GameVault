#!/usr/bin/env bash
# Downloads the test ROMs listed in manifest.tsv into .cache/ and verifies their SHA-256 hashes.
# These suites have no explicit redistribution license, so they are never committed (SPEC §11.3).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
cache="$here/.cache"

grep -v '^#' "$here/manifest.tsv" | while IFS=$'\t' read -r path sha url; do
  [ -z "$path" ] && continue
  target="$cache/$path"
  if [ -f "$target" ] && echo "$sha  $target" | sha256sum --check --status; then
    continue
  fi
  mkdir -p "$(dirname "$target")"
  echo "Downloading $path"
  curl -fsSL --retry 3 -o "$target.tmp" "$url"
  if ! echo "$sha  $target.tmp" | sha256sum --check --status; then
    rm -f "$target.tmp"
    echo "SHA-256 mismatch for $path (from $url)" >&2
    exit 1
  fi
  mv "$target.tmp" "$target"
done
echo "Test ROMs are ready in $cache"

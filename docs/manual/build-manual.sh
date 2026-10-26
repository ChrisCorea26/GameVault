#!/usr/bin/env bash
# Builds docs/manual/GameVault-User-Manual.pdf from the Markdown chapters in docs/manual/.
# Needs pandoc and a Chromium-based browser (Chrome, Chromium or Edge, version 131 or later).
# Set BROWSER to the browser's path if it isn't found automatically.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/GameVault-User-Manual.pdf"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

command -v pandoc >/dev/null || { echo "pandoc is required: https://pandoc.org/installing.html" >&2; exit 1; }

browser="${BROWSER:-}"
if [[ -z "$browser" ]]; then
  for candidate in google-chrome chromium chromium-browser microsoft-edge /opt/pw-browsers/chromium-*/chrome-linux64/chrome \
                   "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"; do
    if command -v "$candidate" >/dev/null 2>&1 || [[ -x "$candidate" ]]; then browser="$candidate"; break; fi
  done
fi
[[ -n "$browser" ]] || { echo "No Chromium-based browser found. Set BROWSER=/path/to/chrome." >&2; exit 1; }

# Chapters are numbered files; the build order is their file-name order.
chapters=("$here"/[0-9][0-9]-*.md)

pandoc "${chapters[@]}" \
  --from markdown+pipe_tables+fenced_divs+bracketed_spans \
  --to html5 --standalone --toc --toc-depth=2 \
  --metadata title="GameVault User Manual" \
  --template "$here/template.html" \
  --css manual.css \
  --output "$work/manual.html"
cp "$here/manual.css" "$work/"

"$browser" --headless --disable-gpu --no-sandbox --no-pdf-header-footer \
  --print-to-pdf="$out" "file://$work/manual.html" 2>/dev/null

echo "Wrote $out"

#!/usr/bin/env bash
# Phase 56 Task 4: makes forge chat's embedded Inter fonts and their kerning golden tables.
#
#   eng/fonts/inter-subset.sh <dir with Inter 4.1 Inter-SemiBold.ttf and Inter-Bold.ttf>
#
# 1. Subsets each weight to the image-text allowed set (TextArt.Allows) with hb-subset: kerning
#    only (GPOS kern), no hinting (StbTrueType ignores it), no GSUB (nothing substitutes), name IDs
#    13/14 kept so the OFL licence fields stay in the font. Output: src/ForgeMission.Cli/Tui/Graphics/Fonts/.
# 2. Writes the golden table of every pair in the allowed set, shaped by HarfBuzz (hb-shape
#    --features=-calt) against the committed subset: kerning = the first glyph's advance in the pair
#    minus its advance alone, in font units. Output: tests/ForgeMission.Mcl.Tests/Fixtures/Kerning/.
#
# Sources used (Inter 4.1, "Version 4.001;git-9221beed3"):
#   Inter-SemiBold.ttf sha256 78a843fade9d4612a5567302fb595b56976eb5fcebf4fea5a5912d638bafcde3
#   Inter-Bold.ttf     sha256 288316099b1e0a47a4716d159098005eef7c0066921f34e3200393dbdb01947f
# Tools: HarfBuzz 12.1.0 (hb-subset, hb-shape), python3.
set -euo pipefail
export LC_ALL=en_US.UTF-8

src="${1:?usage: inter-subset.sh <dir with Inter-SemiBold.ttf and Inter-Bold.ttf>}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
fonts="$root/src/ForgeMission.Cli/Tui/Graphics/Fonts"
golden="$root/tests/ForgeMission.Mcl.Tests/Fixtures/Kerning"
# Printable ASCII, Latin-1 letters, then · … → ↵ ⇧ – — ‘ ’ “ ” (keep in step with TextArt.Allows).
unicodes="20-7E,C0-D6,D8-F6,F8-FF,B7,2026,2192,21B5,21E7,2013,2014,2018,2019,201C,201D"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

python3 - "$unicodes" "$work" <<'PY'
import sys
chars = []
for part in sys.argv[1].split(','):
    lo, _, hi = part.partition('-')
    chars += [chr(c) for c in range(int(lo, 16), int(hi or lo, 16) + 1)]
open(f'{sys.argv[2]}/alphabet.txt', 'w').write(' '.join(f'{ord(c):04X}' for c in chars) + '\n')
open(f'{sys.argv[2]}/singles.txt', 'w').write('\n'.join(chars) + '\n')
open(f'{sys.argv[2]}/pairs.txt', 'w').write('\n'.join(a + b for a in chars for b in chars) + '\n')
PY

for weight in SemiBold Bold; do
  font="$fonts/Inter-$weight.ttf"
  hb-subset "$src/Inter-$weight.ttf" --unicodes="$unicodes" --layout-features=kern --no-hinting \
    --drop-tables+=GSUB --name-IDs+=13,14 --output-file="$font"
  shape() { hb-shape --features=-calt --no-glyph-names --no-clusters --text-file="$1" "$font"; }
  shape "$work/singles.txt" > "$work/singles.out"
  shape "$work/pairs.txt" > "$work/pairs.out"
  python3 - "$work" "$font" "$(hb-shape --version | head -1)" > "$golden/Inter-$weight.kern.txt" <<'PY'
import hashlib, re, sys
work, font, version = sys.argv[1:]
alphabet = open(f'{work}/alphabet.txt').read().split()
advance = lambda line: [int(a) for a in re.findall(r'\+(-?\d+)', line)]
singles = [advance(line)[0] for line in open(f'{work}/singles.out')]
pairs = [advance(line) for line in open(f'{work}/pairs.out')]
n = len(alphabet)
assert len(singles) == n and len(pairs) == n * n and all(len(p) == 2 for p in pairs)
print(f'# {font.rsplit("/", 1)[-1]}: pair kerning in font units, from {version} --features=-calt')
print(f'# font-sha256 {hashlib.sha256(open(font, "rb").read()).hexdigest()}')
print('# codepoints ' + ' '.join(alphabet))
for i in range(n):
    print(' '.join(str(pairs[i * n + j][0] - singles[i]) for j in range(n)))
PY
done
cp "$src/LICENSE.txt" "$fonts/LICENSE.txt"
ls -l "$fonts" "$golden"

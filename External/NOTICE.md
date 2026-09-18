# Third-party origin

`merge-mansion-api/` originates from https://github.com/mm-utils/merge-mansion-dumper (GPL-3.0),
upstream commit `3d1803d` (2026-02-24, game version 26.02.01), imported into MergeMansionWikiTools
on 2026-03-02 and modified since. The upstream license text is in `LICENSE.upstream`.

The upstream **dumper** (`merge-mansion-dumper/`) was removed on 2026-09-18 (v0.24.72), replaced by
the in-house `MMWT.Dumper/` — a clean-room reimplementation written without reading the upstream
source (spec: `_CONTEXT/_plans/2026-09-06-native-dumper-design.md` §9), verified against it by
byte-comparing output rather than by reading code. It remains in this repository's git history, and
`merge-mansion-api/` above is still upstream-derived, so this notice stays.

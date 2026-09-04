# Dialogue Codex

Every Merge Mansion dialogue line and story-bearing text across all game versions we own, with speakers, order, triggers and per-version history. One place to read from when assembling character stories — no re-scraping.

Design: `_CONTEXT/_plans/2026-09-04-dialogue-codex-design.md` · plan: `_CONTEXT/_plans/2026-09-04-dialogue-codex-plan.md`.

## What is here

| Path | Content |
|---|---|
| `sources.json` | inventory of `APKs\<ver>\`: APK, localization source, embedded config, config archives, dump folders; `missingMonths` = versions to download |
| `strings.json` | **every** English localization key (64 296) as run-length history `[{"from": ver, "value": text}]` over the 36 versions that have a localization |
| `codex.json` | merged model: `lines` (21 180 dialog lines: text/speaker/state runs, `seen` first–last), `stories` (3 310: ordered line ids per version + triggers with version ranges), `characters` (49), `items` (9 889 name/description runs), `tasks` (10 554 hotspot descriptions), `events` (142), `slides` (213 cutscene/slide-looking keys), `gaps` |
| `md/` | readable encyclopedia (generated): `INDEX.md`, `areas/<Area>.md`, `events/<Event>.md`, `characters/<Character>.md`, `misc/unknown-trigger.md`, `misc/removed.md`. Stories are rendered as a script: `**MADDIE** (Worried): text`, earlier wordings struck through with the version they lasted until |
| `build/` | the scripts (below) + `prefixes.json` (story-id prefix → content type; hypotheses for unmatched groups) + `viewer_template.html` |
| `_cache/` | gitignored: per-version raw extracts (`loc/<ver>.json`, `loc/<ver>.en.mpc`, `structure/<ver>/…`) and `viewer.html` (single-file browser, open locally) |

Run-length rule: a new run only when the value differs from the previous run; `value: null` = absent; a version with **no source** for that field is skipped, never interpolated (a gap must not look like a rewrite).

## Regenerate
1. `python Codex/build/inventory.py` → `sources.json`
2. `python Codex/build/extract_loc.py` → `_cache/loc/<ver>.json` (builds DumpHarness into `%TEMP%\mmwt_dumpharness` on first run; `--dump-loc` reads Metaplay `.mpc`)
3. `python Codex/build/extract_structure.py` → `_cache/structure/<ver>/`
4. `python Codex/build/build_codex.py` → `strings.json`, `codex.json` (~35 s)
5. `python Codex/build/render_md.py` → `md/`; `python Codex/build/render_viewer.py` → `_cache/viewer.html`

Tests: `python -m unittest discover -s Codex/build/tests -v` (stdlib `unittest`, no pytest). Steps 2–3 are cached per version; delete `_cache/loc/<ver>.json` or `_cache/structure/<ver>/` to redo one version.

## Coverage (build 2026-09-04, 44 versions)

`loc` = English localization from the APK; `structure` = where speakers/order/triggers come from (`dump` = an existing `Dump` folder; `configArchive` / `embeddedConfig` = today's dumper run over that version's config; `none` = text only).

| Version | loc | structure | files |
|---|---|---|---|
| 22.02.06 | ✓ | embeddedConfig ✓ | dialogues, events |
| 23.06.02 | ✗ | dump | areas, events, chain_item_odds |
| 23.09.02 | ✓ | embeddedConfig ✓ | dialogues |
| 23.11.02 | ✓ | embeddedConfig ✓ | dialogues |
| 23.12.01 | ✓ | none | — |
| 24.01.01 | ✓ | none | — |
| 24.04.01 | ✗ | dump | areas, events, chain_item_odds |
| 24.05.06 | ✗ | dump | areas, events, chain_item_odds |
| 24.07.01 | ✗ | dump | areas, events, chain_item_odds |
| 24.09.02 | ✓ | dump | areas, events, chain_item_odds |
| 24.09.03 | ✓ | dump | areas, events, chain_item_odds |
| 24.10.01 | ✓ | none | — |
| 24.11.02 | ✓ | dump | areas, events, chain_item_odds |
| 25.01.03 | ✗ | dump | areas, events, chain_item_odds |
| 25.02.01 | ✗ | dump | areas, events, chain_item_odds |
| 25.02.02 | ✓ | none | — |
| 25.02.03 | ✗ | dump | areas, events, chain_item_odds |
| 25.03.01 | ✓ | none | — |
| 25.03.02 | ✓ | none | — |
| 25.04.01 | ✓ | none | — |
| 25.04.02 | ✓ | dump | areas, events, chain_item_odds |
| 25.04.03 | ✓ | dump | areas, events, chain_item_odds |
| 25.05.01 | ✓ | configArchive ✓ | dialogues, areas, events, chain_item_odds |
| 25.06.01 | ✓ | configArchive ✓ | dialogues, areas, events, chain_item_odds |
| 25.06.02 | ✓ | dump | areas, events, chain_item_odds |
| 25.07.01 | ✓ | dump | areas, events, chain_item_odds |
| 25.08.01 | ✓ | dump | areas, events, chain_item_odds |
| 25.08.02 | ✗ | dump | areas, events, chain_item_odds |
| 25.09.01 | ✓ | dump | areas, events, chain_item_odds |
| 25.09.02 | ✓ | dump | areas, events, chain_item_odds |
| 25.10.01 | ✓ | dump | areas, events, chain_item_odds |
| 25.10.03 | ✓ | dump | areas, events, chain_item_odds |
| 26.01.01 | ✓ | none | — |
| 26.01.02 | ✓ | dump | areas, events, chain_item_odds |
| 26.02.01 … 26.07.01 (10) | ✓ | dump | dialogues, areas, events, chain_item_odds |

Build summary: 44 versions · 64 296 strings · 21 180 lines (18 965 alive in 26.07.01 = identical to the prototype) · 3 310 stories (2 790 alive, 476 removed since an older version) · 715 lines with at least two distinct wordings over history · 49 speaking characters.

**Missing versions to download** (months with no build in `APKs\`): 2022-03 … 2023-05, 2023-07, 2023-08, 2023-10, 2024-02, 2024-03, 2024-06, 2024-08, 2024-12, 2025-11, 2025-12. Drop the APK/XAPK into `APKs\<YY.MM.NN>\` and re-run steps 1–5; only the new version is extracted.

## Spike S1 — old configs and localization formats (result)

- **Localization:** `LocalizationLanguage.ImportBinary` reads every `en.mpc` from 22.02.06 to 26.07.01. Since 26.07.01 the XAPK wraps the file in an `MPE…` envelope (14-byte header); `extract_loc.strip_envelope` cuts it off (the payload always starts with bytes `0F 02 0C 02`). 22.02.06 has 5 176 strings, 26.07.01 58 371 (= the live CDN L-file).
- **Embedded `SharedGameConfig.mpa` (22.02.06, 23.09.02, 23.11.02) and 2025 config archives:** today's API deserializes `DialogItems`, `StoryElements`, `CharacterNames`, `CollectibleDialoguesInfo` → `dialogues.json` works (588 / 3 763 / 4 507 / 14 269 / 14 635 lines). `Areas` and `MergeChains` come back `null` (schema drift), `events.json` throws for 2023+ configs — for those versions triggers come from the old `Dump` folders instead (`extract_structure` complements them).
- **Line ↔ text key:** event/mystery dialogues use the line id as localization key (`SP_X_Intro_01`); area dialogues use `Dialogue_<id>` (`Dialogue_Pool_Intro_01`); the 2022 main story uses `DialogText_*` ids known only from the config. `build_codex.loc_text` tries `LocalizationId → id → Dialogue_<id>`.

## Gaps (honest list, see `codex.json → gaps`)

| Gap | Count | Meaning |
|---|---|---|
| `unknownTriggerStories` | 982 | groups of lines no dumped structure points at: main story / tutorial started client-side (562), and event types whose dumper does not export dialogue fields (LDE 95, CBE 71, SP 64, Mystery Machine 19, Dig 17, LS 17, …). Closing this needs the dumper to export `StoryElements` and the dialogue fields of the remaining event types — a separate decision (app change). |
| `referencedWithoutLines` | 44 | story ids the game references (birding `LS_*_Dialogue`, `Study*`, `Bonus*`) with no line under that name in any version |
| `locMissing` | 3 894 | lines with no text in any version: silent beats (`NoChange` camera/animation steps, ~2 275 in 26.07.01) plus lines whose key is absent from every localization we have |
| item hashes | — | dumps produced from old configs leave `ItemTypes` in `CollectibleDialogueMapping` as integer hashes (e.g. `8920249`) — shows up in some `misc/removed.md` titles |
| cutscenes | — | `Cutscenes` config carries only ids and locations; their spoken lines are ordinary DialogItems (`LDE_…_CutsceneDialogue1_Dialogue_01`) and are in `lines`; `slides` holds the remaining slide/cutscene-looking keys that no line references |

## Not in scope (yet)
Other languages, the WPF app, a wiki module, character story assembly (sub-project 2).

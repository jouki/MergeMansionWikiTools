# Dialogue Codex

Every Merge Mansion dialogue line and story-bearing text across all game versions we own, with speakers, order, triggers and per-version history. One place to read from when assembling character stories — no re-scraping.

Design: `_CONTEXT/_plans/2026-09-04-dialogue-codex-design.md` · plan: `_CONTEXT/_plans/2026-09-04-dialogue-codex-plan.md`.

## What is here

| Path | Content |
|---|---|
| `sources.json` | inventory of `APKs\<ver>\`: APK, localization source, embedded config, config archives, dump folders; `missingMonths` = versions to download |
| `strings.json` | **every** English localization key (64 296) as run-length history `[{"from": ver, "value": text}]` over the 36 versions that have a localization |
| `codex.json` | merged model: `lines` (21 180 dialog lines: text/speaker/state runs, `seen` first–last), `stories` (3 310: ordered line ids per version + triggers with version ranges), `characters` (46), `items` (9 889 name/description runs), `tasks` (10 554 hotspot descriptions), `events` (142), `slides` (213 cutscene/slide-looking keys), `gaps` |
| `md/` | readable encyclopedia (generated): `INDEX.md`, `areas/<Area>.md`, `events/<Event>.md`, `characters/<Character>.md`, `misc/unknown-trigger.md`, `misc/removed.md`. Stories are rendered as a script: `**MADDIE** (Worried): text`, earlier wordings struck through with the version they lasted until |
| `build/` | the scripts (below) + `prefixes.json` (story-id prefix → content type; hypotheses for unmatched groups) + `viewer_template.html` |
| `_cache/` | gitignored: per-version raw extracts (`loc/<ver>.json`, `loc/<ver>.en.mpc`, `structure/<ver>/…`) and `viewer.html` (single-file browser, open locally) |

Run-length rule: a new run only when the value differs from the previous run; `value: null` = absent; a version with **no source** for that field is skipped, never interpolated (a gap must not look like a rewrite).

## Regenerate
1. `python Codex/build/inventory.py` → `sources.json`
2. `python Codex/build/extract_loc.py` → `_cache/loc/<ver>.json` (builds DumpHarness into `%TEMP%\mmwt_dumpharness` on first run; `--dump-loc` reads Metaplay `.mpc`)
3. `python Codex/build/extract_structure.py` → `_cache/structure/<ver>/` (+ `stories.json` = StoryElements via DumpHarness `--dump-stories` for every version with a config: archives, embedded `.mpa`, and the app's live config for the newest version)
4. `python Codex/build/build_codex.py` → `strings.json`, `codex.json` (~35 s)
5. `python Codex/build/render_md.py` → `md/` (also writes `reruns.json` + `md/misc/reruns.md`); `python Codex/build/render_viewer.py` → `_cache/viewer.html`. `python Codex/build/fetch_area_order.py` refreshes the wiki area order when the mapping module changes.

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
| `unknownTriggerStories` | 22 (was 982 before StoryElements export) | groups of lines no dumped structure points at: main story / tutorial started client-side (562), and event types whose dumper does not export dialogue fields (LDE 95, CBE 71, SP 64, Mystery Machine 19, Dig 17, LS 17, …). Closing this needs the dumper to export `StoryElements` and the dialogue fields of the remaining event types — a separate decision (app change). |
| `referencedWithoutLines` | 3 (was 44) | story ids the game references (birding `LS_*_Dialogue`, `Study*`, `Bonus*`) with no line under that name in any version |
| `locMissing` | 3 894 | lines with no text in any version: silent beats (`NoChange` camera/animation steps, ~2 275 in 26.07.01) plus lines whose key is absent from every localization we have |
| item hashes | — | dumps produced from old configs leave `ItemTypes` in `CollectibleDialogueMapping` as integer hashes (e.g. `8920249`) — shows up in some `misc/removed.md` titles |
| cutscenes | — | `Cutscenes` config carries only ids and locations; their spoken lines are ordinary DialogItems (`LDE_…_CutsceneDialogue1_Dialogue_01`) and are in `lines`; `slides` holds the remaining slide/cutscene-looking keys that no line references |

## Reading rules baked into the build (learned from real data)

- **Speaker resolution** (`build_codex` step 3b): `NoChange` on a side means *the character who stood on that side in the previous line of the same story* (same for the state); a spoken line with neither `LeftSpeaks` nor `RightSpeaks` continues the last speaker. Before this, ~110 spoken lines (e.g. Grandma's "Find it and I'll tell you, dearie.") had no speaker. `NoChange`/`None`/`Empty` are never characters. 13 spoken lines remain speakerless (Bonus/Lounge narration without any character).
- **Last non-empty value** (`render_md.latest`, `reruns.latest`): content removed from the game disappears from newer localizations, so a removed line's last run is `null` while its real text sits one run earlier. Renderers always take the last non-empty run.
- **Event display names**: item/decoration dialogues are keyed by the event's config id (`CBE_MaddieInParis2025`) — resolved to the event's display name via events.json, else `<key>_Event_Name` / `_InfoPanel_Title` / `_Name` / `_Title` in the localization, else `build/event_names.json` (hand-maintained for events no source names). Two runs of one event (`CBE_MaddieInParis2025`, `CBE_MaddieInParis`) therefore land under one name; the key stays in `eventKey`.
- **Item references** in dialogue triggers are int config hashes in dumps made from old configs → resolved to `ItemType` and then to the item's name across all versions (`Luxury Handbag`), fallback internal type, fallback raw hash (20 hashes and 40 internal types have no name anywhere).
- **Area names**: unlocalised `HotspotTitle_LandingRoom` in old dumps → newest localised name for the same `AreaId`, else the localization key, else CamelCase split verified against the wiki mapping (`Landing Room`). Areas are ordered by the wiki `orderingIndex` (`build/area_order.json` from `Module:Datatable/Areas/Mapping`, refresh with `fetch_area_order.py`).
- **Text changes over versions** are classified (`reruns.classify_change`): *cosmetic* = ≥ 90 % similar after normalising quotes/markup/whitespace (typo, punctuation) — not a new version, hidden by default in the viewer and only counted in Markdown; *rewritten* otherwise. Build 2026-09-05: 404 rewrites, 203 cosmetic edits, 134 normalisation-only changes over the whole history.

## Event reruns (`reruns.json`, `md/misc/reruns.md`)

Families = same event type + core name with the run tag stripped (`CBE_MaddieInParis2025` / `CBE_MaddieInParis`, `CBE_TheGreatEscape` / `…B`, `LDE_Hopeberry2024` / `2025`). Stories of consecutive runs are paired by **content** (best text match ≥ 50 %), not by id suffix — reruns renumber their item slots. Verdict per pair: identical / cosmetic / rewritten; unmatched stories are *new* or *dropped*. Result: 12 families with more than one run, all of them **sequels with a new script** (nothing reused except 8 stories of The Great Escape B and 2 of Green Acres Quest); reruns that changed nothing keep the same event key and therefore never appear as a second run. The hand-written reading of each family is in `build/rerun_notes.json` and shown on the page.

**StoryElements are exported** (`DumpHarness --dump-stories`, 2026-09-05): story membership and line order come straight from the config for 22.02.06, 23.09.02, 23.11.02, 25.05.01, 25.06.01 and the live 26.07.01 (2 849 stories). That closed the naming gap (Jailbreak: `SBE_Jailbreak_BookCartFull` → lines `SBE_Jailbreak_1StCartIsFull_01…`; the event now has 95 stories / 405 lines) and cut `unknownTriggerStories` from 982 to 22 and `referencedWithoutLines` from 44 to 3. Stories the config defines but nothing we dump triggers are attributed to their event by id prefix (`kind: event, moment: part of the event`); 665 stories remain without an area/event (main story and tutorial started client-side).

## Placing stories nothing triggers (`md/misc/unassigned.md`, viewer group "Unassigned")

Stories the config defines but nothing we dump triggers are placed in this order: (1) event by id prefix (`SBE_Jailbreak_…`, A/B variant suffix stripped), (2) **area by id prefix** when it equals an area id or name (`Lounge_…`, `ParentsRoom_…`, `SpyRoom_…` → room / POI stories, 303 placed this way), (3) **by hand** from `build/story_assignments.json` (`"<id>"` or `"<prefix>*"` → `{"area": …}` or `{"event": …}`, optional note). What is still unplaced shows up in the viewer's "Area or event" dropdown under **Unassigned — needs your call**, grouped by id prefix (`CBE…`, `XMas…`, `FTUE…`, `Item…`), and in `md/misc/unassigned.md` with the first spoken lines of every story so a human can decide. Nothing in `story_assignments.json` is guessed; add entries only when the placement is known and rebuild.

## Discord screenshots (OCR) — second, lower-trust source

The wiki team's Discord channel `wiki-content-discussion` (id `783385526387998743`) holds 188 threads (2022-08 … 2026-08) of player screenshots: event tasks, items, and dialogues. It is the **only source for 2022 event dialogues** (Ursula's Birthday, Romantic Spot, Halloween/Thanksgiving/Xmas 2022, Pearl of the Ball 2023, …) that never made it into any config we own.

| Step | Script / tool | Output |
|---|---|---|
| inventory of threads | `python Codex/build/discord_inventory.py <channelId> [--messages]` | `_cache/discord/<id>/threads.json`, `messages/<thread>.json` (attachment URLs are signed and expire after ~24 h) |
| download images (standalone, resumable, refreshes expired URLs) | `python Codex/build/discord_download.py <channelId> --workers 6` | `_cache/discord/<id>/images/<thread>/<msg>_<n>_<file>` + `images_index.json` (27 572 files, 47.8 GB, 1 h) |
| raw OCR (Windows OCR, resumable) | `%TEMP%\mmwt_ocrharness\OcrHarness.exe <imagesDir> <ocr.jsonl> --workers 6` (build: `dotnet build _OcrHarness -o %TEMP%\mmwt_ocrharness`) | `_cache/discord/<id>/ocr.jsonl` — every text line with its box (≈600 images/min) |
| dialogue extraction | `python Codex/build/discord_ocr.py <channelId>` | `Codex/discord_dialogues.json` (committed) + `_cache/…/ocr_classified.json` (audit of every image) |
| render | `python Codex/build/render_md.py` | `md/discord/<thread>.md`, listed at the end of `md/INDEX.md` |

**How a dialogue is recognised** (`discord_ocr.classify`): a short capitalised name plate followed, within 10 plate-heights, by sentence text, stopping at UI words (Continue/Skip/…); the lowest such pair on the screen wins. Works for full screenshots (plate at ~77 % of the height, text at ~85 %) and for cropped strips (plate on top). Plates carry in-game names → aliases (`Ursula`→`Grandma`, `Julius`→`AntiqueDealer`) plus plates absent from the codex characters (`Butler`, `Fiona DuVal`, pets). Unknown plates with sentence text are kept in `unknownSpeakersToReview` (mostly area/item headings — reviewed once, real characters moved into `EXTRA_SPEAKERS`). The trailing fragment of the Continue button OCR glues to the last line is stripped; identical (speaker, text) pairs within a thread are collapsed (several players post the same screenshot).

Result (2026-09-05): 27 572 images → 12 956 dialogue screenshots → **12 706 unique lines in 146 threads** (Maddie 5 876, Grandma 1 567, Roddy 749, Mason 748, Jackie 633, Julius 518, Deb 392, Emilio 301, Lady Voyance 249, Pearl 247, Ignatius 164, Bella 157, Butler 138, …), 606 task lists, 8 915 other (items, HUD, shop), 5 095 unknown-plate candidates. Wording may carry OCR slips (`Of__`, `iten`); speaker plates are reliable. Order = message order in the thread, which usually follows the in-game order but is not guaranteed.

## Not in scope (yet)
Other languages, the WPF app, a wiki module, character story assembly (sub-project 2).

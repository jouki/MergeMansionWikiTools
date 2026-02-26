# Mystery Pass Reward Templates — Comparison Report

Generated: 2026-02-24

## Templates Overview

| # | Template | Type | Has Informant Tips | Has Decorations | XP identical |
|---|----------|------|--------------------|-----------------|--------------|
| 1 | Rewards | Standard | No | silver 1,2 + gold 3,4,5 | Yes (shared) |
| 2 | Rewards/2 | Standard | Yes (L29, L49, L50) | silver 1,2 + gold 3,4,5 | Yes (shared) |
| 3 | Rewards/3 | Standard | Yes (L29, L49, L50) | silver 1,2 + gold 3,4,5 | Yes (shared) |
| 4 | Rewards/4 | Standard | Yes (L29, L49, L50) | silver 1,2 + gold 3,4,5 | Yes (shared) |
| 5 | Pet | Pet | Yes (L29, L49, L50) | pet + gold 1,2 | Yes (shared) |
| 6 | Pet/2 | Pet | Yes (L29, L49, L50) | pet + gold 1,2 | Yes (shared) |
| 7 | Pet/3 | Pet | No | pet + gold 1,2 | Yes (shared) |
| 8 | Secrets of Serenity | Standard | No | silver 1,2 + gold 3,4,5 | Yes (shared) |

**All 8 templates share identical XP progression (levels 0-50) and identical premium levels (51-55).**

---

## Duplicate Check

### NEAR-DUPLICATE: Rewards/3 vs Rewards/4

Liší se v **JEDINÉM** poli:

| Level | Tier | Rewards/3 | Rewards/4 |
|-------|------|-----------|-----------|
| 12 | F2P | {{Coins}} **100** | {{Coins}} **50** |

Jinak jsou 100% identické. **Pravděpodobně duplikát** — jeden z nich má chybnou hodnotu Coins.

### NEAR-DUPLICATE: Pet vs Pet/2

Liší se v **JEDINÉM** poli:

| Level | Tier | Pet | Pet/2 |
|-------|------|-----|-------|
| 29 | Silver | {{Item/**nolevel**\|Missing Evidence\|1}} | {{Item/**Group**\|Missing Evidence\|1}} |
| 29 | Gold | {{Item/**nolevel**\|Missing Evidence\|1}} | {{Item/**Group**\|Missing Evidence\|1}} |

Funkčně identické (oba renderují stejně). **Duplikát** — jen jiný template typ pro Missing Evidence.

---

## Level-by-level Differences (Standard templates)

Legenda: **1** = Rewards, **2** = Rewards/2, **3** = Rewards/3, **4** = Rewards/4, **SoS** = Secrets of Serenity

Levely které se neliší mezi žádnými Standard templates: 0, 2, 3, 6, 7, 9, 10, 11, 15, 17, 18, 19, 20, 21, 22, 26, 27, 30, 31, 33, 34, 35, 36, 37, 38, 39, 40, 42, 43, 44, 45

### Level 1 — Silver/Gold

| Template | Silver | Gold |
|----------|--------|------|
| **1, 2, SoS** | {{Gems}} 5 | {{Gems}} 15 |
| **3, 4** | {{Item/Group\|Scissors}} | {{Item/Group\|Blue Card}} |

### Level 4 — F2P/Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2** | {{Gems}} 5 | {{Gems}} 10 | {{Gems}} 20 |
| **3, 4, SoS** | {{Item/Group\|Energy Chest}} | {{Item/Group\|Energy Chest}} | {{Item/Group\|Energy Chest}} |

Wait — SoS at level 4: {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 20 (same as 1,2). Let me correct:

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2, SoS** | {{Gems}} 5 | {{Gems}} 10 | {{Gems}} 20 |
| **3, 4** | {{Item/Group\|Energy Chest}} ×3 | | |

### Level 5 — Gold

| Template | Gold |
|----------|------|
| **1, 2, SoS** | {{Item/Group\|Blue Card}} |
| **3, 4** | {{Coins}} 1000 |

### Level 8 — F2P/Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 30 |
| **3, 4** | {{XPDrop}} 50 / Brown Chest 1 / Brown Chest 2 |
| **SoS** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 30 |

### Level 12 — F2P

| Template | F2P |
|----------|-----|
| **1, 2, SoS** | {{Coins}} 100 |
| **3** | {{Coins}} **100** |
| **4** | {{Coins}} **50** |

### Level 13 — Silver/Gold (minor)

| Template | Silver | Gold |
|----------|--------|------|
| **1, 2** | {{Item\|Brown Chest\|1}} | {{Item\|Brown Chest\|1}} |
| **3, 4, SoS** | {{Item\|Brown Chest\|1}} | {{Item\|Brown Chest\|1}} |

(Identical — no actual difference here)

### Level 16 — F2P

| Template | F2P |
|----------|-----|
| **1, 2** | {{Energy}} **25** |
| **3, 4, SoS** | {{Energy}} **30** |

### Level 24 — all 3 tiers

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2** | {{Item/nolevel\|Energy Chest}} | {{Item/nolevel\|Energy Chest}} | {{Item/nolevel\|Energy Chest}} |
| **3, 4** | {{Gems}} 5 | {{Gems}} 10 | {{Item/nolevel\|Energy Chest}} |
| **SoS** | {{Item/nolevel\|Energy Chest}} ×3 | | |

### Level 25 — Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2, SoS** | {{Energy}} 30 | {{Item/nolevel\|Scissors}} | {{Item/nolevel\|Scissors}} |
| **3, 4** | {{Energy}} 30 | {{Energy}} 50 | {{Item/nolevel\|Scissors}} |

### Level 28 — F2P/Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1, 2** | {{ItemIcon\|Experience_Points_(XP)\|1\|text=50}} | Brown Chest 1 | Brown Chest 2 |
| **3, 4** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 30 |
| **SoS** | {{XP}} 50 / Brown Chest 1 / Brown Chest 2 |

Note: **1** uses `{{ItemIcon|Experience_Points_(XP)|1|text=50}}`, **SoS** uses `{{XP}} 50` — vizuálně jiné, ale stejný záměr.

### Level 29 — Silver/Gold (Informant Tips)

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1** | {{Coins}} 200 | {{Coins}} **800** | {{Coins}} **2000** |
| **2, 3, 4** | {{Coins}} 200 | **Missing Evidence 1** | **Missing Evidence 1** |
| **SoS** | {{Coins}} 200 | {{Coins}} 800 | {{Coins}} 2000 |

### Level 41 — Gold

| Template | Gold |
|----------|------|
| **1, 2, 3, 4** | {{Decoration\|gold\|5}} |
| **SoS** | {{Item\|Fancy Blue Chest\|1}} |

### Level 46 — F2P

| Template | F2P |
|----------|-----|
| **1, 3, 4, SoS** | {{Coins}} 500 |
| **2** | {{ItemIcon\|Unlimited Energy\|1}} |

### Level 47 — Silver

| Template | Silver |
|----------|--------|
| **1, 3, 4, SoS** | {{ItemIcon\|Unlimited Energy\|2}} |
| **2** | 10 h Hourglass |

### Level 48 — F2P/Silver

| Template | F2P | Silver |
|----------|-----|--------|
| **1, 3, 4, SoS** | 2 h Hourglass | 10 h Hourglass |
| **2** | Fancy Blue Chest 1 | Fancy Blue Chest 1 |

### Level 49 — all tiers

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1** | Fancy Blue Chest 1 | Fancy Blue Chest 1 | Ursula's Blue Card 1 |
| **2, 3, 4** | Missing Evidence 1 | Missing Evidence 1 | Ursula's Blue Card 1 |
| **SoS** | Fancy Blue Chest 1 | Fancy Blue Chest 1 | Ursula's Blue Card 1 |

### Level 50 — Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **1** | Ursula's Blue Card 1 | Ursula's Blue Card 1 | **Piggy Bank 2** |
| **2, 3, 4** | Ursula's Blue Card 1 | Ursula's Blue Card 1 | **Missing Evidence 2** |
| **SoS** | Ursula's Blue Card 1 | Ursula's Blue Card 1 | **Decoration gold 5** |

---

## Level-by-level Differences (Pet templates)

Legenda: **P1** = Pet, **P2** = Pet/2, **P3** = Pet/3

Levely které se neliší: 0, 2, 3, 6, 7, 9, 10, 11, 12, 13, 14, 16, 17, 18, 20, 21, 22, 26, 27, 30, 31, 33, 34, 35, 36, 37, 38, 39, 40, 42, 43, 44, 45, 46, 47, 48

### Level 1 — Gold

| Template | Gold |
|----------|------|
| **P1, P2** | {{Item/Group\|Ursula's Blue Card\|1}} |
| **P3** | {{Gems}} 15 |

### Level 4 — all tiers

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **P1, P2** | Energy Chest (nolevel) ×3 | | |
| **P3** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 20 |

### Level 5 — Silver/Gold

| Template | Silver | Gold |
|----------|--------|------|
| **P1, P2** | {{Coins}} 200 | {{Coins}} 1000 |
| **P3** | {{Coins}} 200 | {{Item/Group\|Ursula's Blue Card\|1}} |

### Level 8 — F2P/Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **P1, P2** | {{XP}} 50 | Brown Chest 1 | Brown Chest 2 |
| **P3** | {{Gems}} 5 | {{Gems}} 10 | {{Gems}} 30 |

### Level 15 — Gold

| Template | Gold |
|----------|------|
| **P1, P2, P3** | {{Decoration\|gold\|1}} |

(Shodné)

### Level 19 — Gold

| Template | Gold |
|----------|------|
| **P1, P2** | {{Gems}} 15 |
| **P3** | {{Coins}} 1000 |

### Level 23 — Gold

| Template | Gold |
|----------|------|
| **P1, P2, P3** | {{Energy}} 75 |

(Shodné)

### Level 24 — all tiers

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **P1, P2** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 20 |
| **P3** | Energy Chest (nolevel) ×3 | | |

### Level 25 — Gold

| Template | Gold |
|----------|------|
| **P1, P2, P3** | {{Decoration\|gold\|2}} |

(Shodné)

### Level 28 — F2P/Silver/Gold

| Template | F2P | Silver | Gold |
|----------|-----|--------|------|
| **P1, P2** | {{Gems}} 5 / {{Gems}} 10 / {{Gems}} 30 |
| **P3** | {{XP}} 50 / Brown Chest 1 / Brown Chest 2 |

### Level 29 — Silver/Gold (Informant Tips)

| Template | Silver | Gold |
|----------|--------|------|
| **P1** | Missing Evidence 1 (**nolevel**) | Missing Evidence 1 (**nolevel**) |
| **P2** | Missing Evidence 1 (**Group**) | Missing Evidence 1 (**Group**) |
| **P3** | {{Coins}} 800 | {{Coins}} 2000 |

### Level 32 — Gold

| Template | Gold |
|----------|------|
| **P1, P2, P3** | {{Coins}} 2000 |

(Shodné)

### Level 41 — Gold

| Template | Gold |
|----------|------|
| **P1, P2, P3** | {{Coins}} 3000 |

(Shodné)

### Level 49 — F2P/Silver

| Template | F2P | Silver |
|----------|-----|--------|
| **P1, P2** | Missing Evidence 1 | Missing Evidence 1 |
| **P3** | **Fancy Blue Chest 1** | **Fancy Blue Chest 1** |

### Level 50 — Gold

| Template | Gold |
|----------|------|
| **P1, P2** | Missing Evidence 2 |
| **P3** | {{Coins}} 3000 |

---

## XP Template Notes

- **{{XP}} 50** vs **{{XPDrop}} 50** vs **{{ItemIcon|Experience_Points_(XP)|1|text=50}}** — tři různé formáty pro stejnou odměnu (50 XP). Nekonzistence mezi templates.
- **{{Item/nolevel|Missing Evidence|1}}** vs **{{Item/Group|Missing Evidence|1}}** — funkčně identické, ale nekonzistentní (Pet vs Pet/2).
- **{{Item/Group|Blue Card}}** — bez levelu (v templates 1, SoS). Mělo by být s levelem?

---

## Summary

### Potenciální duplikáty:
1. **Rewards/3 ≈ Rewards/4** — liší se POUZE v Coins na L12 (100 vs 50)
2. **Pet ≈ Pet/2** — liší se POUZE v template typu Missing Evidence na L29 (nolevel vs Group)

### Unikátní templates:
- **Rewards** (1) — nejstarší Standard, bez Informant Tips, Piggy Bank na L50 Gold
- **Rewards/2** — Standard + Informant Tips, přeorganizované L46-48
- **Rewards/3 & /4** — Standard + Informant Tips + Scissors/Blue Card na L1, Energy Chest na L4
- **Pet/3** — Pet bez Informant Tips, výrazně odlišný od Pet/Pet2
- **Secrets of Serenity** — Standard bez Informant Tips, Decoration gold 5 na L50 (místo L41)

# Video → LoRA pipeline — návrh

> Stav: **Fáze 0 (foundation)** — Core abstrakce + čistý frame-sampling plánovač + testy.
> Infrastructure + UI přijdou v dalších fázích.

## Cíl

Uživatel vloží 1..X videí (volitelně + referenční obrázek subjektu, který se má „hlídat").
Aplikace videa **zanalyzuje** — navzorkuje snímky a popíše je. Uživatel si v náhledové
mřížce **odškrtá, co chce** (které snímky / subjekt). Z vybraných snímků se sestaví
**dataset** (obrázky + popisky) a spustí se **LoRA trénink**. Výsledná LoRA jde použít
v Image Studiu i ve videích.

Dvoufázový tok přesně podle zadání: **prvotní analýza → uživatel řekne co chce → trénink.**

## Co už existuje (reuse, NEpsat znovu)

Drtivá většina backendu je hotová — pipeline je hlavně **lepidlo**:

| Stavební kámen | Kde | Co dělá |
|---|---|---|
| `ILoraTrainerService` / `SdScriptsLoraTrainer` | Infrastructure | sd-scripts trénink z `LoraTrainingRequest` (dataset = `LoraTrainingImage(path, caption)`), per-rodina (SD/SDXL/FLUX), progres ze stdout |
| `LoraTrainingParameters.DefaultsFor(model)` | Core | rozumné defaulty rank/alpha/steps/res dle base modelu |
| `TokenOnlyCaptions` + `SanitizeForFolderName` | trainer | pro person/subject LoRA přepíše popisky na trigger token → identita se naváže na token |
| `ILoraCaptionService` / `BlipCaptionService` | Infrastructure | auto-captioning snímků (BLIP / WD14) přes Python ze sd-scripts |
| `ILoraTrainerDependencyService` | Infrastructure | venv + sd-scripts + pip závislosti (sdílí se s captioningem) |
| ffmpeg (imageio-ffmpeg z ComfyUI venv) | `FfmpegVideoJoiner`, `VideoThumbnailGenerator` | extrakce snímků z videa |
| `ILoraLibraryService` | Infrastructure | výsledná LoRA se rovnou objeví v knihovně |

**Důsledek:** captioning ani trénink se **neprogramují znovu**. Fáze 2 jen spustí
extrakci snímků → BLIP captiony (existující služba) → uživatelský výběr → existující trainer.

## Architektura

```
[Videa] ──extract frames (ffmpeg)──▶ kandidátní snímky (PNG v working dir)
                                          │
                                   caption (BLIP/WD14)         ← ILoraCaptionService
                                          │
                                   CandidateFrame[]  ──────────▶ UI mřížka (zaškrtávátka)
                                          │  (uživatel vybere)
                            VideoLoraDatasetBuilder (čistý)
                                          │
                                   LoraTrainingRequest ────────▶ ILoraTrainerService → .safetensors → knihovna
```

### Fáze 0 (tato) — Core foundation
- **Modely** (`Core/Models/VideoLora.cs`): `CandidateFrame`, `FrameSamplingOptions`,
  `VideoLoraAnalysisRequest`, `VideoLoraProgress`, `VideoLoraStage`.
- **Interface** (`Core/Interfaces/IVideoLoraPipelineService.cs`): `AnalyzeAsync` (fáze 1 —
  extrakce + captiony → kandidáti). Fáze 2 (trénink) **reuse** `ILoraTrainerService`.
- **Čistá logika** (`Core/Services/VideoFrameSamplingPlanner.cs`): z délky videa + voleb
  spočítá rovnoměrné časové značky snímků k extrakci (vynechá úvod/závěr, midpoint
  vzorkování → nikdy přesně první/poslední snímek). Plně testovatelné.

### Fáze 1 — Infrastructure: extrakce snímků
- `VideoFrameExtractor` (Infrastructure): ffmpeg `-ss <ts> -i video -frames:v 1 out.png`
  pro každou značku z plánovače (znovu použít lokátor ffmpeg z `FfmpegVideoJoiner`).
- Délka videa: `ffprobe`/ffmpeg, případně `VideoThumbnailGenerator` styl.

### Fáze 2 — Orchestrátor + dataset
- `VideoLoraPipelineService` (Infrastructure) implementuje `IVideoLoraPipelineService`:
  per video plán → extrakce → BLIP captiony → vrátí `CandidateFrame[]`.
- `VideoLoraDatasetBuilder` (Core, čistý): z vybraných snímků + triggeru sestaví
  `IReadOnlyList<LoraTrainingImage>` (token-only nebo „trigger, <caption>“).
- Trénink: postavit `LoraTrainingRequest` + `LoraTrainingParameters.DefaultsFor` → volat trainer.

### Fáze 3 — UI
- Nová záložka pod rozcestníkem **Tvorba** (nebo pod LoRA stránkou): drop videí → „Analyzovat“
  → mřížka náhledů s captiony a zaškrtávátky → název/trigger + base model → „Vytvořit LoRA“
  s progresem (reuse training progress UI z `LoraTrainingPaneViewModel`).

## Otevřené otázky (řeší pozdější fáze)

- **Auto-detekce subjektu** (referenční obrázek „koho hlídat“): v1 = uživatel vybere snímky
  ručně. Fáze 4+ může přidat face/subject matching (CLIP/ArcFace embedding referenčního
  obrázku → skóre podobnosti per snímek → předvybrání). Vyžaduje další model/Python — záměrně
  odloženo, aby v1 nebyl blokovaný.
- **Crop na subjekt** (YOLO/SAM): v1 trénuje na celých snímcích. Detekce + crop je kvalitativní
  vylepšení do budoucna, ne nutnost pro funkční MVP.
- **Deduplikace blízkých snímků**: midpoint vzorkování drift zmírňuje; perceptuální hash
  (vyžaduje pixel access → Infrastructure) je optional vylepšení Fáze 2.

## Guardrails (reálné osoby / souhlas)

LoRA na konkrétní reálnou osobu je citlivá. Pipeline je čistě **lokální** (žádný upload),
ale UI Fáze 3 zobrazí jasné upozornění na souhlas/odpovědnost (trénink jen na osoby, které
k tomu daly souhlas) — stejně jako u jiných subject-LoRA toků v aplikaci.

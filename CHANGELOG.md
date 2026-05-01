# CHANGELOG — AI Studio

Formát: [Semantic Versioning](https://semver.org/). Datum: YYYY-MM-DD.

---

## [Unreleased] — backlog

### Chat
- [ ] LlamaSharp backend — načítání GGUF, streaming tokenů
- [ ] Model unload/load při přepnutí konverzace (VRAM/RAM management)
- [ ] Markdown rendering v bublinkách (kód, bold, seznamy)
- [ ] System prompt per konverzace
- [ ] Export konverzace (TXT, Markdown)
- [ ] Vyhledávání v historii konverzací
- [ ] Rename konverzace dvojklikem

### Image Studio
- [ ] ComfyUI process manager (start/stop/restart/health)
- [ ] Zobrazení vygenerovaných obrázků v canvasu
- [ ] Galerie s metadaty (seed, model, prompt, datum)
- [ ] Drag & drop pro referenční obrázky
- [ ] Inpainting — oprava části obrázku
- [ ] Outpainting — rozšíření obrázku do stran
- [ ] LoRA podpora (výběr + váha)
- [ ] Prompt enhancer — automatické rozšíření promptu
- [ ] Uložení oblíbených promptů

### Model Manager
- [ ] Skutečné stahování přes Civitai API + HuggingFace Hub
- [ ] Download manager — pause/resume, progress bar, checksum
- [ ] Automatická detekce GPU a VRAM
- [ ] Doporučení modelů dle dostupného hardware
- [ ] Aktualizace modelů (nové verze)

### Obecné
- [ ] SQLite persistence — konverzace, galerie, nastavení
- [ ] Light/Dark/System theme switching (funkční přepínání)
- [ ] Lokalizace přes ResX (CS/EN přepínání za běhu)
- [ ] First Run Wizard
- [ ] Automatické aktualizace (GitHub Releases)
- [ ] Inno Setup instalátor pro Windows
- [ ] macOS podpora (DMG, notarizace)

---

## [0.1.0-beta] — 2026-04-27

### Přidáno
- Základní shell aplikace s Avalonia 12 MVVM architekturou
- Sidebar navigace: Chat, Image Studio, Modely, Nastavení
- Settings button pevně ukotven na spodek sidebaru
- Status bar v sidebaru (stav + verze `v0.1.0-beta`)
- Ikona aplikace — SkiaSharp generátor (gradient, AI text, sparkles)

**Chat modul (UI)**
- Seznam konverzací s přidáváním nových chatů
- Každá konverzace: vlastní model (ComboBox), vlastní limit tokenů (NumericUpDown)
- Automatický titulek z první zprávy
- Chat bubliny pro uživatelské zprávy
- Datum a model v náhledu konverzace v listu

**Image Studio (UI)**
- Více generátorů s kartami (tabs) a zavíracím tlačítkem
- Per-generátor: model, prompt, negativní prompt
- Referenční obrázek s nastavením síly reference (Slider)
- Poměr stran: 1:1, 16:9, 9:16, 4:3, 3:4, 21:9
- Kvalita: SD (512), FHD (1920), 2K (2560), 4K (3840)
- Live preview rozlišení (např. `1920 × 1080`)
- Kroky, CFG, Seed + náhodný seed, Počet variant (1–8)
- Loading stav s ProgressBar

**Model Manager (UI)**
- Taby: Vše / Chat / Obrázky
- Fulltext hledání v názvech a popisech
- Per-model metadata: název, popis, velikost, VRAM, kontext tokenů, kvantizace, zdroj, verze
- Badge: Aktivní (zelená), Staženo (fialová)
- Detail panel — vše o vybraném modelu + akce
- Předvyplněný katalog: 5 chat modelů, 5 image modelů
- Tlačítko pro otevření složky modelů v Průzkumníku

**Nastavení (UI)**
- Výběr tématu: System / Dark / Light
- Výběr jazyka: Čeština / English
- Nastavení složky modelů s tlačítkem Procházet
- O aplikaci: verze, engine, platforma

### Technické
- Solution: App + Core + Infrastructure projekty
- CommunityToolkit.Mvvm 8.4.2, Implicit usings, Nullable enabled
- ViewLocator s podporou sub-namespace mappingu
- AppStyles.axaml: navItem, tabBtn, card, primary, secondary, chatInput styly
- SettingsService — JSON persistence v `%AppData%\AIStudio\settings.json`
- IconGen tool (SkiaSharp) pro generování PNG ikony

---

*Projekt ve vývoji. UI shell je hotový, AI backendy (LlamaSharp, ComfyUI) jsou dalším krokem.*

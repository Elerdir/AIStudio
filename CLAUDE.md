# CLAUDE.md — AI Studio

Instrukce pro Claude při práci na tomto projektu.

## Projekt

**AI Studio** — desktopová aplikace kombinující lokální LLM chat (jako LM Studio) a generování obrázků (jako ComfyUI frontend) v jedné aplikaci. Vše lokální, bez cloudu, v češtině.

- Cesta: `C:\temp\AIStudio`
- Solution: `AIStudio.sln`
- Spuštění: `dotnet run --project AIStudio.App/AIStudio.App.csproj`
- Build: `dotnet build`

## Tech stack

- **.NET 10**, **Avalonia 12** (MVVM, compiled bindings)
- **CommunityToolkit.Mvvm 8.4.2** — `[ObservableProperty]`, `[RelayCommand]`
- **SQLite** přes `Microsoft.Data.Sqlite`
- **Serilog** pro logování
- **LlamaSharp** (plánováno) — lokální LLM inference
- **ComfyUI embedded** (plánováno) — Python proces na pozadí pro generování obrázků
- **SkiaSharp** — generování ikony (tools/IconGen)

## Struktura solution

```
AIStudio.sln
├── AIStudio.App/          ← Avalonia UI, ViewModels, Views, Themes
│   ├── ViewModels/
│   │   ├── Chat/          ← ChatPageViewModel, ConversationViewModel, ChatMessage
│   │   ├── ImageStudio/   ← ImageStudioPageViewModel, ImageGeneratorViewModel
│   │   ├── Models/        ← ModelManagerPageViewModel, ModelItemViewModel
│   │   └── Settings/      ← SettingsPageViewModel
│   ├── Views/
│   │   ├── Chat/          ← ChatPageView.axaml
│   │   ├── ImageStudio/   ← ImageStudioPageView.axaml
│   │   ├── Models/        ← ModelManagerPageView.axaml
│   │   └── Settings/      ← SettingsPageView.axaml
│   ├── Themes/
│   │   └── AppStyles.axaml ← navItem, tabBtn, card, primary, secondary, chatInput
│   └── Assets/
│       └── app-icon.png
├── AIStudio.Core/          ← Modely, rozhraní, enumy (bez závislostí na UI)
│   ├── Enums/             ← AppTheme, AppLanguage, NavigationPage
│   ├── Interfaces/        ← ISettingsService
│   └── Models/            ← AppSettings
├── AIStudio.Infrastructure/ ← Implementace služeb, SQLite, Serilog
│   └── Services/          ← SettingsService
└── tools/IconGen/          ← SkiaSharp generátor ikony (jednorázový nástroj)
```

## Konvence

### AXAML
- Compiled bindings (`x:DataType`) jsou povinné — `AvaloniaUseCompiledBindingsByDefault=true`
- V Avalonia 12: `ItemsSource` (ne `Items`), `PlaceholderText` (ne `Watermark`)
- `Border` může mít jen jedno dítě — pro více dětí s `IsVisible` použij `Panel`
- Ikony: Segoe MDL2 Assets (Windows), unicode kódy v AXAML
- Žádné `BoolConverters.IsTrue` ani `StringConverters.Format` — neexistují v Avalonia 12

### ViewModels
- Všechny VM dědí z `ViewModelBase : ObservableObject`
- Async příkazy: metoda musí vracet `Task`, ne `async void`
- `[RelayCommand]` generuje příkaz z private metody

### ViewLocator
Automaticky mapuje `AIStudio.App.ViewModels.X.FooViewModel` → `AIStudio.App.Views.X.FooView`.
Funguje přes replace `ViewModels` → `Views` a `ViewModel` → `View`.

## Design systém

| Token | Hodnota | Použití |
|---|---|---|
| Pozadí | `#0D0D0D` | Hlavní plocha |
| Sidebar | `#161618` | Levý panel |
| Karta | `#1C1C1E` | Card komponenty |
| Panel | `#111113` | Sekundární panely |
| Akcent | `#7C3AED` | Primary tlačítka, aktivní stav |
| Akcent světlý | `#A78BFA` | Aktivní nav item, text |
| Oddělovač | `#2A2A2E` | Borders, dividers |
| Text hlavní | `#EBEBF5` | Primární text |
| Text sekundární | `#888896` | Popisky, metadata |
| Text slabý | `#555560` | Section headers, hints |
| Zelená | `#4ADE80` | Aktivní badge |
| Fialová slabá | `#818CF8` | Staženo badge |

## Plánované moduly (backlog)

### Chat
- [ ] LlamaSharp integrace — načítání GGUF modelu, streaming odpovědí
- [ ] Model unload/load při přepínání konverzací
- [ ] Markdown rendering v bublinkách
- [ ] Export konverzace (TXT, MD)

### Image Studio
- [ ] ComfyUI process manager — start/stop/health check přes HTTP API
- [ ] Zobrazení vygenerovaných obrázků v canvasu
- [ ] Galerie vygenerovaných obrázků s metadaty
- [ ] Drag & drop pro referenční obrázky
- [ ] LoRA podpora

### Modely
- [ ] Skutečné stahování — Civitai API + HuggingFace Hub API
- [ ] Download manager s pause/resume a progress barem
- [ ] Checksum validace po stažení
- [ ] Automatická detekce GPU (VRAM) a doporučení modelů

### Obecné
- [ ] SQLite persistence — konverzace, nastavení, galerie
- [ ] Light/Dark/System theme switching (funkční)
- [ ] Lokalizace přes ResX (CS/EN)
- [ ] First Run Wizard
- [ ] Automatické aktualizace
- [ ] Instalátor (Inno Setup pro Windows)

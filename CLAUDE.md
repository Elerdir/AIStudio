# CLAUDE.md — AI Studio

Instrukce pro Claude při práci na tomto projektu.

## Projekt

**AI Studio** — desktopová aplikace kombinující lokální LLM chat (jako LM Studio) a generování obrázků (jako ComfyUI frontend) v jedné aplikaci. Vše lokální, bez cloudu, v češtině.

- Cesta: `E:\Projects\AIStudio`
- Spuštění: `dotnet run --project AIStudio.App/AIStudio.App.csproj`
- Build: `dotnet build AIStudio.App/AIStudio.App.csproj`
- Pozor: `.sln` soubor není v repozitáři — builduj přímo projekt

## Tech stack

- **.NET 10**, **Avalonia 12.0.1** (MVVM, compiled bindings)
- **CommunityToolkit.Mvvm 8.4.2** — `[ObservableProperty]`, `[RelayCommand]`
- **SQLite** přes `Microsoft.Data.Sqlite`
- **Serilog** pro logování
- **LlamaSharp 0.22** — lokální LLM inference (implementováno)
- **ComfyUI** — Python proces na pozadí pro generování obrázků (implementováno)
- **SkiaSharp** — generování ikony (tools/IconGen)

## Struktura solution

```
AIStudio.App/          ← Avalonia UI, ViewModels, Views, Themes
├── Converters/        ← FileNameWithoutExtensionConverter
├── ViewModels/
│   ├── Chat/          ← ChatPageViewModel, ConversationViewModel, SystemPromptPreset
│   ├── ImageStudio/   ← ImageStudioPageViewModel, ImageGeneratorViewModel, ReferenceImageItem
│   ├── Models/        ← ModelManagerPageViewModel, ModelItemViewModel, RecommendedSectionViewModel
│   └── Settings/      ← SettingsPageViewModel, LogViewerViewModel
├── Views/
│   ├── Chat/          ← ChatPageView.axaml
│   ├── ImageStudio/   ← ImageStudioPageView.axaml (Smart + Manuál mód)
│   ├── Models/        ← ModelManagerPageView.axaml
│   └── Settings/      ← SettingsPageView.axaml, LogViewerWindow.axaml
├── Themes/
│   └── AppStyles.axaml ← navItem, tabBtn, card, primary, secondary, chatInput
└── Assets/
AIStudio.Core/          ← Modely, rozhraní, enumy (bez závislostí na UI)
├── Interfaces/        ← IComfyService, ILlamaService, IChatRepository, IImageRepository,
│                         IImageIntentParser, IImageModelMatcher, IModelDiscoveryService,
│                         IHuggingFaceClient, ICivitaiClient, ISettingsService
└── Models/            ← AppSettings, ComfyGenerationResult, ImageIntent, ModelPick,
                          CivitaiModelInfo, DiscoveredModel
AIStudio.Infrastructure/ ← Implementace služeb
└── Services/          ← LlamaService, ComfyService, ComfyWorkflowBuilder,
                          SettingsService, SqliteChatRepository,
                          ImageIntentParser, ImageModelMatcher, ModelDiscoveryService,
                          HuggingFaceClient, CivitaiClient,
                          ComfyInstaller, PlatformShell, SystemMonitorService
tools/IconGen/          ← SkiaSharp generátor ikony (jednorázový nástroj)
```

## Klíčové konvence

### AXAML
- Compiled bindings (`x:DataType`) jsou povinné — `AvaloniaUseCompiledBindingsByDefault=true`
- V Avalonia 12: `ItemsSource` (ne `Items`), `PlaceholderText` (ne `Watermark`)
- `Border` může mít jen jedno dítě — pro více dětí s `IsVisible` použij `Panel`
- Ikony: Segoe MDL2 Assets (Windows), unicode kódy v AXAML
- **DragDrop API v Avalonia 12.0.1**: `e.DataTransfer.TryGetFiles()` — NE `e.Data.GetFiles()`; `DataFormat` místo `DataFormats`
- Žádné `BoolConverters.IsTrue` ani `StringConverters.Format` — neexistují v Avalonia 12

### ViewModels
- Všechny VM dědí z `ViewModelBase : ObservableObject`
- Async příkazy: metoda musí vracet `Task`, ne `async void`
- `[RelayCommand]` generuje příkaz z private metody

### LlamaSharp (GPU fix)
- `NativeLibraryConfig.All.WithCuda()` MUSÍ být v `static LlamaService()` konstruktoru
- Volá se PŘED jakýmkoliv LLamaWeights/ModelParams — jinak tiše padne na CPU
- Po načtení modelu: `NativeApi.llama_supports_gpu_offload()` → status bar `[GPU ✓]` nebo `[CPU]`

### ComfyUI WebSocket
- `ws://localhost:{port}/ws?clientId={id}` — real-time progress
- Events: `progress` (value/max), `execution_success`, `execution_error`
- Fallback: HTTP polling přes `/history/{promptId}`
- Upload reference images: `POST /upload/image` multipart, vrací `{"name":"..."}`

### ComfyWorkflowBuilder
- `BuildStandard` / `BuildFlux` / `BuildFluxGguf` — txt2img workflows
- `InjectReferenceImages(workflow, emptyLatentKey, ksamplerKey, vaeRef, imageNames, w, h, strength)`
  → LatentBlend multi-reference img2img, funguje na čisté ComfyUI instalaci
- Klíče uzlů: `StandardEmptyLatentKey="5"`, `FluxEmptyLatentKey="2"`, `FluxGgufEmptyLatentKey="4"`

### ViewLocator
Automaticky mapuje `AIStudio.App.ViewModels.X.FooViewModel` → `AIStudio.App.Views.X.FooView`.

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

## Co je implementováno (stav 2026-05)

### Chat
- [x] LlamaSharp — načítání GGUF, streaming odpovědí přes `StatelessExecutor`
- [x] Chat template z GGUF metadat (LLamaTemplate) pro Llama 3, Qwen, Gemma, Mistral, Phi, DeepSeek
- [x] GPU offload — CUDA 12 via `NativeLibraryConfig.All.WithCuda()` v static konstruktoru
- [x] Systémové prompty + presets (`SystemPromptPreset`)
- [x] SQLite persistence konverzací (`SqliteChatRepository`)
- [ ] Markdown rendering v bublinách
- [ ] Export konverzace (TXT, MD)
- [ ] Model unload/load při přepínání konverzací

### Image Studio
- [x] ComfyUI process manager — start/stop, health check, WebSocket progress
- [x] Smart mód — LLM intent parser → auto-výběr modelu, promptu, aspektu
- [x] Manuální mód — klasický form (model, prompt, negative, aspect, quality, seed, steps, CFG)
- [x] Multi-reference img2img — LatentBlend, denoise = 1 - strength
- [x] Drag & drop pro referenční obrázky (oba módy)
- [x] Galerie vygenerovaných obrázků (thumbnail strip, context menu: copy/open/delete)
- [x] SQLite persistence galerie (`IImageRepository`)
- [x] FLUX Schnell/Dev safetensors + FLUX GGUF (UnetLoaderGGUF custom node)
- [x] SD/SDXL checkpointy
- [ ] LoRA podpora
- [ ] IP-Adapter / ControlNet

### Modely
- [x] Civitai API client (vyhledávání, stahování, metadata)
- [x] HuggingFace Hub client
- [x] Model discovery (`ModelDiscoveryService`)
- [x] ComfyUI-GGUF custom node auto-install
- [ ] Download manager s pause/resume a progress barem
- [ ] Checksum validace

### Obecné
- [x] SQLite persistence — konverzace, galerie
- [x] Nastavení — cesty ComfyUI/Python/Models, port, GPU, auto-start
- [x] LogViewer — prohlížeč Serilog logů v UI (`LogViewerWindow`)
- [x] SystemMonitorService — GPU/CPU/RAM metriky
- [ ] Light/Dark/System theme switching (napojení na OS)
- [ ] First Run Wizard
- [ ] Automatické aktualizace
- [ ] Instalátor (Inno Setup)

## Backlog (prioritizováno)

1. **Markdown rendering** — Markdig + custom Avalonia control pro chat bubliny
2. **Smart mode UX** — jasná hláška pokud LLM model není načten (Smart vyžaduje LLM)
3. **Download manager** — progress, pause/resume, checksum
4. **Theme switching** — propojit AppTheme enum s Avalonia FluentTheme variant
5. **First Run Wizard** — první spuštění, nastavení cest, download doporučených modelů

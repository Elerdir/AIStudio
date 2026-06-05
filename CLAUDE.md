# CLAUDE.md — AI Studio

Instrukce pro Claude při práci na tomto projektu.

## Projekt

**AI Studio** — desktopová aplikace kombinující lokální LLM chat (jako LM Studio) a generování obrázků (jako ComfyUI frontend) v jedné aplikaci. Vše lokální, bez cloudu, v češtině. Cílí na *one-click install* zkušenost: uživatel pustí installer, vybere model, hotovo.

- Cesta: `E:\Projects\AIStudio`
- Spuštění: `dotnet run --project AIStudio.App/AIStudio.App.csproj`
- Build: `dotnet build AIStudio.App/AIStudio.App.csproj`
- Testy: `dotnet test AIStudio.Tests/AIStudio.Tests.csproj` (současně 322+ testů, vše projde)
- Solution: `AIStudio.slnx` (nový XML formát; `.sln` v repu není)
- Lokální NuGet feed: `./lib/` (viz `NuGet.config`) — drží `UpdateHub.Client.X.Y.Z.nupkg`,
  aby nebyla potřeba sourozenecká cesta na `E:\Projects\updatehub`. Update procedura
  je v komentáři v `NuGet.config`.

**Cílové platformy:**
- **Windows 10/11 x64** — production, plně testováno (NVIDIA CUDA, AMD/Intel Vulkan + DirectML)
- **macOS 12+ Apple Silicon** (M1/M2/M3/M4) — kód kompiluje, runtime ověřeno CI buildem, **plné runtime ověření čeká na fyzické zařízení**
- Linux ne — záměrně mimo scope

## Tech stack

- **.NET 10**, **Avalonia 12.0.1** (MVVM, compiled bindings)
- **CommunityToolkit.Mvvm 8.4.2** — `[ObservableProperty]`, `[RelayCommand]`
- **SQLite** přes `Microsoft.Data.Sqlite`
- **Serilog** pro logování
- **LlamaSharp 0.22** — lokální LLM inference; backendy: `Cuda12` (NVIDIA), `Vulkan` (cross-vendor Windows), `Metal` (Apple Silicon, conditional)
- **ComfyUI** — Python proces na pozadí; Windows portable build s NVIDIA, AMD/Intel přes `--directml` flag (auto pip install torch-directml), macOS git clone + venv
- **LibreHardwareMonitorLib 0.9.4** — Windows cross-vendor VRAM monitoring (nahrazuje WMI AdapterRAM UInt32 overflow nad 4 GB)
- **Markdig 1.1.3** — Markdown rendering v chat bublinách (vlastní MarkdownViewer control)
- **UpdateHub.Client** — vlastní update server (https://updatehub.niderle.cz, slug `ai-studio`); knihovna distribuovaná jako lokální NuGet v `./lib/`
- **SkiaSharp** — generování ikony (tools/IconGen)

## Struktura solution

```
AIStudio.App/          ← Avalonia UI, ViewModels, Views, Themes
├── Controls/          ← MarkdownViewer (custom Markdig renderer)
├── Converters/        ← FileNameWithoutExtensionConverter
├── ViewModels/
│   ├── Chat/          ← ChatPageViewModel, ConversationViewModel, ChatMessage
│   ├── ImageStudio/   ← ImageStudioPageViewModel, ImageGeneratorViewModel
│   ├── Models/        ← ModelManagerPageViewModel, ModelItemViewModel
│   ├── Settings/      ← SettingsPageViewModel, LogViewerViewModel
│   └── Setup/         ← FirstRunWizardViewModel (7 kroků, auto-instalace ComfyUI)
├── Views/
├── Themes/AppStyles.axaml
├── Info.plist         ← macOS .app bundle metadata (CFBundle*, Apple Silicon only)
└── Assets/

AIStudio.Core/          ← Modely, rozhraní, pure logic (no Avalonia / WMI dependencies)
├── Interfaces/        ← IComfyService, IComfyHttpClient, IComfyInstaller,
│                        IGpuDetector, ISystemMonitorService, ILlamaService,
│                        ISystemPromptPresetService, ILoraLibraryService,
│                        IFluxDependencyService, IUpdateService, IModelDiscoveryService,
│                        IHuggingFaceClient, ICivitaiClient, IChatRepository,
│                        IImageRepository, ISettingsService, IDownloadService
├── Models/            ← AppSettings, Gpu (Vendor/Backend), SystemPromptPreset,
│                        RecommendedModel, ComfyExecutionException, …
└── Services/        ← pure helpery (bez závislostí): TokenEstimator (chars/4),
                       ComfyWorkflowBuilder (workflow JSON), RecommendedModels
                       (katalog), PngTextMetadata (AI provenance)

AIStudio.Infrastructure/ ← Implementace služeb
└── Services/
    ├── ComfyService, ComfyHttpClient
    ├── WindowsComfyInstaller / MacOsComfyInstaller (per-OS impl)
    ├── WindowsGpuDetector / MacOsGpuDetector
    ├── WindowsSystemMonitorService / MacOsSystemMonitorService
    ├── WindowsGpuMemoryProbe / WindowsLiveGpuMonitor (LHM wrappery)
    ├── LlamaService, LoraLibraryService, FluxDependencyService
    ├── SettingsService (DPAPI/Keychain šifrování přes TokenProtection)
    ├── MacOsKeychainKeyStore (security CLI wrapper)
    ├── SystemPromptPresetService, SafetensorsInspector, UpscaleModelService
    ├── HuggingFaceClient, CivitaiClient, ModelDiscoveryService
    ├── DownloadService, UpdateService (UpdateHub.Client SDK)
    ├── ChatImageOrchestrator, KontextDependencyService, PuLIDDependencyService
    └── SqliteChatRepository, SqliteImageRepository

AIStudio.Tests/         ← xUnit + FluentAssertions + NSubstitute, 619 testů
tools/IconGen/          ← SkiaSharp generátor ikony
.github/workflows/
├── ci.yml              ← Windows (hard gate) + macOS arm64 (publish .app, hard gate)
└── release-windows.yml ← tag-triggered Inno Setup .exe build
```

## Klíčové konvence

### AXAML
- Compiled bindings (`x:DataType`) jsou povinné — `AvaloniaUseCompiledBindingsByDefault=true`
- V Avalonia 12: `ItemsSource` (ne `Items`), `PlaceholderText` (ne `Watermark`)
- `Border` může mít jen jedno dítě — pro více dětí s `IsVisible` použij `Panel`
- České uvozovky `„"` v atributech: pozor na kolizi s ASCII `"`. Použij jednoduché apostrofy `'...'` nebo `&quot;` entitu.
- **DragDrop API v Avalonia 12.0.1**: `e.DataTransfer.TryGetFiles()` — NE `e.Data.GetFiles()`; `DataFormat` místo `DataFormats`
- Žádné `BoolConverters.IsTrue` ani `StringConverters.Format` — neexistují v Avalonia 12. `BoolConverters.And` existuje.

### ViewModels
- Všechny VM dědí z `ViewModelBase : ObservableObject`
- Async příkazy: metoda musí vracet `Task`, ne `async void`
- `[RelayCommand]` generuje příkaz z private metody
- VM nesmí dělat I/O ani byznys logiku přímo — to patří do `Infrastructure` services. LLM tah v chatu (load modelu + historie + stream) je vytažený do `IChatTurnService`/`ChatTurnService`; `ChatPageViewModel` ho volá v Send/Regenerate/Edit/Compare/Compact. Zbytek VM (1500+ ř.) jsou už převážně UI příkazy a stav.

### Cross-platform (Windows + macOS)
- Per-OS service registrace v DI je platform-guarded: `if (OperatingSystem.IsWindows()) ...`
- Windows-only třídy nesou `[SupportedOSPlatform("windows")]`, macOS-only `[SupportedOSPlatform("macos")]`
- `Windows*.cs` soubory jsou na non-Windows vyřazené z kompilace přes csproj `Compile Remove` Condition. `MacOs*.cs` kompilují všude (atribut + nepoužívají Windows API přímo).
- `csproj` má kondicionalní `ItemGroup`:
  - Windows: `LLamaSharp.Backend.Cuda12`, `LLamaSharp.Backend.Vulkan`, `LibreHardwareMonitorLib`, `SevenZipExtractor`, `System.Management`, `System.Diagnostics.PerformanceCounter`
  - macOS: `LLamaSharp.Backend.Metal`
- `WithMetal()` z LLamaSharp se volá přes reflection — extension method existuje jen když je Backend.Metal v csproj.

### LlamaSharp (GPU dispatch)
- Backend init JE lazy v `LlamaService.EnsureBackendInitializedAsync` při prvním `LoadModelAsync`, ne v static ctor.
- Switch nad `GpuBackend` z `IGpuDetector`: `Cuda` → `WithCuda()`, `Vulkan` → `WithVulkan()`, `Metal` → reflection InvokeWithMetal, `Cpu` → no-op.
- Po načtení modelu: `NativeApi.llama_supports_gpu_offload()` + **VRAM delta heuristika** — pokud `gpu_offload=true` ale VRAM stoupla <10 % velikosti modelu, status hlásí `[CPU fallback — model se nevešel do VRAM]`.

### ComfyUI WebSocket
- `ws://localhost:{port}/ws?clientId={id}` — real-time progress
- Events: `progress` (value/max), `execution_success`, `execution_error`
- Fallback: HTTP polling přes `/history/{promptId}` v `ComfyHttpClient.FetchHistoryResultAsync`
- `ComfyExecutionException` (Core public class) — odlišuje ComfyUI execution chyby od WebSocket/HTTP problémů

### ComfyWorkflowBuilder
- `BuildStandard` / `BuildFlux` / `BuildFluxGguf` — txt2img workflows
- `InjectReferenceImages` → LatentBlend multi-reference img2img
- `InjectLoras(workflow, modelRef, clipRef, modelConsumerKeys, clipConsumerKeys, loras)` — GGUF má separátní UnetLoader + DualCLIPLoader

### ViewLocator
Automaticky mapuje `AIStudio.App.ViewModels.X.FooViewModel` → `AIStudio.App.Views.X.FooView`.

### TokenProtection
- Šifrování HF/Civitai tokenů v `settings.json`
- Windows: DPAPI (`ProtectedData.Protect(scope=CurrentUser)`)
- macOS: AES-GCM s klíčem z Keychain (`MacOsKeychainKeyStore` přes `security` CLI)
- Linux: deterministic hash z machineName+userName (best-effort)
- Wire format: `enc:v1:<base64>` — idempotentní, legacy plaintext zůstává čitelný a zašifruje se při dalším save.

### SettingsService
- Atomický zápis: `settings.json.tmp` → `File.Replace(temp, main, .bak)`
- Recovery: při missing main soubor obnoví z `.bak`
- Šifrování tokenů transparentně (CloneForDisk před Save, Unprotect po Load)

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
| Zelená | `#4ADE80` | Aktivní badge, GPU OK |
| Jantarová | `#F59E0B` | CPU badge, varování |
| Fialová slabá | `#818CF8` | Staženo badge |

Většina struktur barev je v `App.axaml` `ResourceDictionary.ThemeDictionaries` jako `DynamicResource` (`AppBgBrush`, `CardBgBrush`, …), nikoliv hardcoded. Při change přes `App.ApplyTheme(AppTheme)` se propaguje do celého UI.

## Stav implementace (2026-05)

### Chat
- [x] LlamaSharp s multi-backend dispatch (CUDA/Vulkan/Metal/CPU runtime detection)
- [x] Chat template z GGUF metadat (Llama 3, Qwen, Gemma, Mistral, Phi, DeepSeek)
- [x] GPU/CPU badge + live RAM/VRAM ticker v hlavičce
- [x] **Diagnostika silent CPU fallback** — VRAM delta heuristika hlásí přesnou příčinu (CUDA chybí / model se nevešel / atd.)
- [x] Systémové prompty s perzistencí (`ISystemPromptPresetService`, builtin + custom v JSON)
- [x] SQLite persistence konverzací
- [x] **Markdown rendering** v assistant bublinách (`MarkdownViewer` + Markdig). Během streamu plain text (perf), po dokončení plný render.
- [x] **Export konverzace** TXT / MD (s frontmatter — model, datum, zprávy)
- [x] **Token estimator** (`Core.Services.TokenEstimator`, pure)
- [ ] Model unload/load při přepínání konverzací s jiným modelem (částečně — máme auto-unload)
- [ ] Live token/s ticker

### Image Studio
- [x] ComfyUI process manager s PID file, kill orphan při startu, ProcessExit cleanup
- [x] `ComfyHttpClient` (vytaženo z monolitického `ComfyService`, samostatně testovatelné)
- [x] Smart mód — LLM intent parser → prompt + auto-výběr modelu/aspektu
- [x] Manuální mód
- [x] Multi-reference img2img — LatentBlend
- [x] Drag & drop reference, mouse-wheel zoom
- [x] Galerie obrázků — SQLite + **paginace 50/page** (LoadImagesPagedAsync)
- [x] FLUX Schnell/Dev safetensors + FLUX GGUF (`UnetLoaderGGUF`)
- [x] SD/SDXL checkpointy + ControlNet/VAE filter v pickeru
- [x] **LoRA** — kompletní podpora (GGUF, img2img, auto-scan local + ComfyUI seznam, podadresáře, relativní cesty)
- [x] FLUX dependency check (CLIP-L + T5 + VAE) + auto-download
- [x] **DirectML pro AMD/Intel** (auto pip install torch-directml + `--directml` flag)
- [ ] IP-Adapter
- [ ] Video gen (AnimateDiff / HunyuanVideo / LTX)
- [ ] LoRA training

### Modely
- [x] Civitai API client (vyhledávání, stahování, SHA-256 z metadata)
- [x] HuggingFace Hub client
- [x] Model discovery + recommendations (`RecommendedModels.PickForGpu` adaptivní podle VRAM/vendoru)
- [x] ComfyUI-GGUF custom node auto-install
- [x] **Checksum validace** (SHA-256 chain Civitai → DiscoveredModel → ModelItem → DownloadService)
- [x] Download s progress, retry, partial resume
- [ ] Pause/resume tlačítko v UI
- [ ] Model storage cleanup (delete unused)

### Multi-vendor / multi-platform
- [x] **AMD/Intel Windows** — Vulkan LLM (~70-80 % CUDA rychlosti), DirectML ComfyUI
- [x] **`IGpuDetector` abstrakce** — WindowsGpuDetector (WMI + nvidia-smi + PCI vendor ID parsing) + MacOsGpuDetector (system_profiler JSON)
- [x] **LibreHardwareMonitor** pro VRAM > 4 GB (řeší WMI UInt32 overflow)
- [x] **macOS Apple Silicon kompiluje** — `MacOsGpuDetector` (system_profiler), `MacOsComfyInstaller` (git+venv+pip), `MacOsSystemMonitorService` (sysctl+vm_stat), `MacOsKeychainKeyStore` (security CLI), Metal backend přes reflection
- [x] **Info.plist + CFBundle** metadata pro `.app` bundle, LSRequiresNativeExecution (Apple Silicon only, blokuje Rosetta 2)
- [x] **CI matrix** — Windows + macos-14 (Apple Silicon) jako hard gates
- [ ] **Plné runtime ověření macOS** — čeká na fyzický Mac M-series
- [ ] AppIcon.icns
- [ ] macOS code signing + notarization (vyžaduje Apple Developer účet $99/rok)
- [ ] `.pkg` / `.dmg` installer pro macOS

### Obecné
- [x] **First Run Wizard** — 7 kroků (uvítání, Models složka, GPU detail, tokeny, ComfyUI auto-install, doporučené modely, souhrn)
- [x] Wizard po dokončení auto-spustí stahování modelů na pozadí
- [x] LogViewer
- [x] SystemMonitor (CPU/RAM/VRAM/GPU)
- [x] **Auto-update přes UpdateHub** (`https://updatehub.niderle.cz`, default OFF, SHA-256 verify)
- [x] **Token šifrování** (DPAPI / macOS Keychain / AES-GCM fallback)
- [x] **Atomický zápis settings.json** (.tmp + Replace + .bak recovery)
- [x] **Inno Setup installer** pro Windows
- [x] **Crash handling** — AppDomain.ProcessExit, UIThread.UnhandledException
- [x] **Light/Dark/System theme switching** — `App.ApplyTheme`, Settings picker (okamžitý přepnutí), MarkdownViewer reaguje na `ActualThemeVariant`
- [x] **Light theme audit** — ~330 hardcoded hexů ve Views konsolidováno na `DynamicResource` tokeny (strukturální + accent + semantické). Přidána sada **semantic surface** tokenů (Info/Warn/Err/Ok × Surface/Border/Text) pro barevné status-panely v obou themes. Zbývají jen záměrné výjimky: overlay scrimy (`#AA000000`), modré info-badge, accent/selection a pár vzácných neutrálů.

## Architektura — separace vrstev

| Vrstva | Závisí na | Příklad |
|---|---|---|
| **Core** | nic (jen .NET base) | `Gpu`, `IGpuDetector`, `TokenEstimator` |
| **Infrastructure** | Core, native packages | `WindowsGpuDetector`, `ComfyService`, `LlamaService` |
| **App** | Core, Infrastructure, Avalonia | ViewModels, Views, App.axaml.cs (DI bootstrap) |
| **Tests** | Core, Infrastructure | xUnit testy (no Avalonia) |

VM by neměly volat Infrastructure přímo — používají interface z Core. DI registrace v `App.axaml.cs`.

## Backlog (prioritizováno)

### Probíhá / rozpracováno
- **Video generation (Wan 2.1)** — FUNKČNÍ end-to-end, čeká runtime ověření. Hotovo:
  datová vrstva (`ImageRecord.MediaType` + DB migrace + filtr), ověřené Wan workflow
  buildery (t2v/i2v dle oficiálních ComfyUI grafů), **MP4** výstup přes `VHS_VideoCombine`,
  `text_encoders:` yaml mapping, `WanModels` katalog + `WanDependencyService` (download
  umt5/VAE/clip_vision/diffusion z `Comfy-Org/Wan_2.1_ComfyUI_repackaged`), `VideoGenerationService`
  orchestrátor (workflow→ComfyUI→MP4→galerie `MediaType=video`), `gifs`/`videos` history parser,
  auto-install ComfyUI-VideoHelperSuite (+ffmpeg), **samostatná záložka Video** (t2v + i2v,
  délka/FPS, deps download s progresem) + galerie „Rozhýbat", **inline MP4 přehrávač**
  (`VideoPlayerControl` — LibVLCSharp **core** + WriteableBitmap, NE VideoView kvůli Av12).
  **Zbývá:** runtime ověření na stroji (nativní libVLC + reálná generace) — interop a Wan
  výstup nejdou ověřit buildem. Pozn.: macOS nemá nativní libVLC (jen Windows balíček) →
  přehrávač tam graceful fallbackuje na externí přehrání.
- **ComfyUI řízená aktualizace** — HOTOVO (čeká runtime): `IComfyUpdateService`/`ComfyUpdateService`
  (Infrastructure) — zastaví ComfyUI → `git fetch --tags` → `git checkout v{TestedVersion}` (pinned, NE
  bleeding edge) → `pip install -r requirements.txt` (non-fatal) → UI restartuje proces. Defenzivní:
  guard na git repo (`.git`) + `git` na PATH, jinak jasná hláška. Tlačítko „Sladit s ověřenou verzí
  X.Y.Z" v Nastavení (viditelné jen když git repo + git dostupné + verze nesedí) + průběh/hint.
  **Zbývá runtime:** ověřit git checkout na reálné portable instalaci (custom_nodes/models jsou
  gitignored → checkout je nepřepíše).

### Plánováno (roadmap — větší věci)
- **Dlouhé video + 2× upscale (IMPLEMENTOVÁNO, čeká runtime ověření)** — řetězení ~5s Wan
  segmentů na delší video + ESRGAN upscale nad 480/720p. Hotovo: `VideoSegmentPlanner` (čistá,
  testovaná „smart" segmentace dle cílových sekund × FPS, minimalizuje počet segmentů/drift,
  délky 4n+1, počítá s překryvem posledního snímku), `ComfyWorkflowBuilder.AppendWanLastFrameSave`
  (`ImageFromBatch` index length−1 → `SaveImage` posledního snímku z VAEDecode „8"),
  `BuildVideoUpscalePass` (`VHS_LoadVideoPath`→`ImageUpscaleWithModel`→`ImageScaleBy`→`VHS_VideoCombine`,
  samostatný pass až po uvolnění VRAM), `LongVideoRequest`/`LongVideoProgress`,
  `IVideoGenerationService.GenerateLongVideoAsync` (seg1 t2v z promptu / i2v z obrázku, další i2v
  z carry-frame, per-segment upscale, `FfmpegVideoJoiner` concat `-c copy` přes imageio-ffmpeg
  z ComfyUI, segmenty zůstanou v `longvideo_*` složce jako záloha), Video tab UI (přepínač
  „dlouhé video" + cílová délka s náhledem plánu, checkbox upscale 2×, schování single-length).
  `VideoGenerationRequest` má `Upscale`/`UpscaleModel`. **Zbývá runtime:** ověřit `ImageFromBatch`/
  `VHS_LoadVideoPath` názvy nodů, ffmpeg concat path, drift/čas u delších videí. Pozn.: 60 FPS ×
  2 min = ~90 segmentů (hodiny) — pro plynulost se používá interpolace (níž), ne generace víc FPS.
- **RIFE interpolace — plynulejší video (IMPLEMENTOVÁNO, čeká runtime)** — `ComfyWorkflowBuilder.BuildVideoInterpolatePass`
  (`VHS_LoadVideoPath`→`RIFE VFI` ×2/×3→`VHS_VideoCombine` s frame_rate = zdroj×násobek),
  auto-install `ComfyUI-Frame-Interpolation` (Fannovel16) ve `ComfyInstaller`/`MacOsComfyInstaller`
  + ComfyService startup (RIFE model se dotáhne sám za běhu nodu). `VideoGenerationService.PostProcessAsync`
  řetězí **upscale → interpolace** (upscale dřív = levnější, běží na míň snímcích) pro jedno i dlouhé
  video (per-segment). `VideoGenerationRequest`/`LongVideoRequest` mají `Interpolate`/`InterpolateMultiplier`.
  UI: checkbox „Plynulejší pohyb (RIFE)" + násobek 2–4 s náhledem „16 → 32 fps". **Zbývá runtime:**
  ověřit node `RIFE VFI` (název s mezerou) + auto-download RIFE modelu + requirements-no-cupy.txt.
- **Video → LoRA pipeline** (až budou videa hotová): uživatel vloží 1..X videí (volitelně + referenční obrázek subjektu, který má „hlídat"). Aplikace videa zanalyzuje — detekce/popis osob, objektů, zvířat atd. (frame sampling + detekce/segmentace + caption), uživatel zaškrtne/potvrdí, co chce. Z vybraných framů/crops se sestaví **dataset** (obrázky + captiony) a spustí se **LoRA trénink** (využije stávající `SdScriptsLoraTrainer`). Výsledné LoRA jdou použít v **Image Studiu i ve videích**. Otevřené otázky: jaký detekční model (YOLO/GroundingDINO/SAM přes Python proces? nebo lokální VLM caption?), jak řešit kvalitu/duplicitu framů, NSFW/consent guardrails (viz pravidlo o reálných osobách).
- **Vlastní generátor (nezávislost na ComfyUI)** — dlouhodobý cíl: vlastní inference pipeline **přímo integrovaná v aplikaci** (ne jako externí Python proces), kde si verze modelů/závislostí řeší AI Studio samo a postupně. Kandidáti: managed inference (ONNX Runtime / TorchSharp / vlastní wrapper nad stable-diffusion.cpp / candle), nebo embedded Python s plnou kontrolou. Cíl: one-click, žádná závislost na ComfyUI portable, vlastní správa verzí. Velký záběr — navrhnout architekturu zvlášť.

### Menší / technický dluh
- ~~**Light theme audit**~~ — HOTOVO: strukturální + semantické barvy ve Views jsou theme-aware (`DynamicResource`). Případný drobný dopilování zbývá jen u záměrných výjimek (overlay scrimy, modré badge).
- **Pause/resume v Download manageru** — UI tlačítko + perzistence partial soubor přes restarty
- **macOS reálné ověření + AppIcon.icns + .pkg installer**
- **Refactor velkých VMs** — částečně: LLM tah vytažen do `ChatTurnService` (Infrastructure, unit-testovaný). Zbývá: případné rozdělení `ChatPageViewModel` (1500+ ř.) na tematické partial soubory + zvážit ChatImageOrchestrator sjednocení image-gen větve.

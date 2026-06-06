# Vlastní generátor obrázků — návrh architektury

Status: **návrh / foundation** (Fáze 0). Cíl dlouhodobý — nahradit závislost na
externím ComfyUI vlastní inference pipeline integrovanou přímo v AI Studiu.

## 1. Cíl a kontext

Dnes generování obrázků/videa jede přes **ComfyUI** — externí Python proces, který
AI Studio spouští, instaluje a řídí přes HTTP/WebSocket. Funguje, ale:

- **Velká závislost**: ComfyUI portable (~2 GB), embedded Python, custom nody, ruční
  správa verzí, křehké názvy nodů, nutnost auto-instalací při startu.
- **Není „one-click“ v plném smyslu**: instalace ComfyUI trvá, občas se rozbije
  (názvy větví, requirements, git checkout).
- **Verze řeší uživatel/ComfyUI**, ne AI Studio.

**Cíl:** vlastní inference přímo v aplikaci — žádný externí proces, žádný Python,
žádné ComfyUI. AI Studio si samo stáhne model (GGUF/safetensors) a vygeneruje obrázek
nativně. ComfyUI zůstává jako **pokročilá volitelná cesta** (ControlNet, custom nody,
exotické workflow), ne jako jediná možnost.

## 2. Kandidátní technologie

| Kandidát | Plus | Minus |
|---|---|---|
| **stable-diffusion.cpp** (leejet, ggml/GGUF) | C API → snadné P/Invoke; GGUF kvantizace; backendy CPU/CUDA/Vulkan/Metal; SD1.x/2/SDXL/SD3/FLUX; LoRA/VAE/ControlNet/TAESD; **stejný ekosystém jako llama.cpp** který už používáme přes LlamaSharp | nutné prebuilt nativní liby per backend (jako `LLamaSharp.Backend.*`); feature gap vs ComfyUI (ne celý node graph) |
| **ONNX Runtime** | managed, .NET-friendly, DirectML/CUDA | nutný ONNX export modelů (FLUX/SDXL export je těžký a omezený); méně flexibilní; velké soubory |
| **TorchSharp** (libtorch) | plný PyTorch ekosystém | obří nativní závislosti (~2 GB libtorch); ruční implementace celé diffusion pipeline; složité |
| **Embedded Python** (vlastní, řízený) | plná kompatibilita s diffusers | popírá cíl „žádný Python“; stejné bolesti jako ComfyUI |

## 3. Doporučení: **stable-diffusion.cpp** přes P/Invoke

Nejlepší fit, protože:

1. **Konzistence s existující architekturou.** Už wrapujeme `llama.cpp` přes LlamaSharp
   s **multi-backend dispatch** (CUDA / Vulkan / Metal, lazy init při prvním load —
   viz `LlamaService.EnsureBackendInitializedAsync`). `stable-diffusion.cpp` je SD
   analog od stejné ggml/GGUF rodiny → **stejný mentální model, stejná backend strategie**
   (prebuilt nativní liby per backend, runtime dispatch dle `IGpuDetector`).
2. **GGUF.** Už pracujeme s GGUF u LLM. SD modely v GGUF jsou malé (kvantizace) a rychle
   se načtou. Zapadá do stávající správy modelů (Models složka, `ModelManager`).
3. **One-click, bez Pythonu.** Nativní lib + managed wrapper, nula externích procesů,
   nula ComfyUI portable, nula Pythonu. AI Studio si verze nativní liby řeší samo
   (jako `LLamaSharp.Backend.*` NuGet).
4. **Pokrytí modelů.** SD1.5, SD2, SDXL, SD3, FLUX (schnell/dev) — pokrývá současné
   ComfyUI use-cases (txt2img, img2img, LoRA, VAE).
5. **Backendy = naše cílové platformy.** CPU (všude), CUDA (NVIDIA), Vulkan (AMD/Intel
   Windows), Metal (Apple Silicon) — přesně matchuje cross-vendor/cross-platform cíle.

### Managed binding

Dvě varianty (rozhodne se ve Fázi 1):
- **(a) Existující .NET binding** (např. `StableDiffusion.NET`) — pokud je dost zralý a
  udržovaný a má prebuilt backendy. Rychlejší start.
- **(b) Vlastní P/Invoke** nad `stable-diffusion.h` C API (`new_sd_ctx`, `txt2img`,
  `img2img`, `free_sd_ctx`, `sd_set_progress_callback`). Plná kontrola, vlastní správa
  nativních libů (mirror `LLamaSharp.Backend.*` přístupu). Bezpečnější dlouhodobě.

Doporučení: **začít (a) na ověření konceptu**, ale abstrakce (`INativeImageGenerator`)
je nezávislá na bindingu, takže přechod na (b) nic nerozbije.

## 4. Architektura

### Abstrakční vrstva (Core)

```
INativeImageGenerator (Core/Interfaces)
  ├─ Status: NativeGeneratorStatus (IsAvailable, Backend, BackendInfo, UnavailableReason)
  ├─ IsModelLoaded
  ├─ LoadModelAsync(modelPath, backend, ct)
  ├─ GenerateAsync(NativeImageRequest, IProgress<int>, ct) → NativeImageResult
  └─ UnloadAsync()
```

Modely (Core/Models/NativeGeneration.cs): `NativeImageRequest`, `NativeImageResult`,
`NativeLora`, `NativeGeneratorStatus`, enum `NativeGenBackend` (Cpu/Cuda/Vulkan/Metal),
`NativeModelFamily` (Sd1, Sd2, Sdxl, Sd3, Flux, Unknown).

**Proč vlastní abstrakce a ne sdílení s ComfyUI?** Stávající image-gen je hluboko
svázaný s ComfyUI (`ComfyWorkflowBuilder` node graph, `ComfyService` HTTP/WS). Nativní
generátor má jiný model (přímá inference, ne workflow JSON). Čistší je **paralelní
abstrakce** + v UI přepínač „Generátor: ComfyUI / Vestavěný“, než násilné sjednocení.
Sjednocení (společné `IImageGenerator`) se může udělat později, až bude nativní cesta
zralá.

### Integrace do appky

- **Settings**: `AppSettings.ImageGeneratorBackend` = `ComfyUI` | `Native` (default ComfyUI,
  dokud nativní není zralý). Přepínač v Nastavení.
- **ImageStudio**: dnes volá ComfyUI přes `IChatImageOrchestrator`/`IComfyService`. Až
  bude nativní cesta hotová, `ImageGeneratorViewModel` se podle nastavení rozhodne, kam
  request poslat. Abstrakce drží UI nezměněné.
- **Backend dispatch**: stejně jako `LlamaService` — lazy init nativního backendu při
  prvním `LoadModelAsync`, výběr dle `IGpuDetector` (Cuda → CUDA lib, Vulkan → Vulkan lib,
  Metal → Metal lib, jinak CPU). VRAM delta heuristika pro detekci CPU fallbacku (stejný
  vzor jako u LLM).
- **Správa modelů**: SD GGUF/safetensors modely jdou do Models složky (jako dnes). Rodinu
  modelu (SDXL/FLUX/…) detekuje rozšířený `SafetensorsInspector` (dnes umí jen
  CLIP/VAE/UNet přítomnost — doplní se klasifikace rodiny dle klíčových tensorů/shape).

### Tok generování (txt2img)

```
ImageGeneratorViewModel
  → INativeImageGenerator.LoadModelAsync(model, backend)   (lazy, jen při změně modelu)
  → INativeImageGenerator.GenerateAsync(request, progress) (nativní txt2img, progress callback)
  → výsledek PNG → uložit do galerie (stejně jako dnes ComfyUI výstup)
```

## 5. Fázový plán

- **Fáze 0 (TATO PR) — foundation/design**: tento dokument + `INativeImageGenerator` +
  modely + čistý `NativeSamplerMap` (mapování samplerů na sd.cpp) + testy. Nic se nepřipojuje
  do běžící appky, žádná nativní lib. Cíl: pevný základ + dohodnutá architektura.
- **Fáze 1 — P/Invoke + CPU** *(scaffold HOTOVO, čeká nativní lib + runtime ověření)*:
  managed pipeline kompletní — `StableDiffusionInterop` (P/Invoke na sd.cpp, pinnuté
  signatury), `NativeImageGenerator` (load → txt2img → PNG → uložit, lazy probe nativní
  liby, **graceful fallback** když chybí), `PngEncoder` + `NativeModelDefaults` (čisté,
  testované), DI registrace. **Zbývá ve Fázi 2**: přibalit nativní libu a runtime ověřit
  P/Invoke signatury + reálnou inference na CPU.
- **Fáze 2 — GPU backendy**: CUDA / Vulkan / Metal prebuilt liby podmíněně v csproj
  (mirror `LLamaSharp.Backend.*`), runtime dispatch dle `IGpuDetector`.
- **Fáze 3 — parita základu**: img2img, LoRA, VAE, samplery/schedulery, seed/CFG/steps.
- **Fáze 4 — UI integrace**: přepínač generátoru v Nastavení; `ImageGeneratorViewModel`
  routuje dle nastavení; nativní jako „beta“ vedle ComfyUI.
- **Fáze 5 — model management**: katalog SD GGUF modelů pro nativní cestu (download,
  checksum), FLUX/SDXL varianty, doporučení dle VRAM.
- **Fáze 6 — pokročilé**: ControlNet, upscaling (sd.cpp/ESRGAN), TAESD náhledy, výhledově
  video (až sd.cpp / jiný nativní stack podpoří).

## 6. Rizika a otevřené otázky

### ⚠️ ZJIŠTĚNÍ (2026-06): sd.cpp C API je struct-based a volatilní → P/Invoke přehodnotit

Ověřeno proti reálné hlavičce `include/stable-diffusion.h` z aktuálního releasu
(`master-672-1f9ee88`, ten samý, co shipuje Windows binárky):

- **Náš `StableDiffusionInterop` cílí na STAROU poziční API** (`new_sd_ctx(...22 args...)`,
  `txt2img(...23 args...)`). **Ta už neexistuje.** Při testu by spadl na `EntryPointNotFound`
  / ABI mismatch.
- **Aktuální API je struct-based:** `new_sd_ctx(const sd_ctx_params_t*)` +
  `generate_image(ctx, const sd_img_gen_params_t*)` (ne `txt2img`!). `sd_ctx_params_t` má
  ~50 polí, `sd_img_gen_params_t` má **vnořené struktury** (`sd_image_t`, `sd_sample_params_t`,
  `sd_pm_params_t`, `sd_tiling_params_t`, `sd_cache_params_t`, `sd_hires_params_t`) + pole
  `sd_lora_t[]`. Volá se přes `sd_ctx_params_init()` → override → `new_sd_ctx`.
- API **rychle bobtná** (spectrum, taylorseer, qwen, chroma, audio…). Ruční marshalling
  těchhle vnořených struktur je **vysoce křehký a údržbově drahý** — každý update sd.cpp
  může rozbít ABI.
- **Release zip navíc shipuje `sd-cli.exe` a `sd-server.exe`** (vedle `stable-diffusion.dll`).

**Doporučení — přehodnotit Fázi 2 binding:** místo ruční struct P/Invoke (křehké) zvážit:

1. **Bundled `sd-cli.exe`** *(doporučeno)* — shell-out per generace s CLI argumenty
   (`-M txt2img -m model -p prompt -W -H --steps --cfg-scale -s --sampling-method -o out.png`).
   CLI args jsou **stabilní napříč verzemi** (na rozdíl od struct ABI), robustní, jednorázový
   proces (ne server). Konzistentní s tím, jak už shell-outujeme na ffmpeg/git. Pořád „bez
   Pythonu / bez ComfyUI". `sd_image_t` + `PngEncoder` nepotřeba (CLI rovnou zapíše PNG).
2. **`sd-server.exe`** — malý lokální HTTP server (jako mini-ComfyUI, ale bundled/náš) —
   reintrodukuje proces na pozadí, ale stabilní HTTP API.
3. **Struct P/Invoke** — plná kontrola, ale ruční sync ~50-pole + vnořené struktury proti
   každé verzi. Nejvíc práce a nejkřehčí.

Rozhodnutí je na další iteraci. `INativeImageGenerator` abstrakce zůstává beze změny ať tak
či tak — mění se jen implementace pod ní.


- **Nativní liby**: kde je brát? Existující NuGet vs vlastní build/CI. Velikost (CUDA lib
  je velká — jako `LLamaSharp.Backend.Cuda12`). Distribuce přes náš lokální feed / NuGet.
- **Feature gap**: ControlNet/IP-Adapter/custom nody — sd.cpp pokrývá část, ne celý
  ComfyUI graph. Proto **komplementární**, ne náhrada na 100 % hned. ComfyUI zůstává pro
  pokročilé.
- **Výkon**: sd.cpp je solidní, ale PyTorch/ComfyUI může být u některých operací rychlejší.
  Akceptovatelné kvůli hodnotě „nula závislostí“.
- **FLUX**: velký (12 GB), GGUF kvantizace nutná; ověřit že sd.cpp FLUX cesta je stabilní.
- **macOS**: Metal backend sd.cpp + Apple Silicon — ověřit (stejné parkoviště jako Metal
  LLM).
- **Paměť/VRAM**: souběh s LLM v chatu (FLUX ~12 GB + LLM se na 24 GB nevejdou) — stejný
  problém jako dnes, řeší `FreeMemory`/unload mezi režimy.

## 7. Co je v této (foundation) PR

- Tento návrh.
- `INativeImageGenerator` (Core) + modely + enumy.
- `NativeSamplerMap` (Core, čistá funkce mapování samplerů) + unit testy.
- **Zatím se nic nepřipojuje do běžící appky** (žádné DI, žádná UI, žádná nativní lib) —
  je to čistý základ, na kterém staví Fáze 1+. Build + testy zelené.

# AI Studio

Lokální AI studio pro Windows — chat s jazykovými modely a generování obrázků v jedné aplikaci. Žádný cloud, žádné kredity, vše běží na tvém PC.

## Co umí

| Modul | Stav | Popis |
|---|---|---|
| **Chat** | 🚧 UI hotové | Více konverzací, každá s vlastním modelem a limitem tokenů |
| **Image Studio** | 🚧 UI hotové | Více generátorů najednou, referenční obrázky, varianty, FHD/2K/4K |
| **Model Manager** | 🚧 UI hotové | Katalog modelů, stahování, VRAM info, search |
| **Nastavení** | 🚧 UI hotové | Téma, jazyk, složka modelů |
| **LLM backend** | 📋 Plánováno | LlamaSharp — lokální GGUF inference |
| **Image backend** | 📋 Plánováno | Embedded ComfyUI přes HTTP API |

## Požadavky

- **Windows 10/11** (64-bit)
- **.NET 10 Runtime**
- GPU s VRAM ≥ 4 GB pro menší modely (8B Q4), ≥ 24 GB pro FLUX Dev
- Místo na disku: dle stažených modelů (4–40 GB na model)

## Spuštění (development)

```bash
git clone ...
cd AIStudio
dotnet run --project AIStudio.App/AIStudio.App.csproj
```

## Build

```bash
dotnet build
dotnet publish AIStudio.App/AIStudio.App.csproj -c Release -r win-x64 --self-contained
```

## Struktura

```
AIStudio.App/        Avalonia UI aplikace
AIStudio.Core/       Doménové modely, rozhraní
AIStudio.Infrastructure/  Implementace služeb (SQLite, Serilog)
tools/IconGen/       Generátor ikony (SkiaSharp)
```

Detailní popis viz [ARCHITECTURE.md](ARCHITECTURE.md).

## Modely

Aplikace **nestahuje modely automaticky** — v sekci Modely si vyber co chceš a stáhni ručně nebo přes integrovaný download manager (ve vývoji).

**Doporučené pro začátek:**
- Chat: `Llama 3.1 8B Q4_K_M` (4.7 GB, ~6 GB VRAM)
- Obrázky: `FLUX.1 Schnell` (12 GB, ~12 GB VRAM)

## Technologie

- [Avalonia UI](https://avaloniaui.net/) — cross-platform .NET UI framework
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM pomocník
- [LlamaSharp](https://github.com/SciSharp/LLamaSharp) — .NET binding pro llama.cpp
- [ComfyUI](https://github.com/comfyanonymous/ComfyUI) — image generation engine
- SQLite + Serilog

## Roadmap

Viz [CHANGELOG.md](CHANGELOG.md) pro historii a plán.

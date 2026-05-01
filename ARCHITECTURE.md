# Architektura — AI Studio

## Přehled

```
┌─────────────────────────────────────────────────────────────┐
│                      Avalonia UI (.NET 10)                   │
│                                                              │
│  ┌────────────┐  ┌───────────────┐  ┌──────────────────┐   │
│  │    Chat    │  │ Image Studio  │  │  Model Manager   │   │
│  │  modul     │  │    modul      │  │     modul        │   │
│  └─────┬──────┘  └──────┬────────┘  └────────┬─────────┘   │
│        │                │                    │              │
│  ┌─────▼──────┐  ┌──────▼────────┐           │              │
│  │ LlamaSharp │  │  ComfyUI API  │  ┌────────▼─────────┐   │
│  │  Service   │  │   Client      │  │  Download Service │   │
│  └─────┬──────┘  └──────┬────────┘  │  (HF + Civitai)  │   │
│        │                │           └──────────────────┘   │
│        └────────┬────────┘                                  │
│                 │                                           │
│         ┌───────▼──────┐   ┌──────────────┐               │
│         │   SQLite DB  │   │   Serilog    │               │
│         │  (settings,  │   │   (logging)  │               │
│         │   history)   │   └──────────────┘               │
│         └──────────────┘                                   │
│                                                              │
│  Vedle hlavního procesu (skryté):                           │
│  ┌─────────────────────┐                                    │
│  │   ComfyUI (Python)  │◄── HTTP API ──► ComfyUI Client     │
│  │   localhost:8188     │                                    │
│  └─────────────────────┘                                    │
└─────────────────────────────────────────────────────────────┘
```

## Vrstvy

### AIStudio.Core
Čistá doménová vrstva — žádné závislosti na UI ani infrastruktuře.

```
Core/
├── Enums/
│   ├── AppTheme.cs          System | Dark | Light
│   ├── AppLanguage.cs       Czech | English
│   └── NavigationPage.cs    Chat | ImageStudio | Models | Settings
├── Interfaces/
│   └── ISettingsService.cs  Load/Save AppSettings
└── Models/
    └── AppSettings.cs       Perzistentní nastavení aplikace
```

### AIStudio.Infrastructure
Implementace služeb. Závisí na Core, ne na App.

```
Infrastructure/
├── Services/
│   └── SettingsService.cs   JSON soubor v %AppData%\AIStudio\settings.json
└── Database/                (plánováno) SQLite migrace a repozitáře
```

### AIStudio.App
Avalonia UI aplikace. Závisí na Core i Infrastructure.

```
App/
├── ViewModels/
│   ├── MainWindowViewModel  Navigace, aktivní stránka, StatusText
│   ├── Chat/
│   │   ├── ChatPageViewModel        Seznam konverzací, nový chat, odeslání zprávy
│   │   ├── ConversationViewModel    Titulek, model, maxTokens, zprávy
│   │   └── ChatMessage             Role (User|Assistant), Content, Timestamp
│   ├── ImageStudio/
│   │   ├── ImageStudioPageViewModel  Správa generátorů
│   │   └── ImageGeneratorViewModel   Prompt, model, ratio, quality, seed, ...
│   ├── Models/
│   │   ├── ModelManagerPageViewModel  Katalog, filtrace, stahování
│   │   └── ModelItemViewModel         Metadata modelu
│   └── Settings/
│       └── SettingsPageViewModel      Téma, jazyk, složka modelů
├── Views/                   Pair Views (AXAML + CS) pro každý ViewModel
├── Themes/
│   └── AppStyles.axaml      Sdílené styly (navItem, card, primary, tabBtn, ...)
└── Assets/
    └── app-icon.png         256×256 PNG generovaný přes SkiaSharp
```

## MVVM a navigace

**ViewLocator** automaticky mapuje ViewModely na Views:
```
AIStudio.App.ViewModels.Chat.ChatPageViewModel
→ AIStudio.App.Views.Chat.ChatPageView
```

Navigace funguje přes `MainWindowViewModel.NavigateCommand(NavigationPage)`,
který nastaví `CurrentPage` na instanci příslušného page ViewModelu.
`ContentControl` v `MainWindow.axaml` pak přes `ViewLocator` zobrazí správný View.

## Chat — plánovaná integrace LlamaSharp

```
ConversationViewModel.SelectedModelName  ←→  LlamaSharpService
                                              ├── LoadModelAsync(path)
                                              ├── UnloadModel()
                                              └── StreamAsync(prompt, tokens)
                                                   └── yield return token
```

Při přepnutí konverzace:
1. `LlamaSharpService.UnloadModel()` — uvolní VRAM/RAM
2. `LlamaSharpService.LoadModelAsync(newModel)` — načte nový model
3. Streaming odpovědi → `ConversationViewModel.Messages` přes `ObservableCollection`

## Image Studio — plánovaná integrace ComfyUI

ComfyUI běží jako skrytý Python subprocess (`localhost:8188`).
`ComfyUIClient` komunikuje přes WebSocket + REST:

```
ImageGeneratorViewModel.GenerateCommand
  → ComfyUIClient.QueuePromptAsync(workflow)
  → WebSocket progress events → DownloadProgress binding
  → ComfyUIClient.GetImageAsync(filename)
  → ImageGeneratorViewModel.GeneratedImages (ObservableCollection<Bitmap>)
```

Workflow se sestavuje dynamicky z parametrů (model, sampler, CFG, seed, rozlišení).

## Data storage

```
%AppData%\AIStudio\
├── settings.json        AppSettings (téma, jazyk, cesta k modelům)
├── aistudio.db          SQLite — konverzace, zprávy, galerie, oblíbené prompty
├── Models\              Stažené modely (GGUF, safetensors)
├── Outputs\             Vygenerované obrázky
├── Cache\               ComfyUI cache, náhledy
├── Thumbnails\          Miniatury obrázků
└── Logs\                Serilog rotující logy
```

## Design tokens

Tmavé téma (výchozí):

| Vrstva | Barva | Hex |
|---|---|---|
| Hlavní pozadí | Dark | `#0D0D0D` |
| Sidebar | Sidebar | `#161618` |
| Sekundární panel | Panel | `#111113` |
| Karta | Card | `#1C1C1E` |
| Input | Input | `#1C1C1E` |
| Oddělovač | Border | `#2A2A2E` |
| Akcent | Purple | `#7C3AED` |
| Akcent hover | Purple-dark | `#6D28D9` |
| Akcent text | Purple-light | `#A78BFA` |
| Aktivní highlight | Purple-dim | `#26263A` |
| Text primární | White | `#EBEBF5` |
| Text sekundární | Gray | `#888896` |
| Text slabý | Dim | `#555560` |
| Úspěch | Green | `#4ADE80` |
| Chyba | Red | `#F87171` |

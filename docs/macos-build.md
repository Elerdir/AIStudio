# macOS build — návod

Postup, jak na Apple Silicon Macu (M1/M2/M3/M4) vyrobit `.app` bundle a DMG
installer AI Studia. Intel Mac **není podporován** — `Info.plist` má
`LSRequiresNativeExecution=true`, takže pod Rosetta 2 to ani nepojede.

Spodní hranice: **macOS 12 Monterey** (.NET 10 SDK requirement).

---

## TL;DR

```bash
# 1) Předpoklady (jednorázově)
xcode-select --install                               # Xcode CLI tools
# nainstaluj .NET 10 SDK z https://dotnet.microsoft.com/download/dotnet/10.0

# 2) Build (z root složky repa)
chmod +x scripts/build-macos.sh
./scripts/build-macos.sh --sign-adhoc --dmg

# 3) Test
open artifacts/macos/"AI Studio.app"
```

Výstup: `artifacts/macos/AIStudio-0.1.0-arm64.dmg` — drag-to-Applications DMG.

---

## Co `build-macos.sh` dělá

Pět kroků:

1. **dotnet publish** — `osx-arm64`, `Release`, `self-contained=true`,
   `UseAppHost=true`. To znamená že DMG obsahuje **kompletní .NET 10 runtime**
   uvnitř, uživatel si nic neinstaluje předem (~130 MB navíc).
2. **Sestaví .app bundle** — vytvoří kanonickou strukturu:
   ```
   AI Studio.app/
   ├── Contents/
   │   ├── Info.plist           ← z AIStudio.App/Info.plist
   │   ├── MacOS/
   │   │   ├── AIStudio.App     ← native launcher (s exec bitem)
   │   │   ├── *.dylib          ← LLamaSharp.Backend.Metal, SkiaSharp, …
   │   │   └── … (cca 600 souborů .NET runtime + Avalonia)
   │   └── Resources/
   │       └── AppIcon.icns     ← pokud existuje
   ```
3. **Signing** (3 varianty, viz níže)
4. **Notarizace** (jen s paid Apple Developer účtem)
5. **DMG** přes vestavěný `hdiutil` — zip-compressed UDZO image
   s drag-to-/Applications symlinkem

---

## Tři způsoby podpisu

### A) Unsigned *(default, free)*

```bash
./scripts/build-macos.sh --dmg
```

✅ Pro: zdarma, nic neřeší
❌ Proti: Gatekeeper warning `„AI Studio“ nelze otevřít, protože pochází
od neidentifikovaného vývojáře`

**Workaround pro uživatele:** pravým klikem na ikonku → **Open** (NE dvojklik).
V dialogu klikne **Open** znovu. Toto je **jednorázové** — macOS si zapamatuje
důvěru. Při dalších spuštěních se chová normálně.

Pro distribuci přátelům nebo na GitHub Releases je tahle varianta naprosto OK.

### B) Ad-hoc signed *(free)*

```bash
./scripts/build-macos.sh --sign-adhoc --dmg
```

`codesign --sign -` použije „null" identitu — bundle je formálně podepsaný,
ale nikým konkrétním. Gatekeeper warning **stále vyskočí** (žádný registrovaný
vývojář), ale:
- Aplikace přežije přesun napříč souborovými systémy bez resetu kvarantény
- Některé macOS API (Camera, Microphone) fungují stabilněji
- Binárka má aspoň „nějaký" podpis pro forensic accounting

Pro lokální testování doporučené. Pro distribuci výhody jsou malé.

### C) Developer ID + notarizace *(placené, ~$99/rok)*

```bash
./scripts/build-macos.sh \
    --sign "Developer ID Application: Jméno Příjmení (TEAMID12345)" \
    --notarize-profile "ai-studio-notary" \
    --dmg
```

Tohle je **„production" cesta** — uživatel klikne na DMG, přetáhne do
Applications, dvojklikne ikonku a aplikace se otevře **bez jakéhokoliv
warningu**.

**Předpoklady:**

1. **Apple Developer Program** ($99/rok) → [developer.apple.com](https://developer.apple.com)
2. **Developer ID Application certificate** v Keychainu:
   - Xcode → Settings → Accounts → tvůj Apple ID → Manage Certificates → +
   - Vyber **„Developer ID Application"**
   - Cert se uloží automaticky do login Keychain
3. **Notarization keychain profile** (jednorázově):
   ```bash
   # Vygeneruj App-Specific Password na appleid.apple.com → Sign-In and Security
   # → App-Specific Passwords → „ai-studio-notary"

   xcrun notarytool store-credentials "ai-studio-notary" \
       --apple-id "tvuj@email.com" \
       --team-id "TEAMID12345" \
       --password "xxxx-xxxx-xxxx-xxxx"
   ```
   To uloží credentials zašifrované do macOS Keychainu pod jménem
   `ai-studio-notary`. Pak už build-macos.sh skript jen použije `--keychain-profile`.

**Co se uvnitř děje:**

- Hardened Runtime entitlements (`allow-jit`, `allow-unsigned-executable-memory`,
  `disable-library-validation`, `network.client`, `files.user-selected.read-write`)
  — viz `aistudio.entitlements` který skript generuje. Tyto jsou nutné kvůli:
  - .NET runtime potřebuje JIT
  - LLamaSharp Metal backend načítá unsigned dylibs
  - ComfyUI Python subprocess
- `codesign` rekurzivně podepíše všechny `.dylib`/`.so` a pak bundle
- `notarytool submit --wait` čeká na Apple (typicky 1-5 minut)
- `stapler staple` přilepí notarization ticket k bundle (offline ověření)

---

## AppIcon.icns (volitelné, ale doporučené)

V `AIStudio.App/Resources/AppIcon.icns` — pokud ho tam dáš, skript ho
automaticky zaregistruje v Info.plist a aplikace bude mít vlastní ikonu
v Docku, Finderu i Cmd+Tab.

### Vygenerování z PNG

Apple standard: 16/32/64/128/256/512/1024 px ve dvou variantách (1x i 2x retina).

```bash
# 1) Vyrobit jednotlivé velikosti z source PNG (např. 1024×1024)
mkdir AppIcon.iconset
sips -z 16 16     source.png --out AppIcon.iconset/icon_16x16.png
sips -z 32 32     source.png --out AppIcon.iconset/icon_16x16@2x.png
sips -z 32 32     source.png --out AppIcon.iconset/icon_32x32.png
sips -z 64 64     source.png --out AppIcon.iconset/icon_32x32@2x.png
sips -z 128 128   source.png --out AppIcon.iconset/icon_128x128.png
sips -z 256 256   source.png --out AppIcon.iconset/icon_128x128@2x.png
sips -z 256 256   source.png --out AppIcon.iconset/icon_256x256.png
sips -z 512 512   source.png --out AppIcon.iconset/icon_256x256@2x.png
sips -z 512 512   source.png --out AppIcon.iconset/icon_512x512.png
cp source.png                AppIcon.iconset/icon_512x512@2x.png

# 2) Sestavit do .icns
iconutil -c icns AppIcon.iconset
mv AppIcon.icns AIStudio.App/Resources/
```

V repu už existuje `tools/IconGen` (SkiaSharp). Můžeš ho rozšířit aby vyrobil
i `.icns` (nebo použít `iconutil` jak je výše).

---

## CI build na GitHub Actions

Workflow `.github/workflows/release-macos.yml` (vytvořený samostatným commitem)
buildí .app + DMG na **macos-14 runneru** (Apple Silicon M1, v matrix-CI matice
už ji máme jako hard-gate).

Tag-triggered: pushneš tag `v0.1.0` → workflow vyrobí DMG a publikuje ho
do GitHub Releases.

---

## Co AI Studio na macOS umí (a co ne, zatím)

✅ **Funguje (z designu):**
- Avalonia UI 100% cross-platform
- LLamaSharp s Metal backendem (Apple Silicon GPU acceleration pro LLM)
- ComfyUI auto-installer (git clone + python venv + pip — `MacOsComfyInstaller`)
- GPU detekce přes `system_profiler` (`MacOsGpuDetector`)
- System monitor (CPU/RAM/VRAM ticker přes sysctl + vm_stat)
- Token šifrování přes Keychain (`security` CLI wrapper)

⚠️ **Čeká na fyzický Mac:**
- **Skutečný runtime ověřený** je zatím jen CI build (kompiluje + linkuje)
- Drobné věci jako file dialog UX, drag&drop, Metal performance scaling
  reálně otestovány nebyly. Tvoje M1 je **první real-world testovací target**.

❌ **Neimplementováno:**
- macOS native installer s welcome screenem (DMG je nejjednodušší cesta)
- Auto-update přes Sparkle / UpdateHub.Client (UpdateHub funguje cross-platform,
  ale .app self-update na macOS vyžaduje workaround)
- Code signing CI integration (vyžaduje secret management Developer ID cert)

---

## Troubleshooting

### „AI Studio is damaged and can't be opened" *(po stažení z internetu)*

Quarantine attribute z prohlížeče. Z Terminálu:

```bash
xattr -dr com.apple.quarantine "/Applications/AI Studio.app"
```

Lepší řešení: notarizovat aplikaci (varianta C výše).

### „dotnet: command not found" *(při běhu skriptu)*

Instalátor .NET 10 SDK přidává do `/usr/local/share/dotnet/dotnet`. Pokud
shell to nevidí, přidej do `~/.zshrc`:

```bash
export PATH="$PATH:/usr/local/share/dotnet"
```

### „Library not loaded: libLLamaSharp.dylib"

LLamaSharp Backend.Metal balík nebyl správně publishnut. Ujisti se že csproj
má:

```xml
<ItemGroup Condition="$([MSBuild]::IsOSPlatform('OSX'))">
    <PackageReference Include="LLamaSharp.Backend.Metal" Version="0.27.0" />
</ItemGroup>
```

A že buildoval z macOS (jinak `IsOSPlatform('OSX')` v MSBuild predikátu
vyhodnotí false a balík se vůbec nepřidá).

### Build hlásí „Architecture 'arm64' is not supported"

Cross-compile z Intel/Linux Macu na arm64 nefunguje vždy hladce. Doporučení:
buildovat přímo na Apple Silicon.

### Notarization failed: „The signature does not include a secure timestamp"

Chybí `--timestamp` v `codesign` volání. Skript ho má — ale pokud upravuješ
vlastní variantu, neztrať to. Apple bez timestampu odmítne notarizaci.

### „Bundle format unrecognized" při hdiutil create

Bundle nemá `Contents/MacOS/AIStudio.App` exec bit. Skript ho nastavuje
v Step 2, ale pokud kopíruješ ručně, dej:

```bash
chmod +x "AI Studio.app/Contents/MacOS/AIStudio.App"
```

---

## Reference

- [Apple — Notarizing macOS Software Before Distribution](https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution)
- [Apple — Hardened Runtime](https://developer.apple.com/documentation/security/hardened_runtime)
- [.NET App Host on macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos)
- [Avalonia macOS App Bundle structure](https://docs.avaloniaui.net/docs/deployment/macOS)
- [hdiutil man page](https://ss64.com/osx/hdiutil.html)

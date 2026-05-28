#!/usr/bin/env bash
#
# AI Studio — macOS Apple Silicon build script
# =============================================
#
# Vyrobí .app bundle (a volitelně .dmg installer) pro macOS 12+ na Apple Silicon
# (M1/M2/M3/M4). Intel Mac NEPODPORUJEME — viz Info.plist LSRequiresNativeExecution.
#
# Použití (z root složky repa):
#   ./scripts/build-macos.sh                       # unsigned, jen .app bundle
#   ./scripts/build-macos.sh --dmg                 # + DMG installer
#   ./scripts/build-macos.sh --sign-adhoc --dmg    # ad-hoc signed (free)
#   ./scripts/build-macos.sh --sign "Developer ID Application: Jméno (TEAMID)" \
#                            --notarize-profile "ai-studio-notary" --dmg
#                                                  # paid signing + notarizace
#
# Výstupy:
#   artifacts/macos/AIStudio.app          — bundle, lze přetáhnout do /Applications
#   artifacts/macos/AIStudio-{ver}.dmg    — installer (s --dmg flagem)
#
# Předpoklady (na buildovacím Macu):
#   - macOS 12+ Apple Silicon
#   - .NET 10 SDK   →  https://dotnet.microsoft.com/download
#   - Xcode Command Line Tools  →  xcode-select --install
#   - (optional) Apple Developer ID Application cert v Keychainu pro paid signing
#   - (optional) `xcrun notarytool store-credentials` profile pro notarizaci
#
set -euo pipefail

# ── Konstanty ─────────────────────────────────────────────────────────────────

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
readonly APP_NAME="AIStudio.App"
readonly BUNDLE_NAME="AI Studio"
readonly BUNDLE_ID="cz.niderle.aistudio"
readonly RID="osx-arm64"
readonly TFM="net10.0"
readonly CONFIG="Release"
readonly ARTIFACTS_DIR="$REPO_ROOT/artifacts/macos"
readonly PUBLISH_DIR="$REPO_ROOT/$APP_NAME/bin/$CONFIG/$TFM/$RID/publish"
readonly INFO_PLIST_SRC="$REPO_ROOT/$APP_NAME/Info.plist"

# ── CLI parsing ───────────────────────────────────────────────────────────────

MAKE_DMG=false
SIGN_IDENTITY=""
SIGN_ADHOC=false
NOTARIZE_PROFILE=""
VERSION_OVERRIDE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dmg)               MAKE_DMG=true; shift ;;
        --sign-adhoc)        SIGN_ADHOC=true; shift ;;
        --sign)              SIGN_IDENTITY="$2"; shift 2 ;;
        --notarize-profile)  NOTARIZE_PROFILE="$2"; shift 2 ;;
        --version)           VERSION_OVERRIDE="$2"; shift 2 ;;
        -h|--help)
            sed -n '4,30p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Neznámý argument: $1" >&2
            exit 1
            ;;
    esac
done

# ── Sanity checks ─────────────────────────────────────────────────────────────

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "❌ Tento skript musí běžet na macOS (zjištěno: $(uname -s))." >&2
    echo "   Pro CI použij macOS runner — viz .github/workflows/release-macos.yml" >&2
    exit 1
fi

if [[ "$(uname -m)" != "arm64" ]]; then
    echo "⚠️  Buildujeme na $(uname -m), ale výstup cílí arm64." >&2
    echo "   Cross-compile funguje, ale doporučené je buildovat na Apple Silicon." >&2
fi

if ! command -v dotnet &> /dev/null; then
    echo "❌ dotnet CLI nenalezeno. Nainstaluj .NET 10 SDK z https://dotnet.microsoft.com" >&2
    exit 1
fi

dotnet_version="$(dotnet --version 2>/dev/null || echo unknown)"
if [[ ! "$dotnet_version" =~ ^10\. ]]; then
    echo "⚠️  Očekáván .NET 10 SDK, máš $dotnet_version. Build může selhat na TargetFramework." >&2
fi

# Hardened sign nelze kombinovat s ad-hoc
if [[ -n "$SIGN_IDENTITY" && "$SIGN_ADHOC" == "true" ]]; then
    echo "❌ --sign a --sign-adhoc nelze použít naráz." >&2
    exit 1
fi

# Notarizace vyžaduje paid signing identity
if [[ -n "$NOTARIZE_PROFILE" && -z "$SIGN_IDENTITY" ]]; then
    echo "❌ --notarize-profile vyžaduje --sign s Developer ID Application identitou." >&2
    exit 1
fi

# ── Step 1: dotnet publish ────────────────────────────────────────────────────

echo ""
echo "▶ Step 1/5 — dotnet publish ($RID, $CONFIG)…"

# -p:PublishSingleFile=false   → držíme klasický multi-file layout (.app bundle)
# -p:PublishTrimmed=false      → trim by mohl odstranit Avalonia reflection-only typy
# -p:SelfContained=true        → bundlujeme .NET runtime (uživatel nemusí instalovat)
# -p:UseAppHost=true           → vytvoří native launcher binárku (AIStudio.App)
# -p:DebugType=embedded        → PDB inline, žádné side-by-side soubory
# -p:Version="$VERSION_OVERRIDE" → Info.plist verze (pokud zadáno)

PUBLISH_ARGS=(
    "$REPO_ROOT/$APP_NAME/$APP_NAME.csproj"
    --configuration "$CONFIG"
    --runtime       "$RID"
    --self-contained true
    -p:PublishSingleFile=false
    -p:PublishTrimmed=false
    -p:UseAppHost=true
    -p:DebugType=embedded
)
if [[ -n "$VERSION_OVERRIDE" ]]; then
    PUBLISH_ARGS+=("-p:Version=$VERSION_OVERRIDE")
fi

dotnet publish "${PUBLISH_ARGS[@]}"

if [[ ! -f "$PUBLISH_DIR/$APP_NAME" ]]; then
    echo "❌ Publish nevyrobil $PUBLISH_DIR/$APP_NAME" >&2
    exit 1
fi

# ── Step 2: vytvoř .app bundle strukturu ──────────────────────────────────────

echo ""
echo "▶ Step 2/5 — sestavuji .app bundle…"

mkdir -p "$ARTIFACTS_DIR"

APP_BUNDLE="$ARTIFACTS_DIR/$BUNDLE_NAME.app"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Zkopíruj všechny publish artefakty do MacOS/
# (binárka i .NET runtime dlls, native libs, atd.)
cp -R "$PUBLISH_DIR"/* "$APP_BUNDLE/Contents/MacOS/"

# Info.plist — kanonická umístění
cp "$INFO_PLIST_SRC" "$APP_BUNDLE/Contents/Info.plist"

# Pokud existuje AppIcon.icns v App/Resources/, zkopíruj
if [[ -f "$REPO_ROOT/$APP_NAME/Resources/AppIcon.icns" ]]; then
    cp "$REPO_ROOT/$APP_NAME/Resources/AppIcon.icns" "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
    # Ujisti se že Info.plist má CFBundleIconFile — Avalonia placeholderem
    if ! /usr/libexec/PlistBuddy -c "Print :CFBundleIconFile" "$APP_BUNDLE/Contents/Info.plist" &>/dev/null; then
        /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$APP_BUNDLE/Contents/Info.plist"
        echo "  ✓ Přidána ikona AppIcon.icns + zaregistrována v Info.plist"
    fi
else
    echo "  ⚠ Žádná Resources/AppIcon.icns — bundle použije systémový default."
    echo "    Vygeneruj přes 'tools/IconGen' nebo iconutil (viz docs/macos-build.md)."
fi

# Spustitelnost launcheru — některé filesystem operace přes copy ji zruší
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_NAME"

echo "  ✓ Bundle: $APP_BUNDLE"

# ── Step 3: Signing ───────────────────────────────────────────────────────────

if [[ "$SIGN_ADHOC" == "true" ]]; then
    echo ""
    echo "▶ Step 3/5 — ad-hoc signing (free, bez Apple Developer účtu)…"
    # Ad-hoc sign nezruší Gatekeeper warning, ale aspoň označí binárku jako
    # „nějak signovanou" a quarantine se chová o něco lépe.
    codesign --force --deep --sign - "$APP_BUNDLE"
    echo "  ✓ Ad-hoc signature aplikována"
elif [[ -n "$SIGN_IDENTITY" ]]; then
    echo ""
    echo "▶ Step 3/5 — Developer ID signing: $SIGN_IDENTITY"

    # Hardened Runtime — vyžadováno pro notarizaci
    # Entitlements pokrývají native LLamaSharp libs + JIT pro .NET runtime
    ENTITLEMENTS_FILE="$ARTIFACTS_DIR/aistudio.entitlements"
    cat > "$ENTITLEMENTS_FILE" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <!-- .NET runtime potřebuje JIT compile native code -->
    <key>com.apple.security.cs.allow-jit</key>
    <true/>
    <!-- LLamaSharp / Metal backend načítají dynamicky native dylibs -->
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
    <true/>
    <!-- ComfyUI je Python subprocess s vlastními dylibs — disable validation
         pro načítání nepodepsaných knihoven -->
    <key>com.apple.security.cs.disable-library-validation</key>
    <true/>
    <!-- HuggingFace, Civitai, updatehub.niderle.cz, localhost ComfyUI -->
    <key>com.apple.security.network.client</key>
    <true/>
    <!-- Uživatelská složka (Models, Images), Downloads, atd. -->
    <key>com.apple.security.files.user-selected.read-write</key>
    <true/>
</dict>
</plist>
EOF

    # Podepiš všechny nested binárky odzdola nahoru (--deep + per-file)
    # Pak hlavní bundle s entitlements.
    find "$APP_BUNDLE" -type f \( -name "*.dylib" -o -name "*.so" \) -exec \
        codesign --force --options runtime --timestamp \
                 --sign "$SIGN_IDENTITY" {} \;

    codesign --force --options runtime --timestamp \
             --entitlements "$ENTITLEMENTS_FILE" \
             --sign "$SIGN_IDENTITY" \
             "$APP_BUNDLE"

    codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"
    echo "  ✓ Developer ID signature aplikována s hardened runtime"
else
    echo ""
    echo "▶ Step 3/5 — bez signingu (uživatel uvidí Gatekeeper warning)"
    echo "  ℹ Tip: --sign-adhoc pro free ad-hoc signing"
fi

# ── Step 4: Notarizace (jen pokud paid signing) ───────────────────────────────

if [[ -n "$NOTARIZE_PROFILE" ]]; then
    echo ""
    echo "▶ Step 4/5 — notarizace přes Apple (profile $NOTARIZE_PROFILE)…"

    NOTARY_ZIP="$ARTIFACTS_DIR/notary-submit.zip"
    /usr/bin/ditto -c -k --keepParent "$APP_BUNDLE" "$NOTARY_ZIP"

    xcrun notarytool submit "$NOTARY_ZIP" \
                            --keychain-profile "$NOTARIZE_PROFILE" \
                            --wait

    xcrun stapler staple "$APP_BUNDLE"
    rm -f "$NOTARY_ZIP"
    echo "  ✓ Notarized + stapled — Gatekeeper bude tichý"
else
    echo ""
    echo "▶ Step 4/5 — bez notarizace"
fi

# ── Step 5: DMG (volitelné) ───────────────────────────────────────────────────

if [[ "$MAKE_DMG" == "true" ]]; then
    echo ""
    echo "▶ Step 5/5 — DMG installer…"

    # Verze z Info.plist (nebo z --version override)
    BUNDLE_VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP_BUNDLE/Contents/Info.plist")"
    DMG_PATH="$ARTIFACTS_DIR/AIStudio-${BUNDLE_VERSION}-arm64.dmg"
    DMG_STAGING="$ARTIFACTS_DIR/dmg-staging"

    rm -rf "$DMG_STAGING" "$DMG_PATH"
    mkdir -p "$DMG_STAGING"
    cp -R "$APP_BUNDLE" "$DMG_STAGING/"

    # Symlink na /Applications — uživatel přetáhne ikonku z DMG
    ln -s /Applications "$DMG_STAGING/Applications"

    # hdiutil je vestavěný v macOS — bez závislosti na npm/brew
    hdiutil create -volname "AI Studio" \
                   -srcfolder "$DMG_STAGING" \
                   -ov -format UDZO \
                   "$DMG_PATH"

    # Pokud máme signing identity, podepíšeme i DMG (notarizovaný DMG je čistší
    # UX — uživatel nedostane warning po staženi)
    if [[ -n "$SIGN_IDENTITY" ]]; then
        codesign --force --sign "$SIGN_IDENTITY" "$DMG_PATH"
        if [[ -n "$NOTARIZE_PROFILE" ]]; then
            xcrun notarytool submit "$DMG_PATH" \
                                    --keychain-profile "$NOTARIZE_PROFILE" \
                                    --wait
            xcrun stapler staple "$DMG_PATH"
        fi
    fi

    rm -rf "$DMG_STAGING"

    DMG_SIZE=$(du -h "$DMG_PATH" | cut -f1)
    echo "  ✓ DMG: $DMG_PATH ($DMG_SIZE)"
else
    echo ""
    echo "▶ Step 5/5 — DMG vynechán (přidej --dmg)"
fi

# ── Hotovo ────────────────────────────────────────────────────────────────────

echo ""
echo "═══════════════════════════════════════════════════════════════════════"
echo "✅ Hotovo"
echo ""
echo "📦 Bundle: $APP_BUNDLE"
echo ""
echo "🚀 Test:  open '$APP_BUNDLE'"
echo ""
if [[ "$SIGN_ADHOC" == "true" || -z "$SIGN_IDENTITY" ]]; then
    echo "ℹ Pro první spuštění unsigned/ad-hoc aplikace:"
    echo "   1. Pravým klikem na ikonku → 'Open' (NE dvojklik)"
    echo "   2. V dialogu klikni 'Open' znovu"
    echo "   Toto je jednorázové — macOS si zapamatuje udělení důvěry."
fi
echo "═══════════════════════════════════════════════════════════════════════"

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Serilog;
using AIStudio.Core.Interfaces;
using AIStudio.Core.Models;

namespace AIStudio.Infrastructure.Services;

public sealed class ComfyService : IComfyService, IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private Process? _process;
    private readonly string _clientId = Guid.NewGuid().ToString("N")[..12];

    private ComfyStatus _status = ComfyStatus.Stopped;
    private string _statusMessage = "Zastaveno";

    public ComfyStatus Status        => _status;
    public bool        IsRunning     => _status == ComfyStatus.Running;
    public string      StatusMessage => _statusMessage;

    public event Action<ComfyStatus>? StatusChanged;

    public ComfyService(ISettingsService settings)
    {
        _settings = settings;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (await IsHealthyAsync())
            SetStatus(ComfyStatus.Running, "Spuštěno (extern)");
        else
            SetStatus(ComfyStatus.Stopped, "Zastaveno");

        if (_settings.Settings.AutoStartComfyUi && !IsRunning)
            await StartAsync();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return true;

        // Pokud na portu už ComfyUI odpovídá, nepokoušej se startovat nový —
        // skončilo by to s [Errno 10048] bind conflict. Místo toho se navážeme
        // na existující instanci. Typicky to nastane když AIStudio zhebl bez
        // korektního shutdownu a child ComfyUI proces přežil.
        if (await IsHealthyAsync())
        {
            var existingPort = _settings.Settings.ComfyUiPort;
            SetStatus(ComfyStatus.Running, $"ComfyUI již běží (port {existingPort})");
            Log.Information("ComfyService: ComfyUI už běží na portu {Port}, " +
                            "nezakládám nový proces — nakonektuju se na existující.",
                            existingPort);
            return true;
        }

        var dir = _settings.Settings.ComfyUiDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            SetStatus(ComfyStatus.Error, "Chyba: cesta ke ComfyUI není nastavena");
            return false;
        }

        var mainPy = Path.Combine(dir, "main.py");
        if (!File.Exists(mainPy))
        {
            SetStatus(ComfyStatus.Error, $"Chyba: main.py nenalezeno v {dir}");
            return false;
        }

        // Před spuštěním propíšeme AIStudio Models adresář do ComfyUI přes
        // extra_model_paths.yaml. Bez toho by ComfyUI viděl jen své vlastní
        // models/checkpoints a uživatel by musel modely ručně přesouvat.
        try
        {
            WriteExtraModelPathsYaml(dir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyService: zápis extra_model_paths.yaml selhal — ComfyUI nemusí vidět AIStudio modely");
        }

        SetStatus(ComfyStatus.Starting, "Spouštím ComfyUI… (může trvat 1-3 minuty)");

        var python = string.IsNullOrWhiteSpace(_settings.Settings.PythonPath)
            ? "python"
            : _settings.Settings.PythonPath;

        var port = _settings.Settings.ComfyUiPort;

        // Detekce ComfyUI Portable (embedded python). Když ano:
        //   • flag -s = nečíst user site-packages (vyžadováno embedded buildem)
        //   • flag --windows-standalone-build = ComfyUI ví, že je portable, použije lokální cesty
        //   • working directory = parent ComfyUI/ (kde leží python_embeded vedle ComfyUI)
        // Bez těchto flagů se ComfyUI Portable občas spustí, ale chová se divně.
        // Replikujeme to, co dělá distribuční run_nvidia_gpu.bat.
        var isPortable = python.Contains("python_embeded", StringComparison.OrdinalIgnoreCase);

        var arguments = isPortable
            ? $"-s \"{mainPy}\" --windows-standalone-build --port {port} --listen 127.0.0.1"
            : $"\"{mainPy}\" --port {port} --listen 127.0.0.1";

        var workingDir = isPortable
            ? (Path.GetDirectoryName(dir) ?? dir)   // ComfyUI_windows_portable/ namísto ComfyUI/
            : dir;

        var psi = new ProcessStartInfo
        {
            FileName               = python,
            Arguments              = arguments,
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        Log.Information("ComfyService: spouštím {Python} {Args} (cwd={Cwd}, portable={Portable})",
                        python, arguments, workingDir, isPortable);

        try
        {
            _process = Process.Start(psi);
            if (_process is null)
            {
                SetStatus(ComfyStatus.Error, "Chyba: nelze spustit proces");
                return false;
            }

            // Stdout/stderr proudy aktivně čteme, jinak by se interní pipe buffer
            // zaplnil a Python by se zablokoval na write. Logujeme INF pro stdout
            // (ComfyUI banner, „Starting server" atd.) a WRN pro stderr.
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Information("[ComfyUI] {Line}", e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Warning("[ComfyUI] {Line}", e.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // První start ComfyUI Portable je dlouhý — Python kompiluje .pyc pro
            // tisíce modulů, importuje torch + CUDA, alokuje VRAM. S aktivním
            // Defenderem (skenuje každý .pyc) klidně 3+ minuty. Dáváme tedy
            // 180 s timeout, ale uvnitř detekujeme i HasExited (pokud Python
            // padne, oznámíme to ihned místo čekání na timeout).
            const int timeoutSeconds = 180;
            for (var i = 0; i < timeoutSeconds; i++)
            {
                await Task.Delay(1000, ct);

                // Proces už není naživu → spadl
                if (_process.HasExited)
                {
                    var code = _process.ExitCode;
                    SetStatus(ComfyStatus.Error,
                        $"ComfyUI proces skončil (exit code {code}). " +
                        $"Detail v logu (%AppData%\\AIStudio\\logs\\).");
                    Log.Error("ComfyService: proces ukončen s kódem {Code} po {Sec} s", code, i + 1);
                    return false;
                }

                if (await IsHealthyAsync())
                {
                    SetStatus(ComfyStatus.Running, $"Spuštěno (port {port})");
                    Log.Information("ComfyService: zdravotní check OK po {Sec} s", i + 1);
                    return true;
                }

                // Periodicky aktualizovat status — uživatel vidí, že to ještě jede
                if (i is 15 or 45 or 90 or 135)
                {
                    SetStatus(ComfyStatus.Starting,
                        $"Spouštím ComfyUI… ({i} s, max {timeoutSeconds} s)");
                }
            }

            SetStatus(ComfyStatus.Error,
                $"Timeout: ComfyUI se nespustilo do {timeoutSeconds} s. " +
                $"Možná chyba v logu — viz %AppData%\\AIStudio\\logs\\.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus(ComfyStatus.Error, $"Chyba: {ex.Message}");
            Log.Error(ex, "ComfyService: StartAsync selhalo");
            return false;
        }
    }

    public Task StopAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
                _process = null;
            }
        }
        catch { /* best effort */ }

        SetStatus(ComfyStatus.Stopped, "Zastaveno");
        return Task.CompletedTask;
    }

    // ── ComfyUI API ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetCheckpointsAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/object_info/CheckpointLoaderSimple", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement
                    .GetProperty("CheckpointLoaderSimple")
                    .GetProperty("input")
                    .GetProperty("required")
                    .GetProperty("ckpt_name")[0] is { ValueKind: JsonValueKind.Array } arr)
            {
                return arr.EnumerateArray()
                          .Select(e => e.GetString() ?? "")
                          .Where(s => !string.IsNullOrEmpty(s))
                          .OrderBy(s => s)
                          .ToList();
            }
        }
        catch { /* ComfyUI nedostupné nebo jiný formát */ }
        return Array.Empty<string>();
    }

    public async Task<string> QueuePromptAsync(Dictionary<string, object> workflow,
                                                CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            prompt    = workflow,
            client_id = _clientId,
        });

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{BaseUrl}/prompt", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("prompt_id").GetString()
               ?? throw new InvalidOperationException("ComfyUI nevratilo prompt_id");
    }

    public async Task<ComfyGenerationResult?> WaitForResultAsync(
        string promptId, IProgress<int>? progress, CancellationToken ct)
    {
        progress?.Report(0);

        while (!ct.IsCancellationRequested)
        {
            // Zkontrolujeme historii
            try
            {
                var resp = await _http.GetAsync($"{BaseUrl}/history/{promptId}", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty(promptId, out var entry))
                    {
                        var images = new List<ComfyImageRef>();
                        if (entry.TryGetProperty("outputs", out var outputs))
                        {
                            foreach (var node in outputs.EnumerateObject())
                            {
                                if (!node.Value.TryGetProperty("images", out var imgs)) continue;
                                foreach (var img in imgs.EnumerateArray())
                                {
                                    images.Add(new ComfyImageRef(
                                        img.GetProperty("filename").GetString() ?? "",
                                        img.TryGetProperty("subfolder", out var sf)
                                            ? sf.GetString() ?? "" : "",
                                        img.TryGetProperty("type", out var t)
                                            ? t.GetString() ?? "output" : "output"));
                                }
                            }
                        }

                        if (images.Count > 0)
                        {
                            progress?.Report(100);
                            return new ComfyGenerationResult(promptId, images, DateTime.Now);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* přejdeme */ }

            // Zkontrolujeme, jestli prompt stále běží (progress ~50%)
            try
            {
                var queueResp = await _http.GetAsync($"{BaseUrl}/queue", ct);
                if (queueResp.IsSuccessStatusCode)
                {
                    var queueJson = await queueResp.Content.ReadAsStringAsync(ct);
                    using var queueDoc = JsonDocument.Parse(queueJson);

                    var isRunning = queueDoc.RootElement
                        .GetProperty("queue_running")
                        .EnumerateArray()
                        .Any(item => item.GetArrayLength() > 1
                                  && item[1].GetString() == promptId);

                    progress?.Report(isRunning ? 50 : 10);
                }
            }
            catch { /* přejdeme */ }

            await Task.Delay(600, ct);
        }

        return null;
    }

    public async Task<byte[]> DownloadImageAsync(string filename, string subfolder = "",
                                                  string type = "output",
                                                  CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/view?" +
                  $"filename={Uri.EscapeDataString(filename)}" +
                  $"&subfolder={Uri.EscapeDataString(subfolder)}" +
                  $"&type={Uri.EscapeDataString(type)}";

        return await _http.GetByteArrayAsync(url, ct);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private string BaseUrl =>
        $"http://localhost:{_settings.Settings.ComfyUiPort}";

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var       resp = await _http.GetAsync($"{BaseUrl}/system_stats", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Vytvoří v ComfyUI rootu soubor <c>extra_model_paths.yaml</c>, který ComfyUI
    /// načte při startu a přidá AIStudio Models adresář mezi cesty pro modely.
    /// Mapuje stejnou složku na všechny relevantní typy — uživatel pak může mít
    /// modely v plochém Models/ adresáři nebo v podsložkách (checkpoints/, loras/…).
    ///
    /// FLUX GGUF je „diffusion_models"/„unet" v ComfyUI, SDXL safetensors je
    /// „checkpoints" — proto mapujeme do obou kategorií.
    /// </summary>
    private void WriteExtraModelPathsYaml(string comfyUiDir)
    {
        var modelsDir = string.IsNullOrWhiteSpace(_settings.Settings.ModelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "AIStudio", "Models")
            : _settings.Settings.ModelsDirectory;

        if (!Directory.Exists(modelsDir))
        {
            Log.Information("ComfyService: Models adresář {Dir} neexistuje, extra_model_paths.yaml se nezapisuje", modelsDir);
            return;
        }

        // YAML potřebuje forward-slashy — i na Windows ComfyUI/Python s tím
        // pracuje konzistentněji než s backslashem (escape sequence).
        var modelsPath = modelsDir.Replace('\\', '/').TrimEnd('/');

        // Multi-line value (YAML "block scalar" |) umožňuje uvést více cest
        // pro jednu kategorii — uživatel tak může mít modely buď přímo v Models/
        // (relativní cesta `.`) nebo v podsložce typu checkpoints/.
        var yaml =
            "# Generováno AI Studio — propojuje knihovnu modelů s ComfyUI." + "\n" +
            "# Tento soubor je při každém startu ComfyUI z AIStudia přepisován," + "\n" +
            "# úpravy ručně budou ztraceny."                                    + "\n" +
            "aistudio:" + "\n" +
            $"  base_path: {modelsPath}" + "\n" +
            "  checkpoints: |" + "\n" +
            "    ." + "\n" +
            "    checkpoints/" + "\n" +
            "  diffusion_models: |" + "\n" +
            "    ." + "\n" +
            "    diffusion_models/" + "\n" +
            "    unet/" + "\n" +
            "  unet: |" + "\n" +
            "    ." + "\n" +
            "    unet/" + "\n" +
            "  loras: |" + "\n" +
            "    ." + "\n" +
            "    loras/" + "\n" +
            "  vae: |" + "\n" +
            "    ." + "\n" +
            "    vae/" + "\n" +
            "  upscale_models: |" + "\n" +
            "    ." + "\n" +
            "    upscale_models/" + "\n" +
            "  embeddings: |" + "\n" +
            "    ." + "\n" +
            "    embeddings/" + "\n" +
            "  controlnet: |" + "\n" +
            "    ." + "\n" +
            "    controlnet/" + "\n" +
            "  clip: |" + "\n" +
            "    ." + "\n" +
            "    clip/" + "\n" +
            "  clip_vision: |" + "\n" +
            "    ." + "\n" +
            "    clip_vision/" + "\n" +
            "  style_models: |" + "\n" +
            "    ." + "\n" +
            "    style_models/" + "\n";

        var yamlPath = Path.Combine(comfyUiDir, "extra_model_paths.yaml");
        File.WriteAllText(yamlPath, yaml);
        Log.Information("ComfyService: extra_model_paths.yaml zapsán → {Path} (base: {Base})",
                        yamlPath, modelsPath);
    }

    private void SetStatus(ComfyStatus status, string message)
    {
        _status        = status;
        _statusMessage = message;
        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _process?.Dispose();
    }
}

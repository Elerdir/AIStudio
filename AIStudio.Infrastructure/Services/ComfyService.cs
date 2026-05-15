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
    private readonly IComfyInstaller  _installer;
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

    /// <summary>
    /// Cesta k PID souboru — zapisujeme PID spuštěného ComfyUI procesu,
    /// aby ho při startu i pádu šlo spolehlivě dohledat a ukončit.
    /// </summary>
    private static string PidFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIStudio", "comfy.pid");

    public ComfyService(ISettingsService settings, IComfyInstaller installer)
    {
        _settings  = settings;
        _installer = installer;
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        Log.Information("ComfyService: InitializeAsync — kontrola zbylých procesů před startem");

        // Vždy zabij případný zbylý ComfyUI z předchozí session (crash, kill apod.)
        await KillOrphanAsync();

        if (_settings.Settings.AutoStartComfyUi)
        {
            Log.Information("ComfyService: AutoStart je zapnut — spouštím ComfyUI");
            await StartAsync();
        }
        else
        {
            // AutoStart vypnut — zkusíme se připojit na případné ručně spuštěné ComfyUI
            if (await IsHealthyAsync())
            {
                SetStatus(ComfyStatus.Running, "Spuštěno (extern — AutoStart vypnut)");
                Log.Information("ComfyService: AutoStart vypnut, nalezeno běžící ComfyUI na portu {Port} — připojeno",
                                _settings.Settings.ComfyUiPort);
            }
            else
            {
                SetStatus(ComfyStatus.Stopped, "Zastaveno");
                Log.Information("ComfyService: AutoStart vypnut, ComfyUI neběží — čekám na ruční start");
            }
        }
    }

    /// <summary>
    /// Najde a ukončí ComfyUI proces z předchozí session pomocí PID souboru.
    /// Bezpečné i pokud PID soubor neexistuje nebo proces už neběží.
    /// </summary>
    private async Task KillOrphanAsync()
    {
        // 1) Zkus zabit přes PID soubor (nejspolehlivější — funguje i po crashu)
        if (File.Exists(PidFilePath))
        {
            try
            {
                var pidText = await File.ReadAllTextAsync(PidFilePath);
                if (int.TryParse(pidText.Trim(), out var pid))
                {
                    try
                    {
                        var orphan = Process.GetProcessById(pid);
                        Log.Information("ComfyService: nalezen zbylý ComfyUI proces (PID={Pid}, Name={Name}) — ukončuji",
                                        pid, orphan.ProcessName);
                        orphan.Kill(entireProcessTree: true);
                        await orphan.WaitForExitAsync(CancellationToken.None)
                                    .WaitAsync(TimeSpan.FromSeconds(5));
                        Log.Information("ComfyService: zbylý proces PID={Pid} úspěšně ukončen", pid);
                    }
                    catch (ArgumentException)
                    {
                        // Proces s tímto PID už neexistuje — v pořádku
                        Log.Debug("ComfyService: PID={Pid} z pid souboru již neběží", pid);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "ComfyService: nepodařilo se ukončit zbylý proces PID={Pid}", pid);
                    }
                }
                File.Delete(PidFilePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComfyService: chyba při čtení/mazání PID souboru {Path}", PidFilePath);
            }
        }

        // 2) Záloha: pokud ComfyUI stále odpovídá na HTTP (port), zkus zabít vlastní _process
        if (_process is { HasExited: false })
        {
            Log.Information("ComfyService: ukončuji vlastní _process (PID={Pid}) z minulé session",
                            _process.Id);
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
            catch (Exception ex) { Log.Warning(ex, "ComfyService: kill vlastního _process selhal"); }
            _process = null;
        }
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

        // Cestu k Pythonu si určíme jednou — používáme ji jak pro install custom nodu,
        // tak pro samotné spuštění ComfyUI procesu níž.
        var python = string.IsNullOrWhiteSpace(_settings.Settings.PythonPath)
            ? "python"
            : _settings.Settings.PythonPath;

        // Ověříme + doinstalujeme custom node ComfyUI-GGUF (nutný pro FLUX GGUF).
        // Nesmíme padnout pokud install selže — uživatel může používat SDXL i bez něj.
        try
        {
            if (!_installer.IsGgufNodeInstalled(dir))
            {
                SetStatus(ComfyStatus.Starting, "Instaluji custom node ComfyUI-GGUF…");
                await _installer.EnsureGgufNodeInstalledAsync(dir, python, progress: null, ct);
                Log.Information("ComfyService: ComfyUI-GGUF nainstalován");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyService: instalace ComfyUI-GGUF selhala — FLUX GGUF nebude k dispozici, " +
                            "SDXL safetensors fungují i bez něj");
        }

        SetStatus(ComfyStatus.Starting, "Spouštím ComfyUI… (může trvat 1-3 minuty)");

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

        Log.Information("ComfyService: spouštím {Python} {Args} (cwd={Cwd}, portable={Portable}, port={Port})",
                        python, arguments, workingDir, isPortable, port);

        var startedAt = DateTime.Now;

        try
        {
            _process = Process.Start(psi);
            if (_process is null)
            {
                SetStatus(ComfyStatus.Error, "Chyba: nelze spustit proces");
                Log.Error("ComfyService: Process.Start vrátil null — Python nenalezen nebo odmítl start");
                return false;
            }

            // EnableRaisingEvents musí být nastaveno na Process (ne ProcessStartInfo)
            _process.EnableRaisingEvents = true;

            // Zapiš PID soubor co nejdříve — pro případ pádu aplikace
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
                await File.WriteAllTextAsync(PidFilePath, _process.Id.ToString());
                Log.Information("ComfyService: ComfyUI spuštěno (PID={Pid}), PID uložen do {Path}",
                                _process.Id, PidFilePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComfyService: zápis PID souboru selhal — cleanup při crashu nemusí fungovat");
            }

            // Handler na neočekávané ukončení procesu (crash, kill z venku apod.)
            _process.Exited += (_, _) =>
            {
                var code    = _process?.ExitCode ?? -1;
                var runtime = DateTime.Now - startedAt;
                if (code == 0)
                    Log.Information("ComfyService: ComfyUI (PID={Pid}) ukončeno čistě po {Runtime}",
                                    _process?.Id, runtime.ToString(@"hh\:mm\:ss"));
                else
                    Log.Warning("ComfyService: ComfyUI (PID={Pid}) skončilo s kódem {Code} po {Runtime} — možný crash",
                                _process?.Id, code, runtime.ToString(@"hh\:mm\:ss"));

                // Odstraň PID soubor — proces už neběží
                try { File.Delete(PidFilePath); } catch { /* best effort */ }

                // Pokud jsme neinicializovali stop (např. pád), aktualizuj status
                if (_status == ComfyStatus.Running)
                    SetStatus(ComfyStatus.Error,
                        $"ComfyUI nečekaně skončilo (exit {code}) — restartuj nebo zkontroluj log");
            };

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
        if (_process is { HasExited: false })
        {
            Log.Information("ComfyService: zastavuji ComfyUI (PID={Pid})", _process.Id);
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
                Log.Information("ComfyService: ComfyUI (PID={Pid}) ukončeno", _process?.Id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ComfyService: kill ComfyUI procesu selhal");
            }
            _process = null;
        }
        else
        {
            Log.Information("ComfyService: StopAsync — proces již neběží nebo nebyl spuštěn námi");
        }

        // Odstraň PID soubor pokud existuje
        try { if (File.Exists(PidFilePath)) File.Delete(PidFilePath); } catch { /* best effort */ }

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

    public async Task<IReadOnlyList<string>> GetLorasAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{BaseUrl}/object_info/LoraLoader", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement
                    .GetProperty("LoraLoader")
                    .GetProperty("input")
                    .GetProperty("required")
                    .GetProperty("lora_name")[0] is { ValueKind: JsonValueKind.Array } arr)
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

        // ComfyUI při validation erroru vrací 400 + JSON s detailem v node_errors
        // (např. „CheckpointLoaderSimple: ckpt_name not in []"). Bez parsování by
        // uživatel viděl jen generický 400 Bad Request.
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildValidationErrorMessage(json));
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("prompt_id").GetString()
               ?? throw new InvalidOperationException("ComfyUI nevratilo prompt_id");
    }

    /// <summary>
    /// Z 400 odpovědi ComfyUI poskládá lidsky čitelnou hlášku.
    /// Typický payload obsahuje <c>node_errors</c> mapu node_id → list chyb,
    /// kde každá chyba má <c>message</c> a <c>details</c> (např.
    /// "Value not in list: ckpt_name: 'flux1-dev.gguf' not in []").
    /// </summary>
    private static string BuildValidationErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Hlavní zpráva
            var message = root.TryGetProperty("error", out var err) &&
                          err.ValueKind == JsonValueKind.Object &&
                          err.TryGetProperty("message", out var m)
                ? m.GetString() ?? "Workflow validace selhala"
                : "Workflow validace selhala";

            // Sesbíráme node-level errory, pokud existují
            var details = new List<string>();
            if (root.TryGetProperty("node_errors", out var nodeErrors) &&
                nodeErrors.ValueKind == JsonValueKind.Object)
            {
                foreach (var node in nodeErrors.EnumerateObject())
                {
                    if (!node.Value.TryGetProperty("errors", out var errs)) continue;
                    foreach (var e in errs.EnumerateArray())
                    {
                        var nodeMsg = e.TryGetProperty("message", out var em) ? em.GetString() : null;
                        var det     = e.TryGetProperty("details", out var ed) ? ed.GetString() : null;
                        if (!string.IsNullOrEmpty(nodeMsg))
                            details.Add(string.IsNullOrEmpty(det) ? nodeMsg : $"{nodeMsg} — {det}");
                    }
                }
            }

            return details.Count == 0
                ? message
                : $"{message}: {string.Join("; ", details)}";
        }
        catch
        {
            // Pokud není parsovatelný JSON, vrátíme aspoň surovou odpověď
            return string.IsNullOrWhiteSpace(json)
                ? "ComfyUI vrátilo prázdnou chybovou odpověď"
                : $"ComfyUI: {json[..Math.Min(json.Length, 300)]}";
        }
    }

    public async Task<string> UploadImageAsync(string localFilePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(localFilePath);
        using var form    = new MultipartFormDataContent();
        using var imgBody = new StreamContent(stream);
        form.Add(imgBody,                               "image", Path.GetFileName(localFilePath));
        form.Add(new StringContent("input"),            "type");
        form.Add(new StringContent("true"),             "overwrite");

        using var resp = await _http.PostAsync($"{BaseUrl}/upload/image", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("name").GetString()
               ?? throw new InvalidOperationException("ComfyUI upload nevrátilo název souboru");
    }

    public async Task<ComfyGenerationResult?> WaitForResultAsync(
        string promptId, IProgress<int>? progress, CancellationToken ct)
    {
        progress?.Report(0);

        // WebSocket real-time progress — při selhání přepneme na polling.
        // POZOR: ComfyExecutionException (execution_error z ComfyUI) se NESMÍ
        // zachytit tady — propagujeme ji rovnou volajícímu, protože generování
        // selhal na straně ComfyUI a polling nepomůže.
        try
        {
            return await WaitViaWebSocketAsync(promptId, progress, ct);
        }
        catch (OperationCanceledException)    { throw; }
        catch (ComfyExecutionException)       { throw; }   // ← ComfyUI execution error, nepollingovat
        catch (Exception ex)
        {
            Log.Warning(ex, "ComfyService: WebSocket connection selhal, přepínám na polling");
            return await WaitViaPollingAsync(promptId, progress, ct);
        }
    }

    /// <summary>Vyvolána pokud ComfyUI samo ohlásí chybu (execution_error event).</summary>
    private sealed class ComfyExecutionException(string message) : Exception(message);

    /// <summary>
    /// Sleduje průběh generování přes ComfyUI WebSocket.
    /// Hlásí reálný krok/maximum (např. „8 / 20"), takže progress bar ukazuje přesnou hodnotu.
    /// </summary>
    private async Task<ComfyGenerationResult?> WaitViaWebSocketAsync(
        string promptId, IProgress<int>? progress, CancellationToken ct)
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        var wsUri    = new Uri($"ws://localhost:{_settings.Settings.ComfyUiPort}/ws?clientId={_clientId}");
        await ws.ConnectAsync(wsUri, ct);

        var buffer = new byte[16_384];

        while (!ct.IsCancellationRequested &&
               ws.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var recv = await ws.ReceiveAsync(buffer, ct);
            if (recv.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                break;

            // ComfyUI může rozsekat velkou zprávu na více framů — pro naše účely
            // (krátké JSON eventy) to nenastane, ale pro jistotu bereme Count z recv.
            var text = Encoding.UTF8.GetString(buffer, 0, recv.Count);

            try
            {
                using var doc  = JsonDocument.Parse(text);
                var root       = doc.RootElement;
                var type       = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                var data       = root.TryGetProperty("data", out var d) ? d : default;

                switch (type)
                {
                    case "progress":
                        // {"type":"progress","data":{"value":5,"max":20,"prompt_id":"..."}}
                        var value = data.GetProperty("value").GetInt32();
                        var max   = data.GetProperty("max").GetInt32();
                        if (max > 0)
                            progress?.Report(Math.Min(99, value * 99 / max));
                        break;

                    case "execution_success":
                        var pid = data.TryGetProperty("prompt_id", out var pidEl)
                            ? pidEl.GetString() : null;
                        if (pid == promptId)
                        {
                            progress?.Report(100);
                            return await FetchHistoryResultAsync(promptId, ct);
                        }
                        break;

                    case "execution_error":
                        var ePid = data.TryGetProperty("prompt_id", out var ePidEl)
                            ? ePidEl.GetString() : null;
                        if (ePid == promptId)
                        {
                            var msg = data.TryGetProperty("exception_message", out var em)
                                ? em.GetString() : "neznámá chyba";
                            // Typ uzlu + název souboru — usnadní debugging
                            var nodeType = data.TryGetProperty("exception_type", out var et)
                                ? et.GetString() : null;
                            var nodeId = data.TryGetProperty("node_id", out var ni)
                                ? ni.GetString() : null;
                            var detail = nodeType is not null
                                ? $"{msg} (uzel {nodeId} [{nodeType}])"
                                : msg;
                            Log.Warning("ComfyUI execution_error: {Detail}", detail);
                            throw new ComfyExecutionException($"ComfyUI chyba: {detail}");
                        }
                        break;
                }
            }
            catch (JsonException) { /* ignoruj neplatný JSON frame */ }
        }

        return null;
    }

    /// <summary>
    /// Načte výsledky z /history/{promptId}.
    /// Detekuje selhání jobu (status.status_str == "error") a vyhodí
    /// <see cref="ComfyExecutionException"/> — polling pak skončí s chybou místo
    /// točení do nekonečna.
    /// </summary>
    private async Task<ComfyGenerationResult?> FetchHistoryResultAsync(
        string promptId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"{BaseUrl}/history/{promptId}", ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(promptId, out var entry)) return null;

        // Pokud history říká, že job selhal, vyhodíme chybu — polling nemá smysl čekat dál.
        if (entry.TryGetProperty("status", out var status))
        {
            var statusStr = status.TryGetProperty("status_str", out var ss) ? ss.GetString() : null;
            var completed = status.TryGetProperty("completed", out var comp) && comp.ValueKind == JsonValueKind.True;
            if (completed && statusStr == "error")
            {
                // Zkusíme vytáhnout chybovou zprávu z messages[].
                var errMsg = "ComfyUI job selhal (bez detailu)";
                if (status.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in msgs.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2)
                        {
                            var mType = m[0].GetString();
                            if (mType == "execution_error" && m[1].ValueKind == JsonValueKind.Object
                                && m[1].TryGetProperty("exception_message", out var em))
                            {
                                errMsg = $"ComfyUI chyba: {em.GetString()}";
                                break;
                            }
                        }
                    }
                }
                throw new ComfyExecutionException(errMsg);
            }
        }

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
                        img.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "",
                        img.TryGetProperty("type", out var t2) ? t2.GetString() ?? "output" : "output"));
                }
            }
        }

        return images.Count > 0 ? new ComfyGenerationResult(promptId, images, DateTime.Now) : null;
    }

    /// <summary>Polling fallback pro případ, že WebSocket connection selže.</summary>
    private async Task<ComfyGenerationResult?> WaitViaPollingAsync(
        string promptId, IProgress<int>? progress, CancellationToken ct)
    {
        // Timeout 5 minut — bez něj by polling chodil donekonečna pokud ComfyUI selže
        // a history nikdy nehlásí completed (např. crash ComfyUI procesu).
        using var timeoutCts  = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts   = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedCt = linkedCts.Token;

        while (!linkedCt.IsCancellationRequested)
        {
            try
            {
                // FetchHistoryResultAsync vyhodí ComfyExecutionException pokud job selhal —
                // nechceme ji zachytit, propagujeme ji volajícímu (GenerateAsync → UI chybová hláška)
                var result = await FetchHistoryResultAsync(promptId, linkedCt);
                if (result is not null)
                {
                    progress?.Report(100);
                    return result;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (ComfyExecutionException)    { throw; }
            catch { /* přejdeme — přechodná HTTP chyba */ }

            try
            {
                var queueResp = await _http.GetAsync($"{BaseUrl}/queue", linkedCt);
                if (queueResp.IsSuccessStatusCode)
                {
                    var queueJson = await queueResp.Content.ReadAsStringAsync(linkedCt);
                    using var queueDoc = JsonDocument.Parse(queueJson);
                    var isRunning = queueDoc.RootElement
                        .GetProperty("queue_running")
                        .EnumerateArray()
                        .Any(item => item.GetArrayLength() > 1 && item[1].GetString() == promptId);
                    progress?.Report(isRunning ? 50 : 10);
                }
            }
            catch { /* přejdeme */ }

            try { await Task.Delay(600, linkedCt); }
            catch (OperationCanceledException) { break; }
        }

        if (timeoutCts.IsCancellationRequested)
            throw new ComfyExecutionException("Timeout: ComfyUI neodpověděl za 5 minut.");

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

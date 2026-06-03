using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace ArmageddonRidge.Tests;

public sealed class HeadlessEdgeStartupTests
{
    private static readonly TimeSpan ServerStartupTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BrowserStartupTimeout = TimeSpan.FromSeconds(15);
    private readonly ITestOutputHelper _output;

    public HeadlessEdgeStartupTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task ClientBootsInHeadlessEdgeBeforeAndAfterDebugBuild()
    {
        var edgePath = FindEdgePath();
        if (edgePath is null)
        {
            _output.WriteLine("Microsoft Edge was not found. Skipping headless browser smoke test.");
            return;
        }

        var repoRoot = FindRepoRoot();
        var clientProject = Path.Combine(repoRoot, "ArmageddonRidge.Client", "ArmageddonRidge.Client.csproj");
        var appPort = GetAvailablePort();
        var appUrl = $"https://localhost:{appPort}/";

        using var app = await StartClientAsync(repoRoot, clientProject, appUrl);
        var firstBoot = await RunHeadlessEdgeAsync(edgePath, appUrl);
        AssertCleanBoot(firstBoot);

        await RunDebugBuildAsync(repoRoot, clientProject);

        var secondBoot = await RunHeadlessEdgeAsync(edgePath, appUrl);
        AssertCleanBoot(secondBoot);
    }

    private static void AssertCleanBoot(BrowserSmokeResult result)
    {
        Assert.True(result.GameRootRendered, "Blazor did not render .game-root.");
        Assert.True(result.BattlefieldRendered, "The battlefield canvas did not render after starting a duel.");
        Assert.True(result.EffectsCanvasRendered, "The WebGPU effects overlay canvas did not render after starting a duel.");
        Assert.True(result.BattleConsoleRendered, "The bottom battle console did not render after starting a duel.");
        Assert.True(result.BattlefieldFpsButtonRendered, "The battlefield FPS button did not render after starting a duel.");
        Assert.True(result.BattlefieldFpsButtonShowsValue, "The battlefield FPS button did not show text like '58 FPS'.");
        Assert.True(result.BattlefieldCanvasChangedAfterAmbientTick, "The battlefield canvas did not update while idle; clouds and weather may be frozen.");
        Assert.True(result.PerfOverlayOpened, "The FPS overlay did not open after clicking FPS.");
        Assert.True(result.PerfOverlayClosed, "The FPS overlay did not close after clicking FPS a second time.");
        Assert.True(result.BrowserResponsiveAfterPerfClose, "The browser did not respond after closing the FPS overlay.");
        Assert.Empty(result.ConsoleErrors);
        Assert.Empty(result.Exceptions);
        Assert.Empty(result.NetworkFailures);
        Assert.Empty(result.FailedResponses);
    }

    private static async Task RunDebugBuildAsync(string repoRoot, string clientProject)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "build",
                    clientProject,
                    "--configuration",
                    "Debug",
                    "--no-restore"
                },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "Failed to start dotnet build.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"dotnet build failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private async Task<RunningProcess> StartClientAsync(string repoRoot, string clientProject, string appUrl)
    {
        var output = new ConcurrentQueue<string>();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "run",
                    "--project",
                    clientProject,
                    "--configuration",
                    "Debug",
                    "--no-restore",
                    "--urls",
                    appUrl.TrimEnd('/')
                },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, args) => EnqueueOutput(output, args.Data);
        process.ErrorDataReceived += (_, args) => EnqueueOutput(output, args.Data);

        Assert.True(process.Start(), "Failed to start dotnet run.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitForServerAsync(appUrl, process, output);
            return new RunningProcess(process, output, _output);
        }
        catch
        {
            KillProcessTree(process);
            throw;
        }
    }

    private static void EnqueueOutput(ConcurrentQueue<string> output, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) output.Enqueue(line);
    }

    private static async Task WaitForServerAsync(string appUrl, Process process, ConcurrentQueue<string> output)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + ServerStartupTimeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Client dev server exited early with code {process.ExitCode}.{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
            }

            try
            {
                using var response = await client.GetAsync(appUrl);
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for {appUrl}. Last error: {lastException?.Message}{Environment.NewLine}{string.Join(Environment.NewLine, output)}");
    }

    private async Task<BrowserSmokeResult> RunHeadlessEdgeAsync(string edgePath, string appUrl)
    {
        var cdpPort = GetAvailablePort();
        var profilePath = Path.Combine(Path.GetTempPath(), $"armageddon-ridge-edge-{Guid.NewGuid():N}");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                ArgumentList =
                {
                    "--headless=new",
                    "--disable-gpu",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--ignore-certificate-errors",
                    $"--remote-debugging-port={cdpPort}",
                    $"--user-data-dir={profilePath}",
                    "about:blank"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "Failed to start Microsoft Edge.");

        try
        {
            var target = await WaitForDevToolsTargetAsync(cdpPort, process);
            await using var client = await CdpClient.ConnectAsync(target.WebSocketDebuggerUrl);
            await client.EnableAsync();
            await client.NavigateAsync(appUrl);
            await Task.Delay(BrowserStartupTimeout);
            var gameRootRendered = await client.EvaluateBooleanAsync("Boolean(document.querySelector('.game-root'))");
            if (gameRootRendered)
            {
                await client.EvaluateBooleanAsync("""
                    (() => {
                        if (!window.AudioContext?.prototype?.resume) return true;
                        const resume = window.AudioContext.prototype.resume;
                        window.AudioContext.prototype.resume = function() {
                            try {
                                const result = resume.call(this);
                                return result?.catch ? result.catch(() => {}) : Promise.resolve();
                            } catch {
                                return Promise.resolve();
                            }
                        };
                        return true;
                    })()
                    """);
                await client.ClickButtonByTextAsync("New Duel");
                await Task.Delay(TimeSpan.FromSeconds(2));
                var shopStartVisible = await client.EvaluateBooleanAsync("""
                    Array.from(document.querySelectorAll('button'))
                        .some(button => button.textContent?.trim() === 'Start battle')
                    """);
                if (shopStartVisible)
                {
                    await client.ClickButtonByTextAsync("Start battle");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            var battlefieldRendered = await client.EvaluateBooleanAsync("Boolean(document.querySelector('canvas.battlefield'))");
            var effectsCanvasRendered = await client.EvaluateBooleanAsync("Boolean(document.querySelector('canvas.battlefield-effects'))");
            var battleConsoleRendered = await client.EvaluateBooleanAsync("Boolean(document.querySelector('.battle-console'))");
            var battlefieldFpsButtonRendered = await client.EvaluateBooleanAsync("Boolean(document.querySelector('.battlefield-fps-button'))");
            var battlefieldFpsButtonShowsValue = await client.EvaluateBooleanAsync("""
                /^\d+ FPS$/.test(document.querySelector('.battlefield-fps-button')?.textContent?.trim() ?? '')
                """);
            var firstBattlefieldFrame = await client.EvaluateStringAsync("document.querySelector('canvas.battlefield')?.toDataURL('image/png') ?? ''");
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            var secondBattlefieldFrame = await client.EvaluateStringAsync("document.querySelector('canvas.battlefield')?.toDataURL('image/png') ?? ''");
            var battlefieldCanvasChangedAfterAmbientTick =
                firstBattlefieldFrame.Length > 0 &&
                secondBattlefieldFrame.Length > 0 &&
                !string.Equals(firstBattlefieldFrame, secondBattlefieldFrame, StringComparison.Ordinal);
            var perfOverlayOpened = false;
            var perfOverlayClosed = false;
            var browserResponsiveAfterPerfClose = false;
            if (battlefieldFpsButtonRendered)
            {
                await client.ClickSelectorAsync(".battlefield-fps-button");
                await Task.Delay(TimeSpan.FromMilliseconds(650));
                perfOverlayOpened = await client.EvaluateBooleanAsync("Boolean(document.querySelector('.perf-overlay'))");
                await client.ClickSelectorAsync(".battlefield-fps-button");
                await Task.Delay(TimeSpan.FromMilliseconds(650));
                perfOverlayClosed = await client.EvaluateBooleanAsync("!document.querySelector('.perf-overlay')");
                browserResponsiveAfterPerfClose = await client.EvaluateBooleanAsync("(() => 21 * 2 === 42)()");
            }

            return new BrowserSmokeResult(
                gameRootRendered,
                battlefieldRendered,
                effectsCanvasRendered,
                battleConsoleRendered,
                battlefieldFpsButtonRendered,
                battlefieldFpsButtonShowsValue,
                battlefieldCanvasChangedAfterAmbientTick,
                perfOverlayOpened,
                perfOverlayClosed,
                browserResponsiveAfterPerfClose,
                client.ConsoleErrors.ToArray(),
                client.Exceptions.ToArray(),
                client.NetworkFailures.Where(f => f.Contains("localhost", StringComparison.OrdinalIgnoreCase)).ToArray(),
                client.FailedResponses.Where(f => f.Contains("localhost", StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        finally
        {
            KillProcessTree(process);
            TryDeleteDirectory(profilePath);
        }
    }

    private static async Task<DevToolsTarget> WaitForDevToolsTargetAsync(int port, Process process)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var endpoint = $"http://127.0.0.1:{port}/json/list";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Microsoft Edge exited early with code {process.ExitCode}.");

            try
            {
                var targets = await http.GetFromJsonAsync<DevToolsTarget[]>(endpoint);
                var page = targets?.FirstOrDefault(target => target.Type == "page");
                if (page is not null) return page;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Timed out waiting for Microsoft Edge DevTools target.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var clientProject = Path.Combine(directory.FullName, "ArmageddonRidge.Client", "ArmageddonRidge.Client.csproj");
            if (File.Exists(clientProject)) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ArmageddonRidge repository root.");
    }

    private static string? FindEdgePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ARMAGEDDONRIDGE_EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            "/usr/bin/microsoft-edge",
            "/usr/bin/microsoft-edge-stable",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record BrowserSmokeResult(
        bool GameRootRendered,
        bool BattlefieldRendered,
        bool EffectsCanvasRendered,
        bool BattleConsoleRendered,
        bool BattlefieldFpsButtonRendered,
        bool BattlefieldFpsButtonShowsValue,
        bool BattlefieldCanvasChangedAfterAmbientTick,
        bool PerfOverlayOpened,
        bool PerfOverlayClosed,
        bool BrowserResponsiveAfterPerfClose,
        string[] ConsoleErrors,
        string[] Exceptions,
        string[] NetworkFailures,
        string[] FailedResponses);

    private sealed record DevToolsTarget(string Type, string WebSocketDebuggerUrl);

    private sealed class RunningProcess : IDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output;
        private readonly ITestOutputHelper _testOutput;

        public RunningProcess(Process process, ConcurrentQueue<string> output, ITestOutputHelper testOutput)
        {
            _process = process;
            _output = output;
            _testOutput = testOutput;
        }

        public void Dispose()
        {
            KillProcessTree(_process);

            foreach (var line in _output)
            {
                _testOutput.WriteLine(line);
            }

            _process.Dispose();
        }
    }

    private sealed class CdpClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private int _nextId;

        public ConcurrentQueue<string> ConsoleErrors { get; } = new();
        public ConcurrentQueue<string> Exceptions { get; } = new();
        public ConcurrentQueue<string> NetworkFailures { get; } = new();
        public ConcurrentQueue<string> FailedResponses { get; } = new();

        private CdpClient(ClientWebSocket socket)
        {
            _socket = socket;
            _ = Task.Run(ReceiveLoopAsync);
        }

        public static async Task<CdpClient> ConnectAsync(string webSocketUrl)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(webSocketUrl), CancellationToken.None);
            return new CdpClient(socket);
        }

        public async Task EnableAsync()
        {
            await SendAsync("Runtime.enable");
            await SendAsync("Network.enable");
            await SendAsync("Page.enable");
            await SendAsync("Log.enable");
        }

        public Task NavigateAsync(string url)
        {
            return SendAsync("Page.navigate", new Dictionary<string, object?> { ["url"] = url });
        }

        public async Task<bool> EvaluateBooleanAsync(string expression)
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true
            });

            if (!result.TryGetProperty("result", out var runtimeResult)) return false;
            if (!runtimeResult.TryGetProperty("value", out var value)) return false;
            return value.ValueKind == JsonValueKind.True;
        }

        public async Task<string> EvaluateStringAsync(string expression)
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true
            });

            if (!result.TryGetProperty("result", out var runtimeResult)) return string.Empty;
            if (!runtimeResult.TryGetProperty("value", out var value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        public async Task ClickButtonByTextAsync(string text)
        {
            var center = await EvaluatePointAsync($$"""
                (() => {
                    const wanted = {{JsonSerializer.Serialize(text)}};
                    const button = Array.from(document.querySelectorAll('button'))
                        .find(candidate => {
                            if (candidate.textContent.trim() !== wanted) return false;
                            const rect = candidate.getBoundingClientRect();
                            const style = getComputedStyle(candidate);
                            return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
                        });
                    if (!button) return null;
                    const rect = button.getBoundingClientRect();
                    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
                })()
                """);
            if (center is null)
            {
                throw new InvalidOperationException($"Could not find button with text '{text}'.");
            }

            await SendMouseAsync("mouseMoved", center.X, center.Y, "none", 0);
            await SendMouseAsync("mousePressed", center.X, center.Y, "left", 1);
            await SendMouseAsync("mouseReleased", center.X, center.Y, "left", 1);
            await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = $$"""
                    (() => {
                        const wanted = {{JsonSerializer.Serialize(text)}};
                        const button = Array.from(document.querySelectorAll('button'))
                            .find(candidate => candidate.textContent.trim() === wanted);
                        if (!button) return false;
                        button.scrollIntoView({ block: 'center', inline: 'center' });
                        button.focus();
                        button.click();
                        return true;
                    })()
                    """,
                ["returnByValue"] = true
            });
        }

        public async Task ClickSelectorAsync(string selector)
        {
            var center = await EvaluatePointAsync($$"""
                (() => {
                    const element = document.querySelector({{JsonSerializer.Serialize(selector)}});
                    if (!element) return null;
                    const rect = element.getBoundingClientRect();
                    if (rect.width <= 0 || rect.height <= 0) return null;
                    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
                })()
                """);
            if (center is null)
            {
                throw new InvalidOperationException($"Could not find visible element matching selector '{selector}'.");
            }

            await SendMouseAsync("mouseMoved", center.X, center.Y, "none", 0);
            await SendMouseAsync("mousePressed", center.X, center.Y, "left", 1);
            await SendMouseAsync("mouseReleased", center.X, center.Y, "left", 1);
        }

        private async Task<ViewportPoint?> EvaluatePointAsync(string expression)
        {
            var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = expression,
                ["returnByValue"] = true
            });

            if (!result.TryGetProperty("result", out var runtimeResult)) return null;
            if (!runtimeResult.TryGetProperty("value", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (!value.TryGetProperty("x", out var x) || !value.TryGetProperty("y", out var y)) return null;
            return new ViewportPoint(x.GetDouble(), y.GetDouble());
        }

        private Task SendMouseAsync(string type, double x, double y, string button, int clickCount) =>
            SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?>
            {
                ["type"] = type,
                ["x"] = x,
                ["y"] = y,
                ["button"] = button,
                ["clickCount"] = clickCount
            });

        private async Task<JsonElement> SendAsync(string method, Dictionary<string, object?>? parameters = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                method,
                @params = parameters ?? new Dictionary<string, object?>()
            });

            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _cts.Token);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), _cts.Token);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[1024 * 64];

            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                try
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    HandleMessage(stream.ToArray());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    return;
                }
            }
        }

        private void HandleMessage(byte[] messageBytes)
        {
            using var document = JsonDocument.Parse(messageBytes);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && _pending.TryRemove(idElement.GetInt32(), out var pending))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    pending.SetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                    return;
                }

                pending.SetResult(root.GetProperty("result").Clone());
                return;
            }

            if (!root.TryGetProperty("method", out var methodElement)) return;

            var method = methodElement.GetString();
            if (!root.TryGetProperty("params", out var parameters)) return;

            switch (method)
            {
                case "Runtime.consoleAPICalled":
                    CaptureConsole(parameters);
                    break;
                case "Runtime.exceptionThrown":
                    Exceptions.Enqueue(parameters.GetProperty("exceptionDetails").GetRawText());
                    break;
                case "Network.loadingFailed":
                    NetworkFailures.Enqueue(parameters.GetRawText());
                    break;
                case "Network.responseReceived":
                    CaptureResponse(parameters);
                    break;
                case "Log.entryAdded":
                    CaptureLogEntry(parameters);
                    break;
            }
        }

        private void CaptureConsole(JsonElement parameters)
        {
            var type = parameters.GetProperty("type").GetString();
            if (type is not ("error" or "assert")) return;

            var builder = new StringBuilder(type);
            if (parameters.TryGetProperty("args", out var args))
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.TryGetProperty("value", out var value)) builder.Append(' ').Append(value);
                    else if (arg.TryGetProperty("description", out var description)) builder.Append(' ').Append(description.GetString());
                }
            }

            ConsoleErrors.Enqueue(builder.ToString());
        }

        private void CaptureLogEntry(JsonElement parameters)
        {
            var entry = parameters.GetProperty("entry");
            var level = entry.GetProperty("level").GetString();
            if (level is not "error") return;

            var source = entry.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() : "log";
            var text = entry.TryGetProperty("text", out var textElement) ? textElement.GetString() : entry.GetRawText();
            ConsoleErrors.Enqueue($"{source}: {text}");
        }

        private void CaptureResponse(JsonElement parameters)
        {
            var response = parameters.GetProperty("response");
            var status = response.GetProperty("status").GetInt32();
            if (status < 400) return;

            var url = response.GetProperty("url").GetString();
            var mimeType = response.GetProperty("mimeType").GetString();
            FailedResponses.Enqueue($"{status} {mimeType} {url}");
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                }
            }
            catch
            {
            }

            _socket.Dispose();
            _cts.Dispose();
        }
    }

    private sealed record ViewportPoint(double X, double Y);
}

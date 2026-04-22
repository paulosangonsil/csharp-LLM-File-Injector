using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Spectre.Console;
using System.Windows.Forms;

namespace CliFileInjector
{
    public class LlmConfig
    {
        public string Name { get; set; } = string.Empty;
        public string UrlSubstring { get; set; } = string.Empty;
        public string TextAreaSelector { get; set; } = string.Empty;
        public bool UsesContentEditable { get; set; }
        public string HomeUrl { get; set; } = string.Empty;
    }

    public class CdpVersionInfo
    {
        public string Browser { get; set; } = string.Empty;
        public string ProtocolVersion { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public string V8Version { get; set; } = string.Empty;
        public string WebKitVersion { get; set; } = string.Empty;
        public string WebSocketDebuggerUrl { get; set; } = string.Empty;
    }

    public class CdpEndpointCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string VersionEndpointUrl { get; set; } = string.Empty;
    }

    public class CdpSettings
    {
        public int RetryCount { get; set; } = 3;
        public int RetryDelayMilliseconds { get; set; } = 350;
        public List<CdpEndpointCandidate> EndpointCandidates { get; set; } = new();
    }

    public class BrowserLaunchSettings
    {
        public bool EnableLaunchFallback { get; set; } = true;
        public string Channel { get; set; } = "msedge";
        public bool Headless { get; set; }
        public List<string> Arguments { get; set; } = new();
    }

    public class OperationalSettings
    {
        public bool EnableGlobalHotkey { get; set; }
        public int MaxChunkBytes { get; set; } = 37 * 1024;
    }

    public class AppRuntimeOptions
    {
        public bool DiagnoseCdp { get; set; }
        public string WorkingDirectory { get; set; } = string.Empty;
    }

    public class BrowserSessionInfo
    {
        public IBrowser Browser { get; set; } = null!;
        public IBrowserContext Context { get; set; } = null!;
        public string ConnectionMode { get; set; } = string.Empty;
        public bool IsFallbackLaunched { get; set; }
    }

    public class BuildRuntimeOptionsParams
    {
        public string[] Args { get; set; } = Array.Empty<string>();
    }

    public class NormalizeCdpSettingsParams
    {
        public CdpSettings? Settings { get; set; }
    }

    public class NormalizeBrowserLaunchSettingsParams
    {
        public BrowserLaunchSettings? Settings { get; set; }
    }

    public class NormalizeOperationalSettingsParams
    {
        public OperationalSettings? Settings { get; set; }
    }

    public class ResolveCdpVersionInfoParams
    {
        public HttpClient HttpClient { get; set; } = null!;
        public string VersionEndpointUrl { get; set; } = string.Empty;
    }

    public class ConnectToCdpParams
    {
        public IPlaywright Playwright { get; set; } = null!;
        public CdpSettings Settings { get; set; } = null!;
    }

    public class DiagnoseCdpParams
    {
        public IPlaywright Playwright { get; set; } = null!;
        public CdpSettings Settings { get; set; } = null!;
    }

    public class LaunchBrowserParams
    {
        public IPlaywright Playwright { get; set; } = null!;
        public BrowserLaunchSettings Settings { get; set; } = null!;
    }

    public class ConnectBrowserSessionParams
    {
        public IPlaywright Playwright { get; set; } = null!;
        public CdpSettings CdpSettings { get; set; } = null!;
        public BrowserLaunchSettings BrowserLaunchSettings { get; set; } = null!;
    }

    public class EnsureSupportedSessionParams
    {
        public BrowserSessionInfo BrowserSession { get; set; } = null!;
        public IBrowserContext Context { get; set; } = null!;
        public List<LlmConfig> SupportedModels { get; set; } = new();
    }

    internal static class CdpConnector
    {
        public static async Task<IBrowser> ConnectToEdgeAsync(ConnectToCdpParams parameters)
        {
            using var httpClient = new HttpClient();

            Exception? lastException = null;
            int retryCount = Math.Max(1, parameters.Settings.RetryCount), retryDelayMilliseconds = Math.Max(0, parameters.Settings.RetryDelayMilliseconds);

            foreach (var candidate in parameters.Settings.EndpointCandidates)
            {
                for (int attemptIndex = 0; attemptIndex < retryCount; attemptIndex++)
                {
                    try
                    {
                        AnsiConsole.MarkupLine(
                            $"[grey]CDP probe:[/] {Markup.Escape(candidate.Name)} -> {Markup.Escape(candidate.VersionEndpointUrl)} (attempt {attemptIndex + 1}/{retryCount})");

                        var versionInfo = await ResolveVersionInfoAsync(new ResolveCdpVersionInfoParams
                        {
                            HttpClient = httpClient,
                            VersionEndpointUrl = candidate.VersionEndpointUrl
                        });

                        AnsiConsole.MarkupLine($"[grey]CDP browser:[/] {Markup.Escape(versionInfo.Browser)}");
                        AnsiConsole.MarkupLine($"[grey]CDP websocket:[/] {Markup.Escape(versionInfo.WebSocketDebuggerUrl)}");

                        return await parameters.Playwright.Chromium.ConnectOverCDPAsync(versionInfo.WebSocketDebuggerUrl);
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        AnsiConsole.MarkupLine(
                            $"[yellow]CDP attach failed via {Markup.Escape(candidate.Name)} (attempt {attemptIndex + 1}/{retryCount}): {Markup.Escape(ex.Message)}[/]");

                        if (attemptIndex < retryCount - 1 && retryDelayMilliseconds > 0)
                            await Task.Delay(retryDelayMilliseconds);
                    }
                }
            }

            throw new InvalidOperationException(
                "Could not attach to Edge via any configured CDP endpoint.",
                lastException);
        }

        public static async Task DiagnoseAsync(DiagnoseCdpParams parameters)
        {
            using var httpClient = new HttpClient();

            foreach (var candidate in parameters.Settings.EndpointCandidates)
            {
                AnsiConsole.Write(new Rule($"CDP diagnose: {candidate.Name}").RuleStyle("grey"));

                try
                {
                    var versionInfo = await ResolveVersionInfoAsync(new ResolveCdpVersionInfoParams
                    {
                        HttpClient = httpClient,
                        VersionEndpointUrl = candidate.VersionEndpointUrl
                    });

                    AnsiConsole.MarkupLine($"[green]Version endpoint reachable:[/] {Markup.Escape(candidate.VersionEndpointUrl)}");
                    AnsiConsole.MarkupLine($"[grey]Browser:[/] {Markup.Escape(versionInfo.Browser)}");
                    AnsiConsole.MarkupLine($"[grey]Protocol-Version:[/] {Markup.Escape(versionInfo.ProtocolVersion)}");
                    AnsiConsole.MarkupLine($"[grey]webSocketDebuggerUrl:[/] {Markup.Escape(versionInfo.WebSocketDebuggerUrl)}");

                    try
                    {
                        await using var browser = await parameters.Playwright.Chromium.ConnectOverCDPAsync(versionInfo.WebSocketDebuggerUrl);
                        int contextCount = browser.Contexts.Count;
                        int pageCount = browser.Contexts.Sum(x => x.Pages.Count);

                        AnsiConsole.MarkupLine($"[green]Attach OK.[/] Contexts: {contextCount}, Pages: {pageCount}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Attach failed:[/] {Markup.Escape(ex.Message)}");

                        Exception? inner = ex.InnerException;
                        while (inner != null)
                        {
                            AnsiConsole.MarkupLine($"[grey]  Inner:[/] {Markup.Escape(inner.Message)}");
                            inner = inner.InnerException;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Version endpoint failed:[/] {Markup.Escape(ex.Message)}");
                }
            }
        }

        private static async Task<CdpVersionInfo> ResolveVersionInfoAsync(ResolveCdpVersionInfoParams parameters)
        {
            using var response = await parameters.HttpClient.GetAsync(parameters.VersionEndpointUrl);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string browser = TryGetString(root, "Browser");
            string protocolVersion = TryGetString(root, "Protocol-Version");
            string userAgent = TryGetString(root, "User-Agent");
            string v8Version = TryGetString(root, "V8-Version");
            string webKitVersion = TryGetString(root, "WebKit-Version");
            string webSocketDebuggerUrl = TryGetString(root, "webSocketDebuggerUrl");

            if (string.IsNullOrWhiteSpace(webSocketDebuggerUrl))
                throw new InvalidOperationException("CDP endpoint did not return webSocketDebuggerUrl.");

            return new CdpVersionInfo
            {
                Browser = browser,
                ProtocolVersion = protocolVersion,
                UserAgent = userAgent,
                V8Version = v8Version,
                WebKitVersion = webKitVersion,
                WebSocketDebuggerUrl = webSocketDebuggerUrl
            };
        }

        private static string TryGetString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var value))
                return string.Empty;

            if (value.ValueKind != JsonValueKind.String)
                return string.Empty;

            return value.GetString() ?? string.Empty;
        }
    }

    internal static class BrowserLauncher
    {
        public static async Task<BrowserSessionInfo> LaunchAsync(LaunchBrowserParams parameters)
        {
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Channel = string.IsNullOrWhiteSpace(parameters.Settings.Channel) ? "msedge" : parameters.Settings.Channel,
                Headless = parameters.Settings.Headless
            };

            if (parameters.Settings.Arguments != null && parameters.Settings.Arguments.Count > 0)
                launchOptions.Args = parameters.Settings.Arguments;

            var browser = await parameters.Playwright.Chromium.LaunchAsync(launchOptions);
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await page.GotoAsync("about:blank");

            return new BrowserSessionInfo
            {
                Browser = browser,
                Context = context,
                ConnectionMode = $"Launched via Playwright ({launchOptions.Channel})",
                IsFallbackLaunched = true
            };
        }
    }

    class Program
    {
        private static bool _enableGlobalHotkey = false;
        private static int _maxChunkBytes = 37 * 1024;
        private static bool _hotkeyTriggered = false;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_F = 0x46;
        private const int HOTKEY_ID = 9000;

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".cpp", ".c", ".h", ".hpp",
            ".txt", ".json", ".xml", ".html", ".css",
            ".js", ".ts", ".md", ".ps1", ".bat",
            ".sh", ".yml", ".yaml", ".ini", ".sln", ".csproj"
        };

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".so", ".bin",
            ".zip", ".rar", ".7z",
            ".pdf",
            ".png", ".jpg", ".jpeg", ".ico",
            ".pdb", ".obj"
        };

        private record FileEntry(string FileName, string Content, bool IsText);

        private static int Utf8Bytes(string s) => Encoding.UTF8.GetByteCount(s);

        private static List<string> BuildChunks(List<FileEntry> entries)
        {
            var allSegments = new List<string>();

            foreach (var entry in entries)
            {
                if (!entry.IsText)
                {
                    allSegments.Add($"[Context Note: The binary/non-text file '{entry.FileName}' exists in the directory, but its content was omitted.]\n\n");
                    continue;
                }

                string fileContent = entry.Content.Replace("\r\n", "\n");
                string worstHeader = $"--- Start of '{entry.FileName}' (part 999 of 999) ---\n";
                string worstFooter = $"\n--- End of '{entry.FileName}' (part 999 of 999) ---\n\n";
                int markerBytes = Utf8Bytes(worstHeader) + Utf8Bytes(worstFooter);
                int budget = _maxChunkBytes - markerBytes;
                if (budget <= 0) budget = _maxChunkBytes / 2;

                var groups = new List<string>();
                var cur = new StringBuilder();
                int curBytes = 0;

                foreach (string line in fileContent.Split('\n'))
                {
                    string lineNl = line + "\n";
                    int lineBytes = Utf8Bytes(lineNl);

                    if (lineBytes > budget)
                    {
                        if (cur.Length > 0)
                        {
                            groups.Add(cur.ToString());
                            cur.Clear();
                            curBytes = 0;
                        }

                        string remaining = lineNl;
                        while (remaining.Length > 0)
                        {
                            int take = TakeUpToBytes(remaining, budget);
                            groups.Add(remaining[..take]);
                            remaining = remaining[take..];
                        }
                        continue;
                    }

                    if (curBytes + lineBytes > budget && cur.Length > 0)
                    {
                        groups.Add(cur.ToString());
                        cur.Clear();
                        curBytes = 0;
                    }

                    cur.Append(lineNl);
                    curBytes += lineBytes;
                }

                if (cur.Length > 0)
                    groups.Add(cur.ToString());

                int totalParts = groups.Count;
                for (int i = 0; i < totalParts; i++)
                {
                    string partLabel = totalParts > 1 ? $" (part {i + 1} of {totalParts})" : string.Empty;
                    string header = $"--- Start of '{entry.FileName}'{partLabel} ---\n";
                    string footer = $"\n--- End of '{entry.FileName}'{partLabel} ---\n\n";
                    allSegments.Add(header + groups[i] + footer);
                }
            }

            var chunks = new List<string>();
            var chunkBuilder = new StringBuilder();
            int chunkBytes = 0;

            void Flush()
            {
                if (chunkBuilder.Length > 0)
                {
                    chunks.Add(chunkBuilder.ToString());
                    chunkBuilder.Clear();
                    chunkBytes = 0;
                }
            }

            foreach (string seg in allSegments)
            {
                int segBytes = Utf8Bytes(seg);

                if (segBytes > _maxChunkBytes)
                {
                    Flush();
                    chunks.Add(seg);
                    continue;
                }

                if (chunkBytes + segBytes > _maxChunkBytes)
                    Flush();

                chunkBuilder.Append(seg);
                chunkBytes += segBytes;
            }

            Flush();
            return chunks;
        }

        private static int TakeUpToBytes(string s, int maxBytes)
        {
            int bytes = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int cb = Encoding.UTF8.GetByteCount(s, i, 1);
                if (bytes + cb > maxBytes) return i;
                bytes += cb;
            }
            return s.Length;
        }

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            var runtimeOptions = BuildRuntimeOptions(new BuildRuntimeOptionsParams
            {
                Args = args
            });

            string workingDirectory = runtimeOptions.WorkingDirectory;
            string lastBrowserDirectory = workingDirectory;
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            List<string> directoryFilterPatterns = new();

            IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(exeDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var supportedModels = config.GetSection("LlmProviders").Get<List<LlmConfig>>();
            if (supportedModels == null || supportedModels.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No LLM providers configured in appsettings.json.[/]");
                return;
            }

            var cdpSettings = NormalizeCdpSettings(new NormalizeCdpSettingsParams
            {
                Settings = config.GetSection("Cdp").Get<CdpSettings>()
            });

            var browserLaunchSettings = NormalizeBrowserLaunchSettings(new NormalizeBrowserLaunchSettingsParams
            {
                Settings = config.GetSection("BrowserLaunch").Get<BrowserLaunchSettings>()
            });

            var operationalSettings = NormalizeOperationalSettings(new NormalizeOperationalSettingsParams
            {
                Settings = config.GetSection("Operational").Get<OperationalSettings>()
            });

            _enableGlobalHotkey = operationalSettings.EnableGlobalHotkey;
            _maxChunkBytes = operationalSettings.MaxChunkBytes;

            using var playwright = await Playwright.CreateAsync();

            if (runtimeOptions.DiagnoseCdp)
            {
                AnsiConsole.MarkupLine("[blue]CDP diagnose mode enabled.[/]");
                await CdpConnector.DiagnoseAsync(new DiagnoseCdpParams
                {
                    Playwright = playwright,
                    Settings = cdpSettings
                });
                return;
            }

            Thread? hookThread = null;
            if (_enableGlobalHotkey)
            {
                hookThread = new Thread(RegisterGlobalHotKey);
                hookThread.SetApartmentState(ApartmentState.STA);
                hookThread.Start();
                AnsiConsole.MarkupLine("[green]Global hotkey (Ctrl+Alt+F) is ENABLED.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Global hotkey is DISABLED. Use Enter in the console instead.[/]");
            }

            AnsiConsole.MarkupLine("[blue]Connecting to Edge via CDP first, with optional Playwright launch fallback...[/]");

            BrowserSessionInfo browserSession;
            try
            {
                browserSession = await ConnectBrowserSessionAsync(new ConnectBrowserSessionParams
                {
                    Playwright = playwright,
                    CdpSettings = cdpSettings,
                    BrowserLaunchSettings = browserLaunchSettings
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]Failed to establish a usable browser session.[/]");
                AnsiConsole.MarkupLine($"[red]Details: {Markup.Escape(ex.Message)}[/]");
                return;
            }

            var context = browserSession.Context;

            AnsiConsole.MarkupLine($"[green]Service ready.[/] [grey]Mode:[/] {Markup.Escape(browserSession.ConnectionMode)}");
            if (_enableGlobalHotkey)
                AnsiConsole.MarkupLine("Press [yellow]Ctrl + Alt + F[/] from anywhere to inject files.");

            while (true)
            {
                if (_enableGlobalHotkey)
                {
                    if (!_hotkeyTriggered)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    _hotkeyTriggered = false;
                    SetForegroundWindow(GetConsoleWindow());
                    Console.Clear();
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[grey]Directory filter:[/] {FormatDirectoryFilter(directoryFilterPatterns)}");
                    AnsiConsole.MarkupLine("Press [yellow]Enter[/] to open the file browser, [yellow]*[/] to configure directory filter, or [grey]Esc[/] to exit.");

                    var key = Console.ReadKey(intercept: true);

                    if (key.Key == ConsoleKey.Escape)
                        break;

                    if (key.KeyChar == '*')
                    {
                        directoryFilterPatterns = PromptDirectoryFilterPatterns(directoryFilterPatterns);
                        Console.Clear();
                        continue;
                    }

                    if (key.Key != ConsoleKey.Enter)
                        continue;
                }

                var activeSessions = new List<(IPage Page, LlmConfig Config)>();
                foreach (var page in context.Pages)
                {
                    try
                    {
                        var match = supportedModels.FirstOrDefault(m => page.Url.Contains(m.UrlSubstring, StringComparison.OrdinalIgnoreCase));
                        if (match != null && !activeSessions.Any(s => s.Config.Name == match.Name))
                            activeSessions.Add((Page: page, Config: match));
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[grey]Skipped a page (possibly closed): {Markup.Escape(ex.Message)}[/]");
                    }
                }

                if (activeSessions.Count == 0)
                {
                    bool openedFromFallback = await EnsureSupportedSessionAsync(new EnsureSupportedSessionParams
                    {
                        BrowserSession = browserSession,
                        Context = context,
                        SupportedModels = supportedModels
                    });

                    if (openedFromFallback)
                    {
                        AnsiConsole.MarkupLine("[yellow]A provider page was opened in the launched browser. Sign in if needed, then trigger the injector again.[/]");
                        continue;
                    }

                    AnsiConsole.MarkupLine("[red]No supported LLM (ChatGPT, Claude, etc.) open in the current browser context.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine($"[green]{activeSessions.Count} LLM session(s) detected.[/]");

                var (selectedFilePaths, lastVisitedDirectory, cancelled) = InteractiveFileBrowser(
                    lastBrowserDirectory,
                    directoryFilterPatterns);

                if (cancelled)
                {
                    lastBrowserDirectory = lastVisitedDirectory;
                    AnsiConsole.MarkupLine("[yellow]File browser cancelled. Back to idle.[/]");
                    continue;
                }

                if (selectedFilePaths.Count == 0)
                {
                    lastBrowserDirectory = lastVisitedDirectory;
                    AnsiConsole.MarkupLine("[yellow]No files matched the current selection/filter. Back to idle.[/]");
                    continue;
                }

                lastBrowserDirectory = ResolveNextBrowserDirectory(selectedFilePaths, lastVisitedDirectory, lastBrowserDirectory);

                var fileEntries = new List<FileEntry>();
                foreach (var filePath in selectedFilePaths)
                {
                    string fileName = Path.GetFileName(filePath);
                    bool isText = IsTextFile(filePath);
                    string content = string.Empty;

                    if (isText)
                    {
                        try
                        {
                            content = File.ReadAllText(filePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            AnsiConsole.MarkupLine($"[yellow]⚠ Skipped '[white]{Markup.Escape(fileName)}[/]': {Markup.Escape(ex.Message)}[/]");
                            isText = false;
                        }
                    }

                    fileEntries.Add(new FileEntry(fileName, content, isText));
                }

                List<string> chunks = BuildChunks(fileEntries);
                int totalChunks = chunks.Count;

                if (totalChunks > 1)
                    AnsiConsole.MarkupLine($"[yellow]Payload exceeds {_maxChunkBytes / 1024} KB — will be sent in {totalChunks} chunk(s).[/]");

                var targets = new List<(IPage Page, LlmConfig Config)>();
                if (activeSessions.Count == 1)
                {
                    targets.Add(activeSessions[0]);
                }
                else
                {
                    var choices = activeSessions.Select(s => s.Config.Name).ToList();
                    choices.Add("All active sessions");

                    var selectedTarget = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Which LLM do you want to inject into?")
                            .AddChoices(choices));

                    if (selectedTarget == "All active sessions")
                        targets.AddRange(activeSessions);
                    else
                        targets.Add(activeSessions.First(s => s.Config.Name == selectedTarget));
                }

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    if (totalChunks > 1)
                        AnsiConsole.MarkupLine($"\n[blue]Injecting chunk {chunkIndex + 1} of {totalChunks}...[/]");

                    foreach (var target in targets)
                    {
                        try
                        {
                            await target.Page.BringToFrontAsync();
                            var locator = target.Page.Locator(target.Config.TextAreaSelector).First;
                            await locator.FocusAsync();
                            await target.Page.Keyboard.InsertTextAsync(chunks[chunkIndex]);
                            AnsiConsole.MarkupLine($"[green]✔ Injected into {target.Config.Name}![/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Error injecting into {target.Config.Name}: {Markup.Escape(ex.Message)}[/]");
                        }
                    }

                    if (chunkIndex < totalChunks - 1)
                    {
                        SetForegroundWindow(GetConsoleWindow());
                        AnsiConsole.MarkupLine(
                            $"\n[yellow]Chunk {chunkIndex + 1}/{totalChunks} injected.[/] Press [green]Enter[/] to send the next chunk, or [red]Esc[/] to abort.");

                        bool aborted = false;
                        while (true)
                        {
                            var k = Console.ReadKey(intercept: true);
                            if (k.Key == ConsoleKey.Enter) break;
                            if (k.Key == ConsoleKey.Escape)
                            {
                                aborted = true;
                                break;
                            }
                        }

                        if (aborted)
                        {
                            AnsiConsole.MarkupLine("[red]Injection aborted by user.[/]");
                            break;
                        }
                    }
                }

                AnsiConsole.MarkupLine("\n[grey]Done. Trigger again when you need another injection.[/]");
            }
        }

        private static async Task<BrowserSessionInfo> ConnectBrowserSessionAsync(ConnectBrowserSessionParams parameters)
        {
            try
            {
                var browser = await CdpConnector.ConnectToEdgeAsync(new ConnectToCdpParams
                {
                    Playwright = parameters.Playwright,
                    Settings = parameters.CdpSettings
                });

                if (browser.Contexts.Count == 0)
                    throw new InvalidOperationException("Connected over CDP, but no browser contexts were found.");

                return new BrowserSessionInfo
                {
                    Browser = browser,
                    Context = browser.Contexts[0],
                    ConnectionMode = "Attached to existing Edge via CDP",
                    IsFallbackLaunched = false
                };
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[yellow]CDP attach was not successful.[/]");
                AnsiConsole.MarkupLine($"[yellow]Reason:[/] {Markup.Escape(ex.Message)}");

                Exception? inner = ex.InnerException;
                while (inner != null)
                {
                    AnsiConsole.MarkupLine($"[grey]Inner:[/] {Markup.Escape(inner.Message)}");
                    inner = inner.InnerException;
                }

                if (!parameters.BrowserLaunchSettings.EnableLaunchFallback)
                    throw;

                bool shouldLaunch = AnsiConsole.Confirm(
                    "CDP attach failed. Launch a new Microsoft Edge instance controlled by Playwright instead?",
                    true);

                if (!shouldLaunch)
                    throw;

                AnsiConsole.MarkupLine("[blue]Launching a new Edge instance via Playwright...[/]");

                return await BrowserLauncher.LaunchAsync(new LaunchBrowserParams
                {
                    Playwright = parameters.Playwright,
                    Settings = parameters.BrowserLaunchSettings
                });
            }
        }

        private static async Task<bool> EnsureSupportedSessionAsync(EnsureSupportedSessionParams parameters)
        {
            if (!parameters.BrowserSession.IsFallbackLaunched)
                return false;

            var candidates = parameters.SupportedModels
                .Where(x => !string.IsNullOrWhiteSpace(x.HomeUrl))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
                return false;

            var choices = candidates.Select(x => x.Name).ToList();
            choices.Add("Skip for now");

            var selectedName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("No supported provider page is open. Which provider should be opened in the launched browser?")
                    .AddChoices(choices));

            if (selectedName == "Skip for now")
                return false;

            var selectedProvider = candidates.First(x => x.Name == selectedName);
            var page = parameters.Context.Pages.FirstOrDefault() ?? await parameters.Context.NewPageAsync();
            await page.GotoAsync(selectedProvider.HomeUrl);
            return true;
        }

        private static AppRuntimeOptions BuildRuntimeOptions(BuildRuntimeOptionsParams parameters)
        {
            var options = new AppRuntimeOptions();

            foreach (string arg in parameters.Args)
            {
                if (arg.Equals("--diagnose-cdp", StringComparison.OrdinalIgnoreCase))
                {
                    options.DiagnoseCdp = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(options.WorkingDirectory) && Directory.Exists(arg))
                    options.WorkingDirectory = arg;
            }

            if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
                options.WorkingDirectory = Directory.GetCurrentDirectory();

            return options;
        }

        private static CdpSettings NormalizeCdpSettings(NormalizeCdpSettingsParams parameters)
        {
            CdpSettings settings = parameters.Settings ?? new CdpSettings();

            if (settings.RetryCount <= 0)
                settings.RetryCount = 3;

            if (settings.RetryDelayMilliseconds < 0)
                settings.RetryDelayMilliseconds = 350;

            if (settings.EndpointCandidates == null || settings.EndpointCandidates.Count == 0)
            {
                settings.EndpointCandidates = new List<CdpEndpointCandidate>
                {
                    new CdpEndpointCandidate { Name = "localhost", VersionEndpointUrl = "http://localhost:9222/json/version" },
                    new CdpEndpointCandidate { Name = "127.0.0.1", VersionEndpointUrl = "http://127.0.0.1:9222/json/version" }
                };
            }

            settings.EndpointCandidates = settings.EndpointCandidates
                .Where(x => x != null)
                .Select(x => new CdpEndpointCandidate
                {
                    Name = string.IsNullOrWhiteSpace(x.Name) ? "unnamed" : x.Name,
                    VersionEndpointUrl = x.VersionEndpointUrl?.Trim() ?? string.Empty
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.VersionEndpointUrl))
                .ToList();

            if (settings.EndpointCandidates.Count == 0)
                settings.EndpointCandidates.Add(new CdpEndpointCandidate { Name = "localhost", VersionEndpointUrl = "http://localhost:9222/json/version" });

            return settings;
        }

        private static BrowserLaunchSettings NormalizeBrowserLaunchSettings(NormalizeBrowserLaunchSettingsParams parameters)
        {
            BrowserLaunchSettings settings = parameters.Settings ?? new BrowserLaunchSettings();

            if (string.IsNullOrWhiteSpace(settings.Channel))
                settings.Channel = "msedge";

            settings.Arguments ??= new List<string>();
            return settings;
        }

        private static OperationalSettings NormalizeOperationalSettings(NormalizeOperationalSettingsParams parameters)
        {
            OperationalSettings settings = parameters.Settings ?? new OperationalSettings();

            if (settings.MaxChunkBytes <= 0)
                settings.MaxChunkBytes = 37 * 1024;

            return settings;
        }

        private static string ResolveNextBrowserDirectory(List<string> selectedFilePaths, string lastVisitedDirectory, string fallbackDirectory)
        {
            if (selectedFilePaths == null || selectedFilePaths.Count == 0)
                return !string.IsNullOrWhiteSpace(lastVisitedDirectory) ? lastVisitedDirectory : fallbackDirectory;

            var directories = selectedFilePaths
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count == 0)
                return !string.IsNullOrWhiteSpace(lastVisitedDirectory) ? lastVisitedDirectory : fallbackDirectory;

            if (directories.Count == 1)
                return directories[0];

            return !string.IsNullOrWhiteSpace(lastVisitedDirectory) ? lastVisitedDirectory : fallbackDirectory;
        }

        private static List<string> PromptDirectoryFilterPatterns(List<string> currentPatterns)
        {
            Console.Clear();
            AnsiConsole.MarkupLine("[blue]Directory filter configuration[/]");
            AnsiConsole.MarkupLine($"[grey]Current filter:[/] {FormatDirectoryFilter(currentPatterns)}");
            AnsiConsole.MarkupLine("[grey]Examples:[/] .c*, .json, .bak.txt");
            AnsiConsole.MarkupLine("[grey]Notes:[/] applies only to selected directories; individually selected files always bypass the filter.");
            AnsiConsole.MarkupLine("[grey]Leave empty, or type 'clear' / 'none' to remove the filter.[/]\n");

            string input = AnsiConsole.Prompt(new TextPrompt<string>("Extensions / wildcard list:").AllowEmpty());
            var patterns = ParseDirectoryFilterInput(input);

            if (patterns.Count == 0)
                AnsiConsole.MarkupLine("\n[yellow]Directory filter cleared.[/]");
            else
                AnsiConsole.MarkupLine($"\n[green]Directory filter set to:[/] {string.Join(", ", patterns)}");

            AnsiConsole.MarkupLine("[grey]Press any key to return to idle...[/]");
            Console.ReadKey(intercept: true);
            return patterns;
        }

        private static List<string> ParseDirectoryFilterInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new List<string>();

            string normalized = input.Trim();
            if (normalized.Equals("clear", StringComparison.OrdinalIgnoreCase) || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
                return new List<string>();

            return normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatDirectoryFilter(List<string> patterns)
        {
            return patterns == null || patterns.Count == 0 ? "<none>" : string.Join(", ", patterns);
        }

        private static bool MatchesDirectoryFilter(string filePath, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0)
                return true;

            string fileName = Path.GetFileName(filePath);
            foreach (var pattern in patterns)
                if (MatchesSinglePattern(fileName, pattern))
                    return true;

            return false;
        }

        private static bool MatchesSinglePattern(string fileName, string pattern)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(pattern))
                return false;

            string normalizedPattern = NormalizeWildcardPattern(pattern);
            string regexPattern = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
        }

        private static string NormalizeWildcardPattern(string pattern)
        {
            string p = pattern.Trim();
            if (string.IsNullOrWhiteSpace(p))
                return p;

            if (p.StartsWith('.'))
                return "*" + p;

            return p;
        }

        private static void RegisterGlobalHotKey()
        {
            using var window = new Form();
            var handle = window.Handle;

            if (!RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F))
                AnsiConsole.MarkupLine("[yellow]⚠ Failed to register global hotkey (Ctrl+Alt+F). It may be in use by another application.[/]");

            System.Windows.Forms.Application.AddMessageFilter(new HotKeyMessageFilter());
            System.Windows.Forms.Application.Run();
            UnregisterHotKey(handle, HOTKEY_ID);
        }

        private class HotKeyMessageFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                const int WM_HOTKEY = 0x0312;
                if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
                {
                    _hotkeyTriggered = true;
                    return true;
                }
                return false;
            }
        }

        private static (List<string> Files, string LastDirectory, bool Cancelled) InteractiveFileBrowser(string startingPath, List<string> directoryFilterPatterns)
        {
            string currentPath = startingPath;
            var cart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                Console.Clear();
                AnsiConsole.MarkupLine($"[blue]Directory:[/]\n{currentPath}");
                AnsiConsole.MarkupLine($"[grey]Directory filter:[/] {FormatDirectoryFilter(directoryFilterPatterns)}");
                AnsiConsole.MarkupLine("[grey]Note:[/] filter applies only to selected directories; individually selected files are always sent.");

                if (cart.Count > 0)
                {
                    AnsiConsole.MarkupLine("\n[green]Currently Selected for Injection:[/]");
                    foreach (var item in cart)
                        AnsiConsole.MarkupLine($" - {Path.GetFileName(item)}");
                }
                else
                {
                    AnsiConsole.MarkupLine("\n[grey]Nothing selected yet.[/]");
                }

                List<string> dirs, files;
                try
                {
                    dirs = Directory.GetDirectories(currentPath).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).OrderBy(x => x).ToList();
                    files = Directory.GetFiles(currentPath).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).OrderBy(x => x).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine($"[red]Cannot read directory: {Markup.Escape(ex.Message)}[/]");
                    AnsiConsole.MarkupLine("[grey]Press any key to go back...[/]");
                    Console.ReadKey(intercept: true);

                    var parent = Directory.GetParent(currentPath);
                    if (parent != null)
                        currentPath = parent.FullName;

                    continue;
                }

                var displayToFile = new Dictionary<string, string>();
                var choices = new List<string>();

                if (cart.Count > 0)
                    choices.Add("[green]✓ CONFIRM AND INJECT[/]");

                var parentDir = Directory.GetParent(currentPath);
                if (parentDir != null)
                    choices.Add("[blue].. (Go up one folder)[/]");

                foreach (var dir in dirs)
                {
                    string fullDir = Path.Combine(currentPath, dir);
                    string displayed = cart.Contains(fullDir) ? $"[green]✓ 📁 {dir}[/]" : $"[blue]📁 {dir}[/]";
                    choices.Add(displayed);
                    displayToFile[displayed] = dir;
                }

                foreach (var file in files)
                {
                    string fullFile = Path.Combine(currentPath, file);
                    string displayed = cart.Contains(fullFile) ? $"[green]✓ 📄 {file}[/]" : $"📄 {file}";
                    choices.Add(displayed);
                    displayToFile[displayed] = file;
                }

                choices.Add("[red]← CANCEL AND RETURN[/]");

                var prompt = new SelectionPrompt<string>()
                    .Title("\n[grey]Navigate with Arrows. Press [blue]Enter[/] to enter folders, select files, or toggle selection.[/]")
                    .PageSize(20)
                    .AddChoices(choices);

                var selected = AnsiConsole.Prompt(prompt);

                if (selected == "[red]← CANCEL AND RETURN[/]")
                    return (new List<string>(), currentPath, true);

                if (selected == "[green]✓ CONFIRM AND INJECT[/]")
                {
                    var finalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var path in cart)
                    {
                        if (Directory.Exists(path))
                        {
                            IEnumerable<string> enumerated;
                            try
                            {
                                enumerated = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                AnsiConsole.MarkupLine($"[yellow]⚠ Partial read of '[white]{Markup.Escape(Path.GetFileName(path))}[/]': {Markup.Escape(ex.Message)}[/]");
                                enumerated = SafeEnumerateFiles(path, directoryFilterPatterns);
                            }

                            foreach (var f in enumerated)
                            {
                                if (!IsTextFile(f))
                                    continue;
                                if (!MatchesDirectoryFilter(f, directoryFilterPatterns))
                                    continue;
                                if (!File.Exists(f))
                                    continue;

                                finalFiles.Add(f);
                            }
                        }
                        else if (File.Exists(path))
                        {
                            finalFiles.Add(path);
                        }
                    }

                    return (finalFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), currentPath, false);
                }

                if (selected == "[blue].. (Go up one folder)[/]" && parentDir != null)
                {
                    currentPath = parentDir.FullName;
                    continue;
                }

                if (displayToFile.TryGetValue(selected, out string? selectedName))
                {
                    string fullPath = Path.Combine(currentPath, selectedName);

                    if (Directory.Exists(fullPath))
                    {
                        var dirAction = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title($"What do you want to do with [blue]📁 {selectedName}[/]?")
                                .AddChoices(new[]
                                {
                                    "Open Folder",
                                    "Toggle Selection (Select/Deselect for Injection)",
                                    "Cancel"
                                })) ?? string.Empty;

                        if (dirAction == "Open Folder")
                            currentPath = fullPath;
                        else if (dirAction == "Toggle Selection (Select/Deselect for Injection)")
                        {
                            if (!cart.Add(fullPath))
                                cart.Remove(fullPath);
                        }
                    }
                    else
                    {
                        if (!cart.Add(fullPath))
                            cart.Remove(fullPath);
                    }
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string rootPath, List<string> directoryFilterPatterns)
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                IEnumerable<string> subFiles = Enumerable.Empty<string>();
                IEnumerable<string> subDirs = Enumerable.Empty<string>();

                try { subFiles = Directory.GetFiles(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine($"[grey]  Skipped (no access): {Markup.Escape(current)}[/]");
                }

                foreach (var f in subFiles)
                    yield return f;

                try { subDirs = Directory.GetDirectories(current); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine($"[grey]  Skipped subdirs (no access): {Markup.Escape(current)}[/]");
                }

                foreach (var d in subDirs)
                    pending.Push(d);
            }
        }

        private static bool IsTextFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (TextExtensions.Contains(ext)) return true;
            if (BinaryExtensions.Contains(ext)) return false;

            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] buffer = new byte[512];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < bytesRead; i++)
                    if (buffer[i] == 0) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

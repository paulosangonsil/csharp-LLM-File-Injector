using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using Spectre.Console;

namespace CliFileInjector
{
    public class LlmConfig
    {
        public string Name { get; set; } = string.Empty;
        public string UrlSubstring { get; set; } = string.Empty;
        public string TextAreaSelector { get; set; } = string.Empty;
        public bool UsesContentEditable { get; set; }
    }

    class Program
    {
        // ------------------------------------------------------------------
        // Feature Flag: enable or disable global hotkey (Ctrl + Alt + F)
        // ------------------------------------------------------------------
        private static readonly bool ENABLE_GLOBAL_HOTKEY = false;

        // ------------------------------------------------------------------
        // Chunk size limit for injection (in bytes, UTF-8)
        // ------------------------------------------------------------------
        private const int MAX_CHUNK_BYTES = 37 * 1024; // 37 KB

        // ------------------------------------------------------------------
        // P/INVOKE AND WIN32 API (For Global Hotkey and Foreground Window)
        // ------------------------------------------------------------------
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
        private const uint VK_F = 0x46; // F key
        private const int HOTKEY_ID = 9000;

        // ------------------------------------------------------------------
        // FILE RULES
        // ------------------------------------------------------------------
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

        private static bool _hotkeyTriggered = false;

        // ------------------------------------------------------------------
        // CHUNK BUILDER
        // ------------------------------------------------------------------
        private record FileEntry(string FileName, string Content, bool IsText);

        private static int Utf8Bytes(string s) => Encoding.UTF8.GetByteCount(s);

        private static List<string> BuildChunks(List<FileEntry> entries)
        {
            var allSegments = new List<string>();

            foreach (var entry in entries)
            {
                if (!entry.IsText)
                {
                    allSegments.Add(
                        $"[Context Note: The binary/non-text file '{entry.FileName}' exists in the directory, but its content was omitted.]\n\n");
                    continue;
                }

                string fileContent = entry.Content.Replace("\r\n", "\n");

                string worstHeader = $"--- Start of '{entry.FileName}' (part 999 of 999) ---\n";
                string worstFooter = $"\n--- End of '{entry.FileName}' (part 999 of 999) ---\n\n";
                int markerBytes = Utf8Bytes(worstHeader) + Utf8Bytes(worstFooter);
                int budget = MAX_CHUNK_BYTES - markerBytes;
                if (budget <= 0) budget = MAX_CHUNK_BYTES / 2;

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

                if (segBytes > MAX_CHUNK_BYTES)
                {
                    Flush();
                    chunks.Add(seg);
                    continue;
                }

                if (chunkBytes + segBytes > MAX_CHUNK_BYTES)
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

            string workingDirectory =
                args.Length > 0 && Directory.Exists(args[0])
                    ? args[0]
                    : Directory.GetCurrentDirectory();

            string lastBrowserDirectory = workingDirectory;
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            List<string> directoryFilterPatterns = new();

            Thread? hookThread = null;
            if (ENABLE_GLOBAL_HOTKEY)
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

            AnsiConsole.MarkupLine("[blue]Connecting to Edge via CDP (port 9222)...[/]");

            using var playwright = await Playwright.CreateAsync();
            IBrowser browser;
            try
            {
                browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:9222");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("[red]Failed to connect to Edge via CDP.[/]");
                AnsiConsole.MarkupLine("[grey]Make sure Edge is running with --remote-debugging-port=9222.[/]");
                AnsiConsole.MarkupLine($"[red]Details: {Markup.Escape(ex.Message)}[/]");
                return;
            }

            if (browser.Contexts.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No browser contexts found. Open at least one tab in Edge and try again.[/]");
                return;
            }

            var context = browser.Contexts[0];

            AnsiConsole.MarkupLine("[green]Service ready.[/]");
            if (ENABLE_GLOBAL_HOTKEY)
                AnsiConsole.MarkupLine("Press [yellow]Ctrl + Alt + F[/] from anywhere to inject files.");

            while (true)
            {
                if (ENABLE_GLOBAL_HOTKEY)
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
                    AnsiConsole.MarkupLine("[red]No supported LLM (ChatGPT, Claude, etc.) open in Edge.[/]");
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

                lastBrowserDirectory = ResolveNextBrowserDirectory(
                    selectedFilePaths,
                    lastVisitedDirectory,
                    lastBrowserDirectory);

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
                            AnsiConsole.MarkupLine(
                                $"[yellow]⚠ Skipped '[white]{Markup.Escape(fileName)}[/]': {Markup.Escape(ex.Message)}[/]");
                            isText = false;
                        }
                    }

                    fileEntries.Add(new FileEntry(fileName, content, isText));
                }

                List<string> chunks = BuildChunks(fileEntries);
                int totalChunks = chunks.Count;

                if (totalChunks > 1)
                    AnsiConsole.MarkupLine($"[yellow]Payload exceeds {MAX_CHUNK_BYTES / 1024} KB — will be sent in {totalChunks} chunk(s).[/]");

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
                            $"\n[yellow]Chunk {chunkIndex + 1}/{totalChunks} injected.[/] " +
                            "Press [green]Enter[/] to send the next chunk, or [red]Esc[/] to abort.");

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

        private static string ResolveNextBrowserDirectory(
            List<string> selectedFilePaths,
            string lastVisitedDirectory,
            string fallbackDirectory)
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

            string input = AnsiConsole.Prompt(
                new TextPrompt<string>("Extensions / wildcard list:")
                    .AllowEmpty());

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
            if (normalized.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            return normalized
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatDirectoryFilter(List<string> patterns)
        {
            return patterns == null || patterns.Count == 0
                ? "<none>"
                : string.Join(", ", patterns);
        }

        private static bool MatchesDirectoryFilter(string filePath, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0)
                return true;

            string fileName = Path.GetFileName(filePath);

            foreach (var pattern in patterns)
            {
                if (MatchesSinglePattern(fileName, pattern))
                    return true;
            }

            return false;
        }

        private static bool MatchesSinglePattern(string fileName, string pattern)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(pattern))
                return false;

            string normalizedPattern = NormalizeWildcardPattern(pattern);
            string regexPattern =
                "^" +
                Regex.Escape(normalizedPattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") +
                "$";

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

        // ------------------------------------------------------------------
        // WINDOWS MESSAGE PUMP (for global hotkey)
        // ------------------------------------------------------------------
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

        // ------------------------------------------------------------------
        // FILE BROWSER AND UTILITIES
        // ------------------------------------------------------------------
        private static (List<string> Files, string LastDirectory, bool Cancelled) InteractiveFileBrowser(
            string startingPath,
            List<string> directoryFilterPatterns)
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
                    dirs = Directory.GetDirectories(currentPath)
                        .Select(Path.GetFileName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .OrderBy(x => x)
                        .ToList();

                    files = Directory.GetFiles(currentPath)
                        .Select(Path.GetFileName)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .OrderBy(x => x)
                        .ToList();
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
                    string displayed = cart.Contains(fullDir)
                        ? $"[green]✓ 📁 {dir}[/]"
                        : $"[blue]📁 {dir}[/]";

                    choices.Add(displayed);
                    displayToFile[displayed] = dir;
                }

                foreach (var file in files)
                {
                    string fullFile = Path.Combine(currentPath, file);
                    string displayed = cart.Contains(fullFile)
                        ? $"[green]✓ 📄 {file}[/]"
                        : $"📄 {file}";

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

        /// <summary>
        /// Recursively enumerates files, skipping subdirectories that deny access.
        /// Used as a fallback when Directory.GetFiles(AllDirectories) throws.
        /// </summary>
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

using System.Runtime.InteropServices;
using System.Text;
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
        private const int MAX_CHUNK_BYTES = 37 * 1024; // 40 KB

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
        //
        // Strategy:
        //   1. Convert every file into one or more "segments" (strings that
        //      individually fit within MAX_CHUNK_BYTES, including their
        //      Start/End markers).
        //   2. Greedily pack ALL segments (from any file) into chunks.
        //      This means part-2 of FileA and all of FileB can share a chunk
        //      if the combined size fits.
        // ------------------------------------------------------------------
        private record FileEntry(string FileName, string Content, bool IsText);

        private static int Utf8Bytes(string s) => Encoding.UTF8.GetByteCount(s);

        private static List<string> BuildChunks(List<FileEntry> entries)
        {
            // Step 1 — produce a flat list of segments from all files
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

                // Use the worst-case marker size (part 999 of 999) so the
                // content budget is always safe regardless of the final label.
                string worstHeader = $"--- Start of '{entry.FileName}' (part 999 of 999) ---\n";
                string worstFooter = $"\n--- End of '{entry.FileName}' (part 999 of 999) ---\n\n";
                int markerBytes = Utf8Bytes(worstHeader) + Utf8Bytes(worstFooter);
                int budget = MAX_CHUNK_BYTES - markerBytes;
                if (budget <= 0) budget = MAX_CHUNK_BYTES / 2;

                // Split file content into groups that each fit within budget
                var groups = new List<string>();
                var cur = new StringBuilder();
                int curBytes = 0;

                foreach (string line in fileContent.Split('\n'))
                {
                    string lineNl = line + "\n";
                    int lineBytes = Utf8Bytes(lineNl);

                    if (lineBytes > budget)
                    {
                        // Single line exceeds budget — flush and hard-cut
                        if (cur.Length > 0) { groups.Add(cur.ToString()); cur.Clear(); curBytes = 0; }
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
                        groups.Add(cur.ToString()); cur.Clear(); curBytes = 0;
                    }

                    cur.Append(lineNl);
                    curBytes += lineBytes;
                }

                if (cur.Length > 0) groups.Add(cur.ToString());

                // Wrap each group with the correct (part N of M) label
                int totalParts = groups.Count;
                for (int i = 0; i < totalParts; i++)
                {
                    string partLabel = totalParts > 1 ? $" (part {i + 1} of {totalParts})" : string.Empty;
                    string header = $"--- Start of '{entry.FileName}'{partLabel} ---\n";
                    string footer = $"\n--- End of '{entry.FileName}'{partLabel} ---\n\n";
                    allSegments.Add(header + groups[i] + footer);
                }
            }

            // Step 2 — greedily pack all segments into chunks
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
                    // Segment is intrinsically oversized — send alone
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

        // Returns how many leading characters of s fit within maxBytes in UTF-8.
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
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            string workingDirectory =
                args.Length > 0 && Directory.Exists(args[0])
                    ? args[0]
                    : Directory.GetCurrentDirectory();

            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;

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
            var browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:9222");
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
                    AnsiConsole.MarkupLine("\nPress [yellow]Enter[/] to open the file browser, or [grey]Esc[/] to exit.");
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                        break;
                    if (key.Key != ConsoleKey.Enter)
                        continue;
                }

                // 3.1 Detect active LLM sessions
                var activeSessions = new List<(IPage Page, LlmConfig Config)>();
                foreach (var page in context.Pages)
                {
                    var match = supportedModels.FirstOrDefault(m => page.Url.Contains(m.UrlSubstring));
                    if (match != null && !activeSessions.Any(s => s.Config.Name == match.Name))
                        activeSessions.Add((Page: page, Config: match));
                }

                if (activeSessions.Count == 0)
                {
                    AnsiConsole.MarkupLine("[red]No supported LLM (ChatGPT, Claude, etc.) open in Edge.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine($"[green]{activeSessions.Count} LLM session(s) detected.[/]");

                // 3.2 File browser
                var selectedFilePaths = InteractiveFileBrowser(workingDirectory);
                if (selectedFilePaths.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No files selected. Back to standby.[/]");
                    continue;
                }

                // 3.3 Build file entries
                var fileEntries = new List<FileEntry>();
                foreach (var filePath in selectedFilePaths)
                {
                    string fileName = Path.GetFileName(filePath);
                    bool isText = IsTextFile(filePath);
                    string content = isText ? File.ReadAllText(filePath) : string.Empty;
                    fileEntries.Add(new FileEntry(fileName, content, isText));
                }

                // 3.4 Build chunks
                List<string> chunks = BuildChunks(fileEntries);
                int totalChunks = chunks.Count;

                if (totalChunks > 1)
                    AnsiConsole.MarkupLine($"[yellow]Payload exceeds {MAX_CHUNK_BYTES / 1024} KB — will be sent in {totalChunks} chunk(s).[/]");

                // 3.5 Choose target LLM(s)
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

                // 3.6 Inject chunks
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
                            AnsiConsole.MarkupLine($"[red]Error injecting into {target.Config.Name}: {ex.Message}[/]");
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
                            if (k.Key == ConsoleKey.Escape) { aborted = true; break; }
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

        // ------------------------------------------------------------------
        // WINDOWS MESSAGE PUMP (for global hotkey)
        // ------------------------------------------------------------------
        private static void RegisterGlobalHotKey()
        {
            using var window = new Form();
            var handle = window.Handle;
            RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F);
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
        private static List<string> InteractiveFileBrowser(string startingPath)
        {
            string currentPath = startingPath;
            var cart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                Console.Clear();
                AnsiConsole.MarkupLine($"[blue]Directory:[/]\n{currentPath}");

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

                var dirs = Directory.GetDirectories(currentPath).Select(Path.GetFileName).ToList();
                var files = Directory.GetFiles(currentPath).Select(Path.GetFileName).ToList();

                var displayToFile = new Dictionary<string, string>();
                var choices = new List<string>();

                if (cart.Count > 0)
                    choices.Add("[green]✓ CONFIRM AND INJECT[/]");

                var parentDir = Directory.GetParent(currentPath);
                if (parentDir != null)
                    choices.Add("[blue].. (Go up one folder)[/]");

                foreach (var dir in dirs)
                {
                    string fullDir = Path.Combine(currentPath, dir!);
                    string displayed = cart.Contains(fullDir) ? $"[green]✓ 📁 {dir}[/]" : $"[blue]📁 {dir}[/]";
                    choices.Add(displayed);
                    displayToFile[displayed] = dir!;
                }

                foreach (var file in files)
                {
                    string fullFile = Path.Combine(currentPath, file!);
                    string displayed = cart.Contains(fullFile) ? $"[green]✓ 📄 {file}[/]" : $"📄 {file}";
                    choices.Add(displayed);
                    displayToFile[displayed] = file!;
                }

                var prompt = new SelectionPrompt<string>()
                    .Title("\n[grey]Navigate with Arrows. Press [blue]Enter[/] to enter folders, select files, or toggle selection.[/]")
                    .PageSize(15)
                    .AddChoices(choices);

                var selected = AnsiConsole.Prompt(prompt);

                if (selected == "[green]✓ CONFIRM AND INJECT[/]")
                {
                    var finalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in cart)
                    {
                        if (Directory.Exists(path))
                        {
                            foreach (var f in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
                                if (IsTextFile(f)) finalFiles.Add(f);
                        }
                        else if (File.Exists(path))
                        {
                            finalFiles.Add(path);
                        }
                    }
                    return finalFiles.ToList();
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
                                .AddChoices(new[] { "Open Folder", "Toggle Selection (Select/Deselect for Injection)", "Cancel" })
                        ) ?? string.Empty;

                        if (dirAction == "Open Folder")
                            currentPath = fullPath;
                        else if (dirAction == "Toggle Selection (Select/Deselect for Injection)")
                            if (!cart.Add(fullPath)) cart.Remove(fullPath);
                    }
                    else
                    {
                        if (!cart.Add(fullPath)) cart.Remove(fullPath);
                    }
                }
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

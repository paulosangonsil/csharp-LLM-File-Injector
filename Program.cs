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
        private static readonly bool ENABLE_GLOBAL_HOTKEY = true;

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

        // Flow control variable for the hotkey
        private static bool _hotkeyTriggered = false;

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            // Working directory: where you want to browse files (project/source folder)
            string workingDirectory =
                args.Length > 0 && Directory.Exists(args[0])
                    ? args[0]
                    : Directory.GetCurrentDirectory();

            // EXE directory: where BrowserFileInjector.exe and appsettings.json live
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;

            // Start global hotkey listener only if enabled
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

            // 1. Load configuration from appsettings.json (from EXE folder)
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

            // 2. Initialize Playwright and connect to Edge via CDP
            AnsiConsole.MarkupLine("[blue]Connecting to Edge via CDP (port 9222)...[/]");
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectOverCDPAsync("http://localhost:9222");
            var context = browser.Contexts[0];

            AnsiConsole.MarkupLine("[green]Service ready.[/]");
            if (ENABLE_GLOBAL_HOTKEY)
            {
                AnsiConsole.MarkupLine("Press [yellow]Ctrl + Alt + F[/] from anywhere to inject files.");
            }

            // 3. Main loop
            while (true)
            {
                if (ENABLE_GLOBAL_HOTKEY)
                {
                    // Wait for global hotkey
                    if (!_hotkeyTriggered)
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    // Hotkey pressed
                    _hotkeyTriggered = false;
                    SetForegroundWindow(GetConsoleWindow());
                    Console.Clear();
                }
                else
                {
                    // Manual trigger from console
                    AnsiConsole.MarkupLine("\nPress [yellow]Enter[/] to open the file browser, or [grey]Esc[/] to exit.");
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                        break;
                    if (key.Key != ConsoleKey.Enter)
                        continue;
                }

                // 3.1 Detect active LLM sessions in current Edge context
                var activeSessions = new List<(IPage Page, LlmConfig Config)>();
                foreach (var page in context.Pages)
                {
                    var match = supportedModels.FirstOrDefault(m => page.Url.Contains(m.UrlSubstring));
                    if (match != null && !activeSessions.Any(s => s.Config.Name == match.Name))
                    {
                        activeSessions.Add((Page: page, Config: match));
                    }
                }

                if (activeSessions.Count == 0)
                {
                    AnsiConsole.MarkupLine("[red]No supported LLM (ChatGPT, Claude, etc.) open in Edge.[/]");
                    continue;
                }

                AnsiConsole.MarkupLine($"[green]{activeSessions.Count} LLM session(s) detected.[/]");

                // 3.2 Open interactive file browser starting at workingDirectory
                var selectedFilePaths = InteractiveFileBrowser(workingDirectory);
                if (selectedFilePaths.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No files selected. Back to standby.[/]");
                    continue;
                }

                // 3.3 Build the payload from selected files
                var payloadBuilder = new StringBuilder();
                payloadBuilder.AppendLine();

                foreach (var filePath in selectedFilePaths)
                {
                    string fileName = Path.GetFileName(filePath);

                    if (IsTextFile(filePath))
                    {
                        payloadBuilder.AppendLine($"--- Start of {fileName} ---");
                        payloadBuilder.AppendLine(File.ReadAllText(filePath).Replace("\r\n", "\n"));
                        payloadBuilder.AppendLine($"--- End of {fileName} ---");
                        payloadBuilder.AppendLine();
                    }
                    else
                    {
                        payloadBuilder.AppendLine(
                            $"[Context Note: The binary/non-text file '{fileName}' exists in the directory, but its content was omitted.]");
                        payloadBuilder.AppendLine();
                    }
                }

                string payload = payloadBuilder.ToString();

                // 3.4 Choose target LLM(s)
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
                    {
                        targets.AddRange(activeSessions);
                    }
                    else
                    {
                        targets.Add(activeSessions.First(s => s.Config.Name == selectedTarget));
                    }
                }

                // 3.5 Inject into selected targets
                foreach (var target in targets)
                {
                    try
                    {
                        await target.Page.BringToFrontAsync();
                        var locator = target.Page.Locator(target.Config.TextAreaSelector).First;
                        await locator.FocusAsync();
                        await target.Page.Keyboard.InsertTextAsync(payload);

                        AnsiConsole.MarkupLine($"[green]✔ Injected into {target.Config.Name}![/]");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error injecting into {target.Config.Name}: {ex.Message}[/]");
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

            // Register Ctrl + Alt + F
            RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F);

            // Use fully qualified name to avoid ambiguity
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
                    {
                        AnsiConsole.MarkupLine($" - {Path.GetFileName(item)}");
                    }
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
                {
                    choices.Add("[green]✓ CONFIRM AND INJECT[/]");
                }

                var parentDir = Directory.GetParent(currentPath);
                if (parentDir != null)
                {
                    choices.Add("[blue].. (Go up one folder)[/]");
                }

                foreach (var dir in dirs)
                {
                    // If the directory is already in the cart, display it differently
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

                // Action 1: Inject everything
                if (selected == "[green]✓ CONFIRM AND INJECT[/]")
                {
                    var finalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var path in cart)
                    {
                        if (Directory.Exists(path))
                        {
                            var allFilesInDir = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                            foreach (var f in allFilesInDir)
                            {
                                if (IsTextFile(f)) finalFiles.Add(f);
                            }
                        }
                        else if (File.Exists(path))
                        {
                            finalFiles.Add(path);
                        }
                    }
                    return finalFiles.ToList();
                }

                // Action 2: Go back a directory
                if (selected == "[blue].. (Go up one folder)[/]" && parentDir != null)
                {
                    currentPath = parentDir.FullName;
                    continue;
                }

                // Action 3: Is this a directory (Enter or Check/Uncheck?)
                if (displayToFile.TryGetValue(selected, out string? selectedName))
                {
                    string fullPath = Path.Combine(currentPath, selectedName);

                    if (Directory.Exists(fullPath))
                    {
                        // If the user has selected a directory, we ask if they want to enter or select the directory
                        var dirAction = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title($"What do you want to do with [blue]📁 {selectedName}[/]?")
                                .AddChoices(new[] { "Open Folder", "Toggle Selection (Select/Deselect for Injection)", "Cancel" })
                        ) ?? string.Empty;

                        if (dirAction == "Open Folder")
                        {
                            currentPath = fullPath;
                        }
                        else if (dirAction == "Toggle Selection (Select/Deselect for Injection)")
                        {
                            if (!cart.Add(fullPath)) cart.Remove(fullPath);
                        }
                    }
                    else
                    {
                        // It's a file. Just toggle it on or off in the cart
                        if (!cart.Add(fullPath)) cart.Remove(fullPath);
                    }
                }
            }
        }

        private static bool IsTextFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (TextExtensions.Contains(ext))
                return true;

            if (BinaryExtensions.Contains(ext))
                return false;

            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] buffer = new byte[512];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

# LLM File Injector (BrowserFileInjector)

A powerful, background-running C\# CLI utility for developers that seamlessly injects local source code and text files directly into web-based LLMs (ChatGPT, Claude, Perplexity, DeepSeek, Grok, etc.) using a global Windows hotkey.

Instead of paying for API tokens or manually copy-pasting dozens of classes and headers, this tool connects to your existing browser session via CDP (Chrome DevTools Protocol). You just press `Ctrl + Alt + F`, select your files using an interactive console UI, and the payload is instantly typed into your active AI chat.

## ✨ Features

- **Global Hotkey (Ctrl + Alt + F):** Trigger the file browser from anywhere in Windows.
- **Zero API Costs:** Uses your active browser session (Playwright over CDP), bypassing Cloudflare blocks and utilizing your free/plus web accounts.
- **Smart File Browser:** Recursively inject entire directories or select multiple individual files.
- **Binary Detection:** Automatically detects and ignores binary files (like `.dll`, `.exe`, `.pdf`), injecting only a context note so you don't pollute the AI prompt.
- **Multi-LLM Support:** If you have multiple AIs open in different tabs, you can broadcast your code to all of them simultaneously.
- **Configurable:** Driven by an `appsettings.json` file, so you can update CSS selectors when AI websites update their UI without recompiling.

***

## ⚙️ Prerequisites

- **OS:** Windows 10 or 11 (uses `user32.dll` for global hotkey and window management).
- **Runtime:** [.NET 10.0 SDK](https://dotnet.microsoft.com/) (or adjust the `.csproj` to .NET 8/9).
- **Browser:** Microsoft Edge or Google Chrome.

***

## 🛠️ Build and Installation

### 1. Clone the repository

```bash
git clone https://github.com/yourusername/LLM-File-Injector.git
cd LLM-File-Injector
```


### 2. Build the project

This project requires several NuGet packages (Spectre.Console, Playwright, Microsoft.Extensions.Configuration). They will be restored automatically.

```bash
dotnet build -c Debug
```


### 3. Install Playwright Browsers (Required once)

Playwright needs to download its browser binaries to function properly. Navigate to your build output folder and run the installation script:

```bash
cd bin\Debug\net10.0-windows
powershell -ExecutionPolicy Bypass -File playwright.ps1 install
```


***

## 🚀 How to Run

To automate the startup process, the project includes a batch script: `Start-LlmInjector.bat`. This script ensures Edge is started with the necessary remote debugging port (`9222`) and launches the C\# background service.

### Using the Batch Script

Simply double-click `Start-LlmInjector.bat` or run it from the command line passing your current project directory as an argument:

```cmd
Start-LlmInjector.bat "C:\Path\To\Your\Code\Project"
```

**What the script does:**

1. Closes any background instances of Edge.
2. Starts Edge in debugging mode (`--remote-debugging-port=9222`) and opens default LLM tabs.
3. Starts `BrowserFileInjector.exe` passing your project directory as the starting point for the file browser.

### Using the Tool

1. Once the service is running in the background, open your Edge browser and make sure you are logged into your favorite LLM.
2. Press **`Ctrl + Alt + F`** anywhere on your computer.
3. The interactive console window will pop to the front.
4. Use **Arrow Keys** to navigate and **Enter** to open folders or toggle files for injection.
5. Select **`[green]✓ CONFIRM AND INJECT[/]`** when your "cart" is ready.
6. The files will be formatted with clear boundaries (`--- Start of File.cpp ---`) and injected instantly into the AI's text box.

***

## ⚙️ Configuration (`appsettings.json`)

The tool relies on CSS selectors to find the text box of each LLM. Since websites change their front-end, you can easily maintain these selectors without recompiling the C\# code.

The `appsettings.json` file is located in the executable folder (`bin\Debug\net10.0-windows`). Here is the default setup:

```json
{
  "LlmProviders": [
    {
      "Name": "ChatGPT",
      "UrlSubstring": "chatgpt.com",
      "TextAreaSelector": "div.ProseMirror[contenteditable='true']",
      "UsesContentEditable": true
    },
    {
      "Name": "Claude",
      "UrlSubstring": "claude.ai",
      "TextAreaSelector": "div[contenteditable='true']",
      "UsesContentEditable": true
    },
    {
      "Name": "Perplexity",
      "UrlSubstring": "perplexity.ai",
      "TextAreaSelector": "#ask-input",
      "UsesContentEditable": true
    },
    {
      "Name": "DeepSeek",
      "UrlSubstring": "deepseek.com",
      "TextAreaSelector": "textarea[placeholder*='Message DeepSeek']",
      "UsesContentEditable": false
    },
    {
      "Name": "Grok (X)",
      "UrlSubstring": "x.com",
      "TextAreaSelector": "div.ProseMirror[contenteditable='true']",
      "UsesContentEditable": true
    }
  ]
}
```


***

## 📝 License

This project is licensed under the [GNU General Public License v3.0 (GPLv3)](LICENSE).
You are free to use, modify, and distribute this software. However, any commercial product or proprietary software that incorporates this code must also be made open-source under the same terms.


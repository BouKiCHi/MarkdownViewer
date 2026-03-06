using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MarkdownViewer;

public partial class MainWindow : Window {
  private const string VirtualHostName = "markdown.local";
  private const string VirtualHostBaseUri = "https://markdown.local/";

  private readonly SettingRepository settingRepository = new();
  private readonly ObservableCollection<HistoryItem> historyItems = [];
  private string? currentMarkdownPath;
  private bool suppressHistorySelectionChanged;
  private bool webViewEventsRegistered;

  public MainWindow(string? initialFilePath) {
    InitializeComponent();

    HistoryListBox.ItemsSource = historyItems;
    LoadHistory();

    if (!string.IsNullOrWhiteSpace(initialFilePath)) {
      OpenMarkdownFile(initialFilePath);
    }
  }

  private async void Window_Loaded(object sender, RoutedEventArgs e) {
    try {
      await MarkdownWebView.EnsureCoreWebView2Async();
    } catch (Exception ex) {
      MessageBox.Show($"WebView2 の初期化に失敗しました。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
      return;
    }

    RegisterWebViewEvents();

    if (currentMarkdownPath is null) {
      if (TryOpenLatestHistory()) {
        return;
      }

      if (TryOpenMarkdownByDialog()) {
        return;
      }

      await RenderMessageAsync("表示する Markdown ファイルが指定されていません。", "コマンドライン引数にファイルパスを指定してください。");
      return;
    }

    await RenderMarkdownFileAsync(currentMarkdownPath);
  }

  private void RegisterWebViewEvents() {
    if (webViewEventsRegistered || MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    MarkdownWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
    MarkdownWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
    webViewEventsRegistered = true;
  }

  private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
    if (TryOpenExternalUrl(e.Uri)) {
      e.Cancel = true;
    }
  }

  private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) {
    if (TryOpenExternalUrl(e.Uri)) {
      e.Handled = true;
    }
  }

  private static bool TryOpenExternalUrl(string? url) {
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
      return false;
    }

    if (uri.Scheme is not ("http" or "https")) {
      return false;
    }

    if (string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    try {
      Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
      return true;
    } catch {
      return false;
    }
  }

  private bool TryOpenLatestHistory() {
    var latest = historyItems.FirstOrDefault();
    if (latest is null) {
      return false;
    }

    if (!File.Exists(latest.FullPath)) {
      historyItems.Remove(latest);
      SaveHistory();
      return false;
    }

    OpenMarkdownFile(latest.FullPath);
    return true;
  }

  private bool TryOpenMarkdownByDialog() {
    var dialog = new OpenFileDialog {
      Title = "Markdown ファイルを選択",
      Filter = "Markdown Files (*.md;*.markdown)|*.md;*.markdown|All Files (*.*)|*.*",
      CheckFileExists = true,
      Multiselect = false
    };

    if (dialog.ShowDialog(this) != true) {
      return false;
    }

    OpenMarkdownFile(dialog.FileName);
    return true;
  }

  private async void ReloadButton_Click(object sender, RoutedEventArgs e) {
    if (currentMarkdownPath is null) {
      return;
    }

    await RenderMarkdownFileAsync(currentMarkdownPath);
  }

  private void OpenFileButton_Click(object sender, RoutedEventArgs e) {
    TryOpenMarkdownByDialog();
  }

  private void HistoryToggleButton_Click(object sender, RoutedEventArgs e) {
    ApplyHistoryPaneVisibility(HistoryToggleButton.IsChecked == true);
    SaveHistory();
  }

  private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if (suppressHistorySelectionChanged || HistoryListBox.SelectedItem is not HistoryItem selected) {
      return;
    }

    OpenMarkdownFile(selected.FullPath, updateHistory: false);
  }

  private async void OpenMarkdownFile(string filePath, bool updateHistory = true) {
    var fullPath = Path.GetFullPath(filePath);

    if (!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if (staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    if (updateHistory) {
      AddOrMoveHistory(fullPath);
    }
    currentMarkdownPath = fullPath;

    if (MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    await RenderMarkdownFileAsync(fullPath);
  }

  private async Task RenderMarkdownFileAsync(string filePath) {
    if (MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    string markdownText;
    try {
      markdownText = await File.ReadAllTextAsync(filePath);
    } catch (Exception ex) {
      await RenderMessageAsync("Markdown の読み込みに失敗しました。", ex.Message);
      return;
    }

    currentMarkdownPath = filePath;
    PathTextBlock.Text = Path.GetFileName(filePath);
    PathTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68));

    var baseDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
    ConfigureVirtualHostMapping(baseDirectory);

    var html = BuildHtml(markdownText, VirtualHostBaseUri, Path.GetFileName(filePath));
    MarkdownWebView.NavigateToString(html);

    SelectHistoryItem(filePath);
  }

  private async Task RenderMessageAsync(string title, string message) {
    if (MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    var safeTitle = HtmlEncoder.Default.Encode(title);
    var safeMessage = HtmlEncoder.Default.Encode(message);

    var html = $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
<meta charset="utf-8">
<style>
body {
  font-family: "Segoe UI", sans-serif;
  margin: 0;
  padding: 24px;
  background: #ffffff;
  color: #222;
}
h2 { margin: 0 0 12px; }
p { color: #555; }
</style>
</head>
<body>
  <h2>{{safeTitle}}</h2>
  <p>{{safeMessage}}</p>
</body>
</html>
""";

    MarkdownWebView.NavigateToString(html);
    PathTextBlock.Text = title;
    PathTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(180, 40, 40));

    await Task.CompletedTask;
  }

  private static string BuildHtml(string markdownText, string baseUri, string title) {
    var markdownJson = JsonSerializer.Serialize(markdownText);
    var baseUriJson = JsonSerializer.Serialize(baseUri);
    var titleJson = JsonSerializer.Serialize(title);

    return $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self' data: blob: https: file:; img-src 'self' data: https: file:; style-src 'self' 'unsafe-inline' https:; script-src 'self' 'unsafe-inline' https:; font-src 'self' data: https:;">
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/github-markdown-css/5.8.1/github-markdown.min.css">
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/themes/prism-okaidia.min.css">
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/plugins/line-numbers/prism-line-numbers.min.css">
  <style>
    body {
      margin: 0;
      background: #ffffff;
    }

    .markdown-body {
      box-sizing: border-box;
      max-width: 1000px;
      width: 100%;
      margin: 0 auto;
      padding: 20px;
      font-family: "Noto Sans JP", "Hiragino Sans", "Yu Gothic UI", sans-serif;
    }

    pre.language-mermaid {
      background: #f6f8fa;
      border: 1px solid #e1e4e8;
      border-radius: 8px;
      padding: 8px;
      overflow-x: auto;
    }
  </style>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/marked/16.2.0/lib/marked.umd.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/prism.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/components/prism-csharp.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/components/prism-typescript.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/components/prism-json.min.js"></script>
  <script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.30.0/plugins/line-numbers/prism-line-numbers.min.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/@mermaid-js/tiny@11.10.0/dist/mermaid.tiny.min.js"></script>
</head>
<body class="markdown-body">
  <main id="main"></main>
  <script>
    (() => {
      const markdown = {{markdownJson}};
      const baseUri = {{baseUriJson}};
      const documentTitle = {{titleJson}};

      document.title = documentTitle;

      const baseElement = document.createElement('base');
      baseElement.href = baseUri;
      document.head.appendChild(baseElement);

      marked.use({
        renderer: {
          code(codeInfo) {
            const lang = (codeInfo.lang || '').toLowerCase();
            const text = codeInfo.text || '';

            if (lang === 'mermaid') {
              const encodedMermaid = text
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;');
              return '<pre class="language-mermaid">' + encodedMermaid + '</pre>';
            }

            const encoded = text
              .replaceAll('&', '&amp;')
              .replaceAll('<', '&lt;')
              .replaceAll('>', '&gt;');
            const className = lang ? `language-${lang}` : 'language-none';
            return `<pre class="line-numbers"><code class="${className}">${encoded}</code></pre>`;
          }
        }
      });

      marked.setOptions({ breaks: true });
      const html = marked.parse(markdown);
      document.getElementById('main').innerHTML = html;

      const isAbsoluteUrl = (value) => /^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(value) || value.startsWith('//');
      const toAbsoluteUrl = (value) => {
        if (!value || isAbsoluteUrl(value) || value.startsWith('#')) {
          return value;
        }

        try {
          return new URL(value, baseUri).href;
        } catch {
          return value;
        }
      };

      for (const image of document.querySelectorAll('img[src]')) {
        image.src = toAbsoluteUrl(image.getAttribute('src') || '');
      }

      Prism.highlightAll();
      mermaid.initialize({ securityLevel: 'loose', theme: 'neutral' });
      mermaid.init(undefined, document.querySelectorAll('pre.language-mermaid'));
    })();
  </script>
</body>
</html>
""";
  }

  private void ConfigureVirtualHostMapping(string folderPath) {
    if (MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    var fullFolderPath = Path.GetFullPath(folderPath);
    MarkdownWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
      VirtualHostName,
      fullFolderPath,
      CoreWebView2HostResourceAccessKind.Allow);
  }

  private void AddOrMoveHistory(string fullPath) {
    var existing = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    if (existing is not null) {
      historyItems.Remove(existing);
      historyItems.Insert(0, existing);
      SaveHistory();
      return;
    }

    historyItems.Insert(0, new HistoryItem(fullPath));
    SaveHistory();
  }

  private void LoadHistory() {
    var settings = settingRepository.Load();
    ApplyHistoryPaneVisibility(settings.IsHistoryPaneVisible);

    foreach (var path in settings.History) {
      if (string.IsNullOrWhiteSpace(path)) {
        continue;
      }

      var fullPath = Path.GetFullPath(path);
      if (!File.Exists(fullPath)) {
        continue;
      }

      if (historyItems.Any(item => string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))) {
        continue;
      }

      historyItems.Add(new HistoryItem(fullPath));
    }
  }

  private void SaveHistory() {
    var localHistory = historyItems.Select(item => item.FullPath).ToList();

    settingRepository.Update(settings => {
      var mergedHistory = new List<string>();

      foreach (var path in localHistory) {
        if (mergedHistory.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))) {
          continue;
        }

        mergedHistory.Add(path);
      }

      foreach (var path in settings.History) {
        if (string.IsNullOrWhiteSpace(path)) {
          continue;
        }

        var fullPath = Path.GetFullPath(path);
        if (mergedHistory.Any(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))) {
          continue;
        }

        mergedHistory.Add(fullPath);
      }

      settings.History = mergedHistory;
      settings.IsHistoryPaneVisible = HistoryToggleButton.IsChecked == true;
    });
  }

  private void ApplyHistoryPaneVisibility(bool isVisible) {
    HistoryToggleButton.IsChecked = isVisible;
    HistoryColumn.Width = isVisible ? new GridLength(220) : new GridLength(0);
    HistoryListBox.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
  }

  private void SelectHistoryItem(string fullPath) {
    suppressHistorySelectionChanged = true;

    HistoryListBox.SelectedItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    suppressHistorySelectionChanged = false;
  }

  private sealed class HistoryItem {
    public HistoryItem(string fullPath) {
      FullPath = fullPath;
      DisplayName = Path.GetFileName(fullPath);
    }

    public string DisplayName { get; }

    public string FullPath { get; }
  }
}

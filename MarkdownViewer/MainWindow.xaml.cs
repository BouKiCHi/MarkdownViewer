using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MarkdownViewer;

public partial class MainWindow : Window {
  private const string VirtualHostName = "markdown.local";
  private const string VirtualHostBaseUri = "https://markdown.local/";
  private const string AssetHostName = "appassets.local";

  private readonly SettingRepository settingRepository = new();
  private readonly ObservableCollection<HistoryItem> historyItems = [];
  private string? currentMarkdownPath;
  private string currentSourceText = string.Empty;
  private string editorPath = string.Empty;
  private bool isUnsafeHtmlEnabled;
  private bool suppressHistorySelectionChanged;
  private bool webViewEventsRegistered;

  private enum HistoryUpdateMode {
    None,
    AddOrMove,
    AddOnly
  }

  private enum ViewMode {
    Preview,
    Source
  }

  private ViewMode currentViewMode = ViewMode.Preview;

  public MainWindow(string? initialFilePath) {
    InitializeComponent();

    HistoryListBox.ItemsSource = historyItems;
    historyItems.CollectionChanged += HistoryItems_CollectionChanged;
    ApplySettings(settingRepository.Load());
    ApplyViewMode();

    if(!string.IsNullOrWhiteSpace(initialFilePath)) {
      OpenMarkdownFile(initialFilePath, HistoryUpdateMode.AddOnly);
    }
  }

  private void HistoryItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    RefreshHistoryItemDisplayState();
  }

  private async void Window_Loaded(object sender, RoutedEventArgs e) {
    try {
      await MarkdownWebView.EnsureCoreWebView2Async();
    } catch(Exception ex) {
      MessageBox.Show($"WebView2 の初期化に失敗しました。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
      return;
    }

    RegisterWebViewEvents();

    if(currentMarkdownPath is null) {
      if(TryOpenLatestHistory()) {
        return;
      }

      if(TryOpenMarkdownByDialog()) {
        return;
      }

      await RenderMessageAsync("表示する Markdown ファイルが指定されていません。", "コマンドライン引数にファイルパスを指定してください。");
      return;
    }

    await RenderMarkdownFileAsync(currentMarkdownPath);
  }

  private void RegisterWebViewEvents() {
    if(webViewEventsRegistered || MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    ConfigureAssetHostMapping();
    MarkdownWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
    MarkdownWebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
    webViewEventsRegistered = true;
  }

  private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
    if(TryOpenExternalUrl(e.Uri)) {
      e.Cancel = true;
    }
  }

  private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) {
    if(TryOpenExternalUrl(e.Uri)) {
      e.Handled = true;
    }
  }

  private static bool TryOpenExternalUrl(string? url) {
    if(string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
      return false;
    }

    if(uri.Scheme is not ("http" or "https")) {
      return false;
    }

    if(string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    if(string.Equals(uri.Host, AssetHostName, StringComparison.OrdinalIgnoreCase)) {
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
    if(latest is null) {
      return false;
    }

    if(!File.Exists(latest.FullPath)) {
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

    if(dialog.ShowDialog(this) != true) {
      return false;
    }

    OpenMarkdownFile(dialog.FileName);
    return true;
  }

  private async void ReloadButton_Click(object sender, RoutedEventArgs e) {
    if(currentMarkdownPath is null) {
      return;
    }

    await RenderMarkdownFileAsync(currentMarkdownPath);
  }

  private void OpenFileButton_Click(object sender, RoutedEventArgs e) {
    TryOpenMarkdownByDialog();
  }

  private void SettingsButton_Click(object sender, RoutedEventArgs e) {
    var settingsWindow = new SettingsWindow(editorPath) {
      Owner = this
    };

    if(settingsWindow.ShowDialog() != true) {
      return;
    }

    settingRepository.Update(settings => {
      settings.EditorPath = settingsWindow.EditorPath;
    });

    (Application.Current as App)?.RefreshAllWindowsFromSettings();
  }

  private void SourceViewToggleButton_Click(object sender, RoutedEventArgs e) {
    currentViewMode = SourceViewToggleButton!.IsChecked == true ? ViewMode.Source : ViewMode.Preview;
    ApplyViewMode();
  }

  private async void UnsafeHtmlToggleButton_Click(object sender, RoutedEventArgs e) {
    isUnsafeHtmlEnabled = UnsafeHtmlToggleButton!.IsChecked == true;
    UpdateUnsafeHtmlToggleAppearance();

    if(currentMarkdownPath is not null && MarkdownWebView!.CoreWebView2 is not null) {
      await RenderMarkdownFileAsync(currentMarkdownPath);
    }
  }

  private void HistoryToggleButton_Click(object sender, RoutedEventArgs e) {
    ApplyHistoryPaneVisibility(HistoryToggleButton!.IsChecked == true);
    SaveHistory();
  }

  private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(suppressHistorySelectionChanged || HistoryListBox!.SelectedItem is not HistoryItem selected) {
      return;
    }

    OpenMarkdownFile(selected.FullPath, HistoryUpdateMode.None);
  }

  private void HistoryListBoxItem_Loaded(object sender, RoutedEventArgs e) {
    if(sender is not ListBoxItem item) {
      return;
    }

    item.ContextMenu ??= BuildHistoryItemContextMenu();
  }

  private void HistoryListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
    if(sender is not ListBoxItem item) {
      return;
    }

    e.Handled = true;
    item.Focus();

    var contextMenu = item.ContextMenu;
    if(contextMenu is not null) {
      contextMenu.PlacementTarget = item;
      contextMenu.IsOpen = true;
    }
  }

  private void OpenInExplorerMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    OpenFileInExplorer(selectedItem.FullPath);
  }

  private void OpenInNewWindowMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    OpenFileInSeparateWindow(selectedItem.FullPath);
  }

  private void ReloadHistoryItemMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    OpenMarkdownFile(selectedItem.FullPath, HistoryUpdateMode.None);
  }

  private void OpenInEditorMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    OpenFileInEditor(selectedItem.FullPath);
  }

  private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    Clipboard.SetText(selectedItem.FullPath);
  }

  private void RemoveHistoryMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    suppressHistorySelectionChanged = true;
    historyItems.Remove(selectedItem);

    if(ReferenceEquals(HistoryListBox!.SelectedItem, selectedItem)) {
      HistoryListBox.SelectedItem = null;
    }

    suppressHistorySelectionChanged = false;
    SaveHistory();
  }

  private async void OpenMarkdownFile(string filePath, HistoryUpdateMode historyUpdateMode = HistoryUpdateMode.AddOrMove) {
    var fullPath = Path.GetFullPath(filePath);

    if(!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    if(historyUpdateMode != HistoryUpdateMode.None && !string.Equals(currentMarkdownPath, fullPath, StringComparison.OrdinalIgnoreCase)) {
      UpdateHistory(fullPath, historyUpdateMode);
    }
    currentMarkdownPath = fullPath;

    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    await RenderMarkdownFileAsync(fullPath);
  }

  public void HandleActivationRequest(string? filePath) {
    if(WindowState == WindowState.Minimized) {
      WindowState = WindowState.Normal;
    }

    Show();
    Activate();
    Topmost = true;
    Topmost = false;
    Focus();

    if(!string.IsNullOrWhiteSpace(filePath)) {
      OpenMarkdownFile(filePath, HistoryUpdateMode.AddOnly);
    }
  }

  private void OpenFileInExplorer(string filePath) {
    var fullPath = Path.GetFullPath(filePath);
    if(!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    try {
      Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") {
        UseShellExecute = true
      });
    } catch(Exception ex) {
      MessageBox.Show($"エクスプローラを開けませんでした。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OpenFileInSeparateWindow(string filePath) {
    var fullPath = Path.GetFullPath(filePath);
    if(!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    try {
      var window = new MainWindow(fullPath);
      (Application.Current as App)?.RegisterWindow(window);
      window.Show();
    } catch(Exception ex) {
      MessageBox.Show($"別ウインドウを開けませんでした。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OpenFileInEditor(string filePath) {
    var fullPath = Path.GetFullPath(filePath);
    if(!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    try {
      Process.Start(new ProcessStartInfo {
        FileName = editorPath,
        Arguments = $"\"{fullPath}\"",
        UseShellExecute = true
      });
    } catch(Exception ex) {
      MessageBox.Show(
        $"エディタを起動できませんでした。{Environment.NewLine}{editorPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
        "Markdown Viewer",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private ContextMenu BuildHistoryItemContextMenu() {
    var contextMenu = new ContextMenu();

    var reloadMenuItem = new MenuItem { Header = "再読み込み" };
    reloadMenuItem.Click += ReloadHistoryItemMenuItem_Click;
    contextMenu.Items.Add(reloadMenuItem);

    var openInNewWindowMenuItem = new MenuItem { Header = "別ウインドウで開く" };
    openInNewWindowMenuItem.Click += OpenInNewWindowMenuItem_Click;
    contextMenu.Items.Add(openInNewWindowMenuItem);

    var openInEditorMenuItem = new MenuItem { Header = "エディタで編集" };
    openInEditorMenuItem.Click += OpenInEditorMenuItem_Click;
    contextMenu.Items.Add(openInEditorMenuItem);

    var copyPathMenuItem = new MenuItem { Header = "パスをコピー" };
    copyPathMenuItem.Click += CopyPathMenuItem_Click;
    contextMenu.Items.Add(copyPathMenuItem);

    var removeHistoryMenuItem = new MenuItem { Header = "履歴から削除" };
    removeHistoryMenuItem.Click += RemoveHistoryMenuItem_Click;
    contextMenu.Items.Add(removeHistoryMenuItem);

    var openInExplorerMenuItem = new MenuItem { Header = "エクスプローラで開く" };
    openInExplorerMenuItem.Click += OpenInExplorerMenuItem_Click;
    contextMenu.Items.Add(openInExplorerMenuItem);

    return contextMenu;
  }

  private static HistoryItem? GetHistoryItemFromMenuSender(object sender) {
    if(sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu) {
      return null;
    }

    return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as HistoryItem;
  }

  private async Task RenderMarkdownFileAsync(string filePath) {
    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    string markdownText;
    try {
      markdownText = await File.ReadAllTextAsync(filePath);
    } catch(Exception ex) {
      await RenderMessageAsync("Markdown の読み込みに失敗しました。", ex.Message);
      return;
    }

    currentSourceText = markdownText;
    SourceTextBox.Text = markdownText;
    currentMarkdownPath = filePath;
    PathTextBlock.Text = Path.GetFileName(filePath);
    PathTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68));

    var baseDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
    ConfigureVirtualHostMapping(baseDirectory);

    var html = BuildHtml(markdownText, VirtualHostBaseUri, Path.GetFileName(filePath), isUnsafeHtmlEnabled);
    MarkdownWebView.NavigateToString(html);

    SelectHistoryItem(filePath);
  }

  private async Task RenderMessageAsync(string title, string message) {
    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    currentSourceText = $"{title}{Environment.NewLine}{Environment.NewLine}{message}";
    SourceTextBox.Text = currentSourceText;
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

  private static string BuildHtml(string markdownText, string baseUri, string title, bool isUnsafeHtmlEnabled) {
    var markdownJson = JsonSerializer.Serialize(markdownText);
    var baseUriJson = JsonSerializer.Serialize(baseUri);
    var titleJson = JsonSerializer.Serialize(title);
    var isUnsafeHtmlEnabledJson = JsonSerializer.Serialize(isUnsafeHtmlEnabled);

    return $$"""
<!DOCTYPE html>
<html lang="ja">
<head>
  <meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self' data: blob: https://markdown.local https://appassets.local file:; img-src 'self' data: https://markdown.local file:; style-src 'self' 'unsafe-inline' https://appassets.local; script-src 'self' 'unsafe-inline' https://appassets.local; font-src 'self' data: https://appassets.local;">
  <link rel="stylesheet" href="https://appassets.local/vendor/github-markdown-css/github-markdown.css">
  <link rel="stylesheet" href="https://appassets.local/vendor/prism/themes/prism-okaidia.min.css">
  <link rel="stylesheet" href="https://appassets.local/vendor/prism/plugins/line-numbers/prism-line-numbers.min.css">
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
  <script src="https://appassets.local/vendor/marked/marked.umd.js"></script>
  <script src="https://appassets.local/vendor/dompurify.min.js"></script>
  <script src="https://appassets.local/vendor/prism/prism.js"></script>
  <script src="https://appassets.local/vendor/prism/components/prism-csharp.min.js"></script>
  <script src="https://appassets.local/vendor/prism/components/prism-typescript.min.js"></script>
  <script src="https://appassets.local/vendor/prism/components/prism-json.min.js"></script>
  <script src="https://appassets.local/vendor/prism/plugins/line-numbers/prism-line-numbers.min.js"></script>
  <script src="https://appassets.local/vendor/mermaid/mermaid.tiny.js"></script>
</head>
<body class="markdown-body">
  <main id="main"></main>
  <script>
    (() => {
      const markdown = {{markdownJson}};
      const baseUri = {{baseUriJson}};
      const documentTitle = {{titleJson}};
      const isUnsafeHtmlEnabled = {{isUnsafeHtmlEnabledJson}};

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
      const renderedHtml = isUnsafeHtmlEnabled
        ? html
        : DOMPurify.sanitize(html, {
            USE_PROFILES: { html: true },
            ALLOW_DATA_ATTR: false,
            ADD_ATTR: ['class', 'target', 'rel', 'aria-hidden']
          });
      document.getElementById('main').innerHTML = renderedHtml;

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

      for (const sourceElement of document.querySelectorAll('source[src], source[srcset]')) {
        const src = sourceElement.getAttribute('src');
        if (src) {
          sourceElement.src = toAbsoluteUrl(src);
        }

        const srcSet = sourceElement.getAttribute('srcset');
        if (srcSet) {
          sourceElement.srcset = srcSet
            .split(',')
            .map(entry => entry.trim())
            .filter(Boolean)
            .map(entry => {
              const [urlPart, descriptor] = entry.split(/\s+/, 2);
              const absoluteUrl = toAbsoluteUrl(urlPart || '');
              return descriptor ? `${absoluteUrl} ${descriptor}` : absoluteUrl;
            })
            .join(', ');
        }
      }

      Prism.highlightAll();
      mermaid.initialize({ securityLevel: 'strict', theme: 'neutral' });
      mermaid.init(undefined, document.querySelectorAll('pre.language-mermaid'));
    })();
  </script>
</body>
</html>
""";
  }

  private void ConfigureVirtualHostMapping(string folderPath) {
    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    var fullFolderPath = Path.GetFullPath(folderPath);
    MarkdownWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
      VirtualHostName,
      fullFolderPath,
      CoreWebView2HostResourceAccessKind.Allow);
  }

  private void ConfigureAssetHostMapping() {
    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    var assetsFolderPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    MarkdownWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
      AssetHostName,
      assetsFolderPath,
      CoreWebView2HostResourceAccessKind.Allow);
  }

  private void UpdateHistory(string fullPath, HistoryUpdateMode historyUpdateMode) {
    var existing = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    if(existing is not null) {
      if(historyUpdateMode == HistoryUpdateMode.AddOrMove) {
        historyItems.Remove(existing);
        historyItems.Insert(0, existing);
        SaveHistory();
      }

      return;
    }

    historyItems.Insert(0, new HistoryItem(fullPath));
    SaveHistory();
  }

  private void SaveHistory() {
    var localHistory = historyItems.Select(item => item.FullPath).ToList();

    settingRepository.Update(settings => {
      settings.History = localHistory;
      settings.IsHistoryPaneVisible = HistoryToggleButton!.IsChecked == true;
    });

    (Application.Current as App)?.RefreshAllWindowsFromSettings();
  }

  internal void ApplySettings(AppSettings settings) {
    suppressHistorySelectionChanged = true;

    try {
      editorPath = EditorPathResolver.Resolve(settings.EditorPath);
      ApplyHistoryPaneVisibility(settings.IsHistoryPaneVisible);
      UnsafeHtmlToggleButton!.IsChecked = isUnsafeHtmlEnabled;
      UpdateUnsafeHtmlToggleAppearance();

      var currentSelectionPath = (HistoryListBox!.SelectedItem as HistoryItem)?.FullPath;
      var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var orderedPaths = new List<string>();

      foreach(var path in settings.History) {
        if(string.IsNullOrWhiteSpace(path)) {
          continue;
        }

        var fullPath = Path.GetFullPath(path);
        if(!File.Exists(fullPath) || !existingPaths.Add(fullPath)) {
          continue;
        }

        orderedPaths.Add(fullPath);
      }

      historyItems.Clear();
      foreach(var path in orderedPaths) {
        historyItems.Add(new HistoryItem(path));
      }

      if(currentSelectionPath is not null) {
        HistoryListBox.SelectedItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, currentSelectionPath, StringComparison.OrdinalIgnoreCase));
      }
    } finally {
      suppressHistorySelectionChanged = false;
    }
  }

  private void ApplyHistoryPaneVisibility(bool isVisible) {
    HistoryToggleButton!.IsChecked = isVisible;
    HistoryColumn.Width = isVisible ? new GridLength(220) : new GridLength(0);
    HistoryListBox!.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
  }

  private void UpdateUnsafeHtmlToggleAppearance() {
    UnsafeHtmlIconTextBlock!.Foreground = isUnsafeHtmlEnabled
      ? new SolidColorBrush(Color.FromRgb(194, 101, 13))
      : new SolidColorBrush(Color.FromRgb(102, 102, 102));
    UnsafeHtmlToggleButton!.ToolTip = isUnsafeHtmlEnabled
      ? "HTMLサニタイズを無効化中"
      : "HTMLサニタイズを無効化";
  }

  private void ApplyViewMode() {
    var isSourceMode = currentViewMode == ViewMode.Source;
    MarkdownWebView.Visibility = isSourceMode ? Visibility.Collapsed : Visibility.Visible;
    SourceTextBox.Visibility = isSourceMode ? Visibility.Visible : Visibility.Collapsed;
    SourceTextBox.Text = currentSourceText;
    UpdateSourceViewToggleAppearance();
  }

  private void UpdateSourceViewToggleAppearance() {
    var isSourceMode = currentViewMode == ViewMode.Source;
    SourceViewIconTextBlock!.Foreground = isSourceMode
      ? new SolidColorBrush(Color.FromRgb(11, 102, 35))
      : new SolidColorBrush(Color.FromRgb(102, 102, 102));
    SourceViewToggleButton!.ToolTip = isSourceMode
      ? "プレビュー表示に戻す"
      : "ソース表示";
  }

  private void SelectHistoryItem(string fullPath) {
    suppressHistorySelectionChanged = true;

    HistoryListBox!.SelectedItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    suppressHistorySelectionChanged = false;
  }

  private void RefreshHistoryItemDisplayState() {
    var duplicateGroups = historyItems
      .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
      .Where(group => group.Count() > 1)
      .ToList();

    foreach(var item in historyItems) {
      item.SecondaryText = string.Empty;
    }

    foreach(var group in duplicateGroups) {
      var items = group.ToList();
      foreach(var item in items) {
        item.SecondaryText = BuildDistinctDirectorySuffix(item, items);
      }
    }
  }

  private static string BuildDistinctDirectorySuffix(HistoryItem targetItem, IReadOnlyCollection<HistoryItem> items) {
    var targetSegments = GetDirectorySegments(targetItem.FullPath);
    if(targetSegments.Count == 0) {
      return "(親フォルダなし)";
    }

    for(var segmentCount = 1; segmentCount <= targetSegments.Count; segmentCount++) {
      var candidate = JoinTrailingSegments(targetSegments, segmentCount);
      var isUnique = items
        .Where(item => !ReferenceEquals(item, targetItem))
        .All(item => !string.Equals(candidate, JoinTrailingSegments(GetDirectorySegments(item.FullPath), segmentCount), StringComparison.OrdinalIgnoreCase));
      if(isUnique) {
        return candidate;
      }
    }

    return string.Join(Path.DirectorySeparatorChar, targetSegments);
  }

  private static IReadOnlyList<string> GetDirectorySegments(string fullPath) {
    var directoryPath = Path.GetDirectoryName(fullPath);
    if(string.IsNullOrWhiteSpace(directoryPath)) {
      return [];
    }

    var rootPath = Path.GetPathRoot(directoryPath);
    return directoryPath
      .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
      .Where(segment => !string.IsNullOrWhiteSpace(segment) && !string.Equals(segment, rootPath, StringComparison.OrdinalIgnoreCase))
      .ToArray();
  }

  private static string JoinTrailingSegments(IReadOnlyList<string> segments, int count) {
    if(segments.Count == 0) {
      return "(親フォルダなし)";
    }

    return string.Join(Path.DirectorySeparatorChar, segments.Skip(Math.Max(0, segments.Count - count)));
  }

  private sealed class HistoryItem : INotifyPropertyChanged {
    private string secondaryText = string.Empty;

    public HistoryItem(string fullPath) {
      FullPath = fullPath;
      DisplayName = Path.GetFileName(fullPath);
    }

    public string DisplayName { get; }

    public string FullPath { get; }

    public string SecondaryText {
      get => secondaryText;
      set {
        if(string.Equals(secondaryText, value, StringComparison.Ordinal)) {
          return;
        }

        secondaryText = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryTextVisibility)));
      }
    }

    public Visibility SecondaryTextVisibility => string.IsNullOrWhiteSpace(secondaryText) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
  }
}

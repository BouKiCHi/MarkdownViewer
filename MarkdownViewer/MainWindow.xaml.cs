using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MarkdownViewer;

public partial class MainWindow : Window {
  private const string VirtualHostName = "markdown.local";
  private const string VirtualHostBaseUri = "https://markdown.local/";
  private const string AssetHostName = "appassets.local";
  private const string ReleasePageUrl = "https://github.com/BouKiCHi/MarkdownViewer/releases";
  private static readonly RoutedCommand ToggleHistoryPaneCommand = new();
  private static readonly RoutedCommand ReloadMarkdownCommand = new();

  private readonly SettingRepository settingRepository = new();
  private readonly ObservableCollection<HistoryItem> historyItems = [];
  private readonly ObservableCollection<OutlineItem> outlineItems = [];
  private static readonly Regex AtxHeadingRegex = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
  private string? currentMarkdownPath;
  private string currentSourceText = string.Empty;
  private string editorPath = string.Empty;
  private bool isUnsafeHtmlEnabled;
  private bool suppressHistorySelectionChanged;
  private bool webViewEventsRegistered;
  private Point? historyDragStartPoint;
  private HistoryItem? draggedHistoryItem;
  private HistoryItem? dropIndicatorItem;
  private bool dropIndicatorAfter;

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

    CommandBindings.Add(new CommandBinding(ToggleHistoryPaneCommand, ToggleHistoryPaneCommand_Executed));
    CommandBindings.Add(new CommandBinding(ReloadMarkdownCommand, ReloadMarkdownCommand_Executed));
    InputBindings.Add(new KeyBinding(ToggleHistoryPaneCommand, new KeyGesture(Key.B, ModifierKeys.Control)));
    InputBindings.Add(new KeyBinding(ReloadMarkdownCommand, new KeyGesture(Key.R, ModifierKeys.Control)));
    HistoryListBox.ItemsSource = historyItems;
    OutlineListBox.ItemsSource = outlineItems;
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
      var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var webViewUserDataFolder = Path.Combine(localAppDataPath, "MarkdownViewer", "WebView2");
      Directory.CreateDirectory(webViewUserDataFolder);
      var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewUserDataFolder);
      await MarkdownWebView.EnsureCoreWebView2Async(webViewEnvironment);
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
    if(ToolbarContextMenu is null) {
      OpenSettingsDialog();
      return;
    }

    ToolbarContextMenu.PlacementTarget = SettingsButton;
    ToolbarContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
    ToolbarContextMenu.IsOpen = true;
  }

  private void ToolbarSettingsMenuItem_Click(object sender, RoutedEventArgs e) {
    OpenSettingsDialog();
  }

  private void ToolbarAboutMenuItem_Click(object sender, RoutedEventArgs e) {
    var aboutWindow = new AboutWindow(ReleasePageUrl) {
      Owner = this
    };

    aboutWindow.ShowDialog();
  }

  private void OpenSettingsDialog() {
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

  private void OpenReleasePageMenuItem_Click(object sender, RoutedEventArgs e) {
    if(TryOpenExternalUrl(ReleasePageUrl)) {
      return;
    }

    MessageBox.Show($"リリースページを開けませんでした。{Environment.NewLine}{ReleasePageUrl}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
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

  private void ToggleHistoryPaneCommand_Executed(object sender, ExecutedRoutedEventArgs e) {
    var nextValue = HistoryToggleButton!.IsChecked != true;
    ApplyHistoryPaneVisibility(nextValue);
    SaveHistory();
  }

  private async void ReloadMarkdownCommand_Executed(object sender, ExecutedRoutedEventArgs e) {
    if(currentMarkdownPath is null) {
      return;
    }

    await RenderMarkdownFileAsync(currentMarkdownPath);
  }

  private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(suppressHistorySelectionChanged || HistoryListBox!.SelectedItems.Count != 1 || HistoryListBox.SelectedItem is not HistoryItem selected) {
      return;
    }

    OpenMarkdownFile(selected.FullPath, HistoryUpdateMode.None);
  }

  private async void OutlineListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(OutlineListBox.SelectedItem is not OutlineItem selected) {
      return;
    }

    try {
      if(currentViewMode == ViewMode.Source) {
        ScrollSourceToOutline(selected);
      } else {
        await ScrollPreviewToHeadingAsync(selected.Slug);
      }
    } finally {
      OutlineListBox.SelectedItem = null;
    }
  }

  private void HistoryListBoxItem_Loaded(object sender, RoutedEventArgs e) {
    if(sender is not ListBoxItem item) {
      return;
    }

    item.ContextMenu ??= BuildHistoryItemContextMenu();
  }

  private void HistoryListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
    if(sender is not ListBoxItem item || item.DataContext is not HistoryItem historyItem) {
      historyDragStartPoint = null;
      draggedHistoryItem = null;
      return;
    }

    historyDragStartPoint = e.GetPosition(HistoryListBox);
    draggedHistoryItem = historyItem;
  }

  private void HistoryListBoxItem_PreviewMouseMove(object sender, MouseEventArgs e) {
    if(e.LeftButton != MouseButtonState.Pressed || sender is not ListBoxItem item || draggedHistoryItem is null || historyDragStartPoint is null) {
      return;
    }

    var currentPosition = e.GetPosition(HistoryListBox);
    var dragDistance = currentPosition - historyDragStartPoint.Value;
    if(Math.Abs(dragDistance.X) < SystemParameters.MinimumHorizontalDragDistance
      && Math.Abs(dragDistance.Y) < SystemParameters.MinimumVerticalDragDistance) {
      return;
    }

    try {
      DragDrop.DoDragDrop(item, draggedHistoryItem, DragDropEffects.Move);
    } finally {
      historyDragStartPoint = null;
      draggedHistoryItem = null;
    }
  }

  private void HistoryListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
    if(sender is not ListBoxItem item || item.DataContext is not HistoryItem historyItem) {
      return;
    }

    e.Handled = true;

    if(!item.IsSelected) {
      suppressHistorySelectionChanged = true;
      HistoryListBox!.SelectedItems.Clear();
      item.IsSelected = true;
      suppressHistorySelectionChanged = false;
    }

    item.Focus();

    var contextMenu = item.ContextMenu;
    if(contextMenu is not null) {
      contextMenu.PlacementTarget = item;
      contextMenu.IsOpen = true;
    }
  }

  private void HistoryListBox_DragOver(object sender, DragEventArgs e) {
    if(e.Data.GetDataPresent(typeof(HistoryItem))) {
      e.Effects = DragDropEffects.Move;
    } else if(TryGetDroppedMarkdownPaths(e.Data).Count > 0) {
      e.Effects = DragDropEffects.Copy;
    } else {
      e.Effects = DragDropEffects.None;
    }

    if(e.Effects == DragDropEffects.None) {
      ClearDropIndicator();
      e.Handled = true;
      return;
    }

    var targetContainer = ItemsControl.ContainerFromElement(HistoryListBox!, e.OriginalSource as DependencyObject) as ListBoxItem;
    var targetItem = targetContainer?.DataContext as HistoryItem;
    var isAfter = targetContainer is not null && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2;
    UpdateDropIndicator(targetItem, isAfter);
    e.Handled = true;
  }

  private void HistoryListBox_DragLeave(object sender, DragEventArgs e) {
    if(!HistoryListBox!.IsMouseOver) {
      ClearDropIndicator();
    }
  }

  private void HistoryListBox_Drop(object sender, DragEventArgs e) {
    if(e.Data.GetDataPresent(typeof(HistoryItem))) {
      var sourceItem = e.Data.GetData(typeof(HistoryItem)) as HistoryItem;
      if(sourceItem is null) {
        ClearDropIndicator();
        return;
      }

      var targetContainer = ItemsControl.ContainerFromElement(HistoryListBox!, e.OriginalSource as DependencyObject) as ListBoxItem;
      var targetItem = targetContainer?.DataContext as HistoryItem;

      var dropTarget = (IInputElement?)targetContainer ?? HistoryListBox!;
      var targetHeight = targetContainer?.ActualHeight;
      MoveHistoryItem(sourceItem, targetItem, e.GetPosition(dropTarget), targetHeight);
      ClearDropIndicator();
      draggedHistoryItem = null;
      historyDragStartPoint = null;
      e.Handled = true;
      return;
    }

    var droppedPaths = TryGetDroppedMarkdownPaths(e.Data);
    if(droppedPaths.Count == 0) {
      ClearDropIndicator();
      return;
    }

    var fileDropTargetContainer = ItemsControl.ContainerFromElement(HistoryListBox!, e.OriginalSource as DependencyObject) as ListBoxItem;
    var fileDropTargetItem = fileDropTargetContainer?.DataContext as HistoryItem;
    var insertIndex = GetDropInsertIndex(fileDropTargetItem, fileDropTargetContainer, e);
    InsertHistoryItems(droppedPaths, insertIndex);
    OpenMarkdownFile(droppedPaths[0], HistoryUpdateMode.None);
    ClearDropIndicator();
    draggedHistoryItem = null;
    historyDragStartPoint = null;
    e.Handled = true;
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

  private void CopyFileNameMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetHistoryItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    Clipboard.SetText(Path.GetFileName(selectedItem.FullPath));
  }

  private void RemoveHistoryMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItems = GetHistoryItemsFromMenuSender(sender);
    if(selectedItems.Count == 0) {
      return;
    }

    suppressHistorySelectionChanged = true;

    foreach(var selectedItem in selectedItems) {
      historyItems.Remove(selectedItem);
    }

    HistoryListBox!.SelectedItems.Clear();

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

    contextMenu.Items.Add(new Separator());

    var removeHistoryMenuItem = new MenuItem { Header = "選択したタブを削除" };
    removeHistoryMenuItem.Click += RemoveHistoryMenuItem_Click;
    contextMenu.Items.Add(removeHistoryMenuItem);

    contextMenu.Items.Add(new Separator());

    var openInNewWindowMenuItem = new MenuItem { Header = "別ウインドウで開く" };
    openInNewWindowMenuItem.Click += OpenInNewWindowMenuItem_Click;
    contextMenu.Items.Add(openInNewWindowMenuItem);

    var openInEditorMenuItem = new MenuItem { Header = "エディタで編集" };
    openInEditorMenuItem.Click += OpenInEditorMenuItem_Click;
    contextMenu.Items.Add(openInEditorMenuItem);

    var copyFileNameMenuItem = new MenuItem { Header = "ファイル名をコピー" };
    copyFileNameMenuItem.Click += CopyFileNameMenuItem_Click;
    contextMenu.Items.Add(copyFileNameMenuItem);

    var copyPathMenuItem = new MenuItem { Header = "パスをコピー" };
    copyPathMenuItem.Click += CopyPathMenuItem_Click;
    contextMenu.Items.Add(copyPathMenuItem);

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

  private IReadOnlyList<HistoryItem> GetHistoryItemsFromMenuSender(object sender) {
    var contextItem = GetHistoryItemFromMenuSender(sender);
    if(contextItem is null) {
      return [];
    }

    var selectedItems = HistoryListBox!.SelectedItems
      .OfType<HistoryItem>()
      .ToList();

    if(selectedItems.Count == 0) {
      return [contextItem];
    }

    if(selectedItems.Any(item => ReferenceEquals(item, contextItem))) {
      return selectedItems;
    }

    return [contextItem];
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
    UpdateOutline(markdownText);
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
    outlineItems.Clear();
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
      const createSlug = (value) => {
        const normalized = (value || '')
          .normalize('NFKC')
          .trim()
          .toLowerCase()
          .replace(/[^\p{L}\p{N}\s-]/gu, '')
          .replace(/\s+/g, '-')
          .replace(/-+/g, '-')
          .replace(/^-|-$/g, '');
        return normalized || 'section';
      };
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

      const slugCounts = new Map();
      for (const heading of document.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
        if (heading.id) {
          continue;
        }

        const baseSlug = createSlug(heading.textContent || '');
        const index = slugCounts.get(baseSlug) || 0;
        slugCounts.set(baseSlug, index + 1);
        heading.id = index === 0 ? baseSlug : `${baseSlug}-${index + 1}`;
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
      return;
    }

    historyItems.Add(new HistoryItem(fullPath));
    SaveHistory();
  }

  private void MoveHistoryItem(HistoryItem sourceItem, HistoryItem? targetItem, Point dropPosition, double? targetHeight) {
    var sourceIndex = historyItems.IndexOf(sourceItem);
    if(sourceIndex < 0) {
      return;
    }

    var targetIndex = targetItem is null ? historyItems.Count - 1 : historyItems.IndexOf(targetItem);
    if(targetIndex < 0) {
      return;
    }

    if(targetItem is not null && targetHeight.HasValue && dropPosition.Y > targetHeight.Value / 2) {
      targetIndex++;
    }

    if(targetIndex > sourceIndex) {
      targetIndex--;
    }

    if(targetIndex == sourceIndex) {
      return;
    }

    targetIndex = Math.Max(0, Math.Min(targetIndex, historyItems.Count - 1));
    historyItems.Move(sourceIndex, targetIndex);
    HistoryListBox!.SelectedItem = sourceItem;
    SaveHistory();
  }

  private int GetDropInsertIndex(HistoryItem? targetItem, ListBoxItem? targetContainer, DragEventArgs e) {
    if(targetItem is null) {
      return historyItems.Count;
    }

    var targetIndex = historyItems.IndexOf(targetItem);
    if(targetIndex < 0) {
      return historyItems.Count;
    }

    if(targetContainer is not null && e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2) {
      targetIndex++;
    }

    return Math.Max(0, Math.Min(targetIndex, historyItems.Count));
  }

  private void InsertHistoryItems(IReadOnlyList<string> filePaths, int insertIndex) {
    var currentIndex = Math.Max(0, Math.Min(insertIndex, historyItems.Count));
    var changed = false;

    foreach(var filePath in filePaths) {
      var fullPath = Path.GetFullPath(filePath);
      var existingItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(existingItem is not null) {
        var existingIndex = historyItems.IndexOf(existingItem);
        if(existingIndex >= 0) {
          historyItems.RemoveAt(existingIndex);
          if(existingIndex < currentIndex) {
            currentIndex--;
          }
        }
      }

      historyItems.Insert(currentIndex, new HistoryItem(fullPath));
      currentIndex++;
      changed = true;
    }

    if(changed) {
      SaveHistory();
    }
  }

  private static IReadOnlyList<string> TryGetDroppedMarkdownPaths(IDataObject dataObject) {
    if(!dataObject.GetDataPresent(DataFormats.FileDrop)) {
      return [];
    }

    if(dataObject.GetData(DataFormats.FileDrop) is not string[] rawPaths) {
      return [];
    }

    return rawPaths
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Select(Path.GetFullPath)
      .Where(File.Exists)
      .Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".markdown", StringComparison.OrdinalIgnoreCase))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  private void UpdateDropIndicator(HistoryItem? targetItem, bool isAfter) {
    if(ReferenceEquals(dropIndicatorItem, targetItem) && dropIndicatorAfter == isAfter) {
      return;
    }

    ClearDropIndicator();

    if(targetItem is null) {
      return;
    }

    dropIndicatorItem = targetItem;
    dropIndicatorAfter = isAfter;
    targetItem.IsDropTargetBefore = !isAfter;
    targetItem.IsDropTargetAfter = isAfter;
  }

  private void ClearDropIndicator() {
    if(dropIndicatorItem is null) {
      return;
    }

    dropIndicatorItem.IsDropTargetBefore = false;
    dropIndicatorItem.IsDropTargetAfter = false;
    dropIndicatorItem = null;
    dropIndicatorAfter = false;
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
    HistorySplitterColumn.Width = isVisible ? new GridLength(6) : new GridLength(0);
    HistoryListBox!.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    HistoryGridSplitter!.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
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

  private void UpdateOutline(string markdownText) {
    outlineItems.Clear();

    var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    using var reader = new StringReader(markdownText);
    string? line;
    var lineIndex = 0;

    while((line = reader.ReadLine()) is not null) {
      lineIndex++;
      var match = AtxHeadingRegex.Match(line);
      if(!match.Success) {
        continue;
      }

      var level = match.Groups[1].Value.Length;
      var title = match.Groups[2].Value.Trim();
      if(string.IsNullOrWhiteSpace(title)) {
        continue;
      }

      var baseSlug = CreateSlug(title);
      slugCounts.TryGetValue(baseSlug, out var count);
      slugCounts[baseSlug] = count + 1;
      var slug = count == 0 ? baseSlug : $"{baseSlug}-{count + 1}";
      outlineItems.Add(new OutlineItem(title, level, lineIndex, slug));
    }
  }

  private async Task ScrollPreviewToHeadingAsync(string slug) {
    if(MarkdownWebView.CoreWebView2 is null) {
      return;
    }

    var slugJson = JsonSerializer.Serialize(slug);
    await MarkdownWebView.CoreWebView2.ExecuteScriptAsync($$"""
(() => {
  const element = document.getElementById({{slugJson}});
  if (!element) {
    return false;
  }

  element.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return true;
})()
""");
  }

  private void ScrollSourceToOutline(OutlineItem outline) {
    var lineIndex = Math.Max(0, outline.LineNumber - 1);
    if(lineIndex >= SourceTextBox.LineCount) {
      return;
    }

    var characterIndex = SourceTextBox.GetCharacterIndexFromLineIndex(lineIndex);
    if(characterIndex < 0) {
      return;
    }

    SourceTextBox.Focus();
    SourceTextBox.CaretIndex = characterIndex;
    SourceTextBox.ScrollToLine(lineIndex);
  }

  private static string CreateSlug(string value) {
    if(string.IsNullOrWhiteSpace(value)) {
      return "section";
    }

    var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
    var builder = new StringBuilder(normalized.Length);
    var previousWasHyphen = false;

    foreach(var character in normalized) {
      if(char.IsLetterOrDigit(character)) {
        builder.Append(character);
        previousWasHyphen = false;
        continue;
      }

      if(char.IsWhiteSpace(character) || character == '-') {
        if(builder.Length > 0 && !previousWasHyphen) {
          builder.Append('-');
          previousWasHyphen = true;
        }
      }
    }

    var slug = builder.ToString().Trim('-');
    return string.IsNullOrWhiteSpace(slug) ? "section" : slug;
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
    private bool isDropTargetBefore;
    private bool isDropTargetAfter;

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

    public bool IsDropTargetBefore {
      get => isDropTargetBefore;
      set {
        if(isDropTargetBefore == value) {
          return;
        }

        isDropTargetBefore = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTargetBefore)));
      }
    }

    public bool IsDropTargetAfter {
      get => isDropTargetAfter;
      set {
        if(isDropTargetAfter == value) {
          return;
        }

        isDropTargetAfter = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTargetAfter)));
      }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
  }

  private sealed class OutlineItem {
    public OutlineItem(string title, int level, int lineNumber, string slug) {
      Title = title;
      Level = level;
      LineNumber = lineNumber;
      Slug = slug;
      IndentPadding = new Thickness(Math.Max(0, (level - 1) * 14), 4, 10, 4);
    }

    public string Title { get; }

    public int Level { get; }

    public int LineNumber { get; }

    public string Slug { get; }

    public Thickness IndentPadding { get; }
  }
}

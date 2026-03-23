using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace MarkdownViewer;

public partial class MainWindow : Window {
  private const int CurrentDockLayoutVersion = 3;
  private const string VirtualHostName = "markdown.local";
  private const string VirtualHostBaseUri = "https://markdown.local/";
  private const string AssetHostName = "appassets.local";
  private const string ReleasePageUrl = "https://github.com/BouKiCHi/MarkdownViewer/releases";
  private static readonly RoutedCommand ToggleHistoryPaneCommand = new();
  private static readonly RoutedCommand ReloadMarkdownCommand = new();
  private static readonly RoutedCommand GoToLineCommand = new();

  private readonly SettingRepository settingRepository = new();
  private readonly ObservableCollection<HistoryItem> historyItems = [];
  private readonly ObservableCollection<DirectoryFileItem> directoryFileItems = [];
  private readonly ObservableCollection<OutlineItem> outlineItems = [];
  private readonly Dictionary<string, MarkdownDocumentTab> openDocuments = new(StringComparer.OrdinalIgnoreCase);
  private static readonly Regex AtxHeadingRegex = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
  private CoreWebView2Environment? webViewEnvironment;
  private string? startupFilePath;
  private string editorPath = string.Empty;
  private bool isUnsafeHtmlEnabled;
  private bool suppressHistorySelectionChanged;
  private Point? historyDragStartPoint;
  private HistoryItem? draggedHistoryItem;
  private HistoryItem? dropIndicatorItem;
  private bool dropIndicatorAfter;
  private string? currentDirectoryItemsPath;
  private DirectoryFileSortMode currentDirectoryFileSortMode = DirectoryFileSortMode.LastWriteTimeDescending;
  private bool suppressDirectoryFileSortSelectionChanged;

  private enum HistoryUpdateMode {
    None,
    AddOrMove,
    AddOnly
  }

  private enum ViewMode {
    Preview,
    Source
  }

  private enum DirectoryFileSortMode {
    FileNameAscending,
    LastWriteTimeDescending
  }

  private ViewMode currentViewMode = ViewMode.Preview;

  public MainWindow(string? initialFilePath) {
    InitializeComponent();
    startupFilePath = initialFilePath;

    CommandBindings.Add(new CommandBinding(ToggleHistoryPaneCommand, ToggleHistoryPaneCommand_Executed));
    CommandBindings.Add(new CommandBinding(ReloadMarkdownCommand, ReloadMarkdownCommand_Executed));
    CommandBindings.Add(new CommandBinding(GoToLineCommand, GoToLineCommand_Executed));
    InputBindings.Add(new KeyBinding(ToggleHistoryPaneCommand, new KeyGesture(Key.B, ModifierKeys.Control)));
    InputBindings.Add(new KeyBinding(ReloadMarkdownCommand, new KeyGesture(Key.R, ModifierKeys.Control)));
    InputBindings.Add(new KeyBinding(GoToLineCommand, new KeyGesture(Key.G, ModifierKeys.Control)));
    HistoryListBox.ItemsSource = historyItems;
    DirectoryFileListBox.ItemsSource = directoryFileItems;
    OutlineListBox.ItemsSource = outlineItems;
    HistoryAnchorable.PropertyChanged += HistoryAnchorable_PropertyChanged;
    historyItems.CollectionChanged += HistoryItems_CollectionChanged;
    ApplySettings(settingRepository.Load());
    UpdateOutlineLayoutButtonAppearance();
  }

  private void HistoryItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
    RefreshHistoryItemDisplayState();
  }

  private void OutlineAnchorable_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if(e.PropertyName is nameof(LayoutAnchorable.IsVisible) or nameof(LayoutAnchorable.IsAutoHidden) or nameof(LayoutAnchorable.IsHidden)) {
      UpdateOutlineLayoutButtonAppearance();
    }
  }

  private void HistoryAnchorable_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
    if(e.PropertyName is nameof(LayoutAnchorable.IsVisible) or nameof(LayoutAnchorable.IsAutoHidden) or nameof(LayoutAnchorable.IsHidden)) {
      UpdateHistoryToggleState();
    }
  }

  private async void Window_Loaded(object sender, RoutedEventArgs e) {
    try {
      try {
        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var webViewUserDataFolder = Path.Combine(localAppDataPath, "MarkdownViewer", "WebView2");
        Directory.CreateDirectory(webViewUserDataFolder);
        webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewUserDataFolder);
      } catch(Exception ex) {
        MessageBox.Show($"WebView2 の初期化に失敗しました。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        return;
      }

      var settings = settingRepository.Load();
      var canRestoreLayout = settings.DockLayoutVersion == CurrentDockLayoutVersion;
      if(!canRestoreLayout && !string.IsNullOrWhiteSpace(settings.DockLayoutXml)) {
        ClearDockLayoutSetting();
      }

      var layoutLoaded = string.IsNullOrWhiteSpace(startupFilePath) && canRestoreLayout && await TryRestoreDockLayoutAsync(settings.DockLayoutXml);

      if(!string.IsNullOrWhiteSpace(startupFilePath)) {
        OpenMarkdownFile(startupFilePath, HistoryUpdateMode.AddOnly);
        startupFilePath = null;
        return;
      }

      if(!layoutLoaded || GetActiveDocumentTab() is null) {
        if(TryOpenLatestHistory()) {
          return;
        }

        if(TryOpenMarkdownByDialog()) {
          return;
        }
      }
    } catch(Exception ex) {
      ClearDockLayoutSetting();
      MessageBox.Show($"起動時のレイアウト復元に失敗したため、保存レイアウトを破棄して既定状態で起動してください。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
  }

  private void Window_Closing(object? sender, CancelEventArgs e) {
    PersistWindowLayout();
  }

  private void ClearDockLayoutSetting() {
    settingRepository.Update(settings => {
      settings.DockLayoutXml = string.Empty;
      settings.DockLayoutVersion = CurrentDockLayoutVersion;
    });
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
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument?.FullPath is null) {
      return;
    }

    await RenderMarkdownFileAsync(activeDocument);
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

  private void FilesSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(suppressDirectoryFileSortSelectionChanged || sender is not ComboBox comboBox || comboBox.SelectedItem is not ComboBoxItem selectedItem) {
      return;
    }

    SetDirectoryFileSortMode(ParseDirectoryFileSortMode(selectedItem.Tag as string));
  }

  private void CloseDocumentsToRightMenuItem_Click(object sender, RoutedEventArgs e) {
    var targetDocument = GetLayoutDocumentFromMenuSender(sender);
    var documentPane = targetDocument?.Parent as LayoutDocumentPane ?? GetDocumentPane();
    if(targetDocument is null || documentPane is null) {
      return;
    }

    var documentsToClose = documentPane.Children
      .OfType<LayoutDocument>()
      .SkipWhile(document => !ReferenceEquals(document, targetDocument))
      .Skip(1)
      .Where(document => document.CanClose)
      .ToArray();

    foreach(var document in documentsToClose) {
      document.Close();
    }
  }

  private void OpenDocumentInExplorerMenuItem_Click(object sender, RoutedEventArgs e) {
    var targetDocument = GetLayoutDocumentFromMenuSender(sender);
    if(string.IsNullOrWhiteSpace(targetDocument?.ContentId)) {
      return;
    }

    OpenFileInExplorer(targetDocument.ContentId);
  }

  private void SourceViewToggleButton_Click(object sender, RoutedEventArgs e) {
    currentViewMode = SourceViewToggleButton!.IsChecked == true ? ViewMode.Source : ViewMode.Preview;
    ApplyViewMode();
  }

  private void OutlineLayoutButton_Click(object sender, RoutedEventArgs e) {
    ShowOutlinePane();
  }

  private async void UnsafeHtmlToggleButton_Click(object sender, RoutedEventArgs e) {
    isUnsafeHtmlEnabled = UnsafeHtmlToggleButton!.IsChecked == true;
    UpdateUnsafeHtmlToggleAppearance();

    var activeDocument = GetActiveDocumentTab();
    if(activeDocument is not null) {
      await RenderMarkdownFileAsync(activeDocument);
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
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument?.FullPath is null) {
      return;
    }

    await RenderMarkdownFileAsync(activeDocument);
  }

  private async void GoToLineButton_Click(object sender, RoutedEventArgs e) {
    await OpenGoToLineDialogAsync();
  }

  private async void GoToLineCommand_Executed(object sender, ExecutedRoutedEventArgs e) {
    await OpenGoToLineDialogAsync();
  }

  private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(suppressHistorySelectionChanged || HistoryListBox!.SelectedItems.Count != 1 || HistoryListBox.SelectedItem is not HistoryItem selected) {
      return;
    }

    OpenMarkdownFile(selected.FullPath, HistoryUpdateMode.None, focusDisplay: true);
  }

  private async void OutlineListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) {
    if(sender is not ListBox listBox || listBox.SelectedItem is not OutlineItem selected) {
      return;
    }

    try {
      if(currentViewMode == ViewMode.Source) {
        ScrollSourceToOutline(selected);
      } else {
        await ScrollPreviewToHeadingAsync(selected.Slug);
      }
    } finally {
      listBox.SelectedItem = null;
    }
  }

  private void DirectoryFileListBoxItem_Loaded(object sender, RoutedEventArgs e) {
    if(sender is not ListBoxItem item) {
      return;
    }

    item.ContextMenu ??= BuildDirectoryFileItemContextMenu();
  }

  private void DirectoryFileListBoxItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
    if(sender is not ListBoxItem item || item.DataContext is not DirectoryFileItem selectedItem) {
      return;
    }

    e.Handled = true;
    DirectoryFileListBox!.SelectedItem = selectedItem;
    item.Focus();

    if(selectedItem.IsMarkdownFile) {
      OpenMarkdownFile(selectedItem.FullPath, HistoryUpdateMode.AddOrMove, focusDisplay: true);
    }

    DirectoryFileListBox.SelectedItem = null;
  }

  private void DirectoryFileListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) {
    if(sender is not ListBoxItem item || item.DataContext is not DirectoryFileItem) {
      return;
    }

    e.Handled = true;
    DirectoryFileListBox!.SelectedItem = item.DataContext;
    item.Focus();

    var contextMenu = item.ContextMenu;
    if(contextMenu is not null) {
      contextMenu.PlacementTarget = item;
      contextMenu.IsOpen = true;
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

    if(openDocuments.TryGetValue(historyItem.FullPath, out var existingDocument)) {
      ActivateDocumentTab(existingDocument, focusDisplay: true);
    }
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

    var targetContainer = TryGetHistoryListBoxItemFromOriginalSource(e.OriginalSource as DependencyObject);
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

      var targetContainer = TryGetHistoryListBoxItemFromOriginalSource(e.OriginalSource as DependencyObject);
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

    var fileDropTargetContainer = TryGetHistoryListBoxItemFromOriginalSource(e.OriginalSource as DependencyObject);
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

  private async void OpenMarkdownFile(string filePath, HistoryUpdateMode historyUpdateMode = HistoryUpdateMode.AddOrMove, bool focusDisplay = false) {
    var fullPath = Path.GetFullPath(filePath);
    Debug.WriteLine($"[MarkdownViewer] OpenMarkdownFile start. Path={Path.GetFileName(fullPath)} FocusDisplay={focusDisplay}");

    if(!File.Exists(fullPath)) {
      var staleItem = historyItems.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
      if(staleItem is not null) {
        historyItems.Remove(staleItem);
        SaveHistory();
      }

      MessageBox.Show($"指定されたファイルが見つかりません。{Environment.NewLine}{fullPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    if(historyUpdateMode != HistoryUpdateMode.None && !string.Equals(GetActiveDocumentTab()?.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) {
      UpdateHistory(fullPath, historyUpdateMode);
    }

    if(webViewEnvironment is null) {
      startupFilePath = fullPath;
      return;
    }

    var isNewDocument = !openDocuments.ContainsKey(fullPath);
    Debug.WriteLine($"[MarkdownViewer] OpenMarkdownFile before GetOrCreateDocumentTab. Path={Path.GetFileName(fullPath)} IsNewDocument={isNewDocument}");
    var documentTab = GetOrCreateDocumentTab(fullPath);
    if(documentTab is null) {
      Debug.WriteLine($"[MarkdownViewer] OpenMarkdownFile aborted because documentTab is null. Path={Path.GetFileName(fullPath)}");
      return;
    }

    var shouldFocusDisplay = focusDisplay || isNewDocument;
    DebugLog(documentTab, $"OpenMarkdownFile after GetOrCreateDocumentTab. ShouldFocusDisplay={shouldFocusDisplay}");
    ActivateDocumentTab(documentTab, focusDisplay: shouldFocusDisplay);
    DebugLog(documentTab, "OpenMarkdownFile after first ActivateDocumentTab.");

    if(!await EnsureDocumentInitializedAsync(documentTab)) {
      DebugLog(documentTab, "OpenMarkdownFile aborted because document initialization failed.");
      if(isNewDocument) {
        GetDocumentPane()?.Children.Remove(documentTab.LayoutDocument);
        openDocuments.Remove(fullPath);
      }

      return;
    }

    await RenderMarkdownFileAsync(documentTab);
    DebugLog(documentTab, "OpenMarkdownFile after RenderMarkdownFileAsync.");
    ActivateDocumentTab(documentTab, focusDisplay: shouldFocusDisplay);
    DebugLog(documentTab, "OpenMarkdownFile after second ActivateDocumentTab.");

    if(shouldFocusDisplay) {
      _ = Dispatcher.BeginInvoke(
        () => {
          DebugLog(documentTab, "OpenMarkdownFile ContextIdle ActivateDocumentTab.");
          ActivateDocumentTab(documentTab, focusDisplay: true);
        },
        System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
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
      OpenMarkdownFile(filePath, HistoryUpdateMode.AddOnly, focusDisplay: true);
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

  private ContextMenu BuildDirectoryFileItemContextMenu() {
    var contextMenu = new ContextMenu();

    var openInExplorerMenuItem = new MenuItem { Header = "エクスプローラで開く" };
    openInExplorerMenuItem.Click += OpenDirectoryFileInExplorerMenuItem_Click;
    contextMenu.Items.Add(openInExplorerMenuItem);

    return contextMenu;
  }

  private static HistoryItem? GetHistoryItemFromMenuSender(object sender) {
    if(sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu) {
      return null;
    }

    return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as HistoryItem;
  }

  private static DirectoryFileItem? GetDirectoryFileItemFromMenuSender(object sender) {
    if(sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu) {
      return null;
    }

    return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as DirectoryFileItem;
  }

  private static LayoutDocument? GetLayoutDocumentFromMenuSender(object sender) {
    if(sender is not MenuItem menuItem) {
      return null;
    }

    return TryExtractLayoutDocument(menuItem.CommandParameter)
      ?? TryExtractLayoutDocument(menuItem.DataContext)
      ?? TryExtractLayoutDocument((menuItem.Parent as ContextMenu)?.DataContext);
  }

  private static LayoutDocument? TryExtractLayoutDocument(object? candidate) {
    return TryExtractLayoutDocument(candidate, new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance));
  }

  private static LayoutDocument? TryExtractLayoutDocument(object? candidate, HashSet<object> visited) {
    if(candidate is null) {
      return null;
    }

    if(candidate is LayoutDocument layoutDocument) {
      return layoutDocument;
    }

    if(!visited.Add(candidate)) {
      return null;
    }

    var candidateType = candidate.GetType();
    foreach(var propertyName in new[] { "Model", "LayoutElement", "LayoutContent", "Content", "Root" }) {
      var property = candidateType.GetProperty(propertyName);
      if(property is null || property.GetIndexParameters().Length != 0) {
        continue;
      }

      var value = property.GetValue(candidate);
      var resolvedDocument = TryExtractLayoutDocument(value, visited);
      if(resolvedDocument is not null) {
        return resolvedDocument;
      }
    }

    return null;
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

  private void OpenDirectoryFileInExplorerMenuItem_Click(object sender, RoutedEventArgs e) {
    var selectedItem = GetDirectoryFileItemFromMenuSender(sender);
    if(selectedItem is null) {
      return;
    }

    OpenFileInExplorer(selectedItem.FullPath);
  }

  private async Task RenderMarkdownFileAsync(MarkdownDocumentTab documentTab) {
    if(documentTab.WebView.CoreWebView2 is null) {
      DebugLog(documentTab, "Render skipped because CoreWebView2 is null.");
      return;
    }

    string markdownText;
    try {
      markdownText = await File.ReadAllTextAsync(documentTab.FullPath);
    } catch(Exception ex) {
      await RenderMessageAsync(documentTab, "Markdown の読み込みに失敗しました。", ex.Message);
      return;
    }

    documentTab.SourceText = markdownText;
    documentTab.SourceTextBox.Text = markdownText;
    documentTab.OutlineItems = BuildOutlineItems(markdownText);
    documentTab.LayoutDocument.Title = Path.GetFileName(documentTab.FullPath);

    var baseDirectory = Path.GetDirectoryName(documentTab.FullPath) ?? Environment.CurrentDirectory;
    ConfigureVirtualHostMapping(documentTab.WebView, baseDirectory);

    var html = BuildHtml(markdownText, VirtualHostBaseUri, Path.GetFileName(documentTab.FullPath), isUnsafeHtmlEnabled);
    DebugLog(documentTab, $"NavigateToString start. HtmlLength={html.Length}");
    documentTab.WebView.NavigateToString(html);
    documentTab.RequiresActivationRender = false;
    DebugLog(documentTab, "NavigateToString submitted.");

    UpdateWindowForActiveDocument();
    SelectHistoryItem(documentTab.FullPath);
  }

  private async Task RenderMessageAsync(MarkdownDocumentTab documentTab, string title, string message) {
    if(documentTab.WebView.CoreWebView2 is null) {
      return;
    }

    documentTab.SourceText = $"{title}{Environment.NewLine}{Environment.NewLine}{message}";
    documentTab.SourceTextBox.Text = documentTab.SourceText;
    documentTab.OutlineItems = [];
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

    documentTab.LayoutDocument.Title = title;
    documentTab.WebView.NavigateToString(html);
    documentTab.RequiresActivationRender = false;
    UpdateWindowForActiveDocument();

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

      const countLines = (value) => ((value || '').match(/\n/g) || []).length;
      const withSourceLine = (html, lineNumber) => {
        if (!lineNumber) {
          return html;
        }

        return html.replace(/<([a-z0-9-]+)/i, `<$1 data-source-line="${lineNumber}"`);
      };
      const annotateTopLevelSourceLines = (tokens) => {
        let currentLine = 1;
        for (const token of tokens) {
          token._mdvSourceLine = currentLine;
          currentLine += countLines(token.raw);
        }
      };

      const renderer = new marked.Renderer();
      const defaultCodeRenderer = renderer.code;
      const defaultParagraphRenderer = renderer.paragraph;
      const defaultHeadingRenderer = renderer.heading;
      const defaultBlockquoteRenderer = renderer.blockquote;
      const defaultListRenderer = renderer.list;
      const defaultListItemRenderer = renderer.listitem;
      const defaultTableRenderer = renderer.table;
      const defaultHtmlRenderer = renderer.html;
      const defaultHrRenderer = renderer.hr;

      renderer.code = function(codeInfo) {
        const lang = (codeInfo.lang || '').toLowerCase();
        const text = codeInfo.text || '';

        if (lang === 'mermaid') {
          const encodedMermaid = text
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;');
          return withSourceLine('<pre class="language-mermaid">' + encodedMermaid + '</pre>', codeInfo._mdvSourceLine);
        }

        const encoded = text
          .replaceAll('&', '&amp;')
          .replaceAll('<', '&lt;')
          .replaceAll('>', '&gt;');
        const className = lang ? `language-${lang}` : 'language-none';
        return withSourceLine(`<pre class="line-numbers"><code class="${className}">${encoded}</code></pre>`, codeInfo._mdvSourceLine);
      };
      renderer.paragraph = function(token) {
        return withSourceLine(defaultParagraphRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.heading = function(token) {
        return withSourceLine(defaultHeadingRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.blockquote = function(token) {
        return withSourceLine(defaultBlockquoteRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.list = function(token) {
        return withSourceLine(defaultListRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.listitem = function(token) {
        return withSourceLine(defaultListItemRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.table = function(token) {
        return withSourceLine(defaultTableRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.html = function(token) {
        return withSourceLine(defaultHtmlRenderer.call(this, token), token._mdvSourceLine);
      };
      renderer.hr = function(token) {
        return withSourceLine(defaultHrRenderer.call(this, token), token._mdvSourceLine);
      };

      marked.setOptions({
        breaks: true,
        renderer
      });
      const tokens = marked.lexer(markdown);
      annotateTopLevelSourceLines(tokens);
      const html = marked.parser(tokens);
      const renderedHtml = isUnsafeHtmlEnabled
        ? html
        : DOMPurify.sanitize(html, {
            USE_PROFILES: { html: true },
            ALLOW_DATA_ATTR: false,
            ADD_ATTR: ['class', 'target', 'rel', 'aria-hidden', 'data-source-line']
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

  private void ConfigureVirtualHostMapping(WebView2 webView, string folderPath) {
    if(webView.CoreWebView2 is null) {
      return;
    }

    var fullFolderPath = Path.GetFullPath(folderPath);
    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
      VirtualHostName,
      fullFolderPath,
      CoreWebView2HostResourceAccessKind.Allow);
  }

  private void ConfigureAssetHostMapping(WebView2 webView) {
    if(webView.CoreWebView2 is null) {
      return;
    }

    var assetsFolderPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
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
      settings.DirectoryFileSortMode = currentDirectoryFileSortMode.ToString();
    });
  }

  private ListBoxItem? TryGetHistoryListBoxItemFromOriginalSource(DependencyObject? originalSource) {
    if(originalSource is null) {
      return null;
    }

    try {
      return ItemsControl.ContainerFromElement(HistoryListBox!, originalSource) as ListBoxItem;
    } catch {
      return null;
    }
  }

  internal void ApplySettings(AppSettings settings) {
    suppressHistorySelectionChanged = true;

    try {
      editorPath = EditorPathResolver.Resolve(settings.EditorPath);
      currentDirectoryFileSortMode = ParseDirectoryFileSortMode(settings.DirectoryFileSortMode);
      ApplyHistoryPaneVisibility(settings.IsHistoryPaneVisible);
      UpdateHistoryToggleState();
      UnsafeHtmlToggleButton!.IsChecked = isUnsafeHtmlEnabled;
      UpdateUnsafeHtmlToggleAppearance();
      UpdateDirectoryFileSortMenuChecks();

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
    var historyAnchorable = GetHistoryAnchorable();
    if(historyAnchorable is null) {
      HistoryToggleButton!.IsChecked = isVisible;
      return;
    }

    if(isVisible) {
      historyAnchorable.Show();
      historyAnchorable.IsSelected = true;
    } else {
      historyAnchorable.Hide();
    }

    UpdateHistoryToggleState();
  }

  private void UpdateOutlineLayoutButtonAppearance() {
    var outlineAnchorable = GetOutlineAnchorable();
    var isVisible = outlineAnchorable?.IsVisible == true || outlineAnchorable?.IsAutoHidden == true;
    OutlineLayoutIconTextBlock!.Foreground = isVisible
      ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
      : new SolidColorBrush(Color.FromRgb(140, 140, 140));
    OutlineLayoutButton!.ToolTip = isVisible
      ? "アウトラインを表示してアクティブ化"
      : "アウトラインを再表示";
  }

  private void ShowOutlinePane() {
    var outlineAnchorable = GetOutlineAnchorable();
    if(outlineAnchorable is null) {
      return;
    }

    outlineAnchorable.Show();
    outlineAnchorable.IsSelected = true;
    outlineAnchorable.IsActive = true;
    UpdateOutlineLayoutButtonAppearance();
  }

  private void UpdateHistoryToggleState() {
    var historyAnchorable = GetHistoryAnchorable();
    var isVisible = historyAnchorable?.IsVisible == true || historyAnchorable?.IsAutoHidden == true;
    HistoryToggleButton!.IsChecked = isVisible;
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

    foreach(var documentTab in openDocuments.Values) {
      documentTab.WebView.Visibility = isSourceMode ? Visibility.Collapsed : Visibility.Visible;
      documentTab.SourceTextBox.Visibility = isSourceMode ? Visibility.Visible : Visibility.Collapsed;
      documentTab.SourceTextBox.Text = documentTab.SourceText;
    }

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

  private List<OutlineItem> BuildOutlineItems(string markdownText) {
    var items = new List<OutlineItem>();
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
      items.Add(new OutlineItem(title, level, lineIndex, slug));
    }

    return items;
  }

  private async Task ScrollPreviewToHeadingAsync(string slug) {
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument?.WebView.CoreWebView2 is null) {
      return;
    }

    var slugJson = JsonSerializer.Serialize(slug);
    await activeDocument.WebView.CoreWebView2.ExecuteScriptAsync($$"""
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
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument is null) {
      return;
    }

    var lineIndex = Math.Max(0, outline.LineNumber - 1);
    if(lineIndex >= activeDocument.SourceTextBox.LineCount) {
      return;
    }

    var characterIndex = activeDocument.SourceTextBox.GetCharacterIndexFromLineIndex(lineIndex);
    if(characterIndex < 0) {
      return;
    }

    activeDocument.SourceTextBox.Focus();
    activeDocument.SourceTextBox.CaretIndex = characterIndex;
    activeDocument.SourceTextBox.ScrollToLine(lineIndex);
  }

  private async Task OpenGoToLineDialogAsync() {
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument is null) {
      return;
    }

    var lineCount = GetDocumentLineCount(activeDocument);
    var currentLineNumber = GetCurrentLineNumber(activeDocument);
    var dialog = new GoToLineWindow(lineCount, currentLineNumber) {
      Owner = this
    };

    if(dialog.ShowDialog() != true || dialog.SelectedLineNumber is not int lineNumber) {
      return;
    }

    await GoToDocumentLineAsync(activeDocument, lineNumber);
  }

  private async Task GoToDocumentLineAsync(MarkdownDocumentTab documentTab, int lineNumber) {
    var lineIndex = Math.Clamp(lineNumber - 1, 0, Math.Max(0, GetDocumentLineCount(documentTab) - 1));

    ActivateDocumentTab(documentTab, focusDisplay: true);

    if(currentViewMode == ViewMode.Preview) {
      await ScrollPreviewToLineAsync(documentTab, lineNumber);
      return;
    }

    var characterIndex = documentTab.SourceTextBox.GetCharacterIndexFromLineIndex(lineIndex);
    if(characterIndex < 0) {
      return;
    }

    documentTab.SourceTextBox.Focus();
    documentTab.SourceTextBox.CaretIndex = characterIndex;
    documentTab.SourceTextBox.SelectionLength = 0;
    documentTab.SourceTextBox.ScrollToLine(lineIndex);
  }

  private static async Task ScrollPreviewToLineAsync(MarkdownDocumentTab documentTab, int lineNumber) {
    if(documentTab.WebView.CoreWebView2 is null) {
      return;
    }

    await documentTab.WebView.CoreWebView2.ExecuteScriptAsync($$"""
(() => {
  const targetLine = {{lineNumber}};
  const candidates = Array.from(document.querySelectorAll('[data-source-line]'))
    .map(element => ({
      element,
      line: Number.parseInt(element.getAttribute('data-source-line') || '', 10)
    }))
    .filter(item => Number.isFinite(item.line));

  if (candidates.length === 0) {
    return false;
  }

  let target = candidates[0];
  for (const candidate of candidates) {
    if (candidate.line <= targetLine) {
      target = candidate;
      continue;
    }

    if (target.line <= targetLine) {
      break;
    }

    if (candidate.line < target.line) {
      target = candidate;
    }
  }

  target.element.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return true;
})()
""");
  }

  private static int GetDocumentLineCount(MarkdownDocumentTab documentTab) {
    if(string.IsNullOrEmpty(documentTab.SourceText)) {
      return 1;
    }

    var lineCount = 1;
    foreach(var character in documentTab.SourceText) {
      if(character == '\n') {
        lineCount++;
      }
    }

    return lineCount;
  }

  private static int GetCurrentLineNumber(MarkdownDocumentTab documentTab) {
    if(documentTab.SourceTextBox.LineCount <= 0) {
      return 1;
    }

    var currentLineIndex = documentTab.SourceTextBox.GetLineIndexFromCharacterIndex(documentTab.SourceTextBox.CaretIndex);
    return Math.Clamp(currentLineIndex + 1, 1, documentTab.SourceTextBox.LineCount);
  }

  private MarkdownDocumentTab? GetOrCreateDocumentTab(string fullPath) {
    if(openDocuments.TryGetValue(fullPath, out var existingDocument)) {
      DebugLog(existingDocument, "GetOrCreateDocumentTab found existing document.");
      return existingDocument;
    }

    if(webViewEnvironment is null) {
      Debug.WriteLine($"[MarkdownViewer] GetOrCreateDocumentTab aborted because webViewEnvironment is null. Path={Path.GetFileName(fullPath)}");
      return null;
    }

    var documentView = CreateDocumentView();
    var document = new LayoutDocument {
      Title = Path.GetFileName(fullPath),
      ContentId = fullPath,
      Content = documentView.Root
    };

    var documentTab = new MarkdownDocumentTab(fullPath, document, documentView.Root, documentView.WebView, documentView.SourceTextBox);
    openDocuments[fullPath] = documentTab;
    DebugLog(documentTab, "GetOrCreateDocumentTab created new document tab.");
    GetDocumentPane()?.Children.Add(document);
    DebugLog(documentTab, "GetOrCreateDocumentTab added document to pane.");
    return documentTab;
  }

  private async Task<bool> EnsureDocumentInitializedAsync(MarkdownDocumentTab documentTab) {
    if(documentTab.IsInitialized) {
      DebugLog(documentTab, "EnsureDocumentInitializedAsync skipped because already initialized.");
      return true;
    }

    if(documentTab.InitializationTask is not null) {
      DebugLog(documentTab, "EnsureDocumentInitializedAsync awaiting existing initialization task.");
      return await documentTab.InitializationTask;
    }

    if(webViewEnvironment is null) {
      DebugLog(documentTab, "EnsureDocumentInitializedAsync aborted because webViewEnvironment is null.");
      return false;
    }

    documentTab.InitializationTask = EnsureDocumentInitializedCoreAsync(documentTab);
    try {
      return await documentTab.InitializationTask;
    } finally {
      documentTab.InitializationTask = null;
    }
  }

  private async Task<bool> EnsureDocumentInitializedCoreAsync(MarkdownDocumentTab documentTab) {
    try {
      DebugLog(documentTab, "Before EnsureCoreWebView2Async.");
      await documentTab.WebView.EnsureCoreWebView2Async(webViewEnvironment);
      DebugLog(documentTab, $"After EnsureCoreWebView2Async. CoreWebView2Null={documentTab.WebView.CoreWebView2 is null}");
    } catch(Exception ex) {
      DebugLog(documentTab, $"EnsureCoreWebView2Async failed: {ex}");
      MessageBox.Show($"WebView2 の初期化に失敗しました。{Environment.NewLine}{ex.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
      return false;
    }

    RegisterWebViewEvents(documentTab.WebView);
    documentTab.IsInitialized = true;
    ApplyViewMode();
    return true;
  }

  private void RegisterWebViewEvents(WebView2 webView) {
    if(webView.CoreWebView2 is null) {
      return;
    }

    ConfigureAssetHostMapping(webView);
    webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
    webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
    webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
  }

  private static DocumentView CreateDocumentView() {
    var root = new Grid();

    var webView = new WebView2();
    root.Children.Add(webView);

    var sourceTextBox = new TextBox {
      Visibility = Visibility.Collapsed,
      IsReadOnly = true,
      AcceptsReturn = true,
      AcceptsTab = true,
      TextWrapping = TextWrapping.NoWrap,
      HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
      VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
      BorderThickness = new Thickness(0),
      Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
      Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
      Padding = new Thickness(16),
      FontFamily = new FontFamily("Consolas"),
      FontSize = 13
    };
    root.Children.Add(sourceTextBox);

    return new DocumentView(root, webView, sourceTextBox);
  }

  private void ActivateDocumentTab(MarkdownDocumentTab documentTab, bool focusDisplay = false) {
    ApplyDocumentActivation(documentTab, focusDisplay);
    _ = Dispatcher.BeginInvoke(
      () => ApplyDocumentActivation(documentTab, focusDisplay),
      System.Windows.Threading.DispatcherPriority.Background);
  }

  private void ApplyDocumentActivation(MarkdownDocumentTab documentTab, bool focusDisplay) {
    var documentPane = documentTab.LayoutDocument.Parent as LayoutDocumentPane ?? GetDocumentPane();
    if(documentPane is not null) {
      var documentIndex = documentPane.Children.IndexOf(documentTab.LayoutDocument);
      if(documentIndex >= 0) {
        documentPane.SelectedContentIndex = documentIndex;
      }
    }

    documentTab.LayoutDocument.IsSelected = true;
    documentTab.LayoutDocument.IsActive = true;
    MainDockingManager.ActiveContent = documentTab.Root;
    UpdateWindowForActiveDocument();

    if(focusDisplay) {
      FocusDocumentDisplay(documentTab);
    }
  }

  private void FocusDocumentDisplay(MarkdownDocumentTab documentTab) {
    if(currentViewMode == ViewMode.Source) {
      documentTab.SourceTextBox.Focus();
      Keyboard.Focus(documentTab.SourceTextBox);
      return;
    }

    documentTab.WebView.Focus();
    Keyboard.Focus(documentTab.WebView);
  }

  private MarkdownDocumentTab? GetActiveDocumentTab() {
    if(MainDockingManager.ActiveContent is not FrameworkElement activeContent) {
      return GetFocusedOrSelectedDocumentTab();
    }

    return openDocuments.Values.FirstOrDefault(documentTab => ReferenceEquals(documentTab.Root, activeContent))
      ?? GetFocusedOrSelectedDocumentTab();
  }

  private MarkdownDocumentTab? GetFocusedOrSelectedDocumentTab() {
    var activeLayoutDocument = openDocuments.Values
      .Select(documentTab => documentTab.LayoutDocument)
      .FirstOrDefault(layoutDocument => layoutDocument.IsActive || layoutDocument.IsSelected);

    if(activeLayoutDocument is null) {
      return null;
    }

    return openDocuments.Values.FirstOrDefault(documentTab => ReferenceEquals(documentTab.LayoutDocument, activeLayoutDocument));
  }

  private void UpdateWindowForActiveDocument() {
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument is null) {
      directoryFileItems.Clear();
      currentDirectoryItemsPath = null;
      outlineItems.Clear();
      PathTextBlock.Text = string.Empty;
      PathTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68));
      suppressHistorySelectionChanged = true;
      HistoryListBox!.SelectedItem = null;
      suppressHistorySelectionChanged = false;
      UpdateOutlineLayoutButtonAppearance();
      return;
    }

    RefreshDirectoryFileItems(activeDocument.FullPath);
    outlineItems.Clear();
    foreach(var item in activeDocument.OutlineItems) {
      outlineItems.Add(item);
    }

    PathTextBlock.Text = Path.GetFileName(activeDocument.FullPath);
    PathTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68));
    SelectHistoryItem(activeDocument.FullPath);
    UpdateOutlineLayoutButtonAppearance();
  }

  private async void MainDockingManager_ActiveContentChanged(object sender, EventArgs e) {
    var activeDocument = GetActiveDocumentTab();
    if(activeDocument is not null) {
      DebugLog(activeDocument, $"ActiveContentChanged. RequiresActivationRender={activeDocument.RequiresActivationRender}");
    } else {
      Debug.WriteLine("[MarkdownViewer] ActiveContentChanged. No active document.");
    }

    if(activeDocument is not null && !activeDocument.IsInitialized) {
      if(!await EnsureDocumentInitializedAsync(activeDocument)) {
        UpdateWindowForActiveDocument();
        return;
      }
    }

    if(activeDocument?.RequiresActivationRender == true) {
      activeDocument.RequiresActivationRender = false;
      await RenderMarkdownFileAsync(activeDocument);
    }

    UpdateWindowForActiveDocument();
  }

  private void MainDockingManager_DocumentClosed(object sender, EventArgs e) {
    var closedDocuments = openDocuments
      .Where(entry => GetDocumentPane()?.Children.Contains(entry.Value.LayoutDocument) != true)
      .Select(entry => entry.Key)
      .ToArray();

    foreach(var path in closedDocuments) {
      openDocuments.Remove(path);
    }

    UpdateWindowForActiveDocument();
  }

  private LayoutAnchorable? GetOutlineAnchorable() {
    return MainDockingManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(item => string.Equals(item.ContentId, "OutlinePane", StringComparison.Ordinal));
  }

  private LayoutAnchorable? GetFilesAnchorable() {
    return MainDockingManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(item => string.Equals(item.ContentId, "FilesPane", StringComparison.Ordinal));
  }

  private LayoutAnchorable? GetHistoryAnchorable() {
    return MainDockingManager.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(item => string.Equals(item.ContentId, "HistoryPane", StringComparison.Ordinal));
  }

  private LayoutDocumentPane? GetDocumentPane() {
    return MainDockingManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
  }

  private async Task<bool> TryRestoreDockLayoutAsync(string? dockLayoutXml) {
    if(string.IsNullOrWhiteSpace(dockLayoutXml)) {
      return false;
    }

    DetachElementFromParent(HistoryListBox);
    DetachElementFromParent(FilesPaneContentRoot);
    DetachElementFromParent(OutlineListBox);

    foreach(var document in openDocuments.Values) {
      DetachElementFromParent(document.Root);
    }

    openDocuments.Clear();

    try {
      var serializer = new XmlLayoutSerializer(MainDockingManager);
      serializer.LayoutSerializationCallback += LayoutSerializer_LayoutSerializationCallback;

      using var reader = new StringReader(dockLayoutXml);
      serializer.Deserialize(reader);
    } catch {
      openDocuments.Clear();
      settingRepository.Update(settings => {
        settings.DockLayoutXml = string.Empty;
        settings.DockLayoutVersion = CurrentDockLayoutVersion;
      });
      return false;
    }

    var loadedDocuments = openDocuments.Values.ToArray();
    var activeDocument = GetRestoredActiveDocumentTab(loadedDocuments);
    if(activeDocument is not null) {
      if(!await EnsureDocumentInitializedAsync(activeDocument)) {
        return false;
      }

      await RenderMarkdownFileAsync(activeDocument);
    }

    foreach(var documentTab in loadedDocuments) {
      if(ReferenceEquals(documentTab, activeDocument)) {
        continue;
      }

      documentTab.RequiresActivationRender = true;
    }

    var outlineAnchorable = GetOutlineAnchorable();
    if(outlineAnchorable is not null) {
      outlineAnchorable.PropertyChanged += OutlineAnchorable_PropertyChanged;
    }

    var historyAnchorable = GetHistoryAnchorable();
    if(historyAnchorable is not null) {
      historyAnchorable.PropertyChanged += HistoryAnchorable_PropertyChanged;
    }

    ApplyViewMode();
    if(activeDocument is not null) {
      ActivateDocumentTab(activeDocument);
      _ = Dispatcher.BeginInvoke(
        () => ActivateDocumentTab(activeDocument, focusDisplay: true),
        System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    UpdateHistoryToggleState();
    UpdateWindowForActiveDocument();
    return loadedDocuments.Length > 0;
  }

  private void LayoutSerializer_LayoutSerializationCallback(object? sender, LayoutSerializationCallbackEventArgs e) {
    switch(e.Model) {
      case LayoutAnchorable anchorable when string.Equals(anchorable.ContentId, "HistoryPane", StringComparison.Ordinal):
        anchorable.PropertyChanged -= HistoryAnchorable_PropertyChanged;
        anchorable.PropertyChanged += HistoryAnchorable_PropertyChanged;
        DetachElementFromParent(HistoryListBox);
        e.Content = HistoryListBox;
        break;
      case LayoutAnchorable anchorable when string.Equals(anchorable.ContentId, "FilesPane", StringComparison.Ordinal):
        // FilesPane restores the whole root so the sort header stays attached with the list.
        DetachElementFromParent(FilesPaneContentRoot);
        e.Content = FilesPaneContentRoot;
        break;
      case LayoutAnchorable anchorable when string.Equals(anchorable.ContentId, "OutlinePane", StringComparison.Ordinal):
        anchorable.PropertyChanged -= OutlineAnchorable_PropertyChanged;
        anchorable.PropertyChanged += OutlineAnchorable_PropertyChanged;
        DetachElementFromParent(OutlineListBox);
        e.Content = OutlineListBox;
        break;
      case LayoutDocument layoutDocument when !string.IsNullOrWhiteSpace(layoutDocument.ContentId):
        var fullPath = Path.GetFullPath(layoutDocument.ContentId);
        if(!File.Exists(fullPath)) {
          e.Cancel = true;
          return;
        }

        if(openDocuments.TryGetValue(fullPath, out var existingDocument)) {
          existingDocument.LayoutDocument = layoutDocument;
          DetachElementFromParent(existingDocument.Root);
          layoutDocument.Content = existingDocument.Root;
          e.Content = existingDocument.Root;
          return;
        }

        var documentView = CreateDocumentView();
        var documentTab = new MarkdownDocumentTab(fullPath, layoutDocument, documentView.Root, documentView.WebView, documentView.SourceTextBox);
        openDocuments[fullPath] = documentTab;
        e.Content = documentView.Root;
        break;
    }
  }

  private void PersistWindowLayout() {
    var dockLayoutXml = CaptureDockLayout();
    var localHistory = historyItems.Select(item => item.FullPath).ToList();

    settingRepository.Update(settings => {
      settings.History = localHistory;
      settings.IsHistoryPaneVisible = HistoryToggleButton!.IsChecked == true;
      settings.DockLayoutXml = dockLayoutXml;
      settings.DockLayoutVersion = CurrentDockLayoutVersion;
      settings.DirectoryFileSortMode = currentDirectoryFileSortMode.ToString();
    });
  }

  private static void DetachElementFromParent(FrameworkElement? element) {
    if(element is null) {
      return;
    }

    var parent = LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element);
    switch(parent) {
      case Panel panel:
        panel.Children.Remove(element);
        break;
      case Decorator decorator when ReferenceEquals(decorator.Child, element):
        decorator.Child = null;
        break;
      case ContentPresenter presenter when ReferenceEquals(presenter.Content, element):
        presenter.Content = null;
        break;
      case ContentControl control when ReferenceEquals(control.Content, element):
        control.Content = null;
        break;
    }
  }

  private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
    if(sender is not CoreWebView2 coreWebView) {
      Debug.WriteLine($"[MarkdownViewer] NavigationCompleted. SenderType={sender?.GetType().FullName ?? "null"} Success={e.IsSuccess} Status={e.WebErrorStatus}");
      return;
    }

    var documentTab = openDocuments.Values.FirstOrDefault(tab => ReferenceEquals(tab.WebView.CoreWebView2, coreWebView));
    if(documentTab is null) {
      Debug.WriteLine($"[MarkdownViewer] NavigationCompleted. Untracked WebView. Success={e.IsSuccess} Status={e.WebErrorStatus}");
      return;
    }

    DebugLog(documentTab, $"NavigationCompleted. Success={e.IsSuccess} Status={e.WebErrorStatus}");
  }

  private static void DebugLog(MarkdownDocumentTab documentTab, string message) {
    Debug.WriteLine($"[MarkdownViewer] {Path.GetFileName(documentTab.FullPath)} | {message}");
  }

  private static MarkdownDocumentTab? GetRestoredActiveDocumentTab(IReadOnlyList<MarkdownDocumentTab> loadedDocuments) {
    return loadedDocuments.FirstOrDefault(document => document.LayoutDocument.IsActive)
      ?? loadedDocuments.FirstOrDefault(document => document.LayoutDocument.IsSelected)
      ?? loadedDocuments.FirstOrDefault();
  }

  private string CaptureDockLayout() {
    try {
      var serializer = new XmlLayoutSerializer(MainDockingManager);
      using var writer = new StringWriter();
      serializer.Serialize(writer);
      return writer.ToString();
    } catch {
      return string.Empty;
    }
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

  private void RefreshDirectoryFileItems(string fullPath) {
    var directoryPath = Path.GetDirectoryName(fullPath);
    if(string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) {
      directoryFileItems.Clear();
      currentDirectoryItemsPath = null;
      return;
    }

    IReadOnlyList<string> orderedEntries;
    try {
      var markdownEntries = Directory.EnumerateFileSystemEntries(directoryPath)
        .Where(path => File.Exists(path) && IsMarkdownPath(path))
        .Select(Path.GetFullPath);

      orderedEntries = currentDirectoryFileSortMode switch {
        DirectoryFileSortMode.FileNameAscending => markdownEntries
          .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
          .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
          .ToArray(),
        _ => markdownEntries
          .OrderByDescending(GetFileLastWriteTimeUtcSafe)
          .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
          .ToArray()
      };
    } catch {
      return;
    }

    var activeFullPath = Path.GetFullPath(fullPath);
    var canReuseItems = string.Equals(currentDirectoryItemsPath, directoryPath, StringComparison.OrdinalIgnoreCase)
      && orderedEntries.Count == directoryFileItems.Count
      && orderedEntries.SequenceEqual(directoryFileItems.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);

    if(canReuseItems) {
      foreach(var item in directoryFileItems) {
        item.IsActiveDocument = string.Equals(item.FullPath, activeFullPath, StringComparison.OrdinalIgnoreCase);
      }

      return;
    }

    directoryFileItems.Clear();
    currentDirectoryItemsPath = directoryPath;

    foreach(var entry in orderedEntries) {
      directoryFileItems.Add(new DirectoryFileItem(entry, activeFullPath));
    }
  }

  private void SetDirectoryFileSortMode(DirectoryFileSortMode sortMode) {
    if(currentDirectoryFileSortMode == sortMode) {
      UpdateDirectoryFileSortMenuChecks();
      return;
    }

    currentDirectoryFileSortMode = sortMode;
    currentDirectoryItemsPath = null;
    UpdateDirectoryFileSortMenuChecks();
    SaveHistory();
    if(GetActiveDocumentTab() is { } activeDocument) {
      RefreshDirectoryFileItems(activeDocument.FullPath);
    }
  }

  private void UpdateDirectoryFileSortMenuChecks() {
    if(FilesSortComboBox is not null) {
      suppressDirectoryFileSortSelectionChanged = true;
      FilesSortComboBox.SelectedIndex = currentDirectoryFileSortMode == DirectoryFileSortMode.LastWriteTimeDescending ? 0 : 1;
      suppressDirectoryFileSortSelectionChanged = false;
    }
  }

  private static DirectoryFileSortMode ParseDirectoryFileSortMode(string? value) {
    return Enum.TryParse<DirectoryFileSortMode>(value, ignoreCase: true, out var sortMode)
      ? sortMode
      : DirectoryFileSortMode.LastWriteTimeDescending;
  }

  private static DateTime GetFileLastWriteTimeUtcSafe(string fullPath) {
    try {
      return File.GetLastWriteTimeUtc(fullPath);
    } catch {
      return DateTime.MinValue;
    }
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

  private sealed class DirectoryFileItem : INotifyPropertyChanged {
    private bool isActiveDocument;

    public DirectoryFileItem(string fullPath, string activeDocumentPath) {
      FullPath = Path.GetFullPath(fullPath);
      IsMarkdownFile = IsMarkdownPath(fullPath);
      isActiveDocument = string.Equals(FullPath, Path.GetFullPath(activeDocumentPath), StringComparison.OrdinalIgnoreCase);
      DisplayName = Path.GetFileName(fullPath);
    }

    public string DisplayName { get; }

    public string FullPath { get; }

    public bool IsMarkdownFile { get; }

    public bool IsActiveDocument {
      get => isActiveDocument;
      set {
        if(isActiveDocument == value) {
          return;
        }

        isActiveDocument = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActiveDocument)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveIndicatorVisibility)));
      }
    }

    public Visibility ActiveIndicatorVisibility => IsActiveDocument ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
  }

  private static bool IsMarkdownPath(string fullPath) {
    var extension = Path.GetExtension(fullPath);
    return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
      || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
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

  private sealed class MarkdownDocumentTab {
    public MarkdownDocumentTab(string fullPath, LayoutDocument layoutDocument, Grid root, WebView2 webView, TextBox sourceTextBox) {
      FullPath = fullPath;
      LayoutDocument = layoutDocument;
      Root = root;
      WebView = webView;
      SourceTextBox = sourceTextBox;
    }

    public string FullPath { get; }

    public LayoutDocument LayoutDocument { get; set; }

    public Grid Root { get; }

    public WebView2 WebView { get; }

    public TextBox SourceTextBox { get; }

    public string SourceText { get; set; } = string.Empty;

    public List<OutlineItem> OutlineItems { get; set; } = [];

    public bool IsInitialized { get; set; }

    public bool RequiresActivationRender { get; set; }

    public Task<bool>? InitializationTask { get; set; }
  }

  private sealed class DocumentView {
    public DocumentView(Grid root, WebView2 webView, TextBox sourceTextBox) {
      Root = root;
      WebView = webView;
      SourceTextBox = sourceTextBox;
    }

    public Grid Root { get; }

    public WebView2 WebView { get; }

    public TextBox SourceTextBox { get; }
  }
}

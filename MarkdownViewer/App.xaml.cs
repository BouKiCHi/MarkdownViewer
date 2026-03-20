using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MarkdownViewer;

public partial class App : Application {
  private const string SingleInstanceMutexName = @"Local\MarkdownViewer.SingleInstance";
  private const string SingleInstancePipeName = "MarkdownViewer.SingleInstance";

  private Mutex? singleInstanceMutex;
  private CancellationTokenSource? activationListenerCancellation;
  private Task? activationListenerTask;
  private WeakReference<MainWindow>? lastActivatedWindow;

  protected override void OnStartup(StartupEventArgs e) {
    singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var createdNew);
    if (!createdNew) {
      ForwardActivationToRunningInstance(e.Args.FirstOrDefault());
      singleInstanceMutex.Dispose();
      singleInstanceMutex = null;
      Shutdown();
      return;
    }

    DispatcherUnhandledException += OnUnhandledException;

    StartPrimaryWindow(e, e.Args.FirstOrDefault());
  }

  private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
    var ex = e.Exception;
    MessageBox.Show(ex.ToString());
  }

  private void StartPrimaryWindow(StartupEventArgs e, string? initialFilePath) {
    var window = new MainWindow(initialFilePath);
    RegisterWindow(window);
    StartActivationListener(window);

    base.OnStartup(e);
    window.Show();
  }

  protected override void OnExit(ExitEventArgs e) {
    activationListenerCancellation?.Cancel();

    try {
      activationListenerTask?.Wait(TimeSpan.FromSeconds(1));
    } catch {
      // App is shutting down; ignore listener teardown failures.
    }

    activationListenerCancellation?.Dispose();

    if (singleInstanceMutex is not null) {
      singleInstanceMutex.ReleaseMutex();
      singleInstanceMutex.Dispose();
      singleInstanceMutex = null;
    }

    base.OnExit(e);
  }

  private void StartActivationListener(MainWindow window) {
    activationListenerCancellation = new CancellationTokenSource();
    activationListenerTask = Task.Run(() => ListenForActivationRequestsAsync(window, activationListenerCancellation.Token));
  }

  private static async Task ListenForActivationRequestsAsync(MainWindow window, CancellationToken cancellationToken) {
    while (!cancellationToken.IsCancellationRequested) {
      try {
        using var server = new NamedPipeServerStream(
          SingleInstancePipeName,
          PipeDirection.In,
          1,
          PipeTransmissionMode.Byte,
          PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(server);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload)) {
          continue;
        }

        var request = JsonSerializer.Deserialize<ActivationRequest>(payload);
        await window.Dispatcher.InvokeAsync(() => {
          var app = Current as App;
          app?.HandleActivationRequest(request?.FilePath);
        });
      } catch (OperationCanceledException) {
        break;
      } catch {
        await Task.Delay(250);
      }
    }
  }

  private static void ForwardActivationToRunningInstance(string? initialFilePath) {
    var payload = JsonSerializer.Serialize(new ActivationRequest(initialFilePath));

    for (var attempt = 0; attempt < 5; attempt++) {
      try {
        using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
        client.Connect(timeout: 300);

        using var writer = new StreamWriter(client) { AutoFlush = true };
        writer.Write(payload);
        return;
      } catch {
        Thread.Sleep(150);
      }
    }
  }

  internal void RegisterWindow(MainWindow window) {
    window.Activated += Window_Activated;
    window.Closed += Window_Closed;
    lastActivatedWindow = new WeakReference<MainWindow>(window);
    MainWindow ??= window;
  }

  private void Window_Activated(object? sender, EventArgs e) {
    if (sender is MainWindow window) {
      lastActivatedWindow = new WeakReference<MainWindow>(window);
    }
  }

  private void Window_Closed(object? sender, EventArgs e) {
    if (sender is not MainWindow closedWindow) {
      return;
    }

    closedWindow.Activated -= Window_Activated;
    closedWindow.Closed -= Window_Closed;

    if (ReferenceEquals(MainWindow, closedWindow)) {
      MainWindow = GetBestTargetWindow(excludeWindow: closedWindow);
    }

    if (TryGetLastActivatedWindow(out var activeWindow) && ReferenceEquals(activeWindow, closedWindow)) {
      lastActivatedWindow = null;
    }
  }

  private void HandleActivationRequest(string? filePath) {
    var targetWindow = GetBestTargetWindow();
    if (targetWindow is null) {
      var window = new MainWindow(filePath);
      RegisterWindow(window);
      window.Show();
      return;
    }

    targetWindow.HandleActivationRequest(filePath);
  }

  private MainWindow? GetBestTargetWindow(MainWindow? excludeWindow = null) {
    if (TryGetLastActivatedWindow(out var activeWindow) && !ReferenceEquals(activeWindow, excludeWindow)) {
      return activeWindow;
    }

    return Windows
      .OfType<MainWindow>()
      .FirstOrDefault(window => !ReferenceEquals(window, excludeWindow));
  }

  private bool TryGetLastActivatedWindow(out MainWindow? window) {
    window = null;
    return lastActivatedWindow is not null
      && lastActivatedWindow.TryGetTarget(out window)
      && window.IsLoaded;
  }

  internal void RefreshAllWindowsFromSettings() {
    var settings = new SettingRepository().Load();
    foreach (var window in Windows.OfType<MainWindow>()) {
      window.ApplySettings(settings);
    }
  }

  private sealed record ActivationRequest(string? FilePath);
}

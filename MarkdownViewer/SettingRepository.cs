using System.IO;
using System.Text.Json;
using System.Threading;

namespace MarkdownViewer;

public sealed class SettingRepository {
  private const string SettingsFileName = "settings.json";
  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true
  };
  private static readonly Mutex SettingsMutex = new(false, @"Local\MarkdownViewer.Settings");

  private readonly string settingsFilePath;
  private readonly string fallbackSettingsFilePath;

  public SettingRepository() {
    settingsFilePath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
    var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    fallbackSettingsFilePath = Path.Combine(localAppDataPath, "MarkdownViewer", SettingsFileName);
  }

  public AppSettings Load() {
    return WithLock(LoadCore);
  }

  public void Save(AppSettings settings) {
    WithLock(() => SaveCore(settings));
  }

  public void Update(Action<AppSettings> updateAction) {
    WithLock(() => {
      var settings = LoadCore();
      updateAction(settings);
      SaveCore(settings);
    });
  }

  private AppSettings LoadCore() {
    if (TryReadSettings(settingsFilePath, out var primarySettings)) {
      return primarySettings;
    }

    if (TryReadSettings(fallbackSettingsFilePath, out var fallbackSettings)) {
      return fallbackSettings;
    }

    return new AppSettings();
  }

  private void SaveCore(AppSettings settings) {
    if (TryWriteSettings(settingsFilePath, settings)) {
      return;
    }

    TryWriteSettings(fallbackSettingsFilePath, settings);
  }

  private static bool TryReadSettings(string path, out AppSettings settings) {
    settings = new AppSettings();

    if (!File.Exists(path)) {
      return false;
    }

    try {
      var json = File.ReadAllText(path);
      settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
      return true;
    } catch {
      return false;
    }
  }

  private static bool TryWriteSettings(string path, AppSettings settings) {
    try {
      var directoryPath = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(directoryPath)) {
        Directory.CreateDirectory(directoryPath);
      }

      var json = JsonSerializer.Serialize(settings, JsonOptions);
      File.WriteAllText(path, json);
      return true;
    } catch {
      return false;
    }
  }

  private static T WithLock<T>(Func<T> action) {
    var hasLock = false;
    try {
      try {
        hasLock = SettingsMutex.WaitOne();
      } catch (AbandonedMutexException) {
        hasLock = true;
      }

      return action();
    } finally {
      if (hasLock) {
        SettingsMutex.ReleaseMutex();
      }
    }
  }

  private static void WithLock(Action action) {
    var hasLock = false;
    try {
      try {
        hasLock = SettingsMutex.WaitOne();
      } catch (AbandonedMutexException) {
        hasLock = true;
      }

      action();
    } finally {
      if (hasLock) {
        SettingsMutex.ReleaseMutex();
      }
    }
  }
}

public sealed class AppSettings {
  public List<string> History { get; set; } = [];
  public bool IsHistoryPaneVisible { get; set; } = true;
}

using System.IO;
using System.Linq;

namespace MarkdownViewer;

internal static class EditorPathResolver {
  internal static string Resolve(string? configuredPath) {
    if(!string.IsNullOrWhiteSpace(configuredPath)) {
      return configuredPath;
    }

    return TryFindVisualStudioCodePath() ?? "notepad.exe";
  }

  private static string? TryFindVisualStudioCodePath() {
    var pathValue = Environment.GetEnvironmentVariable("PATH");
    if(!string.IsNullOrWhiteSpace(pathValue)) {
      foreach(var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
        try {
          var candidate = Path.Combine(directory, "code.exe");
          if(File.Exists(candidate)) {
            return candidate;
          }
        } catch {
          // Ignore invalid PATH entries.
        }
      }
    }

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    foreach(var candidate in new[] {
      Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
      Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
      Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe")
    }.Where(path => !string.IsNullOrWhiteSpace(path))) {
      if(File.Exists(candidate)) {
        return candidate;
      }
    }

    return null;
  }
}

using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;

namespace MarkdownViewer;

public partial class AboutWindow : Window {
  private readonly string releasePageUrl;

  public AboutWindow(string releasePageUrl) {
    InitializeComponent();
    this.releasePageUrl = releasePageUrl;
    VersionTextBlock.Text = BuildVersionText();
  }

  private void ReleasePageHyperlink_Click(object sender, RoutedEventArgs e) {
    try {
      Process.Start(new ProcessStartInfo(releasePageUrl) { UseShellExecute = true });
    } catch(Exception ex) {
      MessageBox.Show(
        $"リリースページを開けませんでした。{Environment.NewLine}{releasePageUrl}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
        "Markdown Viewer",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private static string BuildVersionText() {
    var assembly = Assembly.GetExecutingAssembly();
    var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if(!string.IsNullOrWhiteSpace(informationalVersion)) {
      return $"Version {informationalVersion}";
    }

    var version = assembly.GetName().Version?.ToString();
    return string.IsNullOrWhiteSpace(version)
      ? "Version unknown"
      : $"Version {version}";
  }
}

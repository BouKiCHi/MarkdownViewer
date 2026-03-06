using System.Windows;

namespace MarkdownViewer;

public partial class App : Application {
  protected override void OnStartup(StartupEventArgs e) {
    base.OnStartup(e);

    var initialFile = e.Args.FirstOrDefault();
    var window = new MainWindow(initialFile);
    window.Show();
  }
}

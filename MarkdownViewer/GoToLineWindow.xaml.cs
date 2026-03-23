using System.Globalization;
using System.Windows;

namespace MarkdownViewer;

public partial class GoToLineWindow : Window {
  private readonly int maximumLineNumber;

  public int? SelectedLineNumber { get; private set; }

  public string LineCountDescription { get; }

  public GoToLineWindow(int maximumLineNumber, int? initialLineNumber = null) {
    InitializeComponent();
    this.maximumLineNumber = Math.Max(1, maximumLineNumber);
    LineCountDescription = $"1 から {this.maximumLineNumber.ToString(CultureInfo.CurrentCulture)} までの行番号を入力してください。";
    DataContext = this;
    LineNumberTextBox.Text = (initialLineNumber ?? 1).ToString(CultureInfo.CurrentCulture);
    Loaded += (_, _) => {
      LineNumberTextBox.Focus();
      LineNumberTextBox.SelectAll();
    };
  }

  private void GoButton_Click(object sender, RoutedEventArgs e) {
    if(!int.TryParse(LineNumberTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var lineNumber)
      || lineNumber < 1
      || lineNumber > maximumLineNumber) {
      MessageBox.Show(
        $"1 から {maximumLineNumber.ToString(CultureInfo.CurrentCulture)} までの行番号を入力してください。",
        Title,
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
      LineNumberTextBox.Focus();
      LineNumberTextBox.SelectAll();
      return;
    }

    SelectedLineNumber = lineNumber;
    DialogResult = true;
  }
}

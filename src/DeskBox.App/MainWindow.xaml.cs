using System.Windows;

namespace DeskBox.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnConfirm(object sender, RoutedEventArgs e) => Close();
}

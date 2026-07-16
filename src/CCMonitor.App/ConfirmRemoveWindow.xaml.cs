using System.Windows;

namespace CCMonitor.App;

public partial class ConfirmRemoveWindow : Window
{
    public ConfirmRemoveWindow(string sessionName)
    {
        InitializeComponent();
        SessionText.Text = sessionName;
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

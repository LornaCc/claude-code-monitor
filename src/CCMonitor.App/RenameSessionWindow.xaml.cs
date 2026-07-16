using System.Windows;

namespace CCMonitor.App;

public partial class RenameSessionWindow : Window
{
    public RenameSessionWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    public string SessionName => NameBox.Text;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

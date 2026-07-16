using System.Windows;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;

namespace CCMonitor.App;

public partial class SettingsWindow : Window
{
    private readonly string _hookCommand;
    private readonly string? _statusLineCommand;

    public MonitorConfig Config { get; private set; }

    public SettingsWindow(MonitorConfig config, string hookCommand, string? statusLineCommand = null)
    {
        InitializeComponent();
        _hookCommand = hookCommand;
        _statusLineCommand = statusLineCommand;
        Config = new MonitorConfig
        {
            AlwaysOnTop = config.AlwaysOnTop,
            ShowWindowsNotifications = config.ShowWindowsNotifications,
            BlockedSound = config.BlockedSound,
            DoneSound = config.DoneSound,
            ErrorSound = config.ErrorSound,
            SavePromptPreview = config.SavePromptPreview,
            SessionRetentionHours = config.SessionRetentionHours,
            WindowLeft = config.WindowLeft,
            WindowTop = config.WindowTop,
            WindowWidth = config.WindowWidth,
            WindowHeight = config.WindowHeight,
            GroupSessionsByStatus = config.GroupSessionsByStatus,
            SessionNames = new Dictionary<string, string>(config.SessionNames)
        };

        AlwaysOnTopBox.IsChecked = Config.AlwaysOnTop;
        NotificationsBox.IsChecked = Config.ShowWindowsNotifications;
        BlockedSoundBox.IsChecked = Config.BlockedSound;
        DoneSoundBox.IsChecked = Config.DoneSound;
        ErrorSoundBox.IsChecked = Config.ErrorSound;
        PromptPreviewBox.IsChecked = Config.SavePromptPreview;
        RetentionBox.Text = Config.SessionRetentionHours.ToString();
        UpdateHookStatus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        Config.ShowWindowsNotifications = NotificationsBox.IsChecked == true;
        Config.BlockedSound = BlockedSoundBox.IsChecked == true;
        Config.DoneSound = DoneSoundBox.IsChecked == true;
        Config.ErrorSound = ErrorSoundBox.IsChecked == true;
        Config.SavePromptPreview = PromptPreviewBox.IsChecked == true;
        Config.SessionRetentionHours = int.TryParse(RetentionBox.Text, out var hours) ? Math.Clamp(hours, 1, 168) : 24;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Reinstall_Click(object sender, RoutedEventArgs e)
    {
        RunHookConfigurationAction(
            service => service.Install(_hookCommand, _statusLineCommand),
            "install");
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        RunHookConfigurationAction(
            service => service.Uninstall(_hookCommand, _statusLineCommand),
            "uninstall");
    }

    private void RunHookConfigurationAction(Action<ClaudeSettingsFileService> action, string actionName)
    {
        try
        {
            action(new ClaudeSettingsFileService());
            UpdateHookStatus();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Could not {actionName} CC Monitor hooks. Claude settings were not changed.\n\n{exception.Message}",
                "CC Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void UpdateHookStatus()
    {
        var installed = new ClaudeSettingsFileService().IsInstalled(_hookCommand, _statusLineCommand);
        HooksStatusText.Text = installed ? "Hooks status: Installed" : "Hooks status: Not installed";
    }
}

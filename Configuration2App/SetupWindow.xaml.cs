using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Configuration2App;

/// <summary>
/// First-run dialog: asks for the executor's workspace folder before the dashboard loads.
/// </summary>
public partial class SetupWindow : Window
{
    public string? SelectedFolder { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();
        var logo = App.TryLoadLogo();
        if (logo != null)
        {
            Icon = logo;
            SetupLogo.Source = logo;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your executor's workspace folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ContinueButton.IsEnabled = Directory.Exists(dialog.FolderName);
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SelectedFolder = FolderBox.Text.Trim();
        DialogResult = true;
    }
}

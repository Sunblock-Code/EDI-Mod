using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Edi.Forms
{
    // Quick "Add Game" popup: pick the type (EDI / Script Player) and the game folder.
    // The caller locates the EdiConfig.json inside the folder and opens the full settings.
    public partial class AddGameDialog : Window
    {
        public string GameType => rbTypeScript.IsChecked == true ? "ScriptPlayer" : "EDI";
        public string FolderPath => CleanPath(txtFolder.Text);

        // Accept paths pasted with surrounding quotes — Windows' "Copy as path" wraps them in
        // double quotes, which otherwise fail Directory.Exists. Trim whitespace + a quote pair.
        public static string CleanPath(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
            return s;
        }

        public AddGameDialog()
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            GameType_Changed(this, null);   // set the initial accent for the default (EDI) selection
        }

        // Color-code the dialog by type: Script Player = blue, EDI = pink. Swapping the local
        // App.Accent / App.AccentSoft recolors the top accent line and the selected-type segment
        // highlight (both DynamicResource); the ADD button is restyled explicitly.
        private void GameType_Changed(object sender, RoutedEventArgs e)
        {
            bool script = rbTypeScript?.IsChecked == true;
            var accent     = script ? System.Windows.Media.Color.FromRgb(0x4D, 0x93, 0xFF)
                                     : System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x8C);
            var accentSoft = script ? System.Windows.Media.Color.FromRgb(0x14, 0x23, 0x3D)
                                     : System.Windows.Media.Color.FromRgb(0x3A, 0x1A, 0x2A);
            var accentBrush     = new System.Windows.Media.SolidColorBrush(accent);     accentBrush.Freeze();
            var accentSoftBrush = new System.Windows.Media.SolidColorBrush(accentSoft); accentSoftBrush.Freeze();
            Resources["App.Accent"]     = accentBrush;
            Resources["App.AccentSoft"] = accentSoftBrush;
            if (btnAdd != null) { btnAdd.Background = accentBrush; btnAdd.BorderBrush = accentBrush; }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Select the game folder" };
            if (Directory.Exists(txtFolder.Text)) dlg.InitialDirectory = txtFolder.Text;
            if (dlg.ShowDialog(this) == true) txtFolder.Text = dlg.FolderName;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
            {
                MessageBox.Show(this, "Pick a valid game folder first.", "Folder needed",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using Edi.Core;
using Edi.Core.Services;
using Microsoft.Win32;

namespace Edi.Forms
{
    // "Create shortcut…" dialog (right-click → Create shortcut on a game card).
    // Outputs a .lnk pointing at a tiny .cmd helper that starts the picked target plus every
    // toggled-on quick program. The .lnk uses the game's icon (converted to .ico if needed).
    //
    // Result: SavedShortcutPath is the .lnk path written, or null if Cancel.
    public partial class CreateShortcutDialog : Window
    {
        public sealed class TargetOption
        {
            public string Label { get; set; } = "";
            public string Path  { get; set; } = "";
            public string Args  { get; set; } = "";
        }

        public sealed class ProgramRow
        {
            public QuickProgram Program { get; set; } = default!;
            public bool Include { get; set; }
            public string DisplayName => string.IsNullOrWhiteSpace(Program?.Name) ? Program?.Path ?? "" : Program!.Name;
        }

        private readonly GameInfo _game;
        public ObservableCollection<TargetOption> Targets { get; } = new();
        public ObservableCollection<ProgramRow>   Programs { get; } = new();
        public string? SavedShortcutPath { get; private set; }

        public CreateShortcutDialog(GameInfo game, IEnumerable<QuickProgram> programs)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            _game = game ?? throw new ArgumentNullException(nameof(game));

            lblHeader.Text = $"Shortcut for \"{_game.Name}\"";

            // Launch targets: default exe + each user-defined LaunchOption.
            if (!string.IsNullOrWhiteSpace(_game.ExePath))
                Targets.Add(new TargetOption { Label = "Default game executable", Path = _game.ExePath!, Args = "" });
            if (_game.LaunchOptions != null)
            {
                foreach (var lo in _game.LaunchOptions)
                {
                    if (string.IsNullOrWhiteSpace(lo.Path)) continue;
                    Targets.Add(new TargetOption { Label = string.IsNullOrWhiteSpace(lo.Name) ? lo.Path : lo.Name, Path = lo.Path, Args = lo.Args ?? "" });
                }
            }
            cmbTarget.ItemsSource = Targets;
            cmbTarget.SelectedIndex = 0;
            cmbTarget.SelectionChanged += (_, __) => UpdateTargetPathLabel();
            UpdateTargetPathLabel();

            // Quick programs: list every configured one with a toggle. Default OFF — user opts in.
            foreach (var p in (programs ?? Enumerable.Empty<QuickProgram>()))
            {
                if (string.IsNullOrWhiteSpace(p?.Path)) continue;
                Programs.Add(new ProgramRow { Program = p, Include = false });
            }
            icPrograms.ItemsSource = Programs;
            if (Programs.Count == 0)
            {
                icPrograms.Visibility = Visibility.Collapsed;
                lblNoPrograms.Visibility = Visibility.Visible;
            }

            // Save location default: the user's Desktop, with the game's name as filename.
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var safe = string.Join("_", _game.Name.Split(Path.GetInvalidFileNameChars()));
            txtSavePath.Text = Path.Combine(desktop, safe + ".lnk");
        }

        private void UpdateTargetPathLabel()
        {
            lblTargetPath.Text = cmbTarget.SelectedItem is TargetOption t ? t.Path : "";
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save shortcut as",
                Filter = "Shortcut (*.lnk)|*.lnk|All files|*.*",
                FileName = Path.GetFileName(txtSavePath.Text),
                InitialDirectory = Directory.Exists(Path.GetDirectoryName(txtSavePath.Text))
                    ? Path.GetDirectoryName(txtSavePath.Text)
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                OverwritePrompt = false,
                AddExtension = true,
                DefaultExt = ".lnk",
            };
            if (dlg.ShowDialog(this) == true)
                txtSavePath.Text = dlg.FileName;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (cmbTarget.SelectedItem is not TargetOption target)
                {
                    Edi.Forms.ThemedDialog.Info(this, "Create shortcut", "No target", "Pick a launch target for the shortcut.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(txtSavePath.Text))
                {
                    Edi.Forms.ThemedDialog.Info(this, "Create shortcut", "No path", "Choose where to save the shortcut.");
                    return;
                }
                var savePath = txtSavePath.Text.Trim();
                if (!savePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) savePath += ".lnk";

                var enabled = Programs.Where(r => r.Include && !string.IsNullOrWhiteSpace(r.Program.Path)).Select(r => r.Program).ToList();
                var iconFile = GameShortcuts.EnsureIcoForGame(_game);   // returns .ico path (or null → exe fallback)

                // Write the launcher .cmd (per-game, under <OutputDir>\shortcuts\). The .lnk then
                // points at this .cmd so a single double-click starts everything in order.
                var helper = GameShortcuts.WriteLauncherCmd(_game, target.Path, target.Args, enabled);

                GameShortcuts.WriteLnk(savePath, helper, _game, iconFile);

                SavedShortcutPath = savePath;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Edi.Forms.ThemedDialog.Info(this, "Create shortcut", "Failed", ex.Message);
            }
        }
    }
}

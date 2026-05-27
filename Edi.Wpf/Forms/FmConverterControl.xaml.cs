using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Edi.Core.Funscript.Fm;
using Edi.Core.Funscript.FileJson;
using Edi.Core.Services;
using Microsoft.Win32;

namespace Edi.Forms
{
    // Rotary fuck-machine converter, hosted as a dockable panel inside the EDI UI. Offline tool that
    // turns a stroke funscript into a rotary POWER-LEVEL script (a C# port of Rriik's
    // OFS-FM-script-converter). Saves a new variant subfolder next to the source; original untouched.
    public partial class FmConverterControl : UserControl
    {
        // Row in the RPM estimator grid.
        public class FmMeasurement : INotifyPropertyChanged
        {
            private string _power = "", _rpm = "";
            public string PowerText { get => _power; set { _power = value; OnChanged(nameof(PowerText)); } }
            public string RpmText   { get => _rpm;   set { _rpm   = value; OnChanged(nameof(RpmText)); } }
            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private string _galleryPath;
        private List<FmDeviceProfile> _profiles = new();
        private readonly ObservableCollection<FmMeasurement> _measurements = new();
        private bool _loading;
        private double _estMin, _estMax;

        // Raised after each successful conversion so the host (MainWindow) can reload galleries.
        public event EventHandler ConversionSaved;

        public FmConverterControl()
        {
            InitializeComponent();
            icMeasure.ItemsSource = _measurements;
            LoadFromSettings();

            // Pick up the active gallery when shown (it changes as the user switches games).
            Loaded += (_, _) => RefreshGallery();
            IsVisibleChanged += (_, _) => { if (IsVisible) RefreshGallery(); };
        }

        // Re-read the current gallery folder + repopulate the source list.
        public void RefreshGallery()
        {
            try { _galleryPath = App.Edi?.GalleryPath; } catch { _galleryPath = null; }
            PopulateSources(SelectedSourcePath());
            UpdateOutputPath();
        }

        // ───────── settings load / save ─────────

        private void LoadFromSettings()
        {
            _loading = true;
            var s = AppLocalSettings.Load();

            _profiles = s.FmProfiles != null && s.FmProfiles.Count > 0
                ? s.FmProfiles.Select(p => p.Clone()).ToList()
                : DefaultProfiles();

            cmbProfile.ItemsSource = null;
            cmbProfile.ItemsSource = _profiles;
            cmbProfile.DisplayMemberPath = nameof(FmDeviceProfile.Name);
            int sel = Math.Max(0, Math.Min(s.FmSelectedProfile, _profiles.Count - 1));
            cmbProfile.SelectedIndex = sel;
            ShowProfileFields(_profiles[sel]);

            var o = s.FmOptions ?? new FmConvertOptions();
            chkSinglePeaks.IsChecked   = o.RecordPowerOnSinglePeaks;
            chkSingleTroughs.IsChecked = o.RecordPowerOnSingleTroughs;
            chkPeakSeries.IsChecked    = o.RecordPowerOnPeakSeries;
            chkTroughSeries.IsChecked  = o.RecordPowerOnTroughSeries;
            chkIgnore0.IsChecked       = o.Ignore0PosSeries;
            chkDropoff.IsChecked       = o.UsePowerDropoff;
            txtDropoffMs.Text          = o.PowerDropoffTimeOffsetMs.ToString();
            chkStep.IsChecked          = o.OverrideStepSize;
            txtStep.Text               = o.PowerLevelStepSize.ToString();
            // txtVariant is set by ShowProfileFields (derived from the selected profile name).

            // seed two estimator rows
            _measurements.Clear();
            _measurements.Add(new FmMeasurement());
            _measurements.Add(new FmMeasurement());
            _loading = false;
        }

        private static List<FmDeviceProfile> DefaultProfiles() => new()
        {
            new FmDeviceProfile { Name = "Generic device", MinRPM = 1.0, MaxRPM = 100.0 },
            new FmDeviceProfile { Name = "Hismith Pro 1 (1kg load)", MinRPM = 17.5, MaxRPM = 254.75 },
        };

        private FmConvertOptions ReadOptions() => new()
        {
            RecordPowerOnSinglePeaks   = chkSinglePeaks.IsChecked == true,
            RecordPowerOnSingleTroughs = chkSingleTroughs.IsChecked == true,
            RecordPowerOnPeakSeries    = chkPeakSeries.IsChecked == true,
            RecordPowerOnTroughSeries  = chkTroughSeries.IsChecked == true,
            Ignore0PosSeries           = chkIgnore0.IsChecked == true,
            UsePowerDropoff            = chkDropoff.IsChecked == true,
            PowerDropoffTimeOffsetMs   = ParseInt(txtDropoffMs.Text, 100),
            OverrideStepSize           = chkStep.IsChecked == true,
            PowerLevelStepSize         = Math.Max(1, ParseInt(txtStep.Text, 1)),
        };

        private void SaveSettings()
        {
            try
            {
                var s = AppLocalSettings.Load();
                s.FmProfiles = _profiles;
                s.FmSelectedProfile = Math.Max(0, cmbProfile.SelectedIndex);
                s.FmOptions = ReadOptions();
                s.FmOutputVariant = SanitizeVariant(txtVariant.Text);
                s.Save();
            }
            catch { }
        }

        // ───────── device profile ─────────

        private FmDeviceProfile CurrentProfile()
        {
            var p = cmbProfile.SelectedItem as FmDeviceProfile ?? new FmDeviceProfile();
            return new FmDeviceProfile
            {
                Name = string.IsNullOrWhiteSpace(txtProfName.Text) ? p.Name : txtProfName.Text.Trim(),
                MinRPM = ParseDouble(txtMinRpm.Text, p.MinRPM),
                MaxRPM = ParseDouble(txtMaxRpm.Text, p.MaxRPM),
            };
        }

        private void ShowProfileFields(FmDeviceProfile p)
        {
            txtProfName.Text = p.Name;
            txtMinRpm.Text = p.MinRPM.ToString("0.##", CultureInfo.CurrentCulture);
            txtMaxRpm.Text = p.MaxRPM.ToString("0.##", CultureInfo.CurrentCulture);
            if (txtVariant != null)
            {
                txtVariant.Text = DeriveVariant(p.Name);
                UpdateOutputPath();
            }
        }

        private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (cmbProfile.SelectedItem is FmDeviceProfile p) ShowProfileFields(p);
        }

        private void ProfNew_Click(object sender, RoutedEventArgs e)
        {
            var p = CurrentProfile();
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "New profile";
            string baseName = p.Name; int i = 2;
            while (_profiles.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                p.Name = $"{baseName} ({i++})";
            _profiles.Add(p);
            RebindProfiles(_profiles.Count - 1);
            Status($"Created profile '{p.Name}'.");
        }

        private void ProfSave_Click(object sender, RoutedEventArgs e)
        {
            int idx = cmbProfile.SelectedIndex;
            if (idx < 0 || idx >= _profiles.Count) return;
            var p = CurrentProfile();
            _profiles[idx].Name = p.Name;
            _profiles[idx].MinRPM = p.MinRPM;
            _profiles[idx].MaxRPM = p.MaxRPM;
            RebindProfiles(idx);
            Status($"Saved profile '{p.Name}'.");
        }

        private void ProfRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_profiles.Count <= 1) { Status("At least one profile must remain."); return; }
            int idx = cmbProfile.SelectedIndex;
            if (idx < 0) return;
            _profiles.RemoveAt(idx);
            RebindProfiles(Math.Min(idx, _profiles.Count - 1));
        }

        private void RebindProfiles(int select)
        {
            _loading = true;
            cmbProfile.ItemsSource = null;
            cmbProfile.ItemsSource = _profiles;
            cmbProfile.SelectedIndex = Math.Max(0, Math.Min(select, _profiles.Count - 1));
            _loading = false;
            if (cmbProfile.SelectedItem is FmDeviceProfile p) ShowProfileFields(p);
        }

        // ───────── unit converter ─────────

        private void UcCompute_Click(object sender, RoutedEventArgs e)
        {
            double cycles = ParseDouble(txtUcCycles.Text, 0);
            double seconds = ParseDouble(txtUcSeconds.Text, 0);
            double rpm = ParseDouble(txtUcRpm.Text, 0);
            double cycleMs = 0;

            if (cycles > 0 && seconds > 0)
            {
                cycleMs = seconds / cycles * 1000.0;
                rpm = 60000.0 / cycleMs;
                txtUcRpm.Text = rpm.ToString("0.##", CultureInfo.CurrentCulture);
            }
            else if (rpm > 0)
            {
                cycleMs = 60000.0 / rpm;
            }

            var prof = CurrentProfile();
            int power = cycleMs > 0 ? FmScriptConverter.PowerLevel(cycleMs, prof, ReadOptions()) : 0;
            lblUcCycleMs.Text = cycleMs > 0 ? $"Cycle: {cycleMs:0.#} ms" : "Cycle: —";
            lblUcRpm.Text = rpm > 0 ? $"RPM: {rpm:0.##}" : "RPM: —";
            lblUcPower.Text = cycleMs > 0 ? $"Power: {power}%" : "Power: —";
        }

        // ───────── RPM estimator ─────────

        private void MeasureAdd_Click(object sender, RoutedEventArgs e) => _measurements.Add(new FmMeasurement());

        private void MeasureRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_measurements.Count <= 1) return;
            if (sender is Button b && b.Tag is FmMeasurement m) _measurements.Remove(m);
        }

        private void Estimate_Click(object sender, RoutedEventArgs e)
        {
            var pts = new List<(double power, double rpm)>();
            foreach (var m in _measurements)
            {
                if (double.TryParse(m.PowerText, NumberStyles.Any, CultureInfo.CurrentCulture, out var pw) &&
                    double.TryParse(m.RpmText, NumberStyles.Any, CultureInfo.CurrentCulture, out var rp) &&
                    pw != 0 && rp != 0)
                    pts.Add((pw, rp));
            }
            if (pts.Count < 2)
            {
                lblEstimate.Text = "Need at least 2 measurements.";
                btnApplyEstimate.IsEnabled = false;
                return;
            }
            double mx = pts.Average(p => p.power), my = pts.Average(p => p.rpm);
            double sxx = pts.Sum(p => (p.power - mx) * (p.power - mx));
            double sxy = pts.Sum(p => (p.power - mx) * (p.rpm - my));
            if (Math.Abs(sxx) < 1e-9) { lblEstimate.Text = "Measurements must use different power levels."; return; }
            double gain = sxy / sxx;
            double offset = my - gain * mx;
            _estMax = 100 * gain + offset;   // RPM at 100%
            _estMin = 1 * gain + offset;     // RPM at 1%
            lblEstimate.Text = $"min {_estMin:0.##} / max {_estMax:0.##} RPM";
            btnApplyEstimate.IsEnabled = _estMax > _estMin && _estMin >= 0;
        }

        private void ApplyEstimate_Click(object sender, RoutedEventArgs e)
        {
            if (_estMax <= _estMin) return;
            txtMinRpm.Text = _estMin.ToString("0.##", CultureInfo.CurrentCulture);
            txtMaxRpm.Text = _estMax.ToString("0.##", CultureInfo.CurrentCulture);
            Status("Applied estimated RPM range to the profile fields. Click 'Save' to keep it.");
        }

        // ───────── source selection ─────────

        private class SourceItem
        {
            public string Display { get; set; }
            public string Path { get; set; }
            public override string ToString() => Display;
        }

        private void PopulateSources(string defaultSource)
        {
            var items = new List<SourceItem>();
            try
            {
                if (!string.IsNullOrWhiteSpace(_galleryPath) && Directory.Exists(_galleryPath))
                {
                    var baseDir = new DirectoryInfo(_galleryPath).FullName.TrimEnd('\\') + "\\";
                    foreach (var f in Directory.EnumerateFiles(_galleryPath, "*.funscript", SearchOption.AllDirectories)
                                                .OrderBy(p => p))
                    {
                        items.Add(new SourceItem { Path = f, Display = f.StartsWith(baseDir) ? f.Substring(baseDir.Length) : Path.GetFileName(f) });
                    }
                }
            }
            catch { }

            cmbSource.ItemsSource = items;
            cmbSource.DisplayMemberPath = nameof(SourceItem.Display);
            if (!string.IsNullOrWhiteSpace(defaultSource))
            {
                var match = items.FirstOrDefault(i => string.Equals(i.Path, defaultSource, StringComparison.OrdinalIgnoreCase));
                if (match != null) cmbSource.SelectedItem = match;
            }
            if (cmbSource.SelectedItem == null && items.Count > 0) cmbSource.SelectedIndex = 0;
        }

        private string SelectedSourcePath()
        {
            if (cmbSource?.SelectedItem is SourceItem si) return si.Path;
            return cmbSource?.Tag as string;   // set by Browse for an out-of-folder file
        }

        private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSourceInfo();

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Funscript (*.funscript)|*.funscript|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(_galleryPath) ? _galleryPath : null,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                var si = new SourceItem { Path = dlg.FileName, Display = Path.GetFileName(dlg.FileName) };
                var list = (cmbSource.ItemsSource as List<SourceItem>) ?? new List<SourceItem>();
                if (!list.Any(i => string.Equals(i.Path, si.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    list = new List<SourceItem>(list) { si };
                    cmbSource.ItemsSource = list;
                }
                cmbSource.SelectedItem = list.First(i => string.Equals(i.Path, si.Path, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void RefreshSourceInfo()
        {
            UpdateOutputPath();
            var path = SelectedSourcePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { lblSourceInfo.Text = ""; return; }
            try
            {
                var f = FunScriptFile.TryRead(path);
                if (f?.actions == null) { lblSourceInfo.Text = "Could not read this funscript."; return; }
                FmScriptConverter.GetPeaksTroughs(f.actions.OrderBy(a => a.at).ToList(), out var pk, out var tr);
                lblSourceInfo.Text = $"{f.actions.Count} actions • {pk.Count} peaks • {tr.Count} troughs";
            }
            catch { lblSourceInfo.Text = ""; }
        }

        private void Variant_TextChanged(object sender, TextChangedEventArgs e) => UpdateOutputPath();

        private void UpdateOutputPath()
        {
            if (lblOutPath == null) return;
            var outPath = ComputeOutputPath(SelectedSourcePath(), txtVariant?.Text);
            lblOutPath.Text = outPath == null
                ? ""
                : "→ " + Path.GetFileName(Path.GetDirectoryName(outPath)) + "\\" + Path.GetFileName(outPath);
        }

        // Converted scripts are saved into a SUBFOLDER of the gallery named after the variant
        // (e.g. Gallery\Hismith\Scene.funscript). EDI's discovery treats the subfolder name as the
        // variant, so it shows up per-device in "Selected Variant". The original is never touched.
        private string ComputeOutputPath(string sourcePath, string variant)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            var baseName = Path.GetFileNameWithoutExtension(sourcePath).Split('.')[0];
            var v = SanitizeVariant(variant);
            if (string.IsNullOrEmpty(v)) v = "FM";
            var root = !string.IsNullOrWhiteSpace(_galleryPath) && Directory.Exists(_galleryPath)
                ? _galleryPath
                : (Path.GetDirectoryName(sourcePath) ?? "");
            return Path.Combine(root, v, $"{baseName}.funscript");
        }

        private static string DeriveVariant(string profileName)
        {
            var first = (profileName ?? "").Trim()
                .Split(new[] { ' ', '-', '_', '(', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            var v = SanitizeVariant(first ?? "");
            return string.IsNullOrEmpty(v) ? "FM" : v;
        }

        private static string SanitizeVariant(string v)
        {
            v = (v ?? "").Trim();
            foreach (var c in new[] { '.', '[', ']', '\\', '/', ':', '*', '?', '"', '<', '>', '|', ' ' })
                v = v.Replace(c.ToString(), "");
            return v;
        }

        // ───────── convert ─────────

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            var sourcePath = SelectedSourcePath();
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                Status("Pick a source funscript first."); return;
            }
            var prof = CurrentProfile();
            if (prof.MaxRPM <= prof.MinRPM)
            {
                Status("Max RPM must be greater than Min RPM."); return;
            }
            var variant = SanitizeVariant(txtVariant.Text);
            if (string.IsNullOrEmpty(variant)) { Status("Enter an output subfolder name."); return; }

            var outPath = ComputeOutputPath(sourcePath, variant);
            if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            {
                Status("Output would overwrite the source — choose a different subfolder name."); return;
            }

            FunScriptFile src;
            try { src = FunScriptFile.Read(sourcePath); }
            catch (Exception ex) { Status("Failed to read source: " + ex.Message); return; }
            if (src?.actions == null || src.actions.Count == 0) { Status("Source has no actions."); return; }

            if (File.Exists(outPath))
            {
                var ans = MessageBox.Show(Window.GetWindow(this),
                    $"'{Path.GetFileName(outPath)}' already exists in '{variant}'. Overwrite it?",
                    "Overwrite variant?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes) { Status("Cancelled."); return; }
            }

            var opts = ReadOptions();
            List<FunScriptAction> converted;
            try { converted = FmScriptConverter.Convert(src.actions, prof, opts); }
            catch (Exception ex) { Status("Conversion error: " + ex.Message); return; }

            if (converted.Count == 0) { Status("Conversion produced no actions — check the options."); return; }

            var outFile = new FunScriptFile
            {
                version = string.IsNullOrEmpty(src.version) ? "1.0" : src.version,
                inverted = false,
                range = src.range > 0 ? src.range : 100,
                actions = converted,
                metadata = src.metadata,
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                outFile.Save(outPath);
            }
            catch (Exception ex) { Status("Failed to save: " + ex.Message); return; }

            SaveSettings();
            Status($"Saved {converted.Count} power-level actions → '{variant}\\{Path.GetFileName(outPath)}'. " +
                   $"Reloading… then pick the '{variant}' variant for your rotary machine in the Devices panel.");
            RefreshSourceInfo();
            ConversionSaved?.Invoke(this, EventArgs.Empty);
        }

        // ───────── helpers ─────────

        private void Status(string msg) { if (lblStatus != null) lblStatus.Text = msg; }

        private static int ParseInt(string s, int fallback)
            => int.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v : fallback;

        private static double ParseDouble(string s, double fallback)
            => double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var v) ? v : fallback;
    }
}

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

        // Source = a GALLERY (group of related .funscript files sharing a base name), not a
        // single funscript. e.g. gallery "SmaGuong" includes "SmaGuong.funscript" +
        // "SmaGuong.pitch.funscript" + "SmaGuong.roll.funscript". Converting a gallery emits a
        // NEW gallery (same axis files) under the variant subfolder named after the device profile.
        private class SourceItem
        {
            public string Display { get; set; }     // gallery name (no .funscript extension)
            public string Path    { get; set; }     // canonical path: the first/main file in the gallery (used for "default source" rehydration)
            public List<string> Files { get; set; } = new();   // every .funscript file in this gallery (main + axis variants)
            public override string ToString() => Display;
        }

        // Walk the gallery folder, group every *.funscript by the base name BEFORE the first dot
        // (so "SmaGuong.pitch.funscript" lives in the same group as "SmaGuong.funscript"). The
        // dropdown shows one row per gallery, hiding the axis-file noise.
        private void PopulateSources(string defaultSourceFile)
        {
            var items = new List<SourceItem>();
            try
            {
                if (!string.IsNullOrWhiteSpace(_galleryPath) && Directory.Exists(_galleryPath))
                {
                    var groups = Directory.EnumerateFiles(_galleryPath, "*.funscript", SearchOption.AllDirectories)
                        .OrderBy(p => p)
                        .GroupBy(p => Path.GetFileNameWithoutExtension(p).Split('.')[0], StringComparer.OrdinalIgnoreCase);

                    foreach (var g in groups)
                    {
                        var files = g.OrderBy(p => p).ToList();
                        items.Add(new SourceItem
                        {
                            Display = g.Key,
                            Path    = files[0],
                            Files   = files,
                        });
                    }
                    items = items.OrderBy(i => i.Display, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch { }

            cmbSource.ItemsSource = items;
            cmbSource.DisplayMemberPath = nameof(SourceItem.Display);
            if (!string.IsNullOrWhiteSpace(defaultSourceFile))
            {
                // Try to match by file path first (so re-opening preserves the selection), then by
                // gallery name as a fallback for cases where the user picked a Browse'd file.
                var matchByFile = items.FirstOrDefault(i => i.Files.Any(f => string.Equals(f, defaultSourceFile, StringComparison.OrdinalIgnoreCase)));
                if (matchByFile != null) cmbSource.SelectedItem = matchByFile;
            }
            if (cmbSource.SelectedItem == null && items.Count > 0) cmbSource.SelectedIndex = 0;
        }

        private SourceItem SelectedSourceGallery() => cmbSource?.SelectedItem as SourceItem;

        // Back-compat: callers that just need "a representative path" (output-path preview,
        // metadata read, default-source persistence) can grab the first file of the gallery.
        private string SelectedSourcePath()
        {
            var g = SelectedSourceGallery();
            if (g != null && g.Files.Count > 0) return g.Files[0];
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
                // Browse selects a single file but we still want to treat it as a gallery — scan its
                // folder for sibling axis files (Name.pitch.funscript, Name.roll.funscript, …) and
                // group them under the base name.
                var dir = Path.GetDirectoryName(dlg.FileName) ?? "";
                var galleryName = Path.GetFileNameWithoutExtension(dlg.FileName).Split('.')[0];
                var siblings = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.funscript", SearchOption.TopDirectoryOnly)
                                .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f).Split('.')[0], galleryName, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(f => f).ToList()
                    : new List<string> { dlg.FileName };
                if (siblings.Count == 0) siblings.Add(dlg.FileName);

                var si = new SourceItem { Path = siblings[0], Display = galleryName, Files = siblings };
                var list = (cmbSource.ItemsSource as List<SourceItem>) ?? new List<SourceItem>();
                if (!list.Any(i => string.Equals(i.Display, si.Display, StringComparison.OrdinalIgnoreCase)))
                {
                    list = new List<SourceItem>(list) { si };
                    cmbSource.ItemsSource = list;
                }
                cmbSource.SelectedItem = list.First(i => string.Equals(i.Display, si.Display, StringComparison.OrdinalIgnoreCase));
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
            var g = SelectedSourceGallery();
            var v = SanitizeVariant(txtVariant?.Text);
            if (string.IsNullOrEmpty(v)) v = "FM";
            if (g == null) { lblOutPath.Text = ""; return; }

            // Show "→ <variant>\<gallery>.funscript (+N axis files)" so the user knows the whole
            // gallery is going to land in the new subfolder, not just the main file.
            var first = ComputeOutputPath(g.Files[0], v);
            string preview = "→ " + Path.GetFileName(Path.GetDirectoryName(first)) + "\\" + Path.GetFileName(first);
            if (g.Files.Count > 1) preview += $"  (+{g.Files.Count - 1} axis file{(g.Files.Count == 2 ? "" : "s")})";
            lblOutPath.Text = preview;
        }

        // Converted scripts are saved into a SUBFOLDER of the gallery named after the variant
        // (e.g. Gallery\HismithPro1\SmaGuong.funscript + Gallery\HismithPro1\SmaGuong.pitch.funscript).
        // EDI's discovery treats the subfolder name as the variant, so the converted gallery shows
        // up per-device in "Selected Variant". The original files are never touched.
        private string ComputeOutputPath(string sourcePath, string variant)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            var fileName = Path.GetFileName(sourcePath);   // keep the full "Name.pitch.funscript" so axis variants survive
            var v = SanitizeVariant(variant);
            if (string.IsNullOrEmpty(v)) v = "FM";
            var root = !string.IsNullOrWhiteSpace(_galleryPath) && Directory.Exists(_galleryPath)
                ? _galleryPath
                : (Path.GetDirectoryName(sourcePath) ?? "");
            return Path.Combine(root, v, fileName);
        }

        // Derive the variant subfolder name from the profile name. Strips any text inside ()
        // and the parens themselves, then drops disallowed filename chars. e.g.
        //   "Hismith Pro 1 (1kg load)" → "HismithPro1"
        //   "Generic device"           → "Genericdevice"
        private static string DeriveVariant(string profileName)
        {
            var name = (profileName ?? "").Trim();
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([^)]*\)\s*", " ").Trim();
            var v = SanitizeVariant(name);
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

        // Convert the whole selected gallery — every axis file (main + .pitch + .roll + …) becomes
        // a power-level script under <gallery>/<variant>/. The user's "single funscript at a time"
        // workflow is gone: a gallery in, a parallel gallery out.
        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            var gallery = SelectedSourceGallery();
            if (gallery == null || gallery.Files.Count == 0)
            {
                Status("Pick a source gallery first."); return;
            }
            var prof = CurrentProfile();
            if (prof.MaxRPM <= prof.MinRPM)
            {
                Status("Max RPM must be greater than Min RPM."); return;
            }
            var variant = SanitizeVariant(txtVariant.Text);
            if (string.IsNullOrEmpty(variant)) { Status("Enter an output subfolder name."); return; }

            // Guard against overwriting the source by aiming the variant folder at the gallery's
            // own folder (would land NewName.funscript next to OldName.funscript, but if variant
            // happened to equal an existing axis name it could still collide — refuse it).
            foreach (var src in gallery.Files)
            {
                var would = ComputeOutputPath(src, variant);
                if (string.Equals(Path.GetFullPath(would), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                {
                    Status("Output would overwrite the source — choose a different subfolder name."); return;
                }
            }

            var opts = ReadOptions();
            var existing = gallery.Files.Select(f => ComputeOutputPath(f, variant)).Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                var ans = MessageBox.Show(Window.GetWindow(this),
                    $"{existing.Count} file(s) already exist in the '{variant}' folder. Overwrite the whole gallery?",
                    "Overwrite variant gallery?", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes) { Status("Cancelled."); return; }
            }

            int converted = 0, skipped = 0, totalActions = 0;
            string firstError = null;
            foreach (var srcPath in gallery.Files)
            {
                FunScriptFile src;
                try { src = FunScriptFile.Read(srcPath); }
                catch (Exception ex) { skipped++; firstError ??= $"{Path.GetFileName(srcPath)}: {ex.Message}"; continue; }
                if (src?.actions == null || src.actions.Count == 0) { skipped++; continue; }

                List<FunScriptAction> outActions;
                try { outActions = FmScriptConverter.Convert(src.actions, prof, opts); }
                catch (Exception ex) { skipped++; firstError ??= $"{Path.GetFileName(srcPath)}: {ex.Message}"; continue; }
                if (outActions.Count == 0) { skipped++; continue; }

                var outPath = ComputeOutputPath(srcPath, variant);
                var outFile = new FunScriptFile
                {
                    version  = string.IsNullOrEmpty(src.version) ? "1.0" : src.version,
                    inverted = false,
                    range    = src.range > 0 ? src.range : 100,
                    actions  = outActions,
                    metadata = src.metadata,
                };
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                    outFile.Save(outPath);
                    converted++;
                    totalActions += outActions.Count;
                }
                catch (Exception ex) { skipped++; firstError ??= $"{Path.GetFileName(srcPath)}: {ex.Message}"; }
            }

            SaveSettings();
            if (converted == 0)
            {
                Status("Conversion produced no files." + (firstError != null ? " First error: " + firstError : ""));
                return;
            }
            var tail = skipped > 0 ? $" ({skipped} file(s) skipped)" : "";
            Status($"Saved {converted} file(s) / {totalActions} actions → '{variant}\\'{tail}. " +
                   $"Reload, then pick the '{variant}' variant for your rotary machine in the Devices panel.");
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

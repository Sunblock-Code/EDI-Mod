using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Edi.Core.Funscript.FileJson;
using Path = System.IO.Path;

namespace Edi.Forms
{
    public partial class FunscriptEditor : Window
    {
        private readonly string _galleryPath;
        private string _galleryName = "Gallery";        // the gallery folder name (shown left of "/")
        private List<string> _allFiles = new();        // every funscript (all variants)
        private List<string> _filesInFolder = new();   // funscripts in the active variant only
        private List<string> _variants = new();        // distinct variant subfolders
        private string _activeVariant = "";            // the variant currently being edited
        private FunScriptFile? _currentFile;

        // Lock lines: points at/below the lower line, or at/above the upper line, are held
        // in place (exempt from the transform). Each line is set by dragging it on the preview.
        private double _lockThreshold = 25;       // lower line ("Lock below")
        private double _lockAboveThreshold = 75;  // upper line ("Lock above")
        private int _dragLine;                    // 0 none, 1 lower, 2 upper

        // Baked locks: turning a lock OFF commits its range so the held points stay frozen
        // (in the preview AND the saved preset) even with the toggle off, until Reset.
        private double? _bakedBelow;
        private double? _bakedAbove;

        // Edit mode. All = one transform applied to every script. Individual = each script
        // remembers its own settings (stored as you click through them).
        private bool _individualMode;
        private bool _loadingSettings;        // suppress storing while applying loaded settings
        private string? _currentPath;
        private readonly Dictionary<string, EditSettings> _fileSettings = new(StringComparer.OrdinalIgnoreCase);

        private const string NewGalleryItem = "New gallery…";
        private const string GalleryMarker  = ".edigallery";
        private const string LockStateFile  = ".edilock";   // present+"unlocked" = unlocked; missing = locked
        private string? _lockedGalleryName;                 // the first (oldest) saved gallery

        private sealed class EditSettings
        {
            public double Min = 0, Max = 100, Intensity = 100;
            public bool Invert;
            public bool LockBelow; public double LockBelowThreshold = 25;
            public bool LockAbove; public double LockAboveThreshold = 75;
            public double? BakedBelow; public double? BakedAbove;
            public int Vibrate;   // oscillating points to insert between each pair (0 = off)
        }

        private EditSettings CurrentSettings() => new EditSettings
        {
            Min = sliderMin.Value, Max = sliderMax.Value, Intensity = sliderIntensity.Value,
            Invert = chkInvert.IsChecked == true,
            LockBelow = chkLock.IsChecked == true, LockBelowThreshold = _lockThreshold,
            LockAbove = chkLockAbove.IsChecked == true, LockAboveThreshold = _lockAboveThreshold,
            BakedBelow = _bakedBelow, BakedAbove = _bakedAbove,
            Vibrate = (int)sliderVibrate.Value,
        };

        private void ApplySettings(EditSettings s)
        {
            _loadingSettings = true;
            sliderMin.Value = s.Min; sliderMax.Value = s.Max; sliderIntensity.Value = s.Intensity;
            sliderVibrate.Value = s.Vibrate;
            chkInvert.IsChecked = s.Invert;
            _lockThreshold = s.LockBelowThreshold; _lockAboveThreshold = s.LockAboveThreshold;
            _bakedBelow = s.BakedBelow; _bakedAbove = s.BakedAbove;
            chkLock.IsChecked = s.LockBelow; chkLockAbove.IsChecked = s.LockAbove;
            _loadingSettings = false;
            UpdateLockCursor();
            OnTransformChanged();
        }

        // Build the output actions for a source list: remap positions, then (if Vibrate > 0)
        // insert oscillating points between each consecutive pair to create a buzz.
        private static List<FunScriptAction> BuildActions(List<FunScriptAction> src, EditSettings s)
        {
            var pts = src.Select(a => new FunScriptAction { at = a.at, pos = TransformWith(a.pos, s) }).ToList();
            if (s.Vibrate < 1 || pts.Count < 2) return pts;

            int density = s.Vibrate;
            int strokeHi = (int)Math.Max(s.Min, s.Max);
            int strokeLo = (int)Math.Min(s.Min, s.Max);

            var outp = new List<FunScriptAction>(pts.Count * (density + 1));
            for (int i = 0; i < pts.Count; i++)
            {
                outp.Add(pts[i]);
                if (i == pts.Count - 1) break;

                var a = pts[i];
                var b = pts[i + 1];
                long dt = b.at - a.at;
                if (dt <= 1) continue;

                int hi = Math.Max(a.pos, b.pos);
                int lo = Math.Min(a.pos, b.pos);
                if (hi == lo) { hi = strokeHi; lo = strokeLo; }   // flat run -> buzz the full stroke range

                for (int k = 1; k <= density; k++)
                {
                    long t = a.at + dt * k / (density + 1);
                    outp.Add(new FunScriptAction { at = t, pos = (k % 2 == 1) ? hi : lo });
                }
            }
            return outp;
        }

        private static int TransformWith(int pos, EditSettings s)
        {
            bool heldBelow = (s.LockBelow && pos <= s.LockBelowThreshold) || (s.BakedBelow.HasValue && pos <= s.BakedBelow.Value);
            bool heldAbove = (s.LockAbove && pos >= s.LockAboveThreshold) || (s.BakedAbove.HasValue && pos >= s.BakedAbove.Value);
            if (heldBelow || heldAbove) return pos;

            int p = pos;
            if (s.Invert) p = 100 - p;
            double withIntensity = 50.0 + (p - 50.0) * (s.Intensity / 100.0);
            int lo = (int)s.Min, hi = (int)s.Max;
            if (hi < lo) (lo, hi) = (hi, lo);
            double mapped = lo + (withIntensity / 100.0) * (hi - lo);
            return Math.Max(0, Math.Min(100, (int)Math.Round(mapped)));
        }

        public FunscriptEditor(string galleryPath)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();

            _galleryPath = galleryPath;

            sliderMin.ValueChanged       += (_, __) => OnTransformChanged();
            sliderMax.ValueChanged       += (_, __) => OnTransformChanged();
            sliderIntensity.ValueChanged += (_, __) => OnTransformChanged();
            sliderVibrate.ValueChanged   += (_, __) => OnTransformChanged();
            chkInvert.Checked            += (_, __) => OnTransformChanged();
            chkInvert.Unchecked          += (_, __) => OnTransformChanged();
            // Turning a lock on hands control to the live (draggable) line; turning it off
            // bakes the current threshold so the held points stay frozen.
            chkLock.Checked              += (_, __) => { if (!_loadingSettings) _bakedBelow = null;             UpdateLockCursor(); OnTransformChanged(); };
            chkLock.Unchecked            += (_, __) => { if (!_loadingSettings) _bakedBelow = _lockThreshold;   UpdateLockCursor(); OnTransformChanged(); };
            chkLockAbove.Checked         += (_, __) => { if (!_loadingSettings) _bakedAbove = null;             UpdateLockCursor(); OnTransformChanged(); };
            chkLockAbove.Unchecked       += (_, __) => { if (!_loadingSettings) _bakedAbove = _lockAboveThreshold; UpdateLockCursor(); OnTransformChanged(); };
            chkIndividual.Checked        += (_, __) => OnModeChanged();
            chkIndividual.Unchecked      += (_, __) => OnModeChanged();

            Loaded += (_, __) => LoadFileList();
        }

        private void LoadFileList()
        {
            if (string.IsNullOrWhiteSpace(_galleryPath) || !Directory.Exists(_galleryPath))
            {
                MessageBox.Show(this, $"Gallery folder not found:\n{_galleryPath}",
                    "Funscript Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
                return;
            }

            _galleryName = new DirectoryInfo(_galleryPath.TrimEnd('\\', '/')).Name;
            PopulateSaveTargets();

            // Scan recursively — funscripts often live in variant subfolders
            // like "detailed/", "linear/", etc. Skip Edi's bundle outputs and any
            // saved galleries (marked subfolders, or legacy preset_* folders).
            _allFiles = Directory
                .GetFiles(_galleryPath, "*.funscript", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var fileName = Path.GetFileName(f);
                    if (fileName.StartsWith("bundle.", StringComparison.OrdinalIgnoreCase)) return false;
                    var rel = Path.GetRelativePath(_galleryPath, f);
                    var topSeg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (IsSavedGallery(topSeg)) return false;
                    return true;
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Variants = distinct first path segments ("" = files sitting in the gallery root).
            _variants = _allFiles.Select(VariantOf).Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            if (_variants.Count == 0) _variants.Add("");
            _activeVariant = _variants[0];
            ApplyVariantFilter();
        }

        // The variant subfolder of a file ("" for a root-level file).
        private string VariantOf(string fullPath)
        {
            var rel = Path.GetRelativePath(_galleryPath, fullPath);
            int slash = rel.IndexOfAny(new[] { '\\', '/' });
            return slash >= 0 ? rel.Substring(0, slash) : "";
        }

        // Rebuild the file list for the active variant only.
        private void ApplyVariantFilter()
        {
            runGalleryPrefix.Text = _galleryName + "/";
            runVariant.Text = string.IsNullOrEmpty(_activeVariant) ? "  (root)" : "  " + _activeVariant;
            lnkVariant.IsEnabled = _variants.Count > 1;   // only clickable when there's more than one

            _filesInFolder = _allFiles.Where(f => VariantOf(f) == _activeVariant).ToList();

            // Names within one variant are unique, so just show the file name.
            lstFiles.ItemsSource = _filesInFolder.Select(f => StripVariant(RelNoExt(f))).ToList();
            lblFileCount.Text = _variants.Count > 1
                ? $"{_filesInFolder.Count} files · {_variants.Count} variants (click the variant to switch)"
                : $"{_filesInFolder.Count} files in this gallery";

            if (_filesInFolder.Count > 0)
            {
                lblNoFiles.Visibility = Visibility.Collapsed;
                lstFiles.SelectedIndex = 0;
            }
            else
            {
                lblNoFiles.Visibility = Visibility.Visible;
                lblStatus.Text = "";
            }
        }

        // Click the variant in the header to cycle to the next variant subfolder.
        private void Variant_Click(object sender, RoutedEventArgs e)
        {
            if (_variants.Count < 2) return;
            int i = _variants.IndexOf(_activeVariant);
            _activeVariant = _variants[(i + 1) % _variants.Count];
            ApplyVariantFilter();
        }

        // Relative path under the gallery, forward slashes, no ".funscript".
        private string RelNoExt(string fullPath)
        {
            var rel = Path.GetRelativePath(_galleryPath, fullPath);
            return rel.Substring(0, rel.Length - ".funscript".Length).Replace('\\', '/');
        }

        // Drop the leading variant folder (e.g. "detailed/Foo" -> "Foo").
        private static string StripVariant(string relNoExt)
        {
            int slash = relNoExt.IndexOf('/');
            return slash >= 0 ? relNoExt.Substring(slash + 1) : relNoExt;
        }

        // A top-level subfolder is a saved gallery if it carries our marker (or is a legacy preset_).
        private bool IsSavedGallery(string topSeg)
            => topSeg.StartsWith("preset_", StringComparison.OrdinalIgnoreCase)
               || File.Exists(Path.Combine(_galleryPath, topSeg, GalleryMarker));

        private void PopulateSaveTargets(string? select = null)
        {
            var saved = Directory.GetDirectories(_galleryPath)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(IsSavedGallery)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = new List<string> { NewGalleryItem };
            items.AddRange(saved);
            cmbSaveTarget.ItemsSource = items;
            cmbSaveTarget.SelectedItem = (select != null && items.Contains(select)) ? select : NewGalleryItem;

            // The "first" (oldest) saved gallery is the lockable one.
            _lockedGalleryName = Directory.GetDirectories(_galleryPath)
                .Where(d => IsSavedGallery(new DirectoryInfo(d).Name))
                .OrderBy(Directory.GetCreationTimeUtc)
                .Select(d => new DirectoryInfo(d).Name)
                .FirstOrDefault();

            UpdateSaveTargetUI();
            UpdateLockUi();
        }

        private void cmbSaveTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSaveTargetUI();
            UpdateLockUi();
        }

        private void UpdateSaveTargetUI()
        {
            if (txtNewGalleryName == null) return;
            bool isNew = (cmbSaveTarget.SelectedItem as string ?? NewGalleryItem) == NewGalleryItem;
            txtNewGalleryName.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
            if (isNew && string.IsNullOrWhiteSpace(txtNewGalleryName.Text))
                txtNewGalleryName.Text = "gallery_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }

        // ----- Locking the first saved gallery -----------------------------------

        // Locked unless an ".edilock" file exists saying "unlocked". Only the first gallery can lock.
        private bool IsGalleryLocked(string? name)
        {
            if (string.IsNullOrEmpty(name) || name != _lockedGalleryName) return false;
            var f = Path.Combine(_galleryPath, name, LockStateFile);
            if (!File.Exists(f)) return true;
            try { return !File.ReadAllText(f).Trim().Equals("unlocked", StringComparison.OrdinalIgnoreCase); }
            catch { return true; }
        }

        private void SetGalleryLocked(string name, bool locked)
        {
            var f = Path.Combine(_galleryPath, name, LockStateFile);
            try
            {
                if (locked) { if (File.Exists(f)) File.Delete(f); }   // missing = locked (default)
                else File.WriteAllText(f, "unlocked");
            }
            catch { }
        }

        // Lock emoji shows (with reserved space) only when the save target is the lockable gallery.
        private void UpdateLockUi()
        {
            if (lblGalleryLock == null) return;
            var sel = cmbSaveTarget.SelectedItem as string;
            if (_lockedGalleryName != null && sel == _lockedGalleryName)
            {
                bool locked = IsGalleryLocked(_lockedGalleryName);
                lblGalleryLock.Visibility = Visibility.Visible;
                lblGalleryLock.Text = locked ? "\U0001F512" : "\U0001F513";   // 🔒 / 🔓
                lblGalleryLock.ToolTip = locked
                    ? "Locked — click and type UNLOCK to allow saving over it"
                    : "Unlocked — click to re-lock";
            }
            else
            {
                lblGalleryLock.Visibility = Visibility.Hidden;   // Hidden (not Collapsed) so "Gallery/" doesn't shift
            }
        }

        private void Lock_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lockedGalleryName == null) return;
            if (IsGalleryLocked(_lockedGalleryName))
            {
                bool ok = ThemedDialog.RequireText(this, "Unlock gallery",
                    $"Unlock “{_lockedGalleryName}”?",
                    "This is the protected first gallery. Type UNLOCK to allow saving over it.", "UNLOCK");
                if (!ok) return;
                SetGalleryLocked(_lockedGalleryName, false);
            }
            else
            {
                SetGalleryLocked(_lockedGalleryName, true);
            }
            UpdateLockUi();
        }

        private void lstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstFiles.SelectedIndex < 0 || lstFiles.SelectedIndex >= _filesInFolder.Count) return;

            var path = _filesInFolder[lstFiles.SelectedIndex];
            _currentPath = path;
            var rel = RelNoExt(path);
            // PREVIEW header shows just the funscript name (the variant lives in the FUNSCRIPTS header).
            lblSelectedPath.Text = Path.GetFileNameWithoutExtension(path);
            // FUNSCRIPTS header: "Gallery/" (white) + variant subfolder (accent), e.g. Gallery/detailed.
            int slash = rel.IndexOf('/');
            //   (non-breaking spaces) push the variant clear of the slash.
            if (slash >= 0) { runGalleryPrefix.Text = _galleryName + "/"; runVariant.Text = "  " + rel.Substring(0, slash); }
            else            { runGalleryPrefix.Text = _galleryName + "/"; runVariant.Text = ""; }
            _currentFile = FunScriptFile.TryRead(path);
            if (_currentFile == null)
            {
                lblStatus.Text = $"Could not read {Path.GetFileName(path)}";
                _currentFile = null;
                ClearCanvas();
                return;
            }

            var actions = _currentFile.actions;
            if (actions == null || actions.Count == 0)
            {
                lblStatus.Text = $"{Path.GetFileName(path)} has no actions";
                lblTimeRange.Text = "";
            }
            else
            {
                var lastMs = actions[^1].at;
                var duration = TimeSpan.FromMilliseconds(lastMs);
                lblTimeRange.Text = $"{actions.Count} points · {duration:m\\:ss\\.fff}";
                lblStatus.Text = "";
            }

            // In Individual mode, load this script's own settings (or defaults if untouched).
            if (_individualMode)
                ApplySettings(_fileSettings.TryGetValue(path, out var fs) ? fs : new EditSettings());

            DrawPreview();
        }

        private void OnTransformChanged()
        {
            if (lblMin != null)       lblMin.Text       = ((int)sliderMin.Value) + "%";
            if (lblMax != null)       lblMax.Text       = ((int)sliderMax.Value) + "%";
            if (lblIntensity != null) lblIntensity.Text = ((int)sliderIntensity.Value) + "%";
            if (lblVibrate != null)   lblVibrate.Text   = sliderVibrate.Value < 1 ? "off" : ((int)sliderVibrate.Value).ToString();
            UpdateLockLabel();
            DrawPreview();
            if (!_loadingSettings && _individualMode && _currentPath != null)
                _fileSettings[_currentPath] = CurrentSettings();
        }

        private void OnModeChanged()
        {
            _individualMode = chkIndividual.IsChecked == true;
            // Seed the current file with what's on screen so toggling in keeps the visible edit.
            if (_individualMode && _currentPath != null)
                _fileSettings[_currentPath] = CurrentSettings();
            if (lblModeHint != null)
                lblModeHint.Text = _individualMode ? "edits saved per script" : "same edit on every script";
            if (lblSaveBtn != null)
                lblSaveBtn.Text = _individualMode ? "SAVE FUNSCRIPT" : "SAVE ALL";
        }

        private void UpdateLockLabel()
        {
            if (lblLock != null)
                lblLock.Text = chkLock?.IsChecked == true ? $"≤ {(int)Math.Round(_lockThreshold)}%"
                             : _bakedBelow.HasValue       ? $"≤ {(int)Math.Round(_bakedBelow.Value)}% (baked)"
                             : "";
            if (lblLockAbove != null)
                lblLockAbove.Text = chkLockAbove?.IsChecked == true ? $"≥ {(int)Math.Round(_lockAboveThreshold)}%"
                                  : _bakedAbove.HasValue            ? $"≥ {(int)Math.Round(_bakedAbove.Value)}% (baked)"
                                  : "";
        }

        private void UpdateLockCursor()
        {
            if (canvasPreview != null)
                canvasPreview.Cursor = (chkLock?.IsChecked == true || chkLockAbove?.IsChecked == true)
                    ? Cursors.SizeNS : Cursors.Arrow;
        }

        private void canvasPreview_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawPreview();
        }

        private void ClearCanvas()
        {
            canvasGrid?.Children.Clear();
            canvasPreview?.Children.Clear();
        }

        private void DrawPreview()
        {
            ClearCanvas();
            if (_currentFile?.actions == null || _currentFile.actions.Count == 0) return;
            if (canvasPreview == null) return;

            double w = canvasPreview.ActualWidth;
            double h = canvasPreview.ActualHeight;
            if (w < 4 || h < 4) return;

            // Background gridlines (25, 50, 75 %)
            var gridBrush = new SolidColorBrush(Color.FromArgb(60, 200, 210, 230));
            foreach (var pct in new[] { 0.25, 0.50, 0.75 })
            {
                var y = h * pct;
                canvasGrid.Children.Add(new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = pct == 0.50 ? 1 : 0.5,
                    StrokeDashArray = new DoubleCollection { 2, 4 },
                });
            }

            var actions = _currentFile.actions;
            long maxTime = actions[^1].at;
            if (maxTime <= 0) maxTime = 1;

            // Original (dim)
            var orig = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(120, 180, 188, 200)),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
            };
            // Modified (accent)
            var mod = new Polyline
            {
                Stroke = (Brush)(TryFindResource("App.Accent") ?? Brushes.Magenta),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            };

            var settings = CurrentSettings();
            foreach (var a in actions)
            {
                double x = (a.at / (double)maxTime) * w;
                orig.Points.Add(new Point(x, ((100 - a.pos) / 100.0) * h));
            }
            foreach (var a in BuildActions(actions, settings))
            {
                double x = (a.at / (double)maxTime) * w;
                mod.Points.Add(new Point(x, ((100 - a.pos) / 100.0) * h));
            }

            canvasPreview.Children.Add(orig);
            canvasPreview.Children.Add(mod);

            // Draggable lock lines + shaded "held" regions.
            var accentBrush = (Brush)(TryFindResource("App.Accent") ?? Brushes.Magenta);
            var accentColor = (accentBrush as SolidColorBrush)?.Color ?? Colors.Magenta;
            if (chkLock?.IsChecked == true)
                DrawLockLine(_lockThreshold, below: true, w, h, accentBrush, accentColor);
            if (chkLockAbove?.IsChecked == true)
                DrawLockLine(_lockAboveThreshold, below: false, w, h, accentBrush, accentColor);
        }

        private void DrawLockLine(double threshold, bool below, double w, double h, Brush accentBrush, Color accentColor)
        {
            double ly = ((100.0 - threshold) / 100.0) * h;

            // Held region: below the lower line (ly..h) or above the upper line (0..ly).
            var region = new Rectangle
            {
                Width = w,
                Height = below ? Math.Max(0, h - ly) : Math.Max(0, ly),
                Fill = new SolidColorBrush(Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)),
            };
            Canvas.SetLeft(region, 0);
            Canvas.SetTop(region, below ? ly : 0);
            canvasGrid.Children.Add(region);

            canvasPreview.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = ly, Y2 = ly,
                Stroke = accentBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 5, 3 },
            });
            var handle = new Ellipse { Width = 11, Height = 11, Fill = accentBrush };
            Canvas.SetLeft(handle, w - 14);
            Canvas.SetTop(handle, ly - 5.5);
            canvasPreview.Children.Add(handle);
        }

        // --- Lock line dragging ---------------------------------------------

        private void canvasPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            double h = canvasPreview.ActualHeight;
            if (h < 4) return;
            bool lowOn  = chkLock?.IsChecked == true;
            bool highOn = chkLockAbove?.IsChecked == true;
            if (!lowOn && !highOn) return;

            double y = e.GetPosition(canvasPreview).Y;
            if (lowOn && highOn)
            {
                double yLow  = ((100.0 - _lockThreshold)      / 100.0) * h;
                double yHigh = ((100.0 - _lockAboveThreshold) / 100.0) * h;
                _dragLine = Math.Abs(y - yLow) <= Math.Abs(y - yHigh) ? 1 : 2;
            }
            else _dragLine = lowOn ? 1 : 2;

            canvasPreview.CaptureMouse();
            SetLockFromY(y);
            e.Handled = true;
        }

        private void canvasPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragLine == 0) return;
            SetLockFromY(e.GetPosition(canvasPreview).Y);
        }

        private void canvasPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragLine == 0) return;
            _dragLine = 0;
            canvasPreview.ReleaseMouseCapture();
        }

        private void SetLockFromY(double y)
        {
            double h = canvasPreview.ActualHeight;
            if (h < 4) return;
            // Canvas y grows downward; pos 100 is at the top, pos 0 at the bottom.
            double pos = Math.Max(0, Math.Min(100, 100.0 - (y / h) * 100.0));
            if (_dragLine == 2) _lockAboveThreshold = pos;
            else                _lockThreshold = pos;
            UpdateLockLabel();
            DrawPreview();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            sliderMin.Value = 0;
            sliderMax.Value = 100;
            sliderIntensity.Value = 100;
            sliderVibrate.Value = 0;
            chkInvert.IsChecked = false;
            chkLock.IsChecked = false;
            chkLockAbove.IsChecked = false;
            _lockThreshold = 25;
            _lockAboveThreshold = 75;
            // Clear baked ranges AFTER unchecking (the Unchecked handler would otherwise re-bake).
            _bakedBelow = null;
            _bakedAbove = null;
            OnTransformChanged();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void SaveGallery_Click(object sender, RoutedEventArgs e)
        {
            if (_filesInFolder.Count == 0)
            {
                MessageBox.Show(this, "No funscripts to save.", "Funscript Editor",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Make sure the current script's edits are captured (Individual mode).
            if (_individualMode && _currentPath != null)
                _fileSettings[_currentPath] = CurrentSettings();

            // Resolve the destination folder name from the dropdown / name box.
            var sel = cmbSaveTarget.SelectedItem as string ?? NewGalleryItem;
            string folderName;
            if (sel == NewGalleryItem)
            {
                folderName = (txtNewGalleryName.Text ?? "").Trim();
                foreach (var c in Path.GetInvalidFileNameChars())
                    folderName = folderName.Replace(c, '_');
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = "gallery_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }
            else folderName = sel;

            var destFolder = Path.Combine(_galleryPath, folderName);

            // The first (locked) gallery can't be overwritten until it's unlocked.
            if (folderName == _lockedGalleryName && IsGalleryLocked(_lockedGalleryName))
            {
                ThemedDialog.Info(this, "Gallery locked", $"“{folderName}” is locked.",
                    "Select it in the Save dropdown, then click the 🔒 next to Gallery/ and type UNLOCK before saving over it.");
                return;
            }

            if (Directory.Exists(destFolder) && Directory.EnumerateFileSystemEntries(destFolder).Any())
            {
                var overwrite = MessageBox.Show(this,
                    $"Gallery \"{folderName}\" already exists.\n\nOverwrite its contents?",
                    "Gallery exists", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            try { Directory.CreateDirectory(destFolder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to create folder:\n{ex.Message}",
                    "Save error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Marker so this folder is treated as a saved gallery (kept out of the file list, listed in the dropdown).
            try { File.WriteAllText(Path.Combine(destFolder, GalleryMarker), ""); } catch { }

            var global = CurrentSettings();
            int saved = 0, failed = 0;
            foreach (var srcPath in _filesInFolder)
            {
                try
                {
                    var src = FunScriptFile.TryRead(srcPath);
                    if (src == null) { failed++; continue; }

                    var s = _individualMode
                        ? (_fileSettings.TryGetValue(srcPath, out var fs) ? fs : new EditSettings())
                        : global;

                    if (src.actions != null && src.actions.Count > 0)
                    {
                        var built = BuildActions(src.actions, s);
                        src.actions.Clear();
                        src.actions.AddRange(built);
                    }

                    // Preserve relative folder structure (e.g. detailed/...funscript)
                    var rel = Path.GetRelativePath(_galleryPath, srcPath);
                    var destPath = Path.Combine(destFolder, rel);
                    var destDir  = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    src.Save(destPath);
                    saved++;
                }
                catch
                {
                    failed++;
                }
            }

            // Copy Definitions.csv / BundleDefinition.txt so the result is a standalone gallery.
            foreach (var sidecar in new[] { "Definitions.csv", "BundleDefinition.txt" })
            {
                var src = Path.Combine(_galleryPath, sidecar);
                if (File.Exists(src))
                {
                    try { File.Copy(src, Path.Combine(destFolder, sidecar), overwrite: true); }
                    catch { }
                }
            }

            PopulateSaveTargets(folderName);

            var msg = $"Saved {saved} funscripts to:\n{destFolder}";
            if (failed > 0) msg += $"\n({failed} failed)";
            msg += _individualMode
                ? "\n\nEach script saved with its own edits."
                : "\n\nThe same edit was applied to every script.";
            msg += "\n\nPoint a game's Gallery override at this folder to use it.";

            MessageBox.Show(this, msg, "Gallery saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

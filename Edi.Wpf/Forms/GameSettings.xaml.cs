using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Edi.Core;
using Edi.Core.Services;
using Microsoft.Win32;

namespace Edi.Forms
{
    public partial class GameSettings : Window
    {
        public GameInfo? Result { get; private set; }
        public bool DeleteRequested { get; private set; }

        private readonly bool _isNew;
        private readonly ObservableCollection<LaunchOption> _launchOptions = new();

        public GameSettings(GameInfo? existing, bool isNew)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            icLaunch.ItemsSource = _launchOptions;

            // Open large enough to show all fields without scrolling when the screen allows.
            var wa = SystemParameters.WorkArea;
            Height = Math.Min(1040, wa.Height - 24);
            Width  = Math.Min(720, wa.Width - 24);

            _isNew = isNew;
            Title = isNew ? "Add Game" : "Edit Game";
            btnDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;

            if (existing != null)
            {
                txtName.Text = existing.Name ?? "";
                txtConfigPath.Text = existing.Path ?? "";
                txtExePath.Text = existing.ExePath ?? "";
                txtGalleryPath.Text = existing.GalleryPath ?? "";
                txtInfoPath.Text = existing.InfoPath ?? "";
                txtImagePath.Text = existing.ImagePath ?? "";
                if (existing.LaunchOptions != null)
                    foreach (var lo in existing.LaunchOptions)
                        _launchOptions.Add(new LaunchOption { Name = lo.Name, Path = lo.Path, Args = lo.Args });
            }

            // Game type (default EDI). Checking a radio fires GameType_Changed → relabels the path field.
            if (string.Equals(existing?.GameType, "ScriptPlayer", StringComparison.OrdinalIgnoreCase))
                rbTypeScript.IsChecked = true;
            else
                rbTypeEdi.IsChecked = true;

            txtExePath.TextChanged += (_, _) => UpdateLaunchVisibility();

            // Strip surrounding quotes live so a "Copy as path" paste (…"C:\foo"…) cleans itself up
            // visually instead of showing the quotes.
            foreach (var tb in new[] { txtConfigPath, txtExePath, txtGalleryPath, txtInfoPath })
                tb.TextChanged += StripQuotes_TextChanged;

            // When adding a game, best-guess all optional fields from the EdiConfig's folder.
            if (isNew) TryAutoFill();

            // Always auto-find the gallery/scripts folder when it's blank — even when EDITING an
            // existing game (e.g. one added before auto-detect existed). Re-runs when the config
            // path changes. Never overwrites a path the user has already set.
            AutoFillGalleryIfEmpty();
            txtConfigPath.TextChanged += (_, _) => AutoFillGalleryIfEmpty();

            UpdateLaunchVisibility();
        }

        // Auto-populate the optional fields (executable / gallery override / info doc) by scanning
        // the folder that holds EdiConfig.json. Only fills fields the user hasn't already set.
        private void TryAutoFill()
        {
            string config = AddGameDialog.CleanPath(txtConfigPath.Text);
            string? dir = null;
            try { dir = Path.GetDirectoryName(config); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            if (string.IsNullOrWhiteSpace(txtExePath.Text))
            {
                var exe = DetectGameExe(dir);
                if (exe != null) txtExePath.Text = exe;
            }
            if (string.IsNullOrWhiteSpace(txtGalleryPath.Text))
            {
                // Script Player games use a "Scripts" folder; EDI games use "Gallery".
                var gal = DetectGallery(dir, rbTypeScript.IsChecked == true ? "Scripts" : "Gallery");
                if (gal != null) txtGalleryPath.Text = gal;
            }
            if (string.IsNullOrWhiteSpace(txtInfoPath.Text))
            {
                var info = DetectInfo(dir);
                if (info != null) txtInfoPath.Text = info;
            }
            if (string.IsNullOrWhiteSpace(txtImagePath.Text))
            {
                var banner = DetectBanner(dir);
                if (banner != null) txtImagePath.Text = banner;
            }
        }

        // Auto-detect the gallery/scripts folder from the EdiConfig's folder, but ONLY when the field
        // is blank (never clobbers a user-set path). Safe to call repeatedly (open, config change,
        // type change) — it no-ops once a path is present or the folder can't be found.
        private void AutoFillGalleryIfEmpty()
        {
            if (txtGalleryPath == null || !string.IsNullOrWhiteSpace(txtGalleryPath.Text)) return;
            string config = AddGameDialog.CleanPath(txtConfigPath.Text);
            string? dir = null;
            try { dir = Path.GetDirectoryName(config); } catch { }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            // Script Player games use a "Scripts" folder; EDI games use "Gallery".
            string folderName = rbTypeScript.IsChecked == true ? "Scripts" : "Gallery";
            // Look under the config folder first; then widen to its parent (galleries often sit in a
            // SIBLING like ...\Mage Kanades\GAME\Gallery while EdiConfig.json lives in ...\Mage Kanades\3\).
            var gal = DetectGallery(dir, folderName) ?? DetectGalleryNear(dir, folderName);
            if (gal != null) txtGalleryPath.Text = gal;
        }

        // Widen the gallery search to the config folder's parent (the game root), so a gallery that's a
        // sibling of the EdiConfig folder is still found. Guards against crawling a giant games-root.
        private static string? DetectGalleryNear(string configDir, string folderName)
        {
            try
            {
                var parent = Directory.GetParent(configDir)?.FullName;
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;
                // If the parent has lots of subfolders it's probably a games-root (e.g. ...\Script) —
                // don't crawl it or we'd match some other game's gallery.
                int subdirs = 0;
                try { subdirs = Directory.EnumerateDirectories(parent).Take(40).Count(); } catch { }
                if (subdirs > 30) return null;
                return DetectGallery(parent, folderName);
            }
            catch { return null; }
        }

        // Pick the most likely game .exe next to EdiConfig.json, skipping EDI itself and the
        // usual non-game executables (uninstallers, redists, helpers, unpackers, crash handlers).
        private static readonly string[] _exeSkip =
            { "unins", "setup", "redist", "crashhandler", "crashreport", "crashpad", "helper",
              "unpacker", "dxwebsetup", "directx", "nwjc", "python", "report", "vcredist",
              "dotnet", "cleaner", "updater", "config", "benchmark" };

        // A game folder is "Script Player" if it bundles a funscript player (e.g. ...\FunscriptPlayer v1.3.1);
        // EDI games don't. (Both kinds contain .funscript files, so that alone can't tell them apart.)
        public static bool LooksLikeScriptPlayer(string dir)
        {
            try
            {
                var queue = new Queue<(string path, int depth)>();
                queue.Enqueue((dir, 0));
                while (queue.Count > 0)
                {
                    var (cur, depth) = queue.Dequeue();
                    if (depth > 0 && Path.GetFileName(cur).Replace(" ", "").ToLowerInvariant().Contains("funscriptplayer"))
                        return true;
                    if (depth >= 4) continue;
                    try { foreach (var sub in Directory.EnumerateDirectories(cur, "*", SearchOption.TopDirectoryOnly)) queue.Enqueue((sub, depth + 1)); }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        public static string? DetectGameExe(string dir)
        {
            bool Skip(string p)
            {
                var n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                if (n == "edi") return true;                 // EDI's own launcher
                return _exeSkip.Any(k => n.Contains(k));
            }

            // Choose the most likely game .exe from a candidate set.
            string? Pick(IEnumerable<string> exes)
            {
                var candidates = exes.Where(p => !Skip(p)).ToList();
                if (candidates.Count == 0) return null;
                if (candidates.Count == 1) return candidates[0];

                // Prefer common launcher names or one matching the folder.
                string folderName = new DirectoryInfo(dir).Name.ToLowerInvariant();
                var byName = candidates.FirstOrDefault(p =>
                {
                    var n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
                    return n is "game" or "nw" or "start" or "play" || n == folderName;
                });
                if (byName != null) return byName;

                // Prefer one sitting in a typical game subfolder.
                var inGameDir = candidates.FirstOrDefault(p =>
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "").ToLowerInvariant();
                    return parent is "game" or "bin" or "app" or "win" or "win64" or "game64";
                });
                if (inGameDir != null) return inGameDir;

                try { return candidates.OrderByDescending(p => new FileInfo(p).Length).First(); }
                catch { return candidates[0]; }
            }

            List<string> Exes(string d)
            {
                try { return Directory.EnumerateFiles(d, "*.exe", SearchOption.TopDirectoryOnly).ToList(); }
                catch { return new List<string>(); }
            }

            // 1) Right next to EdiConfig.json (the common case).
            var hit = Pick(Exes(dir));
            if (hit != null) return hit;

            // 2) Nothing at the top — look one level down (e.g. a "GAME" subfolder).
            try
            {
                var nested = new List<string>();
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                    nested.AddRange(Exes(sub));
                hit = Pick(nested);
                if (hit != null) return hit;
            }
            catch { }

            // 3) Still nothing — maybe an HTML game.
            return FindHtmlGame(dir);
        }

        private static bool IsHtml(string p)
        {
            var e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".html" || e == ".htm";
        }

        // Non-game HTML pages we should never auto-pick as the launchable game.
        private static readonly string[] _htmlSkip =
            { "local", "credits", "readme", "help", "license", "walkthrough", "patreon", "faq", "changelog" };

        // HTML-based games (e.g. RPG Maker MV/MZ web builds): look next to EdiConfig and one level
        // down. Skip obvious non-game pages, then prefer a game/www folder, then index.html.
        private static string? FindHtmlGame(string dir)
        {
            try
            {
                var all = new List<string>();
                void Collect(string d)
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(d, "*.htm*", SearchOption.TopDirectoryOnly))
                        {
                            if (!IsHtml(f)) continue;
                            var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                            if (_htmlSkip.Contains(n)) continue;
                            all.Add(f);
                        }
                    }
                    catch { }
                }
                Collect(dir);
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                    Collect(sub);

                if (all.Count == 0) return null;
                if (all.Count == 1) return all[0];

                // Prefer one inside a game/www folder, then index.html, then the largest.
                var inGameDir = all.FirstOrDefault(p =>
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(p) ?? "").ToLowerInvariant();
                    return parent is "game" or "www" or "htmlgame" or "html";
                });
                if (inGameDir != null) return inGameDir;

                var idx = all.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                                                      .Equals("index", StringComparison.OrdinalIgnoreCase));
                if (idx != null) return idx;

                try { return all.OrderByDescending(p => new FileInfo(p).Length).First(); }
                catch { return all[0]; }
            }
            catch { return null; }
        }

        // Auto-find the scripts/gallery folder: look for one named `folderName` next to EdiConfig,
        // then one level down. Returns its full path (so it's filled in even when it's the default).
        public static string? DetectGallery(string dir, string folderName)
        {
            // Find the Scripts/Gallery folder at any reasonable depth (some games bundle a player,
            // e.g. ...\game\FunscriptPlayer\scripts). Shallowest match wins.
            var found = FindNamedDir(dir, folderName, maxDepth: 6);
            if (found == null) return null;

            // Script Player: the .funscript files often sit in a per-title subfolder
            // (e.g. ...\scripts\summer). If the scripts folder has none directly but a subfolder
            // does, point at that subfolder instead.
            if (folderName.Equals("Scripts", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bool hasDirect = Directory.EnumerateFiles(found, "*.funscript", SearchOption.TopDirectoryOnly).Any();
                    if (!hasDirect)
                    {
                        var withScripts = Directory.EnumerateDirectories(found, "*", SearchOption.TopDirectoryOnly)
                            .Where(d => { try { return Directory.EnumerateFiles(d, "*.funscript", SearchOption.TopDirectoryOnly).Any(); } catch { return false; } })
                            .ToList();
                        if (withScripts.Count == 1) return withScripts[0];
                        if (withScripts.Count > 1)
                        {
                            // Prefer a subfolder whose name matches the game (e.g. "summer" ~ "Summer Memories").
                            string game = new DirectoryInfo(dir).Name.ToLowerInvariant();
                            var match = withScripts.FirstOrDefault(d =>
                            {
                                var n = Path.GetFileName(d).ToLowerInvariant();
                                return n.Length > 1 && (game.Contains(n) || n.Contains(game));
                            });
                            if (match != null) return match;
                        }
                    }
                }
                catch { }
            }
            return found;
        }

        // Breadth-first search for the shallowest directory named `name` (case-insensitive) under
        // `root`, descending at most `maxDepth` levels. Returns null if not found.
        private static string? FindNamedDir(string root, string name, int maxDepth)
        {
            try
            {
                var queue = new Queue<(string path, int depth)>();
                queue.Enqueue((root, 0));
                while (queue.Count > 0)
                {
                    var (cur, depth) = queue.Dequeue();
                    if (depth > 0 && Path.GetFileName(cur).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return cur;
                    if (depth >= maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(cur, "*", SearchOption.TopDirectoryOnly))
                            queue.Enqueue((sub, depth + 1));
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Find a likely info doc (.txt/.md/.pdf) next to EdiConfig — prefer readme/info/guide
        // names; otherwise only auto-pick when there's exactly one, to avoid grabbing junk.
        private static readonly string[] _infoExts = { ".txt", ".md", ".pdf" };
        private static readonly string[] _infoPrefer =
            { "info", "readme", "read me", "walkthrough", "guide", "manual", "help", "cheat" };

        public static string? DetectInfo(string dir)
        {
            List<string> Docs(string d)
            {
                try
                {
                    return Directory.EnumerateFiles(d, "*.*", SearchOption.TopDirectoryOnly)
                                    .Where(p => _infoExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
                                    .ToList();
                }
                catch { return new List<string>(); }
            }

            string? Pick(List<string> docs)
            {
                if (docs.Count == 0) return null;
                foreach (var key in _infoPrefer)
                {
                    var hit = docs.FirstOrDefault(p =>
                        Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(key));
                    if (hit != null) return hit;
                }
                return docs.Count == 1 ? docs[0] : null;
            }

            // Prefer a doc right next to EdiConfig.json; otherwise look one level down (e.g. a "GAME" folder).
            var hit = Pick(Docs(dir));
            if (hit != null) return hit;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    hit = Pick(Docs(sub));
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        // Auto-find a cover/banner for the game: a static image first (preferring cover/banner-ish
        // names, else the largest), otherwise a short trailer video (the cards play it on hover).
        private static readonly string[] _bannerImgExt = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
        private static readonly string[] _bannerVidExt = { ".mp4", ".m4v", ".mov" };
        private static readonly string[] _bannerImgPrefer =
            { "cover", "banner", "header", "capsule", "poster", "library", "hero", "grid", "key", "main", "box", "title", "folder" };
        private static readonly string[] _bannerVidPrefer = { "micro", "trailer", "preview", "teaser", "cover", "loop" };

        public static string? DetectBanner(string dir)
        {
            try
            {
                // The game folder + a few likely media subfolders + each immediate subfolder.
                var folders = new List<string> { dir };
                try { folders.AddRange(Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly)); } catch { }
                folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                IEnumerable<string> FilesWith(string[] exts) =>
                    folders.SelectMany(f =>
                    {
                        try { return Directory.EnumerateFiles(f, "*.*", SearchOption.TopDirectoryOnly)
                                    .Where(p => exts.Contains(Path.GetExtension(p).ToLowerInvariant())); }
                        catch { return Enumerable.Empty<string>(); }
                    }).ToList();

                // 1) Static image (always-visible banner).
                var images = FilesWith(_bannerImgExt).ToList();
                if (images.Count > 0)
                {
                    foreach (var key in _bannerImgPrefer)
                    {
                        var hit = images.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(key));
                        if (hit != null) return hit;
                    }
                    try { return images.OrderByDescending(p => new FileInfo(p).Length).First(); } catch { return images[0]; }
                }

                // 2) Trailer video (cards play it on hover/selection). Prefer a short one.
                var vids = FilesWith(_bannerVidExt).ToList();
                if (vids.Count > 0)
                {
                    foreach (var key in _bannerVidPrefer)
                    {
                        var hit = vids.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(key));
                        if (hit != null) return hit;
                    }
                    try { return vids.OrderBy(p => new FileInfo(p).Length).First(); } catch { return vids[0]; }   // smallest ~ shortest
                }
            }
            catch { }
            return null;
        }

        private void GameType_Changed(object sender, RoutedEventArgs e)
        {
            if (lblConfigTitle == null) return;
            bool script = rbTypeScript.IsChecked == true;
            lblConfigTitle.Text = script
                ? "SCRIPT PLAYER LOCATION  ·  EDICONFIG.JSON"
                : "EDI LOCATION  ·  EDICONFIG.JSON";

            // The gallery field is the "scripts" folder for Script Player games.
            if (lblGalleryTitle != null)
                lblGalleryTitle.Text = script ? "SCRIPTS FOLDER" : "GALLERY FOLDER";
            if (lblGalleryHint != null)
                lblGalleryHint.Text = script
                    ? @"defaults to  .\Scripts  next to Funscript Player.exe"
                    : @"defaults to  .\Gallery  next to EdiConfig.json";

            // Color-code the whole dialog: Script Player = blue, EDI = pink. Swapping the local
            // App.Accent recolors all DynamicResource usages (section icons, the identity-card
            // outline, textbox focus); the accent buttons are restyled explicitly.
            var accent = script
                ? System.Windows.Media.Color.FromRgb(0x4D, 0x93, 0xFF)
                : System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x8C);
            var accentBrush = new System.Windows.Media.SolidColorBrush(accent);
            accentBrush.Freeze();
            Resources["App.Accent"] = accentBrush;
            if (btnLaunch != null) { btnLaunch.Background = accentBrush; btnLaunch.BorderBrush = accentBrush; }
            if (btnSave   != null) { btnSave.Background   = accentBrush; btnSave.BorderBrush   = accentBrush; }

            // Switching type changes which folder to look for (Gallery vs Scripts) — re-detect if blank.
            AutoFillGalleryIfEmpty();
        }

        private void UpdateLaunchVisibility()
        {
            var path = txtExePath.Text?.Trim();
            btnLaunch.Visibility = (!string.IsNullOrEmpty(path) && File.Exists(path))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void BrowseConfig_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select EdiConfig.json",
                Filter = "EdiConfig.json|EdiConfig.json|JSON files|*.json|All files|*.*",
                FilterIndex = 1,
                FileName = txtConfigPath.Text,
            };
            if (dlg.ShowDialog(this) == true)
            {
                txtConfigPath.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(txtName.Text))
                {
                    try { txtName.Text = new DirectoryInfo(Path.GetDirectoryName(dlg.FileName)!).Name; }
                    catch { }
                }
            }
        }

        private void BrowseExe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select game executable",
                Filter = "Game (exe / html)|*.exe;*.html;*.htm|Executables|*.exe|HTML games|*.html;*.htm|All files|*.*",
                FilterIndex = 1,
                FileName = txtExePath.Text,
            };
            if (dlg.ShowDialog(this) == true)
            {
                txtExePath.Text = dlg.FileName;
            }
        }

        // ───────── extra launch options ─────────

        private void AddLaunch_Click(object sender, RoutedEventArgs e) => _launchOptions.Add(new LaunchOption());

        private void RemoveLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is LaunchOption lo)
                _launchOptions.Remove(lo);
        }

        private void BrowseLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button b || b.Tag is not LaunchOption lo) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select launch target",
                Filter = "Programs (exe / bat / lnk / html)|*.exe;*.bat;*.cmd;*.lnk;*.html;*.htm|All files|*.*",
                FileName = lo.Path,
            };
            if (dlg.ShowDialog(this) == true)
            {
                lo.Path = dlg.FileName;   // INotifyPropertyChanged (Fody) refreshes the bound TextBox
                if (string.IsNullOrWhiteSpace(lo.Name))
                    lo.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
            }
        }

        private void BrowseGallery_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select gallery folder (folder of .funscript files)",
                Multiselect = false,
            };
            var seed = txtGalleryPath.Text;
            if (!string.IsNullOrWhiteSpace(seed) && Directory.Exists(seed))
            {
                dlg.InitialDirectory = seed;
            }
            else if (!string.IsNullOrWhiteSpace(txtConfigPath.Text))
            {
                try { dlg.InitialDirectory = Path.GetDirectoryName(txtConfigPath.Text); }
                catch { }
            }
            if (dlg.ShowDialog(this) == true)
            {
                txtGalleryPath.Text = dlg.FolderName;
            }
        }

        private void BrowseInfo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select an info file (txt, md or pdf)",
                Filter = "Info files|*.txt;*.md;*.pdf|Text files|*.txt;*.md|PDF files|*.pdf|All files|*.*",
                FilterIndex = 1,
                FileName = txtInfoPath.Text,
            };
            if (dlg.ShowDialog(this) == true)
            {
                txtInfoPath.Text = dlg.FileName;
            }
        }

        private void BrowseImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select a cover image",
                Filter = "Cover image or video|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.m4v;*.mov|All files|*.*",
                FilterIndex = 1,
                FileName = txtImagePath.Text,
            };
            if (dlg.ShowDialog(this) == true)
            {
                txtImagePath.Text = dlg.FileName;
            }
        }

        // When a field gains focus, WPF scrolls it flush against the ScrollViewer's clip edge,
        // which shaves the control's focus border (looked "cut off" on the GAME EXECUTABLE box).
        // Inflate the bring-into-view rect so a few pixels of breathing room are always kept.
        private bool _bringingIntoView;
        private void Scroll_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (_bringingIntoView || e.TargetObject is not FrameworkElement fe) return;
            if (fe is System.Windows.Controls.ScrollViewer) return;
            e.Handled = true;
            _bringingIntoView = true;
            try { fe.BringIntoView(new Rect(-8, -8, fe.ActualWidth + 16, fe.ActualHeight + 16)); }
            finally { _bringingIntoView = false; }
        }

        // ===================== Cover image (preview + right-click options) =====================

        private bool _strippingQuotes;

        // Remove a surrounding pair of double-quotes from a path field (in place). Returns true if it
        // changed the text (which re-fires TextChanged with the cleaned value).
        private bool StripSurroundingQuotes(System.Windows.Controls.TextBox tb)
        {
            if (_strippingQuotes || tb?.Text == null) return false;
            var t = tb.Text;
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            {
                _strippingQuotes = true;
                tb.Text = t.Substring(1, t.Length - 2).Trim();
                tb.CaretIndex = tb.Text.Length;
                _strippingQuotes = false;
                return true;
            }
            return false;
        }

        private void StripQuotes_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb) StripSurroundingQuotes(tb);
        }

        private void ImagePath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (StripSurroundingQuotes(txtImagePath)) return;   // re-fires with the cleaned path
            UpdateCoverPreview();
        }

        private void UpdateCoverPreview()
        {
            var path = txtImagePath.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".mp4" or ".m4v" or ".mov")
                {
                    imgPreview.Source = null;
                    lblNoCover.Text = "Video cover — plays on the card";
                    lblNoCover.Visibility = Visibility.Visible;
                    return;
                }
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.DecodePixelWidth = 600;
                    bmp.EndInit();
                    bmp.Freeze();
                    imgPreview.Source = bmp;
                    lblNoCover.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { }
            }
            imgPreview.Source = null;
            lblNoCover.Text = "No cover — right-click for options";
            lblNoCover.Visibility = Visibility.Visible;
        }

        private async void CoverSearch_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CoverSearchDialog(txtName.Text ?? "") { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedImageUrl)) return;
            try { txtImagePath.Text = await DownloadCoverAsync(dlg.SelectedImageUrl, txtName.Text ?? "cover"); }
            catch (Exception ex) { ThemedDialog.Info(this, "Couldn't fetch image", "Download failed", ex.Message); }
        }

        private async void CoverUrl_Click(object sender, RoutedEventArgs e)
        {
            var url = ThemedDialog.Prompt(this, "Set image from URL", "Paste an image URL",
                "The image is downloaded and stored next to your save data, then used as this game's cover.",
                txtImagePath.Text);
            if (string.IsNullOrWhiteSpace(url)) return;
            try { txtImagePath.Text = await DownloadCoverAsync(url, txtName.Text ?? "cover"); }
            catch (Exception ex) { ThemedDialog.Info(this, "Couldn't fetch image", "Download failed", ex.Message); }
        }

        private void CropCover_Click(object sender, RoutedEventArgs e)
        {
            var path = txtImagePath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ThemedDialog.Info(this, "Nothing to crop", "No cover set",
                    "Set a cover image first, then crop it to fit the card.");
                return;
            }
            double h = Math.Max(AppLocalSettings.Load().CardHeight, 1);
            var dlg = new CropDialog(path, 300.0 / h) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.CroppedPath))
                txtImagePath.Text = dlg.CroppedPath;
        }

        private void ClearCover_Click(object sender, RoutedEventArgs e) => txtImagePath.Text = "";

        private static readonly HttpClient _coverHttp = CreateCoverHttp();
        private static HttpClient CreateCoverHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Edi");
            return h;
        }

        private static async Task<string> DownloadCoverAsync(string url, string gameName)
        {
            var bytes = await _coverHttp.GetByteArrayAsync(url);
            var dir = System.IO.Path.Combine(Edi.Core.Edi.OutputDir, "covers");
            Directory.CreateDirectory(dir);
            var ext = System.IO.Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".jpg";
            var safe = string.Join("_", gameName.Split(System.IO.Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "cover";
            var file = System.IO.Path.Combine(dir, safe + ext);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }

        private void LaunchGame_Click(object sender, RoutedEventArgs e)
        {
            var path = txtExePath.Text?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to launch:\n{ex.Message}", "Launch error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = txtName.Text?.Trim() ?? "";
            // CleanPath also strips surrounding quotes from "Copy as path" pastes.
            var configPath = AddGameDialog.CleanPath(txtConfigPath.Text);
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "Name cannot be empty.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(configPath))
            {
                MessageBox.Show(this, "EdiConfig.json path is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string? exe     = string.IsNullOrWhiteSpace(txtExePath.Text)     ? null : AddGameDialog.CleanPath(txtExePath.Text);
            string? gallery = string.IsNullOrWhiteSpace(txtGalleryPath.Text) ? null : AddGameDialog.CleanPath(txtGalleryPath.Text);
            string? info    = string.IsNullOrWhiteSpace(txtInfoPath.Text)    ? null : AddGameDialog.CleanPath(txtInfoPath.Text);
            string? image   = string.IsNullOrWhiteSpace(txtImagePath.Text)   ? null : txtImagePath.Text.Trim();

            // Keep only launch options that have a target; default a blank name to the file name.
            var launch = _launchOptions
                .Where(o => !string.IsNullOrWhiteSpace(o.Path))
                .Select(o =>
                {
                    var p = AddGameDialog.CleanPath(o.Path);
                    var nm = string.IsNullOrWhiteSpace(o.Name)
                        ? (Path.GetFileNameWithoutExtension(p) ?? "Launch")
                        : o.Name.Trim();
                    return new LaunchOption { Name = nm, Path = p, Args = (o.Args ?? "").Trim() };
                })
                .ToList();

            Result = new GameInfo(name, configPath)
            {
                ExePath = exe,
                GalleryPath = gallery,
                InfoPath = info,
                ImagePath = image,
                GameType = rbTypeScript.IsChecked == true ? "ScriptPlayer" : "EDI",
                LaunchOptions = launch.Count > 0 ? launch : null,
            };
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
            Close();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(this,
                $"Remove \"{txtName.Text}\" from the games list?",
                "Confirm remove",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            DeleteRequested = true;
            Result = null;
            DialogResult = true;
            Close();
        }
    }
}

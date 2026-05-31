using AvalonDock.Layout;
using Edi.Core;
using Edi.Core.Device;
using Edi.Core.Device.Simulator;
using Edi.Core.Gallery.Funscript;
using Edi.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Edi.Core.Device.Buttplug;
using Edi.Core.Device.EStim;
using Edi.Core.Device.Handy;
using Edi.Core.Device.Interfaces;
using Edi.Core.Device.OSR;
using Edi.Core.Gallery;
using Path = System.IO.Path;

namespace Edi.Forms
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IEdi edi = App.Edi;
        public EdiConfig config;
        public GalleryConfig galleryConfig;
        public HandyConfig handyConfig;
        public ButtplugConfig buttplugConfig;
        public EStimConfig estimConfig;
        public OSRConfig osrConfig;
        private Timer timer;
        private bool launched;
        private record AudioDevice(int id, string name);
        private record ComPort(string name, string? value);
        private record ChannelsNames(string name, string? value);
        private DataGridColumn _channelColumn;
        private bool _suppressGameReload;
        private static readonly HttpClient _coverHttp = CreateCoverHttp();

        // Programs shown on the top quick-launch bar (Intiface, funscript player, etc.).
        public System.Collections.ObjectModel.ObservableCollection<QuickProgram> QuickPrograms { get; } = new();

        // Launcher-pill layout: Vertical = URL stacked under the name, Horizontal = URL beside it.
        public static readonly DependencyProperty LauncherUrlOrientationProperty =
            DependencyProperty.Register(nameof(LauncherUrlOrientation), typeof(Orientation), typeof(MainWindow),
                new PropertyMetadata(Orientation.Vertical));
        public Orientation LauncherUrlOrientation
        {
            get => (Orientation)GetValue(LauncherUrlOrientationProperty);
            set => SetValue(LauncherUrlOrientationProperty, value);
        }

        // Drives the rounded-clip radius of each game card (the card border/outline use Card.Corner;
        // the clip geometry reads this so the cover art rounds to the same amount).
        public static readonly DependencyProperty CardCornerValueProperty =
            DependencyProperty.Register(nameof(CardCornerValue), typeof(double), typeof(MainWindow),
                new PropertyMetadata(8.0));
        public double CardCornerValue
        {
            get => (double)GetValue(CardCornerValueProperty);
            set => SetValue(CardCornerValueProperty, value);
        }

        public MainWindow()
        {
            config = edi.ConfigurationManager.Get<EdiConfig>();
            handyConfig = edi.ConfigurationManager.Get<HandyConfig>();
            galleryConfig = edi.ConfigurationManager.Get<GalleryConfig>();
            buttplugConfig = edi.ConfigurationManager.Get<ButtplugConfig>();
            estimConfig = edi.ConfigurationManager.Get<EStimConfig>();
            osrConfig = edi.ConfigurationManager.Get<OSRConfig>();
            gamesConfig = edi.ConfigurationManager.Get<GamesConfig>();
            List<Core.Gallery.Definition.DefinitionGallery> galleries = ReloadGalleries();

            viewModel = new MainWindowViewModel
            {
                config = config,
                handyConfig = handyConfig,
                buttplugConfig = buttplugConfig,
                galleryConfig = galleryConfig,
                estimConfig = estimConfig,
                osrConfig = osrConfig,
                gamesConfig = gamesConfig,
                devices = edi.Devices,
                channels = edi.Player.Channels,
                galleries = galleries,
            };
            this.DataContext = viewModel;
            DarkTitleBar.Apply(this);
            InitializeComponent();

            // Add column visibility control after InitializeComponent
            DevicesGrid.Loaded += (s, e) =>
            {
                _channelColumn = DevicesGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == "Channel");
                UpdateChannelColumnVisibility();
            };

            // Add property change handler for viewModel
            viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(viewModel.config))
                {
                    UpdateChannelColumnVisibility();
                }
            };

            edi.DeviceCollector.OnloadDevice += DeviceCollector_OnloadDeviceAsync;
            edi.DeviceCollector.OnUnloadDevice += DeviceCollector_OnUnloadDevice;
            edi.OnChangeStatus += Edi_OnChangeStatus;

            timer = new Timer(RefrehGrid);
            timer.Change(3000, 3000);
            // Visible confirmation in the Log panel that this build will never auto-start a game.
            // If you ever see this AND the game still opens by itself, the trigger is outside EDI
            // (Startup folder shortcut, scheduled task, in-game wrapper script).
            try { AppendLog("EDI started — game auto-launch is disabled. Use LAUNCH GAME to start a game."); } catch { }

            Closing += MainWindow_Closing;

            edi.Player.ChannelsChanged += (channels) => viewModel.UpdateChannels(channels);

            LoadForm();
        }

        private void UpdateChannelColumnVisibility()
        {

            if (_channelColumn != null && viewModel?.config != null)
            {
                try
                {
                    _channelColumn.Visibility = viewModel.config.UseChannels ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
            }
        }

        private List<Core.Gallery.Definition.DefinitionGallery> ReloadGalleries()
        {
            var galleries = edi.Definitions.Where(x => x.Type != "filler").ToList();

            galleries.Insert(0, new Core.Gallery.Definition.DefinitionGallery { Name = "" });
            galleries.Insert(1, new Core.Gallery.Definition.DefinitionGallery { Name = "(Random)" });
            galleries.InsertRange(2, edi.Definitions.Where(x => x.Type == "filler"));
            return galleries;
        }
        private void RefrehGrid(object? o)
        {
            // Auto-launch is COMPLETELY removed from this timer. Earlier builds gated it behind
            // AppLocalSettings.BlockAutoLaunch (default true); this build has zero code paths
            // that can launch a game from a device-ready signal — the gate isn't even consulted.
            // Use the LAUNCH GAME button or a Create-shortcut .lnk to start a game manually.
            //
            // If you ever see the game start "on its own" with this build deployed, the trigger
            // is something OUTSIDE Edi.exe — most often:
            //   • a desktop .lnk from "Create shortcut" left in the Startup folder
            //   • a Windows Task Scheduler entry or shell:startup item
            //   • the launcher being started from inside a per-game folder whose helper exe
            //     spawns the game alongside the in-game Edi.exe (that nested copy isn't this build).
            _ = o;
        }
        private void ExecuteCommandOrOpenPath(string commandOrPath)
        {
            try
            {
                if (commandOrPath.StartsWith("http://") || commandOrPath.StartsWith("https://"))
                {
                    // Abrir URL en el navegador predeterminado
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = commandOrPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Resolve a relative path against the selected game's config folder,
                    // not EDI's working directory (the standalone exe runs from GAME\EDI).
                    var resolved = commandOrPath;
                    if (!Path.IsPathRooted(resolved))
                    {
                        var game = GamesComboBox?.SelectedItem as GameInfo ?? gamesConfig?.SelectedGameinfo;
                        var baseDir = !string.IsNullOrWhiteSpace(game?.Path) ? Path.GetDirectoryName(game.Path) : null;
                        if (!string.IsNullOrEmpty(baseDir))
                            resolved = Path.GetFullPath(Path.Combine(baseDir, commandOrPath));
                    }

                    if (File.Exists(resolved) || Directory.Exists(resolved))
                    {
                        Process.Start(new ProcessStartInfo(resolved)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(resolved) ?? ""
                        });
                    }
                    else
                    {
                        throw new FileNotFoundException($"El archivo o comando no existe: {resolved}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al ejecutar el comando o abrir la ruta: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadForm()
        {
            RestoreWindowBounds();
            var audios = new List<AudioDevice>() { new AudioDevice(-1, "None") };
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                audios.Add(new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName));
            }
            audioDevicesComboBox.ItemsSource = audios;
            loadOSRPorts();
            DevicesGrid.ItemsSource = edi.Devices;
            // The embedded Playback visualizer registers a hidden "Preview Device"; keep it out of the grid.
            var devView = System.Windows.Data.CollectionViewSource.GetDefaultView(edi.Devices);
            if (devView != null) devView.Filter = o => o is not Edi.Core.Device.Simulator.PreviewDevice;
            logList.ItemsSource = _logEntries;
            InitSettingsPanel();
            InitQuickPrograms();
            StartStatusPolling();   // periodic dot poll for Stash / Glyph / future URL pills
            SetupDocking();
            InitTopBarChips();

            // The host's startup load only sees the default "./Gallery" (usually empty), so the restored
            // game's real gallery — and each device's "Selected Variant" list — never loads until the user
            // manually re-picks the game. Load it once the UI is rendered (after host startup, so the same
            // code path the game-picker uses is safe to call).
            ContentRendered += async (_, __) => await TryLoadSelectedGameOnStartup();
        }

        private bool _startupGameLoaded;
        private async Task TryLoadSelectedGameOnStartup()
        {
            if (_startupGameLoaded) return;
            _startupGameLoaded = true;

            // (1) If the launcher has a restored selected game, load its gallery the same way the
            //     game-picker does. A per-game EDI has no selected game — it already loaded its own
            //     .\Gallery via the host, so we just skip this part.
            try
            {
                var game = gamesConfig?.SelectedGameinfo;
                bool hasGallery = game != null && !string.IsNullOrWhiteSpace(game.GalleryPath) && System.IO.Directory.Exists(game.GalleryPath);
                bool hasConfig  = game != null && !string.IsNullOrWhiteSpace(game.Path) && System.IO.File.Exists(game.Path);
                if (hasGallery || hasConfig)
                {
                    await edi.Init(game.Path, game.GalleryPath);
                    viewModel.galleries = ReloadGalleries();
                }
            }
            catch { /* fall through — still attach the preview below */ }

            // (2) Always light up the Playback visualizer (unless disabled in Settings → Dev). This runs
            //     for BOTH the launcher and a per-game EDI, so the script shows during playback either way.
            try
            {
                if (_previewDevice == null && AppLocalSettings.Load().PreviewDeviceEnabled)
                    ShowEmbeddedPreview();
            }
            catch { }
        }

        // Move the panel content from the (hidden) classic grid into the dockable panes, so users
        // can drag/float/tab/dock them. Panel internals are untouched — only their host changes.
        private void SetupDocking()
        {
            try { dockManager.Theme = new AvalonDock.Themes.Vs2013DarkTheme(); } catch { }

            // Restore the saved arrangement if there is one; otherwise drop the cards into the
            // default panes. Each card is placed EXACTLY ONCE — assigning a card that already has
            // a parent throws and would silently revert to the default layout.
            if (!TryRestoreDockLayout())
                InjectDefaultDockLayout();

            // Apply the correct Info-tab state now that its pane has content.
            RefreshInfoPanel();

            // Give each pane a tab/caption icon, and force the Playback caption casing
            // (the saved layout may still carry the old "PLAYBACK").
            ApplyPaneChrome();

            // After a conversion, reload galleries so the new variant shows in the Devices list.
            if (FmConverterCard != null) FmConverterCard.ConversionSaved += FmConverter_OnSaved;

            // Auto-persist the arrangement so it survives even a hard kill / crash.
            StartDockAutoSave();
        }

        // Per-pane tab/caption icon (a Segoe MDL2 glyph rendered to a vector image) + canonical title.
        private void ApplyPaneChrome()
        {
            var root = dockManager?.Layout;
            if (root == null) return;
            var all = root.Descendents().OfType<LayoutAnchorable>().ToList();
            if (root.Hidden != null) all.AddRange(root.Hidden.OfType<LayoutAnchorable>());
            foreach (var a in all)
            {
                a.IconSource = IconForId(a.ContentId);
                if (a.ContentId == "playback" && a.Title != "Playback") a.Title = "Playback";
            }
        }

        private readonly Dictionary<string, ImageSource> _paneIconCache = new();
        private ImageSource? IconForId(string? id)
        {
            string glyph = id switch
            {
                "playback"   => "",   // play
                "log"        => "",   // page / list
                "options"    => "",   // devices
                "liveedit"   => "",   // edit
                "game"       => "",   // gamepad
                "connection" => "",   // link
                "info"       => "",   // info
                _            => ""
            };
            // Derive the Segoe MDL2 glyph from an explicit codepoint (avoids literal-glyph encoding issues).
            int cp = id switch
            {
                "playback"   => 0xE768,   // Play
                "log"        => 0xE7C3,   // Page / list
                "options"    => 0xE772,   // Devices
                "liveedit"   => 0xE70F,   // Edit
                "game"       => 0xE7FC,   // Gamepad
                "connection" => 0xE968,   // Link
                "info"       => 0xE946,   // Info
                "fmconverter" => 0xE895,  // Sync / rotation — rotary FM converter
                _            => 0
            };
            if (cp == 0) return null;
            glyph = char.ConvertFromUtf32(cp);
            if (_paneIconCache.TryGetValue(glyph, out var cached)) return cached;
            try
            {
                var tf = new Typeface(new FontFamily("Segoe MDL2 Assets"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    tf, 16, Brushes.White, 1.0);
                var geo = ft.BuildGeometry(new System.Windows.Point(0, 0));
                var gd = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), null, geo);
                gd.Freeze();
                var di = new DrawingImage(gd);
                di.Freeze();
                _paneIconCache[glyph] = di;
                return di;
            }
            catch { return null; }
        }

        private FrameworkElement CardForId(string id) => id switch
        {
            "connection" => ConnectionCard,
            "game"       => GameCardBorder,
            "playback"   => PlaybackCard,
            "liveedit"   => LiveEditCard,
            "log"        => LogCard,
            "options"    => OptionsCard,
            "info"       => InfoCard,
            "fmconverter" => FmConverterCard,
            "previewplayer" => PreviewPlayerCard,
            _            => null
        };

        // Move a card out of wherever it currently lives into the given pane.
        private void PlaceCard(AvalonDock.Layout.LayoutAnchorable a, FrameworkElement card)
        {
            if (a == null || card == null) return;
            (card.Parent as Panel)?.Children.Remove(card);
            card.Margin = new Thickness(0);
            card.Visibility = Visibility.Visible;
            a.Content = card;
        }

        private void InjectDefaultDockLayout()
        {
            PlaceCard(anchConnection, ConnectionCard);
            PlaceCard(anchGame,       GameCardBorder);
            PlaceCard(anchPlayback,   PlaybackCard);
            PlaceCard(anchLiveEdit,   LiveEditCard);
            PlaceCard(anchLog,        LogCard);
            PlaceCard(anchOptions,    OptionsCard);
            PlaceCard(anchInfo,       InfoCard);
            PlaceCard(anchFm,         FmConverterCard);
            anchFm?.Hide();   // FM converter panel is optional — off until toggled on in Settings
            PlaceCard(anchPreviewPlayer, PreviewPlayerCard);
            anchPreviewPlayer?.Hide();   // Preview Player panel is optional — off until toggled on
        }

        // Older saved layouts (EdiLayout.config) predate the FM converter pane. If a restore didn't
        // produce one, create it (hidden) in an existing pane so the Settings toggle can show it.
        private void EnsureFmAnchorable()
        {
            try
            {
                if (Anch("fmconverter") != null) return;
                var pane = dockManager.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault();
                if (pane == null || FmConverterCard == null) return;
                (FmConverterCard.Parent as Panel)?.Children.Remove(FmConverterCard);
                FmConverterCard.Margin = new Thickness(0);
                FmConverterCard.Visibility = Visibility.Visible;
                var a = new LayoutAnchorable
                {
                    ContentId = "fmconverter",
                    Title = "FM Converter",
                    CanClose = false,
                    CanHide = true,
                    Content = FmConverterCard,
                };
                pane.Children.Add(a);
                a.Hide();   // off by default; toggled on from Settings → Panels
            }
            catch { }
        }

        // Same idea as EnsureFmAnchorable: older saved layouts predate the Preview Player pane,
        // so when we restore one we have to retrofit the anchorable or it'll never appear.
        private void EnsurePreviewPlayerAnchorable()
        {
            try
            {
                if (Anch("previewplayer") != null) return;
                var pane = dockManager.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault();
                if (pane == null || PreviewPlayerCard == null) return;
                (PreviewPlayerCard.Parent as Panel)?.Children.Remove(PreviewPlayerCard);
                PreviewPlayerCard.Margin = new Thickness(0);
                PreviewPlayerCard.Visibility = Visibility.Visible;
                var a = new LayoutAnchorable
                {
                    ContentId = "previewplayer",
                    Title = "Preview Player",
                    CanClose = false,
                    CanHide = true,
                    Content = PreviewPlayerCard,
                };
                pane.Children.Add(a);
                a.Hide();   // off by default; toggled on from Settings → Panels or the eye button
            }
            catch { }
        }

        // Use the real exe directory (Environment.ProcessPath), NOT AppContext.BaseDirectory — the
        // latter points at the temp self-extract folder for single-file builds, so the file would
        // be written somewhere transient and never restored.
        private static string DockLayoutFile
        {
            get
            {
                var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                return System.IO.Path.Combine(dir, "EdiLayout.config");
            }
        }

        private System.Windows.Threading.DispatcherTimer _dockAutoSaveTimer;
        private string _lastSavedDockXml;

        private string SerializeDockLayout()
        {
            try
            {
                var sw = new System.IO.StringWriter();
                new AvalonDock.Layout.Serialization.XmlLayoutSerializer(dockManager).Serialize(sw);
                return sw.ToString();
            }
            catch { return null; }
        }

        private void SaveDockLayout()
        {
            var xml = SerializeDockLayout();
            if (xml == null) return;
            try { File.WriteAllText(DockLayoutFile, xml); _lastSavedDockXml = xml; } catch { }
        }

        // Re-persist the layout a few seconds after it changes, so moves/resizes survive even a hard
        // kill or crash (not just a clean close). Only writes when the serialized layout differs.
        private void StartDockAutoSave()
        {
            _lastSavedDockXml   = SerializeDockLayout();   // baseline = the layout we just restored/built
            _lastSavedBoundsKey = WindowBoundsKey();       // baseline so we don't write until something moves
            _dockAutoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _dockAutoSaveTimer.Tick += (_, _) =>
            {
                // Dock layout (pane / box widths) — write only when it actually changed.
                var xml = SerializeDockLayout();
                if (xml != null && xml != _lastSavedDockXml)
                {
                    try { File.WriteAllText(DockLayoutFile, xml); _lastSavedDockXml = xml; } catch { }
                }
                // Window size/position — auto-persist too. The Closing handler also saves these, but
                // it never runs if EDI is force-killed / crashes / is closed by a launcher, so without
                // this the window size silently reverts. Only writes when the bounds actually changed.
                SaveWindowBoundsIfChanged();
            };
            _dockAutoSaveTimer.Start();
        }

        private string _lastSavedBoundsKey = "";

        private string WindowBoundsKey()
        {
            try
            {
                var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                return $"{WindowState}|{r.Left:F0}|{r.Top:F0}|{r.Width:F0}|{r.Height:F0}";
            }
            catch { return ""; }
        }

        private void SaveWindowBoundsIfChanged()
        {
            var key = WindowBoundsKey();
            if (key == "" || key == _lastSavedBoundsKey) return;
            _lastSavedBoundsKey = key;
            SaveWindowBounds();
        }

        private bool TryRestoreDockLayout()
        {
            if (!File.Exists(DockLayoutFile)) return false;
            try
            {
                var ser = new AvalonDock.Layout.Serialization.XmlLayoutSerializer(dockManager);
                ser.LayoutSerializationCallback += (s, e) =>
                {
                    // Pull each card out of the hidden classic grid into its restored pane. Removing
                    // it from its current parent first avoids the "already has a parent" exception.
                    var card = CardForId(e.Model.ContentId);
                    if (card is null) { e.Cancel = true; return; }
                    (card.Parent as Panel)?.Children.Remove(card);
                    card.Margin = new Thickness(0);
                    card.Visibility = Visibility.Visible;
                    e.Content = card;
                };
                using (var sr = new StreamReader(DockLayoutFile))
                    ser.Deserialize(sr);

                // Deserializing replaces the anchorable objects, so re-resolve the one we keep
                // poking at runtime (the Info pane show/hide).
                anchInfo = dockManager.Layout.Descendents()
                    .OfType<LayoutAnchorable>()
                    .FirstOrDefault(a => a.ContentId == "info") ?? anchInfo;

                EnsureFmAnchorable();   // older saved layouts predate the FM converter panel
                EnsurePreviewPlayerAnchorable();   // ...and predate the Preview Player panel
                return true;
            }
            catch
            {
                // Corrupt/incompatible layout file — discard it and fall back to the default panes.
                try { File.Delete(DockLayoutFile); } catch { }
                return false;
            }
        }

        // ===================== Playback log =====================

        private readonly System.Collections.ObjectModel.ObservableCollection<LogEntry> _logEntries = new();

        private void AppendLog(string message)
        {
            // Newest line is pinned on TOP and is the "current" (highlighted); the previous current
            // drops into history (dim). Oldest lines are trimmed off the bottom.
            if (_logEntries.Count > 0) _logEntries[0].IsCurrent = false;
            _logEntries.Insert(0, new LogEntry { Text = message, IsCurrent = true });
            while (_logEntries.Count > 250) _logEntries.RemoveAt(_logEntries.Count - 1);
            logScroll?.ScrollToTop();
        }

        // ===================== Inline settings panel =====================

        private bool _infoShown;
        private bool _infoEnabled;
        private string? _infoPdfPath;
        private const double InfoColWidth = 340;
        private bool _gameLeftShown;
        private const double GameLeftColWidth = 300;
        private double _gameLeftColWidth = GameLeftColWidth;   // remembered game-list panel width
        private bool _initSettings;   // suppress the portable-mode prompt while loading saved settings

        private void InitSettingsPanel()
        {
            _initSettings = true;
            var s = AppLocalSettings.Load();
            bool custom = s.PortableData || !string.IsNullOrWhiteSpace(s.SaveDataLocation);
            chkCustomSaveLoc.IsChecked = custom;
            txtSaveLoc.IsEnabled = custom;
            btnBrowseSaveLoc.IsEnabled = custom;
            txtSaveLoc.Text = Edi.Core.Edi.OutputDir;
            chkPreviewInMain.IsChecked = s.PreviewInMain;
            txtGameFolder.Text = s.GameFolder ?? "";
            txtFunscriptPlayer.Text = EffectiveFunscriptPlayerLocation();
            if (txtSteamGridKey != null) txtSteamGridKey.Text = s.SteamGridDbApiKey ?? "";
            // chkMoveGame removed — GAME is its own AvalonDock pane now, the card layout is always on.
            chkShowInfo.IsChecked = s.ShowInfoTab;
            // Dev: preview-device toggle (guarded so loading it doesn't create the device before the gallery is ready)
            _suppressPreviewToggle = true;
            if (chkPreviewDevice != null) chkPreviewDevice.IsChecked = s.PreviewDeviceEnabled;
            _suppressPreviewToggle = false;
            sliderCardHeight.Value = s.CardHeight;
            sliderCoverBlur.Value = s.CoverBlur;
            sliderDarkness.Value = s.ScrimDarkness;
            ApplyCardMetrics(s.CardHeight, s.CoverBlur);
            ApplyBadgeShape(s.BadgeShape);
            ApplyCardStyle(s.CardStyle);
            ApplySelStyle(s.SelStyle);
            ApplyTitleAlign(s.TitleAlign);
            ApplyTitleVAlign(s.TitleVAlign);
            ApplyTitleSize(s.TitleSize);
            ApplyIconShape(s.IconShape);
            ApplyCorner(s.CardCorner);
            ApplyOutline(s.OutlineThickness);
            ApplySearchBar(s.ShowSearchBar);
            chkStackLauncher.IsChecked = s.LauncherStackUrl;
            LauncherUrlOrientation = s.LauncherStackUrl ? Orientation.Vertical : Orientation.Horizontal;
            if (chkBlockAutoLaunch != null) chkBlockAutoLaunch.IsChecked = s.BlockAutoLaunch;
            if (s.ColGameLeftWidth is double glw && glw > 100) _gameLeftColWidth = glw;
            _initSettings = false;

            // GAME layout: always "moved" (card layout) — the dropdown view is gone with the chkMoveGame toggle.
            ApplyGameLayout(true);
            // The Info panel only shows when the user has enabled it AND the game has an info file.
            ApplyInfoTab(s.ShowInfoTab);

            // Restore the saved Connection/Options split (kept as star weights so it stays resizable).
            if (s.ColConnWidth is double cw && cw > 50 && s.ColOptsWidth is double ow && ow > 50)
            {
                colConn.Width = new GridLength(cw, GridUnitType.Star);
                colOpts.Width = new GridLength(ow, GridUnitType.Star);
            }
        }

        private void ApplyGameLayout(bool moved)
        {
            // The GAME panel is hosted in its own dock pane now, so the old far-left-column
            // reparenting is gone — this just switches the dropdown view vs the card-list view.
            if (GameClassic != null) GameClassic.Visibility = moved ? Visibility.Collapsed : Visibility.Visible;
            if (GameMoved   != null) GameMoved.Visibility   = moved ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SettingsPopup.IsOpen) RefreshPanelChecks();
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
        }

        // Toggle the Games sub-popup (Games Location + Auto-add), opened from the Settings popup's
        // top-right "Games…" button. Lives in a separate popup so the Data tab stays focused on core
        // paths. PlacementTarget is set explicitly here because sibling popups have separate
        // NameScopes — the XAML ElementName binding doesn't reliably resolve across them.
        private void SettingsGames_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsGamesPopup == null) return;
            SettingsGamesPopup.PlacementTarget = btnSettingsGames;
            SettingsGamesPopup.IsOpen = !SettingsGamesPopup.IsOpen;
        }

        // ===================== Dock panel show/hide toggles =====================
        private bool _refreshingPanels;

        private LayoutAnchorable Anch(string contentId)
        {
            var root = dockManager?.Layout;
            if (root == null) return null;
            return root.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId)
                ?? root.Hidden?.OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);
        }

        private bool IsAnchVisible(string contentId)
        {
            var root = dockManager?.Layout;
            if (root == null) return true;
            return root.Hidden?.Any(a => a.ContentId == contentId) != true;   // visible = not in Hidden
        }

        private void RefreshPanelChecks()
        {
            if (chkPanelConnection == null) return;
            _refreshingPanels = true;
            chkPanelConnection.IsChecked = IsAnchVisible("connection");
            chkPanelGame.IsChecked       = IsAnchVisible("game");
            chkPanelPlayback.IsChecked   = IsAnchVisible("playback");
            chkPanelLiveEdit.IsChecked   = IsAnchVisible("liveedit");
            chkPanelLog.IsChecked        = IsAnchVisible("log");
            chkPanelOptions.IsChecked    = IsAnchVisible("options");
            if (chkPanelFm != null) chkPanelFm.IsChecked = IsAnchVisible("fmconverter");
            if (chkPanelPreviewPlayer != null) chkPanelPreviewPlayer.IsChecked = IsAnchVisible("previewplayer");
            _refreshingPanels = false;
        }

        private void PanelToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_refreshingPanels) return;
            if (sender is not CheckBox cb || cb.Tag is not string id) return;
            var a = Anch(id);
            if (a == null) return;
            if (cb.IsChecked == true)
            {
                // Preview Player needs a live PreviewDevice driving its ProgressBar — spin one up
                // the moment the pane is shown.
                if (id == "previewplayer") EnsurePreviewPlayerDevice();
                a.Show();
                a.IsActive = true;
            }
            else
            {
                a.Hide();
                if (id == "previewplayer") TeardownPreviewPlayerDevice();
            }
        }

        // Persist the auto-launch kill-switch as soon as it's toggled. Re-arm `launched` if the
        // user re-enables auto-launch mid-session so the timer can fire ExecuteOnReady once.
        private void BlockAutoLaunch_Toggled(object sender, RoutedEventArgs e)
        {
            if (chkBlockAutoLaunch == null) return;
            var s = AppLocalSettings.Load();
            s.BlockAutoLaunch = chkBlockAutoLaunch.IsChecked == true;
            s.Save();
            if (!s.BlockAutoLaunch) launched = false;   // allow the next device-ready tick to fire
        }

        // Toggle launcher-pill layout between URL-under-name (Vertical) and URL-beside-name (Horizontal).
        private void StackLauncher_Toggled(object sender, RoutedEventArgs e)
        {
            if (chkStackLauncher == null) return;
            bool stack = chkStackLauncher.IsChecked == true;
            LauncherUrlOrientation = stack ? Orientation.Vertical : Orientation.Horizontal;
            if (_initSettings) return;
            var s = AppLocalSettings.Load();
            s.LauncherStackUrl = stack;
            s.Save();
        }

        // Eye / View button (next to the GAME edit cog): card height + cover blur. Anchor the popup
        // to whichever eye was clicked (classic vs moved layout share one popup).
        private void ViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is UIElement el) ViewPopup.PlacementTarget = el;
            if (!ViewPopup.IsOpen) RefreshCardOptionRadios();
            ViewPopup.IsOpen = !ViewPopup.IsOpen;
        }

        // The sliders apply live via *_Changed; persist them when the popup closes so the
        // chosen height/blur survive the next launch without needing the main Save button.
        private void ViewPopup_Closed(object sender, EventArgs e)
        {
            if (_initSettings) return;
            try
            {
                var s = AppLocalSettings.Load();
                s.CardHeight    = sliderCardHeight.Value;
                s.CoverBlur     = sliderCoverBlur.Value;
                s.ScrimDarkness = sliderDarkness.Value;
                s.BadgeShape    = CurrentBadgeShape();
                s.CardStyle     = CurrentCardStyle();
                s.TitleAlign    = CurrentTitleAlign();
                s.TitleVAlign   = CurrentTitleVAlign();
                s.TitleSize     = CurrentTitleSize();
                s.IconShape     = CurrentIconShape();
                s.CardCorner    = CurrentCorner();
                s.OutlineThickness = CurrentOutline();
                s.SelStyle      = CurrentSelStyle();
                s.ShowSearchBar = chkSearchBar?.IsChecked == true;
                s.Save();
            }
            catch { /* non-fatal: values still applied live this session */ }
        }

        // ===================== Support / donate popup =====================
        // Opens a popup that (a) clearly states this is a community mod, (b) credits + links the
        // original EDI author, and (c) lists the baked-in donation options (Ko-fi link + Monero).
        private System.Collections.ObjectModel.ObservableCollection<DonationLink> _donationLinks;
        private string _moneroAddress = "";

        private void Support_Click(object sender, RoutedEventArgs e)
        {
            if (!DonatePopup.IsOpen) LoadDonationLinks();
            DonatePopup.IsOpen = !DonatePopup.IsOpen;
        }

        private void LoadDonationLinks()
        {
            const string kofiUrl = "https://ko-fi.com/sunblockbukkake";
            var s = AppLocalSettings.Load();

            // First run: seed common platforms. Ko-fi ships with the real link so it works out of the box.
            if (!s.DonationsInitialized)
            {
                s.DonationsInitialized = true;
                s.KofiLinkSeeded = true;
                if (s.DonationLinks.Count == 0)
                {
                    s.DonationLinks.Add(new DonationLink { Label = "Ko-fi",   Url = kofiUrl });
                    s.DonationLinks.Add(new DonationLink { Label = "Patreon", Url = "" });
                    s.DonationLinks.Add(new DonationLink { Label = "PayPal",  Url = "" });
                }
                s.Save();
            }
            // One-time migration for installs created before Ko-fi was baked in: fill the blank
            // Ko-fi row (or add it). Runs once, so later user edits/removals are respected.
            else if (!s.KofiLinkSeeded)
            {
                s.KofiLinkSeeded = true;
                var kofi = s.DonationLinks.FirstOrDefault(d =>
                    string.Equals((d.Label ?? "").Trim(), "Ko-fi", StringComparison.OrdinalIgnoreCase));
                if (kofi == null)
                    s.DonationLinks.Insert(0, new DonationLink { Label = "Ko-fi", Url = kofiUrl });
                else if (string.IsNullOrWhiteSpace(kofi.Url))
                    kofi.Url = kofiUrl;
                s.Save();
            }

            _donationLinks = new System.Collections.ObjectModel.ObservableCollection<DonationLink>(
                (s.DonationLinks ?? new List<DonationLink>())
                    .Select(d => new DonationLink { Label = d.Label ?? "", Url = d.Url ?? "" }));

            if (icDonate != null) icDonate.ItemsSource = _donationLinks;

            _moneroAddress = (s.MoneroAddress ?? "").Trim();
            if (txtMoneroAddr != null) txtMoneroAddr.Text = _moneroAddress;
            UpdateMoneroVisibility();

            RefreshDonationEmptyState();
        }

        private void UpdateMoneroVisibility()
        {
            bool has = !string.IsNullOrWhiteSpace(_moneroAddress);
            if (cardMonero != null)
                cardMonero.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has) UpdateMoneroQr(); else if (qrMonero != null) qrMonero.Source = null;
        }

        // Render the Monero address as a scannable QR (encoded as a monero: URI so wallets autofill).
        // Uses QRCoder's PngByteQRCode renderer — pure managed, no System.Drawing dependency.
        private void UpdateMoneroQr()
        {
            if (qrMonero == null) return;
            var addr = (_moneroAddress ?? "").Trim();
            if (string.IsNullOrWhiteSpace(addr)) { qrMonero.Source = null; return; }
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                var data = gen.CreateQrCode("monero:" + addr, QRCoder.QRCodeGenerator.ECCLevel.M);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                qrMonero.Source = bmp;
            }
            catch { qrMonero.Source = null; }
        }

        private void RefreshDonationEmptyState()
        {
            bool anyConfigured = (_donationLinks != null && _donationLinks.Any(d => !string.IsNullOrWhiteSpace(d.Url)))
                                 || !string.IsNullOrWhiteSpace(_moneroAddress);
            if (txtNoDonations != null)
                txtNoDonations.Visibility = anyConfigured ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DonationOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                url = url.Trim();
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
                ExecuteCommandOrOpenPath(url);
            }
        }

        // Credit link to the upstream project (verified: the repo this mod was cloned from).
        private void OriginalRepo_Click(object sender, RoutedEventArgs e)
            => ExecuteCommandOrOpenPath("https://github.com/NoGRo/Edi");

        // Monero is copy-only: we put the public receive address on the clipboard. The app never
        // initiates a transaction or touches a wallet/private key.
        private async void MoneroCopy_Click(object sender, RoutedEventArgs e)
        {
            var addr = (txtMoneroAddr?.Text ?? _moneroAddress ?? "").Trim();
            if (string.IsNullOrWhiteSpace(addr)) return;
            try { Clipboard.SetText(addr); } catch { }
            if (lblMoneroCopy != null)
            {
                lblMoneroCopy.Text = "Copied";
                try { await Task.Delay(1400); } catch { }
                if (lblMoneroCopy != null) lblMoneroCopy.Text = "Copy";
            }
        }

        private void DonatePopup_Closed(object sender, EventArgs e)
        {
            try
            {
                if (_donationLinks == null) return;
                var s = AppLocalSettings.Load();
                s.DonationsInitialized = true;
                s.DonationLinks = _donationLinks
                    .Select(d => new DonationLink { Label = (d.Label ?? "").Trim(), Url = (d.Url ?? "").Trim() })
                    .Where(d => !(string.IsNullOrWhiteSpace(d.Label) && string.IsNullOrWhiteSpace(d.Url)))
                    .ToList();
                s.MoneroAddress = (_moneroAddress ?? "").Trim();
                s.Save();
            }
            catch { /* non-fatal */ }
        }

        // Push the card height + cover-blur values into the live resources the card template reads.
        private void ApplyCardMetrics(double height, double blur)
        {
            Resources["Card.MinHeight"] = height;
            Resources["Card.BlurRadius"] = blur;
            if (lblCardHeight != null) lblCardHeight.Text = ((int)height).ToString();
            if (lblCoverBlur != null) lblCoverBlur.Text = ((int)blur).ToString();
        }

        private void CardHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblCardHeight == null) return;
            lblCardHeight.Text = ((int)e.NewValue).ToString();
            Resources["Card.MinHeight"] = e.NewValue;
            RecomputeIconMetrics();   // keep the icon box sized to the new card height
        }

        private void CoverBlur_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblCoverBlur == null) return;
            lblCoverBlur.Text = ((int)e.NewValue).ToString();
            Resources["Card.BlurRadius"] = e.NewValue;
            SyncBlurStyleRadio();
        }

        private void Darkness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (lblDarkness == null) return;
            lblDarkness.Text = ((int)e.NewValue) + "%";
            // In Solid style the scrim is forced off; otherwise the slider drives it.
            if (rbStyleSolid?.IsChecked != true) Resources["Card.ScrimOpacity"] = e.NewValue / 100.0;
            SyncBlurStyleRadio();
        }

        // ---- Badge shape + card style (the eye-button "Game cards" popup) ----
        // Pill = fully rounded ends; Rounded = soft corners; Square = sharp corners.
        private static CornerRadius BadgeRadiusFor(string? shape) => shape switch
        {
            "Square"  => new CornerRadius(0),
            "Rounded" => new CornerRadius(4),
            _         => new CornerRadius(11),   // Pill (default)
        };

        private void ApplyBadgeShape(string? shape) => Resources["Card.BadgeRadius"] = BadgeRadiusFor(shape);

        // Cover = blurred art behind the card; Solid = no art (opacity 0 hides cover/scrim/video/gif).
        private void ApplyCardStyle(string? style)
        {
            bool solid = string.Equals(style, "Solid", StringComparison.OrdinalIgnoreCase);
            Resources["Card.CoverOpacity"] = solid ? 0.0 : 1.0;
            Resources["Card.ScrimOpacity"] = solid ? 0.0 : (sliderDarkness?.Value ?? 70) / 100.0;
        }

        // How the *selected* game card is highlighted. Independent of the card "Outline" setting so
        // the current selection is always obvious. Drives the Sel.* resources the card template reads.
        private void ApplySelStyle(string? style)
        {
            switch ((style ?? "Glow").Trim().ToLowerInvariant())
            {
                case "border":
                    Resources["Sel.BorderThickness"] = new Thickness(3);
                    Resources["Sel.BarWidth"]    = 0.0;
                    Resources["Sel.GlowBlur"]    = 0.0;
                    Resources["Sel.GlowOpacity"] = 0.0;
                    Resources["Sel.FillBrush"]   = SelFill("#26FF3D93");
                    break;
                case "bar":
                    Resources["Sel.BorderThickness"] = new Thickness(1.5);
                    Resources["Sel.BarWidth"]    = 6.0;
                    Resources["Sel.GlowBlur"]    = 0.0;
                    Resources["Sel.GlowOpacity"] = 0.0;
                    Resources["Sel.FillBrush"]   = SelFill("#26FF3D93");
                    break;
                case "solid":
                    Resources["Sel.BorderThickness"] = new Thickness(1.5);
                    Resources["Sel.BarWidth"]    = 0.0;
                    Resources["Sel.GlowBlur"]    = 0.0;
                    Resources["Sel.GlowOpacity"] = 0.0;
                    Resources["Sel.FillBrush"]   = SelFill("#5AFF2D8C");
                    break;
                default: // Glow (obvious accent border + glow)
                    Resources["Sel.BorderThickness"] = new Thickness(2.5);
                    Resources["Sel.BarWidth"]    = 0.0;
                    Resources["Sel.GlowBlur"]    = 18.0;
                    Resources["Sel.GlowOpacity"] = 0.9;
                    Resources["Sel.FillBrush"]   = SelFill("#26FF3D93");
                    break;
            }
        }

        private static SolidColorBrush SelFill(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private string CurrentSelStyle() =>
            rbSelBorder?.IsChecked == true ? "Border" :
            rbSelBar?.IsChecked    == true ? "Bar" :
            rbSelSolid?.IsChecked  == true ? "Solid" : "Glow";

        // Card title alignment within its column.
        private void ApplyTitleAlign(string? a) =>
            Resources["Card.TitleAlign"] = string.Equals(a, "Center", StringComparison.OrdinalIgnoreCase)
                ? TextAlignment.Center : TextAlignment.Left;

        // Card title vertical placement within the card (Top / Center / Bottom).
        private void ApplyTitleVAlign(string? v) =>
            Resources["Card.TitleVAlign"] =
                string.Equals(v, "Top", StringComparison.OrdinalIgnoreCase)    ? VerticalAlignment.Top :
                string.Equals(v, "Bottom", StringComparison.OrdinalIgnoreCase) ? VerticalAlignment.Bottom :
                                                                                 VerticalAlignment.Center;

        private string CurrentTitleVAlign() =>
            rbTitleTop?.IsChecked == true ? "Top" :
            rbTitleBottom?.IsChecked == true ? "Bottom" : "Center";

        private string CurrentBadgeShape() =>
            rbBadgeSquare?.IsChecked == true ? "Square" :
            rbBadgeRounded?.IsChecked == true ? "Rounded" : "Pill";

        private string CurrentCardStyle() => rbStyleSolid?.IsChecked == true ? "Solid" : "Cover";

        private string CurrentTitleAlign() => rbTitleCenter?.IsChecked == true ? "Center" : "Left";

        // Reflect the saved badge/style/title choices in the popup's segmented toggles when it opens.
        private void RefreshCardOptionRadios()
        {
            if (rbBadgePill == null || rbStyleCover == null || rbTitleLeft == null || rbTitleSmall == null || rbSelGlow == null) return;
            var s = AppLocalSettings.Load();
            rbBadgeSquare.IsChecked  = s.BadgeShape == "Square";
            rbBadgeRounded.IsChecked = s.BadgeShape == "Rounded";
            rbBadgePill.IsChecked    = s.BadgeShape != "Square" && s.BadgeShape != "Rounded";
            rbStyleSolid.IsChecked   = string.Equals(s.CardStyle, "Solid", StringComparison.OrdinalIgnoreCase);
            rbStyleCover.IsChecked   = !rbStyleSolid.IsChecked;
            rbTitleCenter.IsChecked  = string.Equals(s.TitleAlign, "Center", StringComparison.OrdinalIgnoreCase);
            rbTitleLeft.IsChecked    = !rbTitleCenter.IsChecked;
            // Title vertical placement
            bool vTop    = string.Equals(s.TitleVAlign, "Top", StringComparison.OrdinalIgnoreCase);
            bool vBottom = string.Equals(s.TitleVAlign, "Bottom", StringComparison.OrdinalIgnoreCase);
            rbTitleTop.IsChecked    = vTop;
            rbTitleBottom.IsChecked = vBottom;
            rbTitleMiddle.IsChecked = !vTop && !vBottom;
            // Title size
            rbTitleSmall.IsChecked = s.TitleSize <= 13.5;
            rbTitleLarge.IsChecked = s.TitleSize >= 17;
            rbTitleMed.IsChecked   = s.TitleSize > 13.5 && s.TitleSize < 17;
            // Icon shape
            bool iconCircle = string.Equals(s.IconShape, "Circle", StringComparison.OrdinalIgnoreCase);
            bool iconRound  = string.Equals(s.IconShape, "Rounded", StringComparison.OrdinalIgnoreCase);
            rbIconCircle.IsChecked  = iconCircle;
            rbIconRounded.IsChecked = iconRound;
            rbIconSquare.IsChecked  = !iconCircle && !iconRound;
            // Corners
            rbCornerSquare.IsChecked  = s.CardCorner <= 0.5;
            rbCornerRound.IsChecked   = s.CardCorner >= 14;
            rbCornerRounded.IsChecked = s.CardCorner > 0.5 && s.CardCorner < 14;
            // Outline
            rbOutlineNone.IsChecked = s.OutlineThickness <= 0.5;
            rbOutlineBold.IsChecked = s.OutlineThickness >= 1.5;
            rbOutlineThin.IsChecked = s.OutlineThickness > 0.5 && s.OutlineThickness < 1.5;
            // Selected-card highlight style
            rbSelBorder.IsChecked = string.Equals(s.SelStyle, "Border", StringComparison.OrdinalIgnoreCase);
            rbSelBar.IsChecked    = string.Equals(s.SelStyle, "Bar", StringComparison.OrdinalIgnoreCase);
            rbSelSolid.IsChecked  = string.Equals(s.SelStyle, "Solid", StringComparison.OrdinalIgnoreCase);
            rbSelGlow.IsChecked   = rbSelBorder.IsChecked != true && rbSelBar.IsChecked != true && rbSelSolid.IsChecked != true;
            // Search bar
            chkSearchBar.IsChecked = s.ShowSearchBar;
            // Outline the blur-style preset that matches the current blur + darkness
            SyncBlurStyleRadio();
        }

        private void BadgeShape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string shape) ApplyBadgeShape(shape);
        }

        private void CardStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string style) ApplyCardStyle(style);
        }

        private void SelStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string st) ApplySelStyle(st);
        }

        private void TitleAlign_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string a) ApplyTitleAlign(a);
        }

        private void TitleVAlign_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string v) ApplyTitleVAlign(v);
        }

        // Blur-style presets (radio toggles). Clicking one sets the blur + darkness sliders together;
        // the preset matching the current values is outlined as "current" (SyncBlurStyleRadio).
        private bool _blurSync;
        private static (double blur, double dark) BlurPresetValues(string? preset) => preset switch
        {
            "Heavy"   => (34.0, 72.0),
            "Frosted" => (28.0, 28.0),   // glassy: strong blur, light scrim
            "Sharp"   => (0.0,  45.0),
            _         => (18.0, 60.0),   // Soft (default)
        };
        private void BlurStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (_blurSync) return;   // checked programmatically by the sync pass → don't re-apply
            if (sender is not RadioButton rb || rb.Tag is not string preset) return;
            var (blur, dark) = BlurPresetValues(preset);
            _blurSync = true;
            if (sliderCoverBlur != null) sliderCoverBlur.Value = blur;
            if (sliderDarkness  != null) sliderDarkness.Value  = dark;
            _blurSync = false;
        }
        // Outline whichever preset matches the current blur + darkness (none when it's a custom mix).
        private void SyncBlurStyleRadio()
        {
            if (rbBlurSoft == null || _blurSync) return;
            double blur = sliderCoverBlur?.Value ?? -1, dark = sliderDarkness?.Value ?? -1;
            string? match = null;
            foreach (var name in new[] { "Soft", "Heavy", "Frosted", "Sharp" })
            {
                var (b, d) = BlurPresetValues(name);
                if (Math.Abs(blur - b) < 0.5 && Math.Abs(dark - d) < 0.5) { match = name; break; }
            }
            _blurSync = true;
            rbBlurSoft.IsChecked  = match == "Soft";
            rbBlurHeavy.IsChecked = match == "Heavy";
            rbBlurFrost.IsChecked = match == "Frosted";
            rbBlurSharp.IsChecked = match == "Sharp";
            _blurSync = false;
        }

        // ---- Title size / icon shape / card corners / outline / search bar ----
        private void ApplyTitleSize(double size) => Resources["Card.TitleSize"] = size;
        private static double TitleSizeFor(string? tag) => tag switch { "Small" => 13.0, "Large" => 18.0, _ => 15.0 };
        private double CurrentTitleSize() => rbTitleSmall?.IsChecked == true ? 13.0 : rbTitleLarge?.IsChecked == true ? 18.0 : 15.0;
        private void TitleSize_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb && rb.Tag is string t) ApplyTitleSize(TitleSizeFor(t)); }

        private string _iconShape = "Square";
        private void ApplyIconShape(string? shape) { _iconShape = shape ?? "Square"; RecomputeIconMetrics(); }

        // Size/inset/radius for the card icon. Square & Rounded fill the card edge-to-edge; Circle is
        // inset a few px and uses a half-size radius so it's a clean circle that never clips on an edge.
        private void RecomputeIconMetrics()
        {
            double h = (sliderCardHeight != null && sliderCardHeight.Value > 0) ? sliderCardHeight.Value
                     : (Resources["Card.MinHeight"] is double d ? d : 44);
            if (string.Equals(_iconShape, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                const double inset = 5;
                double size = Math.Max(8, h - inset * 2);
                Resources["Card.IconSize"]   = size;
                Resources["Card.IconMargin"] = new Thickness(inset);
                Resources["Card.IconRadius"] = new CornerRadius(size / 2);   // exact circle
            }
            else
            {
                Resources["Card.IconSize"]   = h;
                Resources["Card.IconMargin"] = new Thickness(0);
                Resources["Card.IconRadius"] = new CornerRadius(string.Equals(_iconShape, "Rounded", StringComparison.OrdinalIgnoreCase) ? 10 : 0);
            }
        }
        private string CurrentIconShape() =>
            rbIconCircle?.IsChecked == true ? "Circle" : rbIconRounded?.IsChecked == true ? "Rounded" : "Square";
        private void IconShape_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb && rb.Tag is string t) ApplyIconShape(t); }

        private void ApplyCorner(double v)
        {
            Resources["Card.Corner"] = new CornerRadius(v);
            CardCornerValue = v;   // keep the cover-art clip radius in sync with the border
        }
        private static double CornerFor(string? tag) => tag switch { "Square" => 0.0, "Pill" => 16.0, _ => 8.0 };
        private double CurrentCorner() => rbCornerSquare?.IsChecked == true ? 0.0 : rbCornerRound?.IsChecked == true ? 16.0 : 8.0;
        private void Corner_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb && rb.Tag is string t) ApplyCorner(CornerFor(t)); }

        private void ApplyOutline(double t) => Resources["Card.OutlineThickness"] = new Thickness(t);
        private static double OutlineFor(string? tag) => tag switch { "None" => 0.0, "Bold" => 2.0, _ => 1.0 };
        private double CurrentOutline() => rbOutlineNone?.IsChecked == true ? 0.0 : rbOutlineBold?.IsChecked == true ? 2.0 : 1.0;
        private void Outline_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb && rb.Tag is string t) ApplyOutline(OutlineFor(t)); }

        // Search bar above the game card list — filters the cards live by name.
        private void SearchBar_Toggled(object sender, RoutedEventArgs e) => ApplySearchBar(chkSearchBar?.IsChecked == true);
        private void ApplySearchBar(bool on)
        {
            if (searchRow != null) searchRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
            {
                if (txtGameSearch != null && txtGameSearch.Text.Length > 0) txtGameSearch.Text = "";
                ApplyGameFilter(null);
            }
        }
        private void GameSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => ApplyGameFilter(txtGameSearch?.Text);
        private void ApplyGameFilter(string? text)
        {
            var src = lstGames?.ItemsSource;
            if (src == null) return;
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(src);
            if (view == null) return;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) { view.Filter = null; return; }
            view.Filter = o => (o as GameInfo)?.Name?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CustomSaveLoc_Toggled(object sender, RoutedEventArgs e)
        {
            bool on = chkCustomSaveLoc.IsChecked == true;
            if (txtSaveLoc == null) return;
            txtSaveLoc.IsEnabled = on;
            btnBrowseSaveLoc.IsEnabled = on;

            if (!on)
            {
                txtSaveLoc.Text = Edi.Core.Edi.DefaultOutputDir;
                return;
            }

            if (_initSettings) return;   // don't prompt while loading saved settings

            // Offer portable mode (a "Data" folder next to the exe) when turning custom on.
            bool yes = ThemedDialog.Confirm(this, "Portable mode", "Use portable mode?",
                $"Keep EDI's save data in a \"Data\" folder next to the program:\n{Edi.Core.Edi.PortableDataDir}\n\nChoose No to pick your own location.");
            if (yes)
                txtSaveLoc.Text = Edi.Core.Edi.PortableDataDir;
            else if (string.IsNullOrWhiteSpace(txtSaveLoc.Text) || txtSaveLoc.Text == Edi.Core.Edi.DefaultOutputDir)
                txtSaveLoc.Text = Edi.Core.Edi.OutputDir;
        }

        private void BrowseSaveLoc_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Select save-data folder", Multiselect = false };
            if (Directory.Exists(txtSaveLoc.Text)) dlg.InitialDirectory = txtSaveLoc.Text;
            if (dlg.ShowDialog(this) == true) txtSaveLoc.Text = dlg.FolderName;
        }

        private void BrowseGameFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Select the root folder that holds your games", Multiselect = false };
            if (Directory.Exists(txtGameFolder.Text)) dlg.InitialDirectory = txtGameFolder.Text;
            if (dlg.ShowDialog(this) == true) txtGameFolder.Text = dlg.FolderName;
        }

        private void BrowseFunscriptPlayer_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select your funscript player (ScriptPlayer, MultiFunPlayer, …)",
                Filter = "Programs|*.exe;*.lnk|All files|*.*",
                FilterIndex = 1,
                FileName = txtFunscriptPlayer.Text,
            };
            if (dlg.ShowDialog(this) == true) txtFunscriptPlayer.Text = dlg.FileName;
        }

        private void SettingsSave_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppLocalSettings.Load();

            bool custom = chkCustomSaveLoc.IsChecked == true;
            string oldDir = Edi.Core.Edi.OutputDir;
            string newDir = custom ? (txtSaveLoc.Text ?? "").Trim() : Edi.Core.Edi.DefaultOutputDir;

            // Portable mode = the chosen folder is the "Data" folder next to the exe (relocatable).
            bool portable = false;
            if (custom && !string.IsNullOrWhiteSpace(newDir))
            {
                try
                {
                    portable = string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDir)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Edi.Core.Edi.PortableDataDir)),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            if (custom && string.IsNullOrWhiteSpace(newDir))
            {
                MessageBox.Show(this, "Enter a folder path, or turn off custom save-data location.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool locationChanged;
            try
            {
                locationChanged = !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(newDir)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldDir)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { ShowSettingsError(ex); return; }

            if (locationChanged)
            {
                bool oldHasData = Directory.Exists(oldDir) && Directory.EnumerateFileSystemEntries(oldDir).Any();
                if (oldHasData)
                {
                    var ans = MessageBox.Show(this,
                        $"Move all existing save data to the new location?\n\nFrom:  {oldDir}\nTo:      {newDir}\n\nRecommended — this keeps your config, recordings and bundles.",
                        "Move save data", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (ans == MessageBoxResult.Cancel) return;
                    if (ans == MessageBoxResult.Yes)
                    {
                        if (!TryMoveSaveData(oldDir, newDir)) return;
                    }
                    else
                    {
                        try { Directory.CreateDirectory(newDir); } catch (Exception ex) { ShowSettingsError(ex); return; }
                    }
                }
                else
                {
                    try { Directory.CreateDirectory(newDir); } catch (Exception ex) { ShowSettingsError(ex); return; }
                }
            }

            settings.PortableData = portable;
            settings.SaveDataLocation = (custom && !portable) ? newDir : null;
            settings.PreviewInMain = chkPreviewInMain.IsChecked == true;
            settings.GameFolder = string.IsNullOrWhiteSpace(txtGameFolder.Text) ? null : txtGameFolder.Text.Trim();
            settings.FunscriptPlayerPath = string.IsNullOrWhiteSpace(txtFunscriptPlayer.Text) ? null : AddGameDialog.CleanPath(txtFunscriptPlayer.Text);
            settings.SteamGridDbApiKey = txtSteamGridKey?.Text?.Trim() ?? "";
            // MoveGameLayout pinned to true — the toggle was removed (GAME pane covers it).
            settings.MoveGameLayout = true;
            settings.ShowInfoTab = chkShowInfo.IsChecked == true;
            settings.CardHeight = sliderCardHeight.Value;
            settings.CoverBlur = sliderCoverBlur.Value;
            settings.Save();
            ApplyGameLayout(true);
            ApplyInfoTab(settings.ShowInfoTab);

            Edi.Core.Edi.SetOutputDir(newDir);
            txtSaveLoc.Text = Edi.Core.Edi.OutputDir;
            // If preview-in-main was turned off while docked, undock it.
            if (!settings.PreviewInMain && _previewShown) HideEmbeddedPreview();
            // Refresh the games dropdown so folder icons reflect the new Game Folder.
            GameIconConverter.ClearCache();
            GamesComboBox.Items.Refresh();
            SettingsPopup.IsOpen = false;

            if (locationChanged)
                MessageBox.Show(this,
                    "Save-data location updated.\nRestart EDI so every component uses the new path.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Copy the whole save-data tree to newDir; delete the source only if every file copied.
        private bool TryMoveSaveData(string oldDir, string newDir)
        {
            try
            {
                Directory.CreateDirectory(newDir);
                var failed = new List<string>();
                foreach (var file in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(oldDir, file);
                    var dest = Path.Combine(newDir, rel);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest, overwrite: true);
                    }
                    catch { failed.Add(rel); }
                }

                if (failed.Count > 0)
                {
                    MessageBox.Show(this,
                        $"Copied to the new location, but {failed.Count} file(s) were in use and skipped:\n\n" +
                        $"{string.Join("\n", failed.Take(8))}{(failed.Count > 8 ? "\n…" : "")}\n\n" +
                        "The old folder was kept. Close EDI and move any remaining files manually.",
                        "Move save data", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                }

                try { Directory.Delete(oldDir, true); } catch { /* new location is primary; leftover old folder is harmless */ }
                return true;
            }
            catch (Exception ex)
            {
                ShowSettingsError(ex);
                return false;
            }
        }

        private void ShowSettingsError(Exception ex)
            => MessageBox.Show(this, $"Could not prepare the new save-data location:\n{ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);

        // ===================== Info panel =====================

        private void ApplyInfoTab(bool enabled)
        {
            _infoEnabled = enabled;
            RefreshInfoPanel();
        }

        // Show the Info panel only when the user enabled it AND the current game (or the exe
        // folder) actually has an info file. Re-run whenever the selected game changes.
        private void RefreshInfoPanel()
        {
            // SelectionChanged can fire during InitializeComponent, before these named
            // elements are assigned — bail out until the visual tree is ready.
            if (InfoCard == null) return;

            bool show = _infoEnabled && ResolveInfoPath() != null;

            // Info lives in its own dock pane (tab) now — show/hide the whole anchorable.
            if (anchInfo != null)
            {
                if (show) anchInfo.Show(); else anchInfo.Hide();
            }

            if (show) LoadInfoContent();
        }

        // Icon + vertical divider only appear in the "no real content" (placeholder) state.
        private void SetInfoChrome(bool show)
        {
            infoIcon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            infoVDivider.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LoadInfoContent()
        {
            btnInfoOpen.Visibility = Visibility.Collapsed;
            _infoPdfPath = null;

            var path = ResolveInfoPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetInfoChrome(true);
                txtInfoContent.TextAlignment = TextAlignment.Center;
                txtInfoContent.Text = "No info file found.\n\nSet a per-game Info Location in Game Settings, " +
                                      "or place an Info.txt / Info.md / Info.pdf next to Edi.exe.";
                return;
            }

            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _infoPdfPath = path;
                btnInfoOpen.Visibility = Visibility.Visible;   // keep, for the full visual (images/layout)
                var text = ExtractPdfText(path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetInfoChrome(true);
                    txtInfoContent.TextAlignment = TextAlignment.Center;
                    txtInfoContent.Text = $"{Path.GetFileName(path)}\n\nCouldn't read text from this PDF — " +
                                          "click \"Open PDF\" to view it in your system viewer.";
                }
                else
                {
                    SetInfoChrome(false);
                    txtInfoContent.TextAlignment = TextAlignment.Left;
                    txtInfoContent.Text = text;
                }
            }
            else
            {
                SetInfoChrome(false);
                txtInfoContent.TextAlignment = TextAlignment.Left;
                try { txtInfoContent.Text = File.ReadAllText(path); }
                catch (Exception ex) { txtInfoContent.Text = $"Could not read info file:\n{ex.Message}"; }
            }
        }

        // Pull readable text out of a PDF using PdfPig (pure-managed, no native deps). Words are grouped
        // into lines by vertical position so a README stays readable. Returns null if empty/unreadable.
        private static string? ExtractPdfText(string path)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
                foreach (var page in doc.GetPages())
                {
                    var words = page.GetWords().ToList();
                    if (words.Count > 0)
                    {
                        var lines = words
                            .GroupBy(w => System.Math.Round(w.BoundingBox.Bottom / 3.0))   // ~3pt line buckets
                            .OrderByDescending(g => g.Key)                                  // top of page first
                            .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
                        sb.AppendLine(string.Join("\n", lines)).AppendLine();
                    }
                    if (sb.Length > 300000) break;   // safety cap for very large PDFs
                }
                var result = sb.ToString().Trim();
                return result.Length == 0 ? null : result;
            }
            catch { return null; }
        }

        private string? ResolveInfoPath()
        {
            var game = (GamesComboBox?.SelectedItem as GameInfo) ?? gamesConfig?.SelectedGameinfo;
            if (!string.IsNullOrWhiteSpace(game?.InfoPath) && File.Exists(game.InfoPath))
                return game.InfoPath;

            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            foreach (var name in new[] { "Info.txt", "Info.md", "Info.pdf" })
            {
                var p = Path.Combine(exeDir, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void InfoOpen_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_infoPdfPath) || !File.Exists(_infoPdfPath)) return;
            try { Process.Start(new ProcessStartInfo(_infoPdfPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, $"Could not open:\n{ex.Message}", "Info", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void loadOSRPorts()
        {
            var comPorts = new HashSet<ComPort>() { new ComPort("None", null) };
            try
            {
                foreach (var port in SerialPort.GetPortNames())
                {
                    comPorts.Add(new ComPort(port, port));
                }
            }
            catch (Exception)
            {
            }

            comPortsComboBox.ItemsSource = comPorts;
        }
        private void Edi_OnChangeStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                lblStatus.Content = message;
                AppendLog(message);
            });
        }

        private async void DeviceCollector_OnUnloadDevice(IDevice device, List<IDevice> devices)
        {
            await Task.Delay(1000);
            await Dispatcher.InvokeAsync(() =>
            {
                DevicesGrid.ItemsSource = edi.Devices;

                //DevicesGrid.Items.Refresh();
            });

        }

        private async void DeviceCollector_OnloadDeviceAsync(IDevice device, List<IDevice> devices)
        {
            await Task.Delay(500);

            await Dispatcher.InvokeAsync(() =>
            {
                DevicesGrid.ItemsSource = edi.Devices;
                //DevicesGrid.Items.Refresh();
                // Enforce the live Intensity/Vibration bar levels on the newly-connected device.
                // Without this, a device that connects after startup ignores the bars until one is
                // dragged — so e.g. a vibrator would run at its full configured range instead of the
                // Vibration bar's current value (which now defaults to 0 = silent until raised).
                ApplyIntensities();
            });
        }


        private void Variants_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox? comboBox = sender as ComboBox;
            
            var device = comboBox.DataContext as IDevice;
            _ = edi.DeviceConfiguration.SelectVariant(device, (string)comboBox.SelectedValue);
        }

        private void Channels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox? comboBox = sender as ComboBox;

            var device = comboBox.DataContext as IDevice;
            
            _ = edi.DeviceConfiguration.SelectChannel(device, (string)comboBox.SelectedValue);
        }


        // Breadth-first hunt for an existing EdiConfig.json under `root` (shallowest match wins).
        // Per-directory try/catch so one locked / access-denied subfolder (e.g. a bundled player)
        // can't abort the whole search the way EnumerateFiles(AllDirectories) would. null if none.
        private static string? FindExistingConfig(string root)
        {
            try
            {
                var queue = new Queue<(string path, int depth)>();
                queue.Enqueue((root, 0));
                const int maxDepth = 6;
                while (queue.Count > 0)
                {
                    var (cur, depth) = queue.Dequeue();
                    var cand = System.IO.Path.Combine(cur, "EdiConfig.json");
                    if (File.Exists(cand)) return cand;
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

        // Auto mode: scan the Games Location for every set-up game (a folder that contains an
        // EdiConfig.json somewhere) and add any not already listed, auto-detecting exe/gallery/info/type.
        private async void AutoAddGames_Click(object sender, RoutedEventArgs e)
        {
            try { AppendLog("Auto-add: starting…"); } catch { }
            var root = AddGameDialog.CleanPath(txtGameFolder?.Text);
            if (string.IsNullOrWhiteSpace(root)) root = AppLocalSettings.Load().GameFolder ?? "";
            try { AppendLog($"Auto-add: Games Location = '{root}'"); } catch { }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                try { AppendLog("Auto-add: aborting — Games Location is not a valid folder."); } catch { }
                ThemedDialog.Info(this, "Auto-add games", "Folder not set",
                    "Set a valid Games Location first (Settings ▸ Data).");
                return;
            }

            // Skip games already in the list (match by their EdiConfig.json path).
            var have = new HashSet<string>(
                gamesConfig.GamesInfo.Select(g => g.Path ?? "").Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            string rootCopy = root;
            var found = await Task.Run(() =>
            {
                var list = new List<GameInfo>();
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(rootCopy, "*", SearchOption.TopDirectoryOnly); }
                catch { return list; }
                foreach (var folder in subs)
                {
                    var config = FindExistingConfig(folder);
                    if (config == null || have.Contains(config)) continue;
                    var dir = Path.GetDirectoryName(config) ?? folder;
                    bool script = GameSettings.LooksLikeScriptPlayer(dir);
                    list.Add(new GameInfo(new DirectoryInfo(folder).Name, config)
                    {
                        GameType    = script ? "ScriptPlayer" : "EDI",
                        ExePath     = GameSettings.DetectGameExe(dir),
                        GalleryPath = GameSettings.DetectGallery(dir, script ? "Scripts" : "Gallery"),
                        InfoPath    = GameSettings.DetectInfo(dir),
                        ImagePath   = GameSettings.DetectBanner(folder),
                    });
                }
                return list;
            });

            foreach (var gi in found) gamesConfig.GamesInfo.Add(gi);
            if (found.Count > 0)
            {
                edi.ConfigurationManager.Save(gamesConfig);
                viewModel.galleries = ReloadGalleries();
            }

            try { AppendLog($"Auto-add: scanned — {found.Count} new game(s)."); } catch { }
            int coverFetched = await FetchMissingCoversForLibraryAsync(btnAutoAddGames, logPrefix: "Auto-add");
            if (coverFetched > 0) viewModel.galleries = ReloadGalleries();

            // Compose a message that reflects both what was added and what got covered.
            string title2, body2;
            if (found.Count > 0 && coverFetched > 0)
            {
                title2 = "Done";
                body2  = $"Added {found.Count} game(s) under:\n{root}\n\nAuto-found covers for {coverFetched} game(s) (new + existing that were missing them).";
            }
            else if (found.Count > 0)
            {
                title2 = "Done";
                body2  = $"Added {found.Count} game(s) under:\n{root}";
            }
            else if (coverFetched > 0)
            {
                title2 = "Done";
                body2  = $"No new games found under:\n{root}\n\nAuto-found covers for {coverFetched} existing game(s) that were missing them.";
            }
            else
            {
                title2 = "Nothing new";
                body2  = $"No new games found under:\n{root}\n\n(Auto-add looks for folders that already contain an EdiConfig.json. Existing games already have covers, or no matches were found online.)";
            }
            ThemedDialog.Info(this, "Auto-add games", title2, body2);
        }

        // "Find Banners" button on the Games sub-popup: fetches a cover for every library entry
        // that's currently missing one — same as the cover-fetch step of Auto-add, but standalone
        // so the user doesn't have to re-scan their Games Location.
        private async void FindMissingCovers_Click(object sender, RoutedEventArgs e)
        {
            try { AppendLog("Find banners: starting…"); } catch { }
            int fetched = await FetchMissingCoversForLibraryAsync(sender as Button, logPrefix: "Find banners");
            if (fetched > 0) viewModel.galleries = ReloadGalleries();
            ThemedDialog.Info(this, "Find banners",
                fetched > 0 ? "Done" : "Nothing to find",
                fetched > 0
                    ? $"Auto-found covers for {fetched} game(s) that were missing them."
                    : "No online matches were found for the games missing a cover. Every game in your library either already has one on disk, or none of the sources (DLsite / F95 / itch.io / SteamGridDB) returned a result.");
        }

        // Shared art back-fill loop. Walks the library twice: once for missing banners (ImagePath)
        // and once for missing icons (IconPath). Live progress is shown on `progressBtn` and the
        // status bar so users see what's happening. Returns the combined fetched count.
        private async Task<int> FetchMissingCoversForLibraryAsync(Button? progressBtn, string logPrefix)
        {
            var needsCover = gamesConfig.GamesInfo
                .Where(g => string.IsNullOrWhiteSpace(g.ImagePath) || !System.IO.File.Exists(g.ImagePath))
                .ToList();
            var needsIcon = gamesConfig.GamesInfo
                .Where(g => string.IsNullOrWhiteSpace(g.IconPath) || !System.IO.File.Exists(g.IconPath))
                .ToList();
            try { AppendLog($"{logPrefix}: {needsCover.Count} missing covers, {needsIcon.Count} missing custom icons."); } catch { }
            if (needsCover.Count == 0 && needsIcon.Count == 0) return 0;

            object? origContent = null;
            string? origTooltip = null;
            if (progressBtn != null)
            {
                origContent = progressBtn.Content;
                origTooltip = progressBtn.ToolTip as string;
                progressBtn.IsEnabled = false;
            }
            int coverFetched = 0;
            int iconFetched  = 0;
            try
            {
                // ── Pass 1: covers (DLsite → F95 → itch.io → SteamGridDB, via AutoFindAsync). ──
                if (needsCover.Count > 0 && progressBtn != null)
                    progressBtn.Content = $"Searching covers… 0 / {needsCover.Count}";
                int idx = 0;
                foreach (var gi in needsCover)
                {
                    idx++;
                    if (progressBtn != null) progressBtn.Content = $"Searching covers… {idx} / {needsCover.Count}";
                    try { lblStatus.Content = $"{logPrefix}: cover ({idx}/{needsCover.Count}) {gi.Name}…"; } catch { }
                    try
                    {
                        var url = await CoverSearchDialog.AutoFindAsync(gi.Name);
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        var local = await DownloadCoverAsync(url, gi.Name);
                        await ApplyGameUpdate(gi, gi with { ImagePath = local }, reload: false);
                        coverFetched++;
                        try { AppendLog($"{logPrefix}: covered '{gi.Name}'."); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { AppendLog($"{logPrefix}: skipped '{gi.Name}' cover ({ex.GetType().Name})."); } catch { }
                    }
                }

                // ── Pass 2: SteamGridDB icons (needs API key — silently skips otherwise). ──
                // Only games that haven't already been served by a folder icon / .ico / exe icon are
                // queried, since the resolver still falls back to those when IconPath is empty.
                if (needsIcon.Count > 0 && progressBtn != null)
                    progressBtn.Content = $"Searching icons… 0 / {needsIcon.Count}";
                idx = 0;
                foreach (var gi in needsIcon)
                {
                    idx++;
                    if (progressBtn != null) progressBtn.Content = $"Searching icons… {idx} / {needsIcon.Count}";
                    try { lblStatus.Content = $"{logPrefix}: icon ({idx}/{needsIcon.Count}) {gi.Name}…"; } catch { }
                    try
                    {
                        var url = await CoverSearchDialog.AutoFindIconAsync(gi.Name);
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        // Re-read in case the cover pass already mutated this game in the list.
                        var current = gamesConfig.GamesInfo.FirstOrDefault(x => ReferenceEquals(x, gi))
                                   ?? gamesConfig.GamesInfo.FirstOrDefault(x => x.Name == gi.Name && x.Path == gi.Path);
                        if (current == null) continue;
                        var local = await DownloadIconAsync(url, current.Name);
                        await ApplyGameUpdate(current, current with { IconPath = local }, reload: false);
                        iconFetched++;
                        try { AppendLog($"{logPrefix}: iconed '{gi.Name}'."); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { AppendLog($"{logPrefix}: skipped '{gi.Name}' icon ({ex.GetType().Name})."); } catch { }
                    }
                }
            }
            finally
            {
                if (progressBtn != null)
                {
                    progressBtn.Content   = origContent;
                    progressBtn.ToolTip   = origTooltip;
                    progressBtn.IsEnabled = true;
                }
                try { lblStatus.Content = ""; } catch { }
                try { AppendLog($"{logPrefix}: done — {coverFetched} cover(s), {iconFetched} icon(s)."); } catch { }
            }
            return coverFetched + iconFetched;
        }

        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            var add = new AddGameDialog { Owner = this };
            if (add.ShowDialog() != true) return;

            var folder = add.FolderPath;
            // Don't assume EdiConfig.json sits at the folder root — hunt the tree for the real one and
            // use wherever it actually is (shallowest). Only fall back to the root when none exists yet.
            string configPath = FindExistingConfig(folder) ?? System.IO.Path.Combine(folder, "EdiConfig.json");

            string defaultName;
            try { defaultName = new DirectoryInfo(folder).Name; }
            catch { defaultName = folder; }

            var seed = new GameInfo(defaultName, configPath) { GameType = add.GameType };
            var settings = new GameSettings(seed, isNew: true) { Owner = this };
            if (settings.ShowDialog() != true || settings.Result == null) return;

            var newGame = settings.Result;
            var existingIdx = gamesConfig.GamesInfo.ToList().FindIndex(x => x.Path == newGame.Path);
            if (existingIdx >= 0)
            {
                gamesConfig.GamesInfo[existingIdx] = newGame;
            }
            else
            {
                gamesConfig.GamesInfo.Add(newGame);
            }
            gamesConfig.SelectedGameinfo = newGame;
            edi.ConfigurationManager.Save(gamesConfig);

            await edi.SelectGame(newGame);
            viewModel.galleries = ReloadGalleries();
            UpdateLaunchButton();
            RefreshInfoPanel();
        }

        private async void btnEditGame_Click(object sender, RoutedEventArgs e)
        {
            if (GamesComboBox.SelectedItem is not GameInfo current)
            {
                MessageBox.Show(this, "Select a game first.", "No game selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settings = new GameSettings(current, isNew: false) { Owner = this };
            if (settings.ShowDialog() != true) return;

            var idx = gamesConfig.GamesInfo.IndexOf(current);

            if (settings.DeleteRequested)
            {
                if (idx >= 0) gamesConfig.GamesInfo.RemoveAt(idx);
                if (ReferenceEquals(gamesConfig.SelectedGameinfo, current))
                {
                    gamesConfig.SelectedGameinfo = null;
                }
                edi.ConfigurationManager.Save(gamesConfig);
                return;
            }

            if (settings.Result == null) return;

            var updated = settings.Result;
            if (idx >= 0)
            {
                gamesConfig.GamesInfo[idx] = updated;
            }
            gamesConfig.SelectedGameinfo = updated;
            edi.ConfigurationManager.Save(gamesConfig);

            await edi.SelectGame(updated);
            viewModel.galleries = ReloadGalleries();
            UpdateLaunchButton();
            RefreshInfoPanel();
        }

        private void btnRemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if (GamesComboBox.SelectedItem is not GameInfo current)
            {
                MessageBox.Show(this, "Select a game first.", "No game selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(this,
                $"Remove \"{current.Name}\" from the games list?",
                "Confirm remove",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            gamesConfig.GamesInfo.Remove(current);
            if (ReferenceEquals(gamesConfig.SelectedGameinfo, current))
            {
                gamesConfig.SelectedGameinfo = null;
            }
            edi.ConfigurationManager.Save(gamesConfig);
            UpdateLaunchButton();
        }

        // Remove the RIGHT-CLICKED card's game (context-menu action), not the selected one.
        private void CardRemoveGame_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var confirm = MessageBox.Show(this,
                $"Remove \"{target.Name}\" from the games list?",
                "Confirm remove", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            bool isCurrent = ReferenceEquals(gamesConfig.SelectedGameinfo, target);
            _suppressGameReload = true;
            try
            {
                gamesConfig.GamesInfo.Remove(target);
                if (isCurrent) gamesConfig.SelectedGameinfo = null;
            }
            finally { _suppressGameReload = false; }
            edi.ConfigurationManager.Save(gamesConfig);
            UpdateLaunchButton();
        }

        // ===== Game-card context menu (Move-game layout) =====
        // These act on the RIGHT-CLICKED card (menu DataContext), not the selected game,
        // so editing/setting art never changes which game is currently loaded.

        private static GameInfo? CardGame(object sender) => (sender as FrameworkElement)?.DataContext as GameInfo;

        // Swap a game in the list for an updated copy and persist. Selection is preserved.
        // Reloads the active game only when reload==true AND the edited game is the current one.
        private async Task ApplyGameUpdate(GameInfo old, GameInfo updated, bool reload)
        {
            var idx = gamesConfig.GamesInfo.IndexOf(old);
            if (idx < 0) return;
            bool isCurrent = ReferenceEquals(gamesConfig.SelectedGameinfo, old);

            _suppressGameReload = true;
            try
            {
                gamesConfig.GamesInfo[idx] = updated;
                if (isCurrent) gamesConfig.SelectedGameinfo = updated;
            }
            finally { _suppressGameReload = false; }

            // Bust the icon cache when art changes — the resolver caches by folder, so without this
            // the card would still render the OLD icon/cover until restart.
            if (!string.Equals(old.IconPath, updated.IconPath, StringComparison.OrdinalIgnoreCase)
             || !string.Equals(old.ImagePath, updated.ImagePath, StringComparison.OrdinalIgnoreCase))
            {
                GameIconConverter.ClearCache();
                try { GamesComboBox.Items.Refresh(); } catch { }
                try { lstGames.Items.Refresh(); } catch { }
            }

            edi.ConfigurationManager.Save(gamesConfig);

            if (reload && isCurrent)
            {
                await edi.SelectGame(updated);
                viewModel.galleries = ReloadGalleries();
                RefreshInfoPanel();
            }
            UpdateLaunchButton();
        }

        private async void CardEditGame_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;

            var settings = new GameSettings(target, isNew: false) { Owner = this };
            if (settings.ShowDialog() != true) return;

            if (settings.DeleteRequested)
            {
                var idx = gamesConfig.GamesInfo.IndexOf(target);
                bool isCurrent = ReferenceEquals(gamesConfig.SelectedGameinfo, target);
                _suppressGameReload = true;
                try
                {
                    if (idx >= 0) gamesConfig.GamesInfo.RemoveAt(idx);
                    if (isCurrent) gamesConfig.SelectedGameinfo = null;
                }
                finally { _suppressGameReload = false; }
                edi.ConfigurationManager.Save(gamesConfig);
                UpdateLaunchButton();
                return;
            }

            if (settings.Result == null) return;
            await ApplyGameUpdate(target, settings.Result, reload: true);
        }

        private async void CardSetImageFile_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select a cover image or video",
                Filter = "Cover image or video|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.mp4;*.m4v;*.mov|All files|*.*",
                FilterIndex = 1,
                FileName = target.ImagePath ?? "",
            };
            if (dlg.ShowDialog(this) != true) return;
            await ApplyGameUpdate(target, target with { ImagePath = dlg.FileName }, reload: false);
        }

        private async void CardSetImageUrl_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var url = ThemedDialog.Prompt(this, "Set image from URL", "Paste an image URL",
                "The image is downloaded and stored next to your save data, then used as this game's cover.",
                target.ImagePath);
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var local = await DownloadCoverAsync(url, target.Name);
                await ApplyGameUpdate(target, target with { ImagePath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch image", "Download failed", ex.Message);
            }
        }

        private async void CardFetchCover_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;

            // Open the search dialog (it auto-searches F95 by the game name) so the user can
            // pick the right cover or paste a thread link.
            var dlg = new CoverSearchDialog(target.Name) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedImageUrl)) return;

            try
            {
                var local = await DownloadCoverAsync(dlg.SelectedImageUrl, target.Name);
                await ApplyGameUpdate(target, target with { ImagePath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch image", "Download failed", ex.Message);
            }
        }

        private async void CardCropImage_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            if (string.IsNullOrWhiteSpace(target.ImagePath) || !File.Exists(target.ImagePath))
            {
                ThemedDialog.Info(this, "Nothing to crop", "No cover set",
                    "Set a cover image first, then crop it to fit the card.");
                return;
            }
            // Lock the crop to the card's current shape (list width : card height).
            double w = lstGames.ActualWidth > 60 ? lstGames.ActualWidth : 300;
            double h = Math.Max(AppLocalSettings.Load().CardHeight, 1);
            var dlg = new CropDialog(target.ImagePath, w / h) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.CroppedPath))
                await ApplyGameUpdate(target, target with { ImagePath = dlg.CroppedPath }, reload: false);
        }

        private async void CardClearImage_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            if (string.IsNullOrEmpty(target.ImagePath)) return;
            await ApplyGameUpdate(target, target with { ImagePath = null }, reload: false);
        }

        // ===== Icon submenu handlers =====
        // Mirror the Image submenu handlers above but target IconPath. The card resolver checks
        // IconPath first, so once one of these sets a value the card shows it immediately.

        private async void CardSetIconFile_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var dlg = new OpenFileDialog
            {
                Title = "Select an icon image",
                Filter = "Icon image|*.ico;*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*",
                FilterIndex = 1,
                FileName = target.IconPath ?? "",
            };
            if (dlg.ShowDialog(this) != true) return;
            await ApplyGameUpdate(target, target with { IconPath = dlg.FileName }, reload: false);
        }

        private async void CardSetIconUrl_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var url = ThemedDialog.Prompt(this, "Set icon from URL", "Paste an icon URL",
                "The image is downloaded into <OutputDir>\\icons and used as this game's icon.",
                target.IconPath);
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                var local = await DownloadIconAsync(url, target.Name);
                await ApplyGameUpdate(target, target with { IconPath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch icon", "Download failed", ex.Message);
            }
        }

        private async void CardFetchIcon_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;

            // Multi-source: SteamGridDB icons first (needs the API key), then falls back through
            // DLsite / F95 / itch.io / SteamGridDB grids if no real icon is available. No API key
            // just means the icon-specific search returns nothing — the cover fallback still works.
            try
            {
                var url = await CoverSearchDialog.AutoFindIconAsync(target.Name);
                if (string.IsNullOrWhiteSpace(url))
                {
                    ThemedDialog.Info(this, "Fetch icon", "Nothing found",
                        $"No matches for \"{target.Name}\" on SteamGridDB / DLsite / F95 / itch.io. (Add a free SteamGridDB API key in Settings → Data for the best icon results.)");
                    return;
                }
                var local = await DownloadIconAsync(url, target.Name);
                await ApplyGameUpdate(target, target with { IconPath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch icon", "Download failed", ex.Message);
            }
        }

        // "Create shortcut…" — opens CreateShortcutDialog so the user can pick the launch target
        // (default exe / one of LaunchOptions) and toggle which quick programs (Intiface,
        // Funscript Player, etc.) auto-start with it. On Create, generates a .lnk + helper .cmd
        // and confirms the file path that was written.
        private void CardCreateShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            try
            {
                var dlg = new CreateShortcutDialog(target, QuickPrograms) { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SavedShortcutPath))
                {
                    ThemedDialog.Info(this, "Create shortcut", "Shortcut created",
                        $"Saved to:\n{dlg.SavedShortcutPath}");
                }
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Create shortcut", "Failed", ex.Message);
            }
        }

        // "Fetch video clip" — scrape DLsite then itch.io for a trailer .mp4 and apply it as
        // the card's banner. The existing per-card hover/select player handles .mp4 ImagePaths
        // out of the box, so a successful download turns the card into an animated cover.
        private async void CardFetchVideo_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            try
            {
                var url = await CoverSearchDialog.AutoFindVideoAsync(target.Name);
                if (string.IsNullOrWhiteSpace(url))
                {
                    ThemedDialog.Info(this, "Fetch video clip", "Nothing found",
                        $"No preview clip on DLsite or itch.io for \"{target.Name}\". Try the picker variant — it lists every result so you can pick a sibling product.");
                    return;
                }
                var local = await DownloadVideoAsync(url, target.Name);
                await ApplyGameUpdate(target, target with { ImagePath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch video clip", "Download failed", ex.Message);
            }
        }

        // Picker variant — collects every DLsite + itch.io match into one ImagePickerDialog,
        // showing the product poster as each row's thumbnail. The chosen entry's .mp4 gets
        // downloaded into <OutputDir>\covers\<game>\ and assigned as ImagePath.
        private async void CardFetchVideoPick_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            try
            {
                var hits = await CoverSearchDialog.SearchAllVideosAsync(target.Name);
                if (hits.Count == 0)
                {
                    ThemedDialog.Info(this, "Fetch video clip", "Nothing found",
                        $"No preview clips on DLsite or itch.io for \"{target.Name}\".");
                    return;
                }
                // Build a temp picker that maps each VideoResult to a labeled thumb. FilePath
                // here is the REMOTE .mp4 URL — the dialog returns it as SelectedPath, then
                // we download it after the user confirms.
                var items = hits.Select(v => new ImagePickerDialog.Item
                {
                    FilePath = v.Url,
                    Label    = v.Label,
                    Thumb    = !string.IsNullOrWhiteSpace(v.ThumbUrl) ? new System.Windows.Media.Imaging.BitmapImage(new Uri(v.ThumbUrl)) : null,
                }).ToList();

                var dlg = new ImagePickerDialog(
                    title: $"Choose video clip — {target.Name}",
                    hint:  "Each row is a preview .mp4 scraped from a DLsite/itch.io product page. Pick one and press Use — it'll be downloaded and used as this card's animated banner.",
                    initial: items)
                { Owner = this };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;

                var local = await DownloadVideoAsync(dlg.SelectedPath, target.Name);
                await ApplyGameUpdate(target, target with { ImagePath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch video clip", "Download failed", ex.Message);
            }
        }

        // Download an .mp4 (or .webm) into the per-game cover cache. Defaults to .mp4 if the
        // URL lacks a recognized extension, since both DLsite and itch.io serve MP4 previews.
        private static async Task<string> DownloadVideoAsync(string url, string gameName)
        {
            var bytes = await _coverHttp.GetByteArrayAsync(url);
            var dir = CoverCacheDir(gameName);
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (ext != ".mp4" && ext != ".webm" && ext != ".mov" && ext != ".m4v") ext = ".mp4";
            int i = 1; string file;
            do { file = Path.Combine(dir, $"video_{i:D2}{ext}"); i++; }
            while (File.Exists(file) && i < 999);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }

        // "Auto-fetch banner" — banner analog of the icon Fetch action above. Same multi-source
        // chain (DLsite → F95 → itch.io → SteamGridDB), but applies to ImagePath (the banner)
        // instead of IconPath. Silent first-match pick, no picker dialog.
        private async void CardAutoFetchBanner_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            try
            {
                var url = await CoverSearchDialog.AutoFindAsync(target.Name);
                if (string.IsNullOrWhiteSpace(url))
                {
                    ThemedDialog.Info(this, "Auto-fetch banner", "Nothing found",
                        $"No matches for \"{target.Name}\" on DLsite / F95 / itch.io / SteamGridDB.");
                    return;
                }
                var local = await DownloadCoverAsync(url, target.Name);
                await ApplyGameUpdate(target, target with { ImagePath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't auto-fetch banner", "Download failed", ex.Message);
            }
        }

        private async void CardClearIcon_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            if (string.IsNullOrEmpty(target.IconPath)) return;
            // Drop the custom IconPath → resolver falls back to the folder/.ico/exe chain.
            await ApplyGameUpdate(target, target with { IconPath = null }, reload: false);
        }

        // Opens the CoverSearchDialog (which already hits F95 / DLsite / itch.io / SteamGridDB)
        // and assigns the picked image as the game's ICON instead of its banner. Useful when
        // the auto-fetch picks the wrong one and you want to choose from a result grid.
        private async void CardFetchIconPick_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var dlg = new CoverSearchDialog(target.Name) { Owner = this, Title = $"Find icon — {target.Name}" };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.SelectedImageUrl)) return;
            try
            {
                var local = await DownloadIconAsync(dlg.SelectedImageUrl, target.Name);
                await ApplyGameUpdate(target, target with { IconPath = local }, reload: false);
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Couldn't fetch icon", "Download failed", ex.Message);
            }
        }

        // Same CropDialog the banner uses, locked to 1:1 since icons are square. Writes the
        // cropped output as a new file and points IconPath at it.
        private async void CardCropIcon_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            if (string.IsNullOrWhiteSpace(target.IconPath) || !File.Exists(target.IconPath))
            {
                ThemedDialog.Info(this, "Nothing to crop", "No icon set",
                    "Set an icon first (right-click → Icon → Set from file / Fetch icon / Browse icons), then crop it.");
                return;
            }
            var dlg = new CropDialog(target.IconPath, 1.0) { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.CroppedPath))
                await ApplyGameUpdate(target, target with { IconPath = dlg.CroppedPath }, reload: false);
        }

        // ===== Quick-swap icon source handlers =====
        // Each pins the icon to one explicit source (folder / .ico / exe) by materializing that
        // source into the per-game cache folder and setting IconPath to the cached file.

        private async void CardUseFolderIcon_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var file = CacheFolderIcon(target);
            if (file == null)
            {
                ThemedDialog.Info(this, "Use folder icon", "No folder icon",
                    "This folder doesn't have a custom Windows icon (Properties → Customize → Change Icon). Set one in Windows first, or pick a different source.");
                return;
            }
            await ApplyGameUpdate(target, target with { IconPath = file }, reload: false);
        }

        private async void CardUseIcoInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var ico = FindIcoInFolder(target);
            if (ico == null)
            {
                ThemedDialog.Info(this, "Use .ico in folder", "No .ico file",
                    "No .ico file was found in the game folder. Drop one in there or pick a different source.");
                return;
            }
            await ApplyGameUpdate(target, target with { IconPath = ico }, reload: false);
        }

        private async void CardUseExeIcon_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var file = CacheExeIcon(target);
            if (file == null)
            {
                ThemedDialog.Info(this, "Use exe icon", "No exe set",
                    "Set the game's executable path first (Edit game…) — the exe is where the icon is extracted from.");
                return;
            }
            await ApplyGameUpdate(target, target with { IconPath = file }, reload: false);
        }

        // ===== Browse pickers =====
        // Opens the ImagePickerDialog with every available source as a thumbnail: folder icon,
        // .ico in folder, exe icon, plus everything already cached in <OutputDir>\icons\<game>\.
        // "Fetch more" hits SteamGridDB and refreshes the grid. Click → IconPath update.
        private async void CardBrowseIcons_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            await ShowIconBrowserFor(target);
        }

        private async Task ShowIconBrowserFor(GameInfo target)
        {
            var dir = IconCacheDir(target.Name);
            var items = new List<ImagePickerDialog.Item>();

            // Folder icon (materialize on the fly so it shows in the grid).
            var folderIcon = CacheFolderIcon(target);
            if (folderIcon != null) items.Add(new ImagePickerDialog.Item { FilePath = folderIcon, Label = "Folder icon" });

            // .ico in folder.
            var ico = FindIcoInFolder(target);
            if (ico != null) items.Add(new ImagePickerDialog.Item { FilePath = ico, Label = ".ico in folder" });

            // Exe icon.
            var exe = CacheExeIcon(target);
            if (exe != null) items.Add(new ImagePickerDialog.Item { FilePath = exe, Label = "Exe icon" });

            // Cached SteamGridDB / custom downloads.
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp" && ext != ".bmp" && ext != ".gif" && ext != ".ico") continue;
                    // Skip the synthetic _folder/_exe entries (already added above as labeled rows).
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name == "_folder" || name == "_exe") continue;
                    items.Add(new ImagePickerDialog.Item { FilePath = f, Label = name });
                }
            }

            var dlg = new ImagePickerDialog(
                title: $"Choose icon — {target.Name}",
                hint: "Click a thumbnail and press Use. \"Fetch more\" queries SteamGridDB for additional icon variants (needs the API key in Settings → Data).",
                initial: items)
            {
                Owner = this,
                CacheDir = dir,
                FetchMoreAsync = async () =>
                {
                    var hits = await CoverSearchDialog.SearchSteamGridDbIconsAsync(target.Name);
                    foreach (var h in hits.Take(12))
                    {
                        try { await DownloadIconAsync(h.ImageUrl, target.Name); } catch { }
                    }
                }
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                await ApplyGameUpdate(target, target with { IconPath = dlg.SelectedPath }, reload: false);
            }
        }

        // Parallel browse for cover/banner art. Lists everything cached under <OutputDir>\covers\<game>\
        // and "Fetch more" pulls additional results from DLsite → F95 → itch.io → SteamGridDB.
        private async void CardBrowseCovers_Click(object sender, RoutedEventArgs e)
        {
            if (CardGame(sender) is not GameInfo target) return;
            var dir = CoverCacheDir(target.Name);
            var items = new List<ImagePickerDialog.Item>();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp" && ext != ".bmp" && ext != ".gif") continue;
                    items.Add(new ImagePickerDialog.Item { FilePath = f, Label = Path.GetFileNameWithoutExtension(f) });
                }
            }
            var dlg = new ImagePickerDialog(
                title: $"Choose cover — {target.Name}",
                hint: "Click a thumbnail and press Use. \"Fetch more\" queries DLsite / F95 / itch.io / SteamGridDB for additional cover variants.",
                initial: items)
            {
                Owner = this,
                CacheDir = dir,
                FetchMoreAsync = async () =>
                {
                    // Hit the SAME pipeline AutoFindAsync uses, but cache the FIRST result instead
                    // of pre-filtering. Repeat clicks keep adding new files into the per-game folder.
                    var url = await CoverSearchDialog.AutoFindAsync(target.Name);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        try { await DownloadCoverAsync(url, target.Name); } catch { }
                    }
                }
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                await ApplyGameUpdate(target, target with { ImagePath = dlg.SelectedPath }, reload: false);
            }
        }

        // ===================== Animated (video) covers — only the hovered/selected card plays =====================

        private static readonly string[] _videoExt = { ".mp4", ".m4v", ".mov" };
        private static bool IsVideoFile(string path)
            => _videoExt.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        private void Card_HoverChanged(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBoxItem item) UpdateCardMedia(item);
        }

        private void Card_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBoxItem item) UpdateCardMedia(item);
        }

        // Auto-fetch path: when ImagePath isn't a video/gif and the game's folder has a
        // "media" subfolder, pick a (cached) random video/gif from there. Lets users drop
        // clips into <game>\media\ and have them play on hover without manually pointing
        // ImagePath at one. Stable per-game so the same clip keeps playing for the session.
        private static readonly string[] _animExt = { ".mp4", ".m4v", ".mov", ".gif" };
        private readonly Dictionary<string, string> _mediaFolderPick = new(StringComparer.OrdinalIgnoreCase);
        private string? AutoPickMediaClip(GameInfo? g)
        {
            if (g == null) return null;
            var folder = ResolveGameFolder(g);
            if (folder == null) return null;
            var media = Path.Combine(folder, "media");
            if (!Directory.Exists(media)) return null;
            if (_mediaFolderPick.TryGetValue(media, out var cached) && File.Exists(cached)) return cached;
            try
            {
                var pick = Directory.EnumerateFiles(media, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => _animExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(_ => Guid.NewGuid().ToString())   // random per session
                    .FirstOrDefault();
                if (pick != null) _mediaFolderPick[media] = pick;
                return pick;
            }
            catch { return null; }
        }

        // Play the card's animated cover (mp4 video or animated gif) only while it's hovered or
        // selected; otherwise release it so a long list stays light.
        private void UpdateCardMedia(System.Windows.Controls.ListBoxItem item)
        {
            var game = item.DataContext as GameInfo;
            var path = game?.ImagePath;
            bool isAnim = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                          && _animExt.Contains(Path.GetExtension(path!).ToLowerInvariant());
            if (!isAnim)
            {
                // Fall back to a clip from <game>\media\. Doesn't overwrite ImagePath; just
                // used in-place for hover/select playback.
                var auto = AutoPickMediaClip(game);
                if (!string.IsNullOrWhiteSpace(auto)) path = auto;
            }
            bool exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            bool active = item.IsMouseOver || item.IsSelected;
            string ext = exists ? System.IO.Path.GetExtension(path!).ToLowerInvariant() : "";

            var vid = FindNamed<System.Windows.Controls.MediaElement>(item, "vid");
            if (vid != null)
            {
                if (active && _videoExt.Contains(ext))
                {
                    if (vid.Source == null) vid.Source = new Uri(path!, UriKind.Absolute);
                    vid.Visibility = Visibility.Visible;
                    vid.Play();
                }
                else
                {
                    vid.Stop();
                    vid.Source = null;
                    vid.Visibility = Visibility.Collapsed;
                }
            }

            var gif = FindNamed<System.Windows.Controls.Image>(item, "gifImg");
            if (gif != null)
            {
                if (active && ext == ".gif")
                {
                    if (WpfAnimatedGif.ImageBehavior.GetAnimatedSource(gif) == null)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(path!, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        WpfAnimatedGif.ImageBehavior.SetAnimatedSource(gif, bmp);
                    }
                    gif.Visibility = Visibility.Visible;
                }
                else
                {
                    WpfAnimatedGif.ImageBehavior.SetAnimatedSource(gif, null);
                    gif.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void CardVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MediaElement m) { m.Position = TimeSpan.Zero; m.Play(); }
        }

        private static T? FindNamed<T>(DependencyObject root, string name) where T : System.Windows.FrameworkElement
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T fe && fe.Name == name) return fe;
                var deeper = FindNamed<T>(c, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static HttpClient CreateCoverHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Edi");
            return h;
        }

        // Downloads an image URL into <OutputDir>\covers and returns the local file path.
        // Per-game cache layout: <OutputDir>\covers\<safeName>\<n>.<ext>  and  <OutputDir>\icons\<safeName>\<n>.<ext>
        // Each successful download appends a new file, so previously-downloaded variants stick
        // around for the picker to browse (no need to re-hit DLsite / SteamGridDB).
        private static string SafeGameDirName(string name)
        {
            var safe = string.Join("_", (name ?? "game").Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(safe) ? "game" : safe;
        }

        private static string IconCacheDir(string gameName)
            => Path.Combine(Edi.Core.Edi.OutputDir, "icons", SafeGameDirName(gameName));

        private static string CoverCacheDir(string gameName)
            => Path.Combine(Edi.Core.Edi.OutputDir, "covers", SafeGameDirName(gameName));

        private static Task<string> DownloadCoverAsync(string url, string gameName)
            => DownloadArtAsync(url, CoverCacheDir(gameName), "cover");

        // Same as DownloadCoverAsync but lands in <OutputDir>\icons\<game>\. Kept as its own
        // helper so the call sites read intent-clearly.
        private static Task<string> DownloadIconAsync(string url, string gameName)
            => DownloadArtAsync(url, IconCacheDir(gameName), "icon");

        private static async Task<string> DownloadArtAsync(string url, string targetDir, string defaultName)
        {
            var bytes = await _coverHttp.GetByteArrayAsync(url);
            Directory.CreateDirectory(targetDir);

            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".png";

            // Pick the next free numeric name so duplicates accumulate rather than overwrite.
            int i = 1;
            string file;
            do { file = Path.Combine(targetDir, $"{defaultName}_{i:D2}{ext}"); i++; }
            while (File.Exists(file) && i < 999);

            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }

        // ===== Icon source helpers (for the right-click "Use folder/.ico/exe icon" quick actions) =====

        // Returns the folder backing this game (same logic the ResolveCardIcon resolver uses):
        // prefers GalleryPath, falls back to ExePath's directory.
        private static string? ResolveGameFolder(GameInfo g)
        {
            string? folder = !string.IsNullOrWhiteSpace(g.GalleryPath) && Directory.Exists(g.GalleryPath)
                ? g.GalleryPath
                : (!string.IsNullOrWhiteSpace(g.ExePath) ? Path.GetDirectoryName(g.ExePath) : null);
            return Directory.Exists(folder) ? folder : null;
        }

        // Encode an ImageSource to PNG bytes on disk. Used to materialize transient HICON sources
        // (folder shell icons / exe-extracted icons) into the cache so the picker can show them.
        private static bool SaveImageSourceAsPng(System.Windows.Media.ImageSource src, string file)
        {
            try
            {
                if (src is not System.Windows.Media.Imaging.BitmapSource bs) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bs));
                using var fs = File.Create(file);
                enc.Save(fs);
                return true;
            }
            catch { return false; }
        }

        // Resolve the "best representation" of the folder's icon and cache it as
        // <gameCache>\_folder.png. The order matters because Windows hides folder-icon
        // customization in several places — earlier builds fell through to the generic
        // shell icon too readily and produced "default folder" results.
        //
        //   1) desktop.ini's IconResource / IconFile / IconIndex — the real "Customize" path
        //      • for .ico files use IconBitmapDecoder and grab the largest frame
        //      • otherwise PrivateExtractIcons handles .exe/.dll indexed icons
        //   2) folder.ico / icon.ico / _folder.ico inside the folder (common loose conventions)
        //   3) Shell jumbo for the folder itself (last resort — this is what returns the
        //      generic icon when nothing is customized)
        //
        // The decision is logged so the user can see which source the icon came from when
        // they pick "Use folder icon" and don't see what they expected.
        private string? CacheFolderIcon(GameInfo g)
        {
            var folder = ResolveGameFolder(g);
            if (folder == null) return null;

            System.Windows.Media.ImageSource? img = null;
            string source = "shell-default";

            // 1) desktop.ini → IconResource
            var src = GameIconConverter.ResolveDesktopIniIcon(folder);
            if (src != null && File.Exists(src.Value.file))
            {
                var ext = Path.GetExtension(src.Value.file).ToLowerInvariant();
                if (ext == ".ico")
                {
                    // IconBitmapDecoder reads every frame inside the .ico — taking the largest
                    // frame is reliably the high-res variant. PrivateExtractIcons sometimes
                    // grabs the smallest frame on certain multi-frame icons.
                    try
                    {
                        using var fs = File.OpenRead(src.Value.file);
                        var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(
                            fs,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        var best = dec.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                        if (best != null) { best.Freeze(); img = best; source = "desktop.ini → " + Path.GetFileName(src.Value.file); }
                    }
                    catch { }
                }
                if (img == null)
                {
                    img = FolderIcon.GetHighRes(src.Value.file, 256, src.Value.index)
                       ?? FolderIcon.GetJumbo(src.Value.file)
                       ?? FolderIcon.Get(src.Value.file);
                    if (img != null) source = $"desktop.ini → {Path.GetFileName(src.Value.file)}#{src.Value.index}";
                }
            }

            // 2) folder.ico / icon.ico / _folder.ico loose in the folder
            if (img == null)
            {
                foreach (var candidate in new[] { "folder.ico", "icon.ico", "_folder.ico" })
                {
                    var p = Path.Combine(folder, candidate);
                    if (!File.Exists(p)) continue;
                    try
                    {
                        using var fs = File.OpenRead(p);
                        var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(
                            fs,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        var best = dec.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
                        if (best != null) { best.Freeze(); img = best; source = "loose " + candidate; break; }
                    }
                    catch { }
                }
            }

            // 3) Shell jumbo for the folder (will be the generic folder icon if nothing's customized)
            if (img == null)
            {
                img = FolderIcon.GetJumbo(folder) ?? FolderIcon.Get(folder);
                if (img != null) source = "shell jumbo";
            }
            if (img == null) return null;

            var file = Path.Combine(IconCacheDir(g.Name), "_folder.png");
            try { AppendLog($"Use folder icon: source={source} → {file}"); } catch { }
            return SaveImageSourceAsPng(img, file) ? file : null;
        }

        // First *.ico file sitting in the game folder, or null.
        private static string? FindIcoInFolder(GameInfo g)
        {
            var folder = ResolveGameFolder(g);
            if (folder == null) return null;
            try { return Directory.EnumerateFiles(folder, "*.ico", SearchOption.TopDirectoryOnly).FirstOrDefault(); }
            catch { return null; }
        }

        // Extract the .exe icon at 256px and cache it as <gameCache>\_exe.png. Returns the cached path.
        private string? CacheExeIcon(GameInfo g)
        {
            if (string.IsNullOrWhiteSpace(g.ExePath) || !File.Exists(g.ExePath)) return null;
            var img = FolderIcon.GetHighRes(g.ExePath, 256) ?? FolderIcon.GetJumbo(g.ExePath) ?? FolderIcon.Get(g.ExePath);
            if (img == null) return null;
            var file = Path.Combine(IconCacheDir(g.Name), "_exe.png");
            return SaveImageSourceAsPng(img, file) ? file : null;
        }

        // ===================== Quick-launch bar =====================

        private void InitQuickPrograms()
        {
            var s = AppLocalSettings.Load();
            // Re-run detection on first launch, or to repair a previous bad auto-pick (e.g. a helper exe).
            bool hasBadAuto = s.QuickPrograms.Any(p => IsHelperExe(p.Path));
            if (!s.QuickProgramsInitialized || hasBadAuto)
            {
                s.QuickPrograms.RemoveAll(p => IsHelperExe(p.Path));
                foreach (var p in AutoDetectPrograms())
                    if (!s.QuickPrograms.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase)))
                        s.QuickPrograms.Add(p);
                s.QuickProgramsInitialized = true;
                s.Save();
            }

            // Default the Intiface pill's URL to the standard server address if it has none.
            bool urlChanged = false;
            foreach (var p in s.QuickPrograms)
                if (string.Equals(p.Name, "Intiface", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(p.Url))
                { p.Url = "ws://localhost:12345"; urlChanged = true; }
            if (urlChanged) s.Save();

            QuickPrograms.Clear();
            foreach (var p in s.QuickPrograms) QuickPrograms.Add(p);

            // Render the program pills as real top-bar children (so they drag-reorder with the rest),
            // and keep them in sync as programs are added/removed/edited.
            RebuildProgramPills();
            QuickPrograms.CollectionChanged += (_, __) => RebuildProgramPills();
        }

        // Installer/runtime helper executables we never want to treat as the launchable program.
        private static bool IsHelperExe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string[] bad = { "crashpad", "unins", "setup", "vcredist", "dxsetup", "dxwebsetup",
                             "elevation", "notification_helper", "_handler", "crash" };
            return bad.Any(b => n.Contains(b));
        }

        private void SaveQuickPrograms()
        {
            var s = AppLocalSettings.Load();
            s.QuickPrograms = QuickPrograms.ToList();
            s.QuickProgramsInitialized = true;
            s.Save();
        }

        private void QuickProgram_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not QuickProgram p) return;

            // Glyph is a server+client pair. Start the server first if it isn't already
            // running, then the client. Both paths live on the pill: Path=client (so the
            // generic-launch fallback still works), Url=server.
            if (string.Equals(p.Name, "Glyph", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var server = p.Url;   // we stash the server exe path here on Add
                    if (!string.IsNullOrWhiteSpace(server) && File.Exists(server))
                    {
                        var serverProcName = Path.GetFileNameWithoutExtension(server);
                        var alreadyRunning = System.Diagnostics.Process.GetProcessesByName(serverProcName).Length > 0;
                        if (!alreadyRunning)
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = server,
                                WorkingDirectory = Path.GetDirectoryName(server) ?? Environment.CurrentDirectory,
                                UseShellExecute = true,
                            });
                            try { AppendLog("Glyph: started Glyph Server."); } catch { }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(p.Path) && File.Exists(p.Path))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = p.Path,
                            WorkingDirectory = Path.GetDirectoryName(p.Path) ?? Environment.CurrentDirectory,
                            UseShellExecute = true,
                        });
                    }
                    return;
                }
                catch (Exception ex)
                {
                    ThemedDialog.Info(this, "Glyph", "Couldn't launch", ex.Message);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(p.Path))
            {
                ThemedDialog.Info(this, "Quick launch", "Nothing to launch",
                    "This pill has no Path or URL set. Remove it (right-click) and add it again.");
                return;
            }

            // URL pills (Stash, Swagger-style web entries) open in the default browser via
            // ShellExecute; the File.Exists check only applies to local exes.
            bool isUrl = p.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || p.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isUrl && !File.Exists(p.Path))
            {
                ThemedDialog.Info(this, "Quick launch", "Program not found",
                    $"Couldn't find:\n{p.Path}\n\nRemove it (right-click) and add it again.");
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = p.Path,
                    WorkingDirectory = isUrl ? Environment.CurrentDirectory : (Path.GetDirectoryName(p.Path) ?? Environment.CurrentDirectory),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                ThemedDialog.Info(this, "Quick launch", "Couldn't launch", ex.Message);
            }
        }

        // ───────── top-bar pill drag-reorder ─────────
        // All pills (API Docs, program launchers, connection chips) are direct children of `topBar`.
        // Program pills are created from the hidden template host so they keep their rich icons + handlers.

        private void RebuildProgramPills()
        {
            if (topBar == null || programTemplateHost?.ItemTemplate == null) return;

            foreach (var c in topBar.Children.OfType<FrameworkElement>()
                                    .Where(c => c.Tag is string t && t.StartsWith("prog:")).ToList())
                topBar.Children.Remove(c);

            int at = btnAddTop != null ? topBar.Children.IndexOf(btnAddTop) : topBar.Children.Count;
            foreach (var prog in QuickPrograms)
            {
                if (programTemplateHost.ItemTemplate.LoadContent() is not FrameworkElement pill) continue;
                pill.DataContext = prog;
                pill.Tag = "prog:" + prog.Name;
                topBar.Children.Insert(at++, pill);
            }
            ApplyTopBarOrder();
        }

        private bool _applyingOrder;
        // Arrange the pills to match the saved order; unknown/new pills keep their current relative order.
        private void ApplyTopBarOrder()
        {
            if (topBar == null || _applyingOrder) return;
            _applyingOrder = true;
            try
            {
                var order = AppLocalSettings.Load().TopBarOrder ?? new List<string>();
                var pills = topBar.Children.OfType<FrameworkElement>()
                                  .Where(c => c.Tag is string).ToList();
                var ordered = pills.OrderBy(p =>
                {
                    int i = order.IndexOf((string)p.Tag);
                    return i < 0 ? int.MaxValue : i;
                }).ToList();
                foreach (var p in ordered) topBar.Children.Remove(p);
                int idx = btnAddTop != null ? topBar.Children.IndexOf(btnAddTop) : topBar.Children.Count;
                foreach (var p in ordered) topBar.Children.Insert(idx++, p);
            }
            catch { }
            finally { _applyingOrder = false; }
        }

        private void SaveTopBarOrder()
        {
            if (topBar == null) return;
            try
            {
                var keys = topBar.Children.OfType<FrameworkElement>()
                    .Where(c => c.Tag is string t && (t == "apidocs" || t.StartsWith("prog:") || t.StartsWith("chip:")))
                    .Select(c => (string)c.Tag).ToList();
                var s = AppLocalSettings.Load();
                s.TopBarOrder = keys;
                s.Save();
            }
            catch { }
        }

        // Walk up from a hit element to the pill (a direct, Tagged child of topBar).
        private FrameworkElement FindTopBarPill(DependencyObject src)
        {
            while (src != null)
            {
                if (src is FrameworkElement fe && fe.Tag is string && topBar.Children.Contains(fe)) return fe;
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            return null;
        }

        private System.Windows.Point _pillDragStart;
        private FrameworkElement _pillDown, _pillDragging;

        private void TopBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _pillDragStart = e.GetPosition(topBar);
            _pillDown = FindTopBarPill(e.OriginalSource as DependencyObject);
        }

        private void TopBar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pillDown == null) return;
            var p = e.GetPosition(topBar);
            if (Math.Abs(p.X - _pillDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _pillDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pillDragging = _pillDown;
            _pillDown = null;
            _pillDragging.Opacity = 0.5;
            try { DragDrop.DoDragDrop(_pillDragging, "pill", DragDropEffects.Move); }
            catch { }
            finally
            {
                if (_pillDragging != null) _pillDragging.Opacity = 1;
                _pillDragging = null;
                ClearPillDropAdorner();
            }
        }

        // Insertion-line adorner for the top bar — vertical line snapping between pills as
        // the cursor hovers. Same visual language as the games-list drop indicator so the two
        // drag UIs feel consistent.
        private DragInsertionAdorner? _pillDropAdorner;

        private void TopBar_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = _pillDragging != null ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (_pillDragging == null) return;

            if (_pillDropAdorner == null)
            {
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(topBar);
                if (layer != null) { _pillDropAdorner = new DragInsertionAdorner(topBar); layer.Add(_pillDropAdorner); }
            }
            if (_pillDropAdorner == null) return;

            try
            {
                var target = FindTopBarPill(e.OriginalSource as DependencyObject);
                double x;
                if (target != null && !ReferenceEquals(target, _pillDragging))
                {
                    var left = target.TranslatePoint(new System.Windows.Point(0, 0), topBar);
                    bool after = e.GetPosition(target).X > target.ActualWidth / 2;
                    x = after ? left.X + target.ActualWidth : left.X;
                }
                else
                {
                    // Hovering empty space — snap the line to the trailing edge of the last pill.
                    x = topBar.ActualWidth - 4;
                }
                _pillDropAdorner.SetVertical(x, topBar.ActualHeight);
            }
            catch { }
        }

        private void TopBar_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(topBar);
            if (p.X < 0 || p.Y < 0 || p.X > topBar.ActualWidth || p.Y > topBar.ActualHeight)
                ClearPillDropAdorner();
        }

        private void TopBar_Drop(object sender, DragEventArgs e)
        {
            ClearPillDropAdorner();
            var dragged = _pillDragging;
            if (dragged == null || !(dragged.Tag is string)) return;
            var target = FindTopBarPill(e.OriginalSource as DependencyObject);
            if (target == null || ReferenceEquals(target, dragged)) return;

            topBar.Children.Remove(dragged);
            int ti = topBar.Children.IndexOf(target);
            bool after = e.GetPosition(target).X > target.ActualWidth / 2;
            topBar.Children.Insert(after ? ti + 1 : ti, dragged);
            if (btnAddTop != null) { topBar.Children.Remove(btnAddTop); topBar.Children.Add(btnAddTop); }
            SaveTopBarOrder();
        }

        private void ClearPillDropAdorner()
        {
            if (_pillDropAdorner == null) return;
            try
            {
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(topBar);
                layer?.Remove(_pillDropAdorner);
            }
            catch { }
            _pillDropAdorner = null;
        }

        private void AddProgram_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select a program to add to the quick-launch bar",
                Filter = "Programs|*.exe;*.lnk|All files|*.*",
                FilterIndex = 1,
            };
            if (dlg.ShowDialog(this) != true) return;

            var path = dlg.FileName;
            var defaultName = Path.GetFileNameWithoutExtension(path);
            var name = ThemedDialog.Prompt(this, "Add program", "Name this shortcut",
                "Shown on the top quick-launch bar.", defaultName);
            if (string.IsNullOrWhiteSpace(name)) name = defaultName;

            QuickPrograms.Add(new QuickProgram { Name = name, Path = path });
            SaveQuickPrograms();
        }

        // "+" menu → Intiface: pin Intiface Central as a launcher pill (auto-detect, else browse).
        private void AddIntiface_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "Intiface", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "Intiface", "Already added", "Intiface is already on the top bar.");
                return;
            }

            var hit = AutoDetectPrograms()
                .FirstOrDefault(p => string.Equals(p.Name, "Intiface", StringComparison.OrdinalIgnoreCase));
            string? path = hit?.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate Intiface Central",
                    Filter = "Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }

            QuickPrograms.Add(new QuickProgram { Name = "Intiface", Path = path, Url = "ws://localhost:12345" });
            SaveQuickPrograms();
        }

        // "+" menu → Funscript Player: pin the configured player (Settings ▸ Funscript Player Location), else browse.
        private void AddFunscriptPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "Funscript Player", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "Funscript Player", "Already added", "Funscript Player is already on the top bar.");
                return;
            }

            // Honour a path the user has typed/saved in Settings; otherwise use the default
            // "FunscriptPlayer" folder next to Edi.exe. Either way, resolve it to the player exe.
            string? configured = txtFunscriptPlayer?.Text;
            if (string.IsNullOrWhiteSpace(configured)) configured = EffectiveFunscriptPlayerLocation();
            string? path = ResolvePlayerExe(configured);
            if (path == null)
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate your funscript player",
                    Filter = "Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
                // Remember it as the player that Script Player games launch through.
                var s = AppLocalSettings.Load();
                s.FunscriptPlayerPath = path;
                s.Save();
                if (txtFunscriptPlayer != null) txtFunscriptPlayer.Text = path;
            }

            QuickPrograms.Add(new QuickProgram { Name = "Funscript Player", Path = path });
            SaveQuickPrograms();
        }

        // "+" menu → MultiFunPlayer: tries the registered/portable install paths first, then
        // falls back to an Open-File dialog. Same wave icon as Funscript Player.
        private void AddMultiFunPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "MultiFunPlayer", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "MultiFunPlayer", "Already added", "MultiFunPlayer is already on the top bar.");
                return;
            }

            // Auto-detect via the same uninstall-key scan that handles Intiface. Falls back to a
            // shortlist of common portable layouts before asking the user to browse.
            var hit = AutoDetectPrograms()
                .FirstOrDefault(p => string.Equals(p.Name, "MultiFunPlayer", StringComparison.OrdinalIgnoreCase));
            string? path = hit?.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                foreach (var candidate in new[]
                {
                    @"D:\useful\Multi Fun Player\File\MultiFunPlayer.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MultiFunPlayer", "MultiFunPlayer.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MultiFunPlayer", "MultiFunPlayer.exe"),
                })
                {
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate MultiFunPlayer.exe",
                    Filter = "MultiFunPlayer (*.exe)|MultiFunPlayer.exe|Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }

            QuickPrograms.Add(new QuickProgram { Name = "MultiFunPlayer", Path = path });
            SaveQuickPrograms();
        }

        // "+" menu → OpenFunscripter (OFS): the desktop funscript editor. Tries the
        // registered install via AutoDetectPrograms first (it already knows the "openfunscripter"
        // uninstall key), then a shortlist of known portable layouts before asking the user to
        // browse. Distinct pencil-on-wave pill icon set up in MainWindow.xaml.
        private void AddOfs_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "OpenFunscripter", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "OpenFunscripter", "Already added", "OpenFunscripter is already on the top bar.");
                return;
            }

            var hit = AutoDetectPrograms()
                .FirstOrDefault(p => string.Equals(p.Name, "OpenFunscripter", StringComparison.OrdinalIgnoreCase));
            string? path = hit?.Path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                foreach (var candidate in new[]
                {
                    @"D:\useful\Open Fun Scripter\File\Open Fun Scripter.exe",
                    @"D:\useful\OpenFunscripter\OpenFunscripter.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenFunscripter", "OpenFunscripter.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenFunscripter", "OpenFunscripter.exe"),
                })
                {
                    if (File.Exists(candidate)) { path = candidate; break; }
                }
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate OpenFunscripter.exe",
                    Filter = "OpenFunscripter (*.exe)|OpenFunscripter.exe;Open Fun Scripter.exe|Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                path = dlg.FileName;
            }

            QuickPrograms.Add(new QuickProgram { Name = "OpenFunscripter", Path = path });
            SaveQuickPrograms();
        }

        // "+" menu → Glyph: launches Glyph Server (if not already running) then Glyph Client.
        // Both paths are stashed on the QuickProgram (Path=client, Url=server) so QuickProgram_Click
        // can fire them in order. Auto-detect the standard install layout first, then ask the
        // user to browse if it's not where we expect it.
        private void AddGlyph_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "Glyph", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "Glyph", "Already added", "Glyph is already on the top bar.");
                return;
            }

            string? client = null, server = null;
            foreach (var c in new[]
            {
                @"D:\useful\Glyph\FILE\CLIENT\Glyph Client\Glyph Client.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Glyph", "Glyph Client.exe"),
            })
                if (File.Exists(c)) { client = c; break; }
            foreach (var s in new[]
            {
                @"D:\useful\Glyph\FILE\SERVER\Glyph Server\Glyph Server.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Glyph", "Glyph Server.exe"),
            })
                if (File.Exists(s)) { server = s; break; }

            if (client == null)
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate Glyph Client.exe",
                    Filter = "Glyph Client (Glyph Client.exe)|Glyph Client.exe|Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) != true) return;
                client = dlg.FileName;
            }
            if (server == null)
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Locate Glyph Server.exe (the pair-launched server)",
                    Filter = "Glyph Server (Glyph Server.exe)|Glyph Server.exe|Programs|*.exe;*.lnk|All files|*.*",
                };
                if (dlg.ShowDialog(this) == true) server = dlg.FileName;
                // Optional — if the user cancels, the pill still works for client-only.
            }

            QuickPrograms.Add(new QuickProgram { Name = "Glyph", Path = client, Url = server ?? "", ShowUrl = false });
            SaveQuickPrograms();
        }

        // "+" menu → Stash: URL-only pill that opens http://localhost:9999/ in the browser
        // (Stash's default address). No exe to detect — the URL itself is the Path so the
        // generic QuickProgram_Click can ShellExecute it like any web link.
        private void AddStash_Click(object sender, RoutedEventArgs e)
        {
            if (QuickPrograms.Any(p => string.Equals(p.Name, "Stash", StringComparison.OrdinalIgnoreCase)))
            {
                ThemedDialog.Info(this, "Stash", "Already added", "Stash is already on the top bar.");
                return;
            }
            const string defaultUrl = "http://localhost:9999/";
            QuickPrograms.Add(new QuickProgram { Name = "Stash", Path = defaultUrl, Url = defaultUrl, ShowUrl = true });
            SaveQuickPrograms();
        }

        // Default funscript-player location: a "FunscriptPlayer" folder sitting next to Edi.exe.
        // (Script Player games launch through this player and need no EdiConfig.json of their own.)
        private static string DefaultFunscriptPlayerDir =>
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "FunscriptPlayer");

        // The configured player location, or the default folder next to Edi.exe when nothing is set.
        private static string EffectiveFunscriptPlayerLocation()
        {
            var s = AppLocalSettings.Load();
            return string.IsNullOrWhiteSpace(s.FunscriptPlayerPath) ? DefaultFunscriptPlayerDir : s.FunscriptPlayerPath!;
        }

        // The "Funscript Player Location" setting may be an exe or a folder; resolve it to a launchable exe.
        private static string? ResolvePlayerExe(string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return null;
            var p = AddGameDialog.CleanPath(configured);
            if (File.Exists(p)) return p;
            if (Directory.Exists(p))
            {
                var exes = Directory.EnumerateFiles(p, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                if (exes.Count == 0) return null;
                string[] prefer = { "multifunplayer", "scriptplayer", "openfunscripter", "funscript", "player" };
                foreach (var key in prefer)
                {
                    var match = exes.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(key));
                    if (match != null) return match;
                }
                return exes[0];
            }
            return null;
        }

        private void RemoveProgram_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not QuickProgram p) return;
            QuickPrograms.Remove(p);
            SaveQuickPrograms();
        }

        // Right-click → Edit: for URL-style entries this edits the optional URL shown next to the
        // name (like the API Docs link); for folder-style entries (Funscript Player etc., where
        // Path is a directory) it edits the folder Path itself — URL is meaningless there.
        private void EditProgramUrl_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not QuickProgram p) return;
            int idx = QuickPrograms.IndexOf(p);
            if (idx < 0) return;

            if (p.IsFolderPath)
            {
                var newPath = ThemedDialog.Prompt(this, "Edit Folder Path", $"Folder for {p.Name}",
                    "The folder this entry opens.", p.Path);
                if (newPath == null) return;   // cancelled
                // Replace the item so the bound subtitle refreshes (QuickProgram isn't observable).
                QuickPrograms[idx] = new QuickProgram { Name = p.Name, Path = newPath, Url = p.Url, ShowUrl = p.ShowUrl };
            }
            else
            {
                var url = ThemedDialog.Prompt(this, "Edit URL", $"URL for {p.Name}",
                    "Shown next to the name on the quick-launch bar.", p.Url);
                if (url == null) return;   // cancelled
                QuickPrograms[idx] = new QuickProgram { Name = p.Name, Path = p.Path, Url = url, ShowUrl = p.ShowUrl };
            }
            SaveQuickPrograms();
        }

        // Right-click ▸ "Show URL": toggle whether this launcher's URL line is shown (default on).
        private void ToggleProgramUrl_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not QuickProgram p) return;
            int idx = QuickPrograms.IndexOf(p);
            if (idx < 0) return;
            QuickPrograms[idx] = new QuickProgram { Name = p.Name, Path = p.Path, Url = p.Url, ShowUrl = !p.ShowUrl };
            SaveQuickPrograms();
        }

        // ===================== Top-bar connection chips (Key / EStim / OSR) =====================
        private HashSet<string> _topBarChips = new();

        // Periodic reachability poll for pills that have an IP/URL or a paired server exe.
        // HTTP/HTTPS URLs → 1.5s GET (HEAD is sometimes refused); Glyph → process check on the
        // server exe name. Runs every 15s on a background timer; updates IsReachable on the
        // dispatcher so the pill's status dot binding updates without explicit refresh.
        private System.Threading.Timer? _statusPollTimer;
        private static readonly System.Net.Http.HttpClient _statusHttp = new() { Timeout = TimeSpan.FromMilliseconds(1500) };

        private void StartStatusPolling()
        {
            if (_statusPollTimer != null) return;
            _statusPollTimer = new System.Threading.Timer(_ => _ = PollStatusesAsync(), null, 2000, 15000);
        }

        private async Task PollStatusesAsync()
        {
            try
            {
                var snapshot = QuickPrograms?.ToList() ?? new List<QuickProgram>();
                foreach (var p in snapshot)
                {
                    if (p == null) continue;
                    bool? reachable = null;

                    // Glyph: check the server process by name (the exe is in Url).
                    if (string.Equals(p.Name, "Glyph", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(p.Url) && File.Exists(p.Url))
                            {
                                var name = Path.GetFileNameWithoutExtension(p.Url);
                                reachable = System.Diagnostics.Process.GetProcessesByName(name).Length > 0;
                            }
                        }
                        catch { reachable = null; }
                    }
                    else
                    {
                        // Standard URL pill (Stash / browser-launched http(s)://): GET it.
                        var url = !string.IsNullOrWhiteSpace(p.Url) ? p.Url : p.Path;
                        bool isUrl = !string.IsNullOrWhiteSpace(url)
                                    && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                     || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                        if (isUrl)
                        {
                            try
                            {
                                using var resp = await _statusHttp.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                                reachable = resp.IsSuccessStatusCode || (int)resp.StatusCode < 500;
                            }
                            catch { reachable = false; }
                        }
                    }

                    var target = p; var r = reachable;
                    Dispatcher.InvokeAsync(() => { try { target.IsReachable = r; } catch { } });
                }
            }
            catch { /* poll loop must never throw */ }
        }

        private void InitTopBarChips()
        {
            _topBarChips = new HashSet<string>(AppLocalSettings.Load().TopBarChips ?? new List<string>());
            // Keep chips in sync when the underlying connection config changes anywhere.
            if (handyConfig is System.ComponentModel.INotifyPropertyChanged hc) hc.PropertyChanged += (_, _) => RefreshTopBarChips();
            if (estimConfig is System.ComponentModel.INotifyPropertyChanged ec) ec.PropertyChanged += (_, _) => RefreshTopBarChips();
            if (osrConfig   is System.ComponentModel.INotifyPropertyChanged oc) oc.PropertyChanged += (_, _) => RefreshTopBarChips();
            RefreshTopBarChips();
        }

        private void SaveTopBarChips()
        {
            var s = AppLocalSettings.Load();
            s.TopBarChips = _topBarChips.ToList();
            s.Save();
        }

        private void RefreshTopBarChips()
        {
            if (chipKey == null) return;
            chipKey.Visibility   = _topBarChips.Contains("Key")   ? Visibility.Visible : Visibility.Collapsed;
            chipEStim.Visibility = _topBarChips.Contains("EStim") ? Visibility.Visible : Visibility.Collapsed;
            chipOSR.Visibility   = _topBarChips.Contains("OSR")   ? Visibility.Visible : Visibility.Collapsed;

            bool hasKey = !string.IsNullOrWhiteSpace(handyConfig?.Key);
            lblChipKeyLabel.Text   = hasKey ? "Device Key" : "Add Device Key";
            // Right-click → Show Key reveals the value; default OFF fully hides the value
            // TextBlock (no dots, no placeholder — the "Device Key" label alone is enough).
            var s = AppLocalSettings.Load();
            lblChipKeyVal.Text = hasKey && s.ShowDeviceKey ? handyConfig.Key : "";
            lblChipKeyVal.Visibility = hasKey && s.ShowDeviceKey ? Visibility.Visible : Visibility.Collapsed;
            if (miChipKeyShow != null) miChipKeyShow.IsChecked = s.ShowDeviceKey;
            lblChipEStimVal.Text = EStimDisplayName();
            lblChipOSRVal.Text   = string.IsNullOrWhiteSpace(osrConfig?.COMPort) ? "None" : osrConfig.COMPort;

            // Swagger pill URL line — toggled by right-click on the pill.
            if (lblApiDocsUrl != null)
                lblApiDocsUrl.Visibility = s.ShowApiDocsUrl ? Visibility.Visible : Visibility.Collapsed;
            if (miApiDocsShowUrl != null) miApiDocsShowUrl.IsChecked = s.ShowApiDocsUrl;

            ApplyTopBarOrder();   // keep newly-shown/hidden chips in their saved drag order
        }

        // Right-click → Show Key on the chipKey pill: persists to AppLocalSettings and re-renders.
        private void ChipKeyShow_Click(object sender, RoutedEventArgs e)
        {
            var s = AppLocalSettings.Load();
            s.ShowDeviceKey = miChipKeyShow?.IsChecked == true;
            s.Save();
            RefreshTopBarChips();
        }

        // Right-click → Show URL on the Swagger pill: persists to AppLocalSettings and re-renders.
        private void ApiDocsShowUrl_Click(object sender, RoutedEventArgs e)
        {
            var s = AppLocalSettings.Load();
            s.ShowApiDocsUrl = miApiDocsShowUrl?.IsChecked == true;
            s.Save();
            RefreshTopBarChips();
        }

        // Hides Add-menu items that would create duplicates: chips already pinned, and the
        // Intiface / Funscript Player canned programs once they're in QuickPrograms.
        private void AddTopMenu_Opened(object sender, RoutedEventArgs e)
        {
            // Connection chips — hide entries already in _topBarChips. If all three are pinned,
            // the separator above them stays but only the Add-program row remains useful.
            if (miAddChipKey   != null) miAddChipKey.Visibility   = _topBarChips.Contains("Key")   ? Visibility.Collapsed : Visibility.Visible;
            if (miAddChipEStim != null) miAddChipEStim.Visibility = _topBarChips.Contains("EStim") ? Visibility.Collapsed : Visibility.Visible;
            if (miAddChipOSR   != null) miAddChipOSR.Visibility   = _topBarChips.Contains("OSR")   ? Visibility.Collapsed : Visibility.Visible;

            // Canned programs — hide if the QuickPrograms list already has one with the same Name
            // (case-insensitive, since users sometimes edit the label).
            bool hasIntiface = QuickPrograms.Any(p => string.Equals(p?.Name, "Intiface", StringComparison.OrdinalIgnoreCase));
            bool hasFsPlayer = QuickPrograms.Any(p => string.Equals(p?.Name, "Funscript Player", StringComparison.OrdinalIgnoreCase));
            bool hasMfp      = QuickPrograms.Any(p => string.Equals(p?.Name, "MultiFunPlayer", StringComparison.OrdinalIgnoreCase));
            bool hasStash    = QuickPrograms.Any(p => string.Equals(p?.Name, "Stash", StringComparison.OrdinalIgnoreCase));
            bool hasOfs      = QuickPrograms.Any(p => string.Equals(p?.Name, "OpenFunscripter", StringComparison.OrdinalIgnoreCase));
            bool hasGlyph    = QuickPrograms.Any(p => string.Equals(p?.Name, "Glyph", StringComparison.OrdinalIgnoreCase));
            if (miAddIntiface         != null) miAddIntiface.Visibility         = hasIntiface ? Visibility.Collapsed : Visibility.Visible;
            if (miAddFunscriptPlayer  != null) miAddFunscriptPlayer.Visibility  = hasFsPlayer ? Visibility.Collapsed : Visibility.Visible;
            if (miAddMultiFunPlayer   != null) miAddMultiFunPlayer.Visibility   = hasMfp      ? Visibility.Collapsed : Visibility.Visible;
            if (miAddStash            != null) miAddStash.Visibility            = hasStash    ? Visibility.Collapsed : Visibility.Visible;
            if (miAddOfs              != null) miAddOfs.Visibility              = hasOfs      ? Visibility.Collapsed : Visibility.Visible;
            if (miAddGlyph            != null) miAddGlyph.Visibility            = hasGlyph    ? Visibility.Collapsed : Visibility.Visible;

            // Hide the program/chip separator when there's nothing in either group to separate.
            bool anyChip = (miAddChipKey?.Visibility == Visibility.Visible) || (miAddChipEStim?.Visibility == Visibility.Visible) || (miAddChipOSR?.Visibility == Visibility.Visible);
            bool anyProg = (miAddIntiface?.Visibility == Visibility.Visible)
                        || (miAddFunscriptPlayer?.Visibility == Visibility.Visible)
                        || (miAddMultiFunPlayer?.Visibility == Visibility.Visible)
                        || (miAddStash?.Visibility == Visibility.Visible)
                        || (miAddOfs?.Visibility == Visibility.Visible)
                        || (miAddGlyph?.Visibility == Visibility.Visible);
            if (miAddProgramsSeparator != null)
                miAddProgramsSeparator.Visibility = (anyChip && anyProg) ? Visibility.Visible : Visibility.Collapsed;
        }

        private string EStimDisplayName()
        {
            int id = estimConfig?.DeviceId ?? -1;
            if (id < 0) return "None";
            if (audioDevicesComboBox?.ItemsSource is IEnumerable<AudioDevice> list)
            {
                var m = list.FirstOrDefault(a => a.id == id);
                if (m != null) return m.name;
            }
            return id.ToString();
        }

        // The "+" button opens its menu (Add program / Add Key / EStim / OSR).
        private void AddTop_Click(object sender, RoutedEventArgs e)
        {
            if (btnAddTop.ContextMenu == null) return;
            btnAddTop.ContextMenu.PlacementTarget = btnAddTop;
            btnAddTop.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btnAddTop.ContextMenu.IsOpen = true;
        }

        private void AddChip_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string id) return;
            _topBarChips.Add(id);
            SaveTopBarChips();
            RefreshTopBarChips();
        }

        private void ChipRemove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string id) return;
            _topBarChips.Remove(id);
            SaveTopBarChips();
            RefreshTopBarChips();
        }

        private void ChipKey_Click(object sender, MouseButtonEventArgs e) => EditDeviceKey();
        private void ChipKeyEdit_Click(object sender, RoutedEventArgs e) => EditDeviceKey();   // right-click ▸ Edit
        private void EditDeviceKey()
        {
            if (handyConfig == null) return;
            // Pass the key glyph (Segoe MDL2 E192) so the prompt's header icon matches the
            // device-key chip in the top bar instead of the generic check-circle.
            var v = ThemedDialog.Prompt(this, "Devices Key", "Devices Key",
                "Handy / AutoBlow connection key.", handyConfig.Key, iconGlyph: "");
            if (v == null) return;
            handyConfig.Key = v;
            RefreshTopBarChips();
        }

        private void ChipEStim_Click(object sender, MouseButtonEventArgs e)
        {
            if (estimConfig == null) return;
            var items = (audioDevicesComboBox?.ItemsSource as IEnumerable<AudioDevice>)?.ToList() ?? new List<AudioDevice>();
            var cm = new ContextMenu { PlacementTarget = chipEStim, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var a in items)
            {
                var dev = a;
                var mi = new MenuItem { Header = a.name, IsChecked = a.id == estimConfig.DeviceId };
                mi.Click += (_, _) => { estimConfig.DeviceId = dev.id; RefreshTopBarChips(); };
                cm.Items.Add(mi);
            }
            cm.IsOpen = true;
        }

        private void ChipOSR_Click(object sender, MouseButtonEventArgs e)
        {
            if (osrConfig == null) return;
            var items = (comPortsComboBox?.ItemsSource as IEnumerable<ComPort>)?.ToList() ?? new List<ComPort>();
            var cm = new ContextMenu { PlacementTarget = chipOSR, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var p in items)
            {
                var port = p;
                var mi = new MenuItem { Header = p.name, IsChecked = p.value == osrConfig.COMPort };
                mi.Click += (_, _) => { osrConfig.COMPort = port.value; RefreshTopBarChips(); };
                cm.Items.Add(mi);
            }
            cm.IsOpen = true;
        }

        // ===================== Drag-to-reorder the game card list =====================
        // Richer feedback than the default WPF "system mouse cursor only" drag:
        //  • The source ListBoxItem dims (opacity 0.35) so its slot reads as "moving"
        //  • An adorner draws a thin pink insertion line between rows where the drop
        //    will land, snapping to the half nearest the cursor
        //  • Both effects are torn down on Drop / DragLeave so nothing lingers if the
        //    drag is cancelled (Escape) or dropped outside the list
        private System.Windows.Point _gameDragStart;
        private GameInfo? _gameDragItem;
        private ListBoxItem? _gameDragSourceItem;     // dimmed during drag
        private DragInsertionAdorner? _gameDropAdorner;

        private void lstGames_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _gameDragStart = e.GetPosition(null);
            _gameDragItem  = FindGameInfoFromVisual(e.OriginalSource as DependencyObject);
            _gameDragSourceItem = FindListBoxItem(e.OriginalSource as DependencyObject);
        }

        private void lstGames_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _gameDragItem == null) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _gameDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _gameDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var dragged = _gameDragItem;
            var sourceItem = _gameDragSourceItem;
            _gameDragItem = null;
            _gameDragSourceItem = null;

            // Dim the source row so the user sees what's leaving its slot.
            if (sourceItem != null) sourceItem.Opacity = 0.35;
            try
            {
                DragDrop.DoDragDrop(lstGames, dragged, DragDropEffects.Move);
            }
            finally
            {
                if (sourceItem != null) sourceItem.Opacity = 1.0;
                ClearDropAdorner();
            }
        }

        private void lstGames_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(GameInfo)) is not GameInfo) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Move;
            EnsureDropAdorner();
        }

        // Drives the insertion-line position. Snaps to "above" or "below" the row under
        // the cursor based on whether the cursor is in the upper or lower half of that row.
        private void lstGames_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(GameInfo)) is not GameInfo) { e.Effects = DragDropEffects.None; return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            EnsureDropAdorner();
            var target = FindListBoxItem(e.OriginalSource as DependencyObject);
            if (target == null || _gameDropAdorner == null) return;

            try
            {
                var pos = e.GetPosition(target);
                var top = target.TranslatePoint(new System.Windows.Point(0, 0), lstGames);
                double y = pos.Y < target.ActualHeight / 2 ? top.Y : top.Y + target.ActualHeight;
                _gameDropAdorner.SetLine(y, lstGames.ActualWidth);
            }
            catch { }
        }

        private void lstGames_DragLeave(object sender, DragEventArgs e)
        {
            // Only clear when the cursor actually leaves the list bounds (DragLeave fires for
            // child items too). A point at the very edge still counts as inside.
            var p = e.GetPosition(lstGames);
            if (p.X < 0 || p.Y < 0 || p.X > lstGames.ActualWidth || p.Y > lstGames.ActualHeight)
                ClearDropAdorner();
        }

        private void lstGames_Drop(object sender, DragEventArgs e)
        {
            ClearDropAdorner();
            if (e.Data.GetData(typeof(GameInfo)) is not GameInfo dragged) return;
            var target = FindGameInfoFromVisual(e.OriginalSource as DependencyObject);
            var list = gamesConfig.GamesInfo;
            int from = list.IndexOf(dragged);
            int to   = target != null ? list.IndexOf(target) : list.Count - 1;
            if (from < 0 || to < 0 || from == to) return;
            list.Move(from, to);
            try { edi.ConfigurationManager.Save(gamesConfig); } catch { }
        }

        private void EnsureDropAdorner()
        {
            if (_gameDropAdorner != null) return;
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(lstGames);
            if (layer == null) return;
            _gameDropAdorner = new DragInsertionAdorner(lstGames);
            layer.Add(_gameDropAdorner);
        }

        private void ClearDropAdorner()
        {
            if (_gameDropAdorner == null) return;
            try
            {
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(lstGames);
                layer?.Remove(_gameDropAdorner);
            }
            catch { }
            _gameDropAdorner = null;
        }

        // Walk up to the nearest ListBoxItem ancestor (not just GameInfo) — needed so we can
        // dim and measure the row's bounds independently of its DataContext.
        private static ListBoxItem? FindListBoxItem(DependencyObject? src)
        {
            while (src != null && src is not ListBoxItem)
            {
                src = (src is System.Windows.Media.Visual || src is System.Windows.Media.Media3D.Visual3D)
                    ? System.Windows.Media.VisualTreeHelper.GetParent(src)
                    : LogicalTreeHelper.GetParent(src);
            }
            return src as ListBoxItem;
        }

        // Walk up from the hit element to the ListBoxItem and return its GameInfo (logical hop for
        // non-visual originals like Run, visual hop otherwise).
        private static GameInfo? FindGameInfoFromVisual(DependencyObject? src)
        {
            while (src != null && src is not ListBoxItem)
            {
                src = (src is System.Windows.Media.Visual || src is System.Windows.Media.Media3D.Visual3D)
                    ? System.Windows.Media.VisualTreeHelper.GetParent(src)
                    : LogicalTreeHelper.GetParent(src);
            }
            return (src as ListBoxItem)?.DataContext as GameInfo;
        }

        // Best-effort: find Intiface Central and a funscript player from installed-program records.
        private static List<QuickProgram> AutoDetectPrograms()
        {
            var found = new List<QuickProgram>();
            var wanted = new (string match, string name)[]
            {
                ("intiface",        "Intiface"),
                ("openfunscripter", "OpenFunscripter"),
                ("multifunplayer",  "MultiFunPlayer"),
                ("funscript player","Funscript Player"),
                ("scriptplayer",    "ScriptPlayer"),
            };

            foreach (var (exe, name) in ScanUninstallKeys(wanted))
                if (!found.Any(f => string.Equals(f.Path, exe, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new QuickProgram { Name = name, Path = exe });

            // Intiface Central ships as a per-user Electron app — check its usual locations too.
            if (!found.Any(f => f.Name == "Intiface"))
            {
                foreach (var p in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Intiface Central", "Intiface Central.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intiface Central", "Intiface Central.exe"),
                })
                    if (File.Exists(p)) { found.Add(new QuickProgram { Name = "Intiface", Path = p, Url = "ws://localhost:12345" }); break; }
            }
            return found;
        }

        private static IEnumerable<(string exe, string name)> ScanUninstallKeys((string match, string name)[] wanted)
        {
            var results = new List<(string, string)>();
            var roots = new (RegistryKey hive, string path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (hive, path) in roots)
            {
                RegistryKey? key = null;
                try { key = hive.OpenSubKey(path); } catch { }
                if (key == null) continue;
                using (key)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = key.OpenSubKey(sub);
                            if (k?.GetValue("DisplayName") is not string dn || string.IsNullOrEmpty(dn)) continue;
                            foreach (var w in wanted)
                            {
                                if (dn.IndexOf(w.match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var exe = ResolveExeFromUninstall(k, w.match);
                                if (exe != null && results.All(r => !string.Equals(r.Item1, exe, StringComparison.OrdinalIgnoreCase)))
                                    results.Add((exe, w.name));
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            return results;
        }

        private static string? ResolveExeFromUninstall(RegistryKey k, string matchToken)
        {
            // DisplayIcon usually points straight at the real app exe.
            var icon = (k.GetValue("DisplayIcon") as string)?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(icon))
            {
                var p = icon.Split(',')[0].Trim().Trim('"');
                if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && !IsHelperExe(p))
                    return p;
            }

            // Otherwise scan the install folder, skipping installer/runtime helpers and
            // preferring an exe whose name matches the program (e.g. "Intiface Central.exe").
            if (k.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
            {
                try
                {
                    var exes = Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly)
                                        .Where(e => !IsHelperExe(e))
                                        .ToList();
                    if (exes.Count == 0) return null;

                    var token = matchToken.Replace(" ", "");
                    var match = exes.FirstOrDefault(e =>
                        Path.GetFileNameWithoutExtension(e).Replace(" ", "")
                            .IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (match != null) return match;

                    return exes.OrderByDescending(e => new FileInfo(e).Length).First();
                }
                catch { }
            }
            return null;
        }

        public async void GamesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressGameReload) { UpdateLaunchButton(); return; }
            await Dispatcher.Invoke(async () =>
            {
                if (GamesComboBox.SelectedItem is GameInfo selectedGame)
                {
                    // gamesConfig.SelectedGameinfo = selectedGame;
                    await edi.SelectGame(selectedGame);
                    viewModel.galleries = ReloadGalleries();
                }
                UpdateLaunchButton();
                RefreshInfoPanel();
            });
        }

        private void UpdateLaunchButton()
        {
            var g = GamesComboBox?.SelectedItem as GameInfo;
            var exe = g?.ExePath;
            bool exeOk = !string.IsNullOrWhiteSpace(exe) && System.IO.File.Exists(exe);
            bool hasOpts = g?.LaunchOptions != null && g.LaunchOptions.Any(o => !string.IsNullOrWhiteSpace(o?.Path));
            var ok = exeOk || hasOpts;
            if (btnLaunchGame == null) return;
            btnLaunchGame.IsEnabled = ok;
            btnLaunchGame.ToolTip = ok
                ? (hasOpts ? $"Launch {g.Name} — click to choose a launch option" : $"Launch {g.Name}")
                : "Set the game's .exe path via the gear button to enable";
            if (btnLaunchGameMoved != null) btnLaunchGameMoved.IsEnabled = ok;
        }

        private void btnFunscriptEditor_Click(object sender, RoutedEventArgs e)
        {
            var galleryPath = edi.GalleryPath;
            if (string.IsNullOrWhiteSpace(galleryPath) || !System.IO.Directory.Exists(galleryPath))
            {
                MessageBox.Show(this,
                    "No gallery loaded. Select a game first (Select Game...) so its gallery folder is active.",
                    "Funscript Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new FunscriptEditor(galleryPath) { Owner = this };
            dlg.ShowDialog();
            // Refresh the galleries dropdown — a new preset folder may now exist
            viewModel.galleries = ReloadGalleries();
        }

        // Live Edit → "Fuck Machine converter": opens the offline rotary-machine script converter.
        // It saves a new power-level variant next to the source; the user can then pick that variant
        // per-device in the Devices panel ("Selected Variant").
        // The FM converter is a dockable panel (ContentId "fmconverter") inside the EDI UI. The Live
        // Edit button and the Settings → Panels "FM Converter" switch both just show/focus that pane.
        private void FmConverter_Click(object sender, RoutedEventArgs e)
        {
            var a = Anch("fmconverter");
            if (a == null) return;
            a.Show();
            a.IsActive = true;   // bring its tab to the front
        }

        // After a save, reload galleries so the new variant shows in the Devices "Selected Variant" list.
        private async void FmConverter_OnSaved(object sender, EventArgs e)
        {
            try
            {
                var game = gamesConfig?.SelectedGameinfo;
                await edi.Init(game?.Path, game?.GalleryPath ?? edi?.GalleryPath);
                viewModel.galleries = ReloadGalleries();
            }
            catch { /* user can re-select the game to pick up the variant */ }
        }

        private void btnLaunchGame_Click(object sender, RoutedEventArgs e)
        {
            var g = (GamesComboBox?.SelectedItem as GameInfo) ?? gamesConfig?.SelectedGameinfo;
            if (g == null) return;

            bool hasExe = !string.IsNullOrWhiteSpace(g.ExePath);
            var opts = (g.LaunchOptions ?? new List<LaunchOption>())
                       .Where(o => !string.IsNullOrWhiteSpace(o?.Path)).ToList();

            // No extra launch options → behave exactly as before (launch the configured exe).
            if (opts.Count == 0)
            {
                if (!hasExe)
                {
                    MessageBox.Show(this,
                        "No executable path is configured for this game. Open the gear (⚙) and set 'Game Executable'.",
                        "Launch failed", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                LaunchPath(g.ExePath, null);
                return;
            }

            // Extra options exist → drop a menu down from the button to pick which target to launch.
            var menu = new System.Windows.Controls.ContextMenu();
            if (hasExe) menu.Items.Add(MakeLaunchItem("Launch Game", g.ExePath, null));
            foreach (var o in opts)
                menu.Items.Add(MakeLaunchItem(string.IsNullOrWhiteSpace(o.Name) ? System.IO.Path.GetFileName(o.Path) : o.Name, o.Path, o.Args));

            menu.PlacementTarget = sender as UIElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private System.Windows.Controls.MenuItem MakeLaunchItem(string header, string path, string args)
        {
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = header,
                Icon = new System.Windows.Controls.TextBlock
                {
                    Text = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                },
                ToolTip = string.IsNullOrWhiteSpace(args) ? path : $"{path}  {args}",
            };
            mi.Click += (_, _) => LaunchPath(path, args);
            return mi;
        }

        // Start a launch target (exe / file / URL) with optional arguments.
        private void LaunchPath(string path, string args)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) psi.WorkingDirectory = dir;
                }
                catch { }
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to launch:\n{ex.Message}", "Launch error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ReconnectButton_ClickAsync(object sender, RoutedEventArgs e)
        {
            await Dispatcher.Invoke(async () =>
            {
                loadOSRPorts();
                await edi.InitDevices();
            });
            RefreshTopBarChips();   // ports list refreshed
        }

        // Reflow the Reconnect button: keep it to the right of the toggles when there's room;
        // if it would collide with them, drop it to a full-width row underneath (matching the divider).
        private bool? _reconnectWrapped;
        private void DevActionRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ReconnectButton == null || devToggles == null || devActionRow == null) return;
            double need = devToggles.DesiredSize.Width + 148 + 14;   // toggles + reconnect + gap
            bool wrap = devActionRow.ActualWidth > 0 && devActionRow.ActualWidth < need;
            if (_reconnectWrapped == wrap) return;   // no change → avoid a layout loop
            _reconnectWrapped = wrap;

            if (wrap)
            {
                Grid.SetRow(ReconnectButton, 1);
                Grid.SetColumn(ReconnectButton, 0);
                Grid.SetColumnSpan(ReconnectButton, 2);
                ReconnectButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                ReconnectButton.Width = double.NaN;            // full width = the divider's length
                ReconnectButton.Margin = new Thickness(0, 8, 0, 0);
            }
            else
            {
                Grid.SetRow(ReconnectButton, 0);
                Grid.SetColumn(ReconnectButton, 1);
                Grid.SetColumnSpan(ReconnectButton, 1);
                ReconnectButton.HorizontalAlignment = HorizontalAlignment.Right;
                ReconnectButton.Width = 148;
                ReconnectButton.Margin = new Thickness(0);
            }
        }

        private void Label_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start http://localhost:5000/swagger/index.html") { CreateNoWindow = true });
           
        }

        private async void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.Invoke(async () =>
            {
                var selected = cmbGallerie.Text;
                if (selected == "(Random)")
                    selected = edi.Definitions.OrderBy(x => Guid.NewGuid()).FirstOrDefault()?.Name ?? "";

                await edi.Player.Play(selected, 0);
            });
        }

        private async void btnStop_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.Invoke(async () =>
            {
                await edi.Player.Stop();
            });
        }

        private async void btnPause_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.Invoke(async () =>
            {
                await edi.Player.Pause();
            });
        }

        private async void btnResume_Click(object sender, RoutedEventArgs e)
        {
            await Dispatcher.Invoke(async () =>
            {
                await edi.Player.Resume(false);
            });
        }

        private MainWindowViewModel viewModel;
        private GamesConfig gamesConfig;
        // ...

        // (Action Preview eye button was removed from the Playback transport row. Preview Player
        // is its own dockable pane — toggle from Settings → Panels → Preview Player.)

        // Lazy-create the PreviewDevice that drives the docked Preview Player's vertical
        // ProgressBar. Loading it into the DeviceCollector is what makes the playback engine
        // start streaming positions to it.
        private PreviewDevice _previewPlayerDevice;
        private void EnsurePreviewPlayerDevice()
        {
            if (_previewPlayerDevice != null) return;
            _previewPlayerDevice = new PreviewDevice(
                App.ServiceProvider.GetRequiredService<FunscriptRepository>(),
                App.ServiceProvider.GetRequiredService<ILogger<PreviewDevice>>());
            try { edi.DeviceCollector.LoadDevice(_previewPlayerDevice); } catch { }
            if (PreviewPlayerCard != null) PreviewPlayerCard.DataContext = _previewPlayerDevice;
        }

        private void TeardownPreviewPlayerDevice()
        {
            if (_previewPlayerDevice == null) return;
            try { _previewPlayerDevice.StopGallery(); } catch { }
            try { edi.DeviceCollector.UnloadDevice(_previewPlayerDevice); } catch { }
            _previewPlayerDevice = null;
            if (PreviewPlayerCard != null) PreviewPlayerCard.DataContext = null;
        }

        // ===================== Embedded preview =====================

        private PreviewDevice? _previewDevice;
        private bool _previewShown;
        private bool _suppressPreviewToggle;

        // Dev tab: master switch for the hidden Playback "Preview Device". Off = never create it
        // (keeps it out of the API device list / away from in-game integrations).
        private void PreviewDevice_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressPreviewToggle) return;
            bool on = chkPreviewDevice?.IsChecked == true;
            var s = AppLocalSettings.Load();
            s.PreviewDeviceEnabled = on;
            s.Save();
            if (on) { if (_previewDevice == null) ShowEmbeddedPreview(); }
            else    { if (_previewDevice != null) HideEmbeddedPreview(); }
        }

        private void ToggleEmbeddedPreview()
        {
            if (_previewDevice == null) ShowEmbeddedPreview();
            else HideEmbeddedPreview();
        }

        private void ShowEmbeddedPreview()
        {
            _previewDevice = new PreviewDevice(
                App.ServiceProvider.GetRequiredService<FunscriptRepository>(),
                App.ServiceProvider.GetRequiredService<ILogger<PreviewDevice>>());
            edi.DeviceCollector.LoadDevice(_previewDevice);

            // The visualizer lives in the PLAYBACK card's center area; just point it at
            // the live device (no extra column, no window resize).
            funscriptPreview.Device = _previewDevice;
            _previewShown = true;
        }

        private void HideEmbeddedPreview()
        {
            if (_previewDevice != null)
            {
                try { _previewDevice.StopGallery(); } catch { }
                try { edi.DeviceCollector.UnloadDevice(_previewDevice); } catch { }
                _previewDevice = null;
            }
            funscriptPreview.Device = null;   // falls back to the "Waiting for playback…" placeholder
            _previewShown = false;
        }

     
        public override async void EndInit()
        {
            await Dispatcher.Invoke(async () =>
            {
                await edi.Player.Pause();
            });
            await Task.Delay(1000); 
            base.EndInit();
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyIntensities();

        private void Vibration_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyIntensities();

        // Live edit bars:
        //  • INTENSITY scales every device's output range (Max) — the master output level.
        //  • VIBRATION is an additive vibration OVERLAY (default 0): it adds extra buzz ON TOP of the
        //    playing script for vibrating/oscillating devices, and drives the buzz drawn on the live
        //    Playback preview. It does not mute or scale the script.
        //  • MINIMUM raises every device's output floor (Min). Default 0 keeps the original Min.
        //  • PAUSE OUTPUT flag zeros every device's Max while still letting the script play.
        private bool _liveEditPaused;
        private void ApplyIntensities()
        {
            try
            {
                int stroke = sliderIntensity != null ? (int)sliderIntensity.Value : 100;
                int vibe   = sliderVibration != null ? (int)sliderVibration.Value : 0;
                int floor  = sliderMinimum   != null ? (int)sliderMinimum.Value   : 0;

                // Show the vibration overlay live on the Playback graph (line ripple + dot jitter).
                try { funscriptPreview.VibrationAmount = vibe; } catch { }

                if (edi == null) return;
                var cfg = edi.ConfigurationManager.Get<Edi.Core.Device.DevicesConfig>();
                double overlay = Math.Clamp(vibe / 100.0, 0, 1);
                foreach (var d in edi.Devices)
                {
                    // Vibration overlay → extra buzz added on top of the script (vibrating actuators only).
                    // When paused, zero the overlay so the live preview's ripple goes flat too.
                    if (d is Edi.Core.Device.Buttplug.ButtplugDevice b && b.IsVibration)
                        b.VibrationOverlay01 = _liveEditPaused ? 0 : overlay;

                    if (d is not Edi.Core.Device.Interfaces.IRange r) continue;
                    int dmin = 0, dmax = 100;   // sensible default if the device isn't in the config
                    if (cfg != null && cfg.Devices.TryGetValue(d.Name, out var def) && def != null)
                    {
                        dmin = def.Min; dmax = def.Max;
                    }

                    if (_liveEditPaused)
                    {
                        // Freeze output without stopping playback: pin both ends at the user's MINIMUM
                        // baseline (or 0 if MINIMUM is also 0) so the device holds still.
                        int hold = dmin + (dmax - dmin) * floor / 100;
                        r.Min = hold;
                        r.Max = hold;
                        continue;
                    }

                    // MINIMUM raises the output floor; INTENSITY scales the upper bound. Both are
                    // expressed as a percentage of the device's configured Min↔Max range, then
                    // clamped so the floor never crosses above the ceiling.
                    int newMin = dmin + (dmax - dmin) * floor / 100;
                    int newMax = dmin + (dmax - dmin) * stroke / 100;
                    if (newMin > newMax) newMin = newMax;
                    r.Min = newMin;
                    r.Max = newMax;
                }
            }
            catch { /* live tweak — never let a UI slider throw */ }
        }

        // Pause toggle: holds every device's output at the current MINIMUM baseline (or 0 if
        // MINIMUM is 0). Playback keeps running so the script timeline stays in sync.
        private void PauseOutput_Click(object sender, RoutedEventArgs e)
        {
            _liveEditPaused = btnPauseOutput?.IsChecked == true;
            ApplyIntensities();
        }

        // Reset every Live Edit slider to its default (Intensity 100, Vibration 0, Minimum 0).
        // Also clears the Pause flag so the user gets a clean baseline.
        private void ResetLiveEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sliderIntensity != null) sliderIntensity.Value = 100;
            if (sliderVibration != null) sliderVibration.Value = 0;
            if (sliderMinimum   != null) sliderMinimum.Value   = 0;
            if (btnPauseOutput  != null) btnPauseOutput.IsChecked = false;
            _liveEditPaused = false;
            ApplyIntensities();
        }

        private void btnOpenOutput_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("explorer.exe",Edi.Core.Edi.OutputDir) { UseShellExecute = true });
        }

        // Open SteamGridDB's API-key page so the user can create/copy a free key.
        private void SteamGridGetKey_Click(object sender, RoutedEventArgs e)
            => ExecuteCommandOrOpenPath("https://www.steamgriddb.com/profile/preferences/api");

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowBounds();
            SaveDockLayout();
            if (_previewDevice != null) HideEmbeddedPreview();
            await Dispatcher.Invoke(async () =>
            {
                await edi.Player.Pause();
            });
            await Task.Delay(1000);
        }

        // Restore the main window's size/position/maximized state from the last session.
        private void RestoreWindowBounds()
        {
            try
            {
                var s = AppLocalSettings.Load();
                if (s.WindowWidth is double w and > 300 && s.WindowHeight is double h and > 300)
                {
                    Width = w;
                    Height = h;
                }
                if (s.WindowLeft is double l && s.WindowTop is double t)
                {
                    // Only restore the position if it still lands on a visible monitor.
                    double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
                    double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
                    if (l + 80 < vx + vw && l + Width - 80 > vx && t + 40 < vy + vh && t >= vy - 4)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = l;
                        Top = t;
                    }
                }
                if (s.WindowMaximized) WindowState = WindowState.Maximized;
            }
            catch { }
        }

        // Persist the main window's placement so the next launch reopens the same way.
        private void SaveWindowBounds()
        {
            try
            {
                var s = AppLocalSettings.Load();
                s.WindowMaximized = WindowState == WindowState.Maximized;
                // RestoreBounds holds the normal rect even while maximized/minimized.
                var r = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                if (r.Width > 300 && r.Height > 300)
                {
                    s.WindowWidth = r.Width;
                    s.WindowHeight = r.Height;
                    s.WindowLeft = r.Left;
                    s.WindowTop = r.Top;
                }
                // Inner panel split + card metrics, so they survive without clicking Settings → Save.
                if (colConn != null && colOpts != null && colConn.ActualWidth > 50 && colOpts.ActualWidth > 50)
                {
                    s.ColConnWidth = colConn.ActualWidth;
                    s.ColOptsWidth = colOpts.ActualWidth;
                }
                if (colGameLeft != null && colGameLeft.ActualWidth > 100)
                    s.ColGameLeftWidth = colGameLeft.ActualWidth;
                if (sliderCardHeight != null) s.CardHeight = sliderCardHeight.Value;
                if (sliderCoverBlur != null) s.CoverBlur = sliderCoverBlur.Value;
                s.Save();
            }
            catch { }
        }
    }
    public class LogEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public string Text { get; set; } = "";
        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public class BoolToReadyIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (value is bool && (bool)value) ? "✅" : "🚫";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // For a game whose path lives under the configured "Game Folder", returns the custom
    // icon of its top-level folder (the one shown in Explorer) for the games dropdown.
    public class GameIconConverter : IValueConverter
    {
        private static readonly Dictionary<string, System.Windows.Media.ImageSource?> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache() => _cache.Clear();

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not GameInfo g || string.IsNullOrWhiteSpace(g.Path)) return null;

            var root = AppLocalSettings.Load().GameFolder;
            if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root)) return null;

            string folder;
            try
            {
                var rootFull = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
                var gameFull = System.IO.Path.GetFullPath(g.Path);
                if (!gameFull.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
                // The folder directly under the root (e.g. <root>\Night of Revenge).
                var rest = gameFull.Substring(rootFull.Length + 1);
                var firstSeg = rest.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
                folder = System.IO.Path.Combine(rootFull, firstSeg);
            }
            catch { return null; }

            if (_cache.TryGetValue(folder, out var cached)) return cached;

            System.Windows.Media.ImageSource? icon = null;

            // Icon priority chain (matches the Settings → Games "Find missing icons" info popup):
            //   1) Manually-set or auto-fetched IconPath (right-click → Icon → …, or library back-fill).
            //   2) desktop.ini's IconResource / IconFile (custom folder icon).
            //   3) Any *.ico file sitting directly in the game folder.
            //   4) Shell jumbo (for folders flagged ReadOnly / customized).
            //   5) SteamGridDB icon download (handled out-of-band by the library back-fill; surfaces
            //      here once the file lands in IconPath, so this resolver doesn't make network calls).
            //   6) Game .exe icon (further below).
            if (!string.IsNullOrWhiteSpace(g.IconPath) && System.IO.File.Exists(g.IconPath))
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(g.IconPath);
                    bi.EndInit();
                    bi.Freeze();
                    icon = bi;
                }
                catch { /* fall through to folder-icon chain */ }
            }
            if (icon == null)
            {
                var src = ResolveDesktopIniIcon(folder);
                if (src != null && System.IO.File.Exists(src.Value.file))
                    icon = FolderIcon.GetHighRes(src.Value.file, 256, src.Value.index);
            }
            if (icon == null)
            {
                // Any loose .ico file in the game folder — pick the first one we see.
                try
                {
                    var ico = System.IO.Directory.EnumerateFiles(folder, "*.ico", System.IO.SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (ico != null)
                        icon = FolderIcon.GetHighRes(ico, 256) ?? FolderIcon.GetJumbo(ico) ?? FolderIcon.Get(ico);
                }
                catch { /* ignore folder access issues */ }
            }
            if (icon == null && HasCustomFolderIcon(folder))
                icon = FolderIcon.GetJumbo(folder) ?? FolderIcon.Get(folder);

            // Fall back to the game's exe icon (high-res).
            if (icon == null && !string.IsNullOrWhiteSpace(g.ExePath) && System.IO.File.Exists(g.ExePath))
                icon = FolderIcon.GetHighRes(g.ExePath, 256) ?? FolderIcon.GetJumbo(g.ExePath) ?? FolderIcon.Get(g.ExePath);

            // Last resort: the game's downloaded cover (auto-fetched via SteamGridDB / DLsite / F95).
            // Lets games with no folder-icon or exe-icon still show something representative on the card.
            if (icon == null && !string.IsNullOrWhiteSpace(g.ImagePath) && System.IO.File.Exists(g.ImagePath))
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(g.ImagePath);
                    bi.EndInit();
                    bi.Freeze();
                    icon = bi;
                }
                catch { /* leave icon null */ }
            }

            _cache[folder] = icon;
            return icon;
        }

        // Parse desktop.ini for the custom icon's source file + index (IconResource, or IconFile/IconIndex).
        // Internal so MainWindow.CacheFolderIcon (right-click → Icon → Use folder icon) can reuse it.
        internal static (string file, int index)? ResolveDesktopIniIcon(string folder)
        {
            try
            {
                var ini = System.IO.Path.Combine(folder, "desktop.ini");
                if (!System.IO.File.Exists(ini)) return null;

                string? file = null;
                int index = 0;
                foreach (var raw in System.IO.File.ReadAllLines(ini))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    if (key.Equals("IconResource", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = val.LastIndexOf(',');
                        if (comma > 0) { file = val.Substring(0, comma).Trim(); int.TryParse(val.Substring(comma + 1), out index); }
                        else file = val;
                    }
                    else if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) file = val;
                    else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out index);
                }

                if (string.IsNullOrWhiteSpace(file)) return null;
                file = Environment.ExpandEnvironmentVariables(file);
                if (!System.IO.Path.IsPathRooted(file))
                    file = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, file));
                return (file, index);
            }
            catch { return null; }
        }

        // True if the folder defines a custom icon via desktop.ini (Properties → Customize → Change Icon).
        private static bool HasCustomFolderIcon(string folder)
        {
            try
            {
                var ini = System.IO.Path.Combine(folder, "desktop.ini");
                if (!System.IO.File.Exists(ini)) return false;
                var text = System.IO.File.ReadAllText(ini);
                return text.IndexOf("IconResource", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("IconFile", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Maps the intensity slider's value (0–100) to a star GridLength so the pink fill column grows
    // proportionally and reaches the full width at 100%. Parameter "fill" = value, "rest" = remainder.
    public class IntensityFillConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = value is double d ? d : 0;
            v = Math.Max(0, Math.Min(100, v));
            bool rest = (parameter as string) == "rest";
            return new GridLength(rest ? 100 - v : v, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Loads a game's manually-set cover image (GameInfo.ImagePath) as a downscaled, frozen
    // bitmap for the card background. Returns null when no image is set or the file is missing.
    public class GameCoverConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = (value as GameInfo)?.ImagePath;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 1080;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Produces a rounded-rectangle clip geometry sized to an element (bind to its ActualWidth/Height).
    // Used to clip game cards — including a blurred cover's effect bleed — to clean rounded corners.
    public class RoundedClipConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double w || values[1] is not double h || w <= 0 || h <= 0)
                return null;
            double r = 6;
            if (values.Length >= 3 && values[2] is double vr) r = vr;     // radius supplied via a binding
            else if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var pr))
                r = pr;
            var geo = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, w, h), r, r);
            geo.Freeze();
            return geo;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Visible only when the game has a usable cover image on disk (drives the card background).
    public class GameHasCoverConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = (value as GameInfo)?.ImagePath;
            return (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // Returns the Windows shell icon for a quick-launch program's executable.
    public class ProgramIconConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var prog = value as QuickProgram;
            var path = prog?.Path;

            // Intiface gets its official bundled icon instead of the (low-res) exe icon.
            if ((prog?.Name?.IndexOf("intiface", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (path?.IndexOf("intiface", StringComparison.OrdinalIgnoreCase) >= 0))
                return IntifaceIcon;

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            // Prefer the program's high-resolution (up to 256px) icon; fall back to the 32px shell icon.
            return FolderIcon.GetHighRes(path) ?? FolderIcon.Get(path);
        }

        private static ImageSource? _intifaceIcon;
        private static ImageSource? IntifaceIcon
        {
            get
            {
                if (_intifaceIcon == null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri("pack://application:,,,/intiface_icon.png", UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        _intifaceIcon = bmp;
                    }
                    catch { }
                }
                return _intifaceIcon;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    internal static class FolderIcon
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // Extracts an icon from an exe/dll at a requested pixel size (returns the embedded
        // high-res icon when present, else the best available scaled up).
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
                                                      IntPtr[] phicon, int[] piconid, int nIcons, int flags);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr i, IntPtr m, ref int x);
            [PreserveSig] int ReplaceIcon(int i, IntPtr icon, ref int x);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr image, IntPtr mask);
            [PreserveSig] int AddMasked(IntPtr image, int mask, ref int x);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SYSICONINDEX = 0x000004000;
        private const int SHIL_JUMBO = 0x4;       // 256x256
        private const int SHIL_EXTRALARGE = 0x2;  // 48x48
        private const int ILD_TRANSPARENT = 0x1;
        private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

        // Extract a specific icon from an exe/dll/ico at a requested pixel size.
        public static System.Windows.Media.ImageSource? GetHighRes(string file, int size = 256, int index = 0)
        {
            try
            {
                var h = new IntPtr[1];
                var id = new int[1];
                int n = PrivateExtractIcons(file, index, size, size, h, id, 1, 0);
                if (n <= 0 || h[0] == IntPtr.Zero) return null;
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        h[0], System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(h[0]); }
            }
            catch { return null; }
        }

        // High-res shell icon for any path (folder or file) via the system jumbo image list.
        public static System.Windows.Media.ImageSource? GetJumbo(string path, int shil = SHIL_JUMBO)
        {
            try
            {
                var shfi = new SHFILEINFO();
                var r = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_SYSICONINDEX);
                if (r == IntPtr.Zero) return null;

                var guid = IID_IImageList;
                if (SHGetImageList(shil, ref guid, out var iml) != 0 || iml == null) return null;
                if (iml.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out var hicon) != 0 || hicon == IntPtr.Zero) return null;
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hicon, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(hicon); }
            }
            catch { return null; }
        }

        public static System.Windows.Media.ImageSource? Get(string folder)
        {
            try
            {
                var shfi = new SHFILEINFO();
                SHGetFileInfo(folder, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (shfi.hIcon == IntPtr.Zero) return null;
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        shfi.hIcon, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(shfi.hIcon); }
            }
            catch { return null; }
        }
    }

    // Thin pink line + soft glow drawn into an adorner layer to show where a dragged item
    // will land. Used for both the vertical games list (Horizontal=false → horizontal line
    // between rows) and the horizontal top-bar pills (Horizontal=true → vertical line between
    // pills). Either way the indicator is hit-test invisible and rebuilt on each DragOver.
    internal sealed class DragInsertionAdorner : System.Windows.Documents.Adorner
    {
        private double _y;
        private double _w;
        private bool _horizontal;   // true → vertical line spanning the bar height
        private double _spanLen;    // perpendicular length (height for vertical, width for horizontal)
        private static readonly System.Windows.Media.Pen _pen = MakePen();
        private static System.Windows.Media.Pen MakePen()
        {
            var brush = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint   = new System.Windows.Point(1, 0),
            };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0x2D, 0x8C), 0.0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x2D, 0x8C), 0.5));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x33, 0xFF, 0x2D, 0x8C), 1.0));
            brush.Freeze();
            var pen = new System.Windows.Media.Pen(brush, 2.5);
            pen.Freeze();
            return pen;
        }

        public DragInsertionAdorner(System.Windows.UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x8C),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.7,
            };
        }

        // Horizontal-line API (games list): y is the Y coord in adorned coords, width is the span.
        public void SetLine(double y, double width)
        {
            if (!_horizontal == false && _y == y && _w == width) return;
            _horizontal = false; _y = y; _w = width;
            InvalidateVisual();
        }

        // Vertical-line API (top bar): x is the X coord in adorned coords, height is the span.
        public void SetVertical(double x, double height)
        {
            if (_horizontal && _y == x && _spanLen == height) return;
            _horizontal = true; _y = x; _spanLen = height;
            InvalidateVisual();
        }

        protected override void OnRender(System.Windows.Media.DrawingContext dc)
        {
            var dot = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x2D, 0x8C));
            dot.Freeze();
            if (_horizontal)
            {
                if (_spanLen <= 0) return;
                dc.DrawLine(_pen, new System.Windows.Point(_y, 2), new System.Windows.Point(_y, _spanLen - 2));
                dc.DrawEllipse(dot, null, new System.Windows.Point(_y, 2), 3, 3);
                dc.DrawEllipse(dot, null, new System.Windows.Point(_y, _spanLen - 2), 3, 3);
            }
            else
            {
                if (_w <= 0) return;
                dc.DrawLine(_pen, new System.Windows.Point(2, _y), new System.Windows.Point(_w - 2, _y));
                dc.DrawEllipse(dot, null, new System.Windows.Point(2, _y), 3, 3);
                dc.DrawEllipse(dot, null, new System.Windows.Point(_w - 2, _y), 3, 3);
            }
        }
    }
}

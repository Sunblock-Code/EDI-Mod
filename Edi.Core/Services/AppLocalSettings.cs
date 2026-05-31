using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Edi.Core.Services
{
    // A program shown on the top quick-launch bar (e.g. Intiface, a funscript player).
    public class QuickProgram : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Url { get; set; } = "";   // optional, shown next to the name like the API Docs URL
        public bool ShowUrl { get; set; } = true;   // whether the URL line is shown on the pill

        // Runtime-only status flag (NOT persisted). null = not yet polled / no URL to poll;
        // true = the URL/port responded; false = unreachable. Drives the small dot overlay in
        // the pill so the user can see at a glance which services are actually up.
        private bool? _isReachable;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool? IsReachable
        {
            get => _isReachable;
            set { if (_isReachable != value) { _isReachable = value; OnChanged(nameof(IsReachable)); OnChanged(nameof(HasStatusDot)); } }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasStatusDot => IsReachable.HasValue;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        // True for entries that have no URL set — the URL options ("Edit URL", "Show URL") don't
        // apply, so the context menu falls back to "Edit Folder Path…" (editing Path) instead.
        // The Funscript Player is the canonical case: no link, just a folder/exe path the launcher
        // opens. Name kept as IsFolderPath because that's how it reads on the menu trigger.
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsFolderPath => string.IsNullOrWhiteSpace(Url);
    }

    // A "support this mod" donation destination shown in the Support popup
    // (e.g. Ko-fi, Patreon, PayPal). Plain class so System.Text.Json round-trips it cleanly.
    public class DonationLink
    {
        public string Label { get; set; } = "";
        public string Url { get; set; } = "";
    }

    // Machine-local settings stored next to the Edi.exe (NOT under OutputDir),
    // because the save-data location override has to be readable before OutputDir
    // resolves — UserConfig.json itself lives inside OutputDir.
    public class AppLocalSettings
    {
        public string? SaveDataLocation { get; set; }
        public bool ShowInfoTab { get; set; }
        public bool PreviewInMain { get; set; }
        public bool PreviewDeviceEnabled { get; set; } = true;   // Dev: load the hidden Playback "Preview Device"
        public string? GameFolder { get; set; }
        public string? FunscriptPlayerPath { get; set; }   // player used to launch Script Player games
        public string SteamGridDbApiKey { get; set; } = "";   // user's steamgriddb.com API key (cover/banner source)
        public bool PortableData { get; set; }
        public bool MoveGameLayout { get; set; }
        public bool QuickProgramsInitialized { get; set; }
        public List<QuickProgram> QuickPrograms { get; set; } = new();
        public bool DonationsInitialized { get; set; }          // seed common platform rows once
        public bool KofiLinkSeeded { get; set; }                // one-time fill of the baked-in Ko-fi link
        public List<DonationLink> DonationLinks { get; set; } = new();
        // Monero receive address shown (with a Copy button) in the Support popup. Public-by-design;
        // copy-only, never used to send funds. Blank hides the Monero card.
        public string MoneroAddress { get; set; } = "8AKehPGkA4UTw92xa4xXp8Qa99ZfrUUHsE21Hi9bVz4d8j5aEVgUEPSgR69j7XMXTYYNhArcsjCivAfVZyJmRaNX9wBzLLk";
        public double CardHeight { get; set; } = 44;
        public double CoverBlur { get; set; } = 18;
        public string BadgeShape { get; set; } = "Pill";   // EDI/Player badge: Pill | Rounded | Square
        public string CardStyle { get; set; } = "Cover";   // game-card art: Cover (blurred art) | Solid (no art)
        public double ScrimDarkness { get; set; } = 70;     // darkness of the scrim over cover art (0-85)
        public string TitleAlign { get; set; } = "Left";    // card title alignment: Left | Center
        public string TitleVAlign { get; set; } = "Center"; // card title vertical placement: Top | Center | Bottom
        public double TitleSize { get; set; } = 15;         // card title font size
        public string IconShape { get; set; } = "Square";   // game icon shape: Square | Rounded | Circle
        public double CardCorner { get; set; } = 8;         // card corner roundness (0=square)
        public double OutlineThickness { get; set; } = 1;   // card outline thickness (0=none)
        public string SelStyle { get; set; } = "Glow";      // how the selected game card looks: Glow | Border | Bar | Solid
        public bool ShowSearchBar { get; set; }             // show the search bar above the game card list
        public bool LauncherStackUrl { get; set; } = true;   // launcher pills: URL under name (vs beside)
        public List<string> TopBarChips { get; set; } = new();   // pinned connection chips: "Key","EStim","OSR"
        public List<string> TopBarOrder { get; set; } = new();   // drag-reorder order of top-bar pills (keys: "apidocs","prog:<name>","chip:Key"…)

        // Right-click toggles for the connection / Swagger pills.
        // ShowDeviceKey OFF (default) masks the Handy device key with •••• since it's a secret;
        // user opts in via the chip's right-click → Show Key.
        // ShowApiDocsUrl ON (default) shows "localhost:5000" under the Swagger label;
        // user can hide it via the pill's right-click → Show URL toggle.
        public bool ShowDeviceKey { get; set; }
        public bool ShowApiDocsUrl { get; set; } = true;

        // Hard kill-switch for any device-ready auto-launch path (legacy EdiConfig.json
        // ExecuteOnReady, fallback to Selected Game's exe, etc.). Defaults to TRUE so games
        // never start themselves — the user has to press LAUNCH GAME or use a shortcut.
        // Toggle from Settings → Display → "Block auto-launch when device connects".
        public bool BlockAutoLaunch { get; set; } = true;

        // ── Rotary fuck-machine script converter (Live Edit → FM converter) ──
        public List<global::Edi.Core.Funscript.Fm.FmDeviceProfile> FmProfiles { get; set; } = new();
        public int FmSelectedProfile { get; set; }
        public global::Edi.Core.Funscript.Fm.FmConvertOptions FmOptions { get; set; } = new();
        public string FmOutputVariant { get; set; } = "FM";

        // Main window placement, restored on next launch.
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public bool WindowMaximized { get; set; }

        // Inner panel split (Connection vs Options columns), restored on next launch.
        public double? ColConnWidth { get; set; }
        public double? ColOptsWidth { get; set; }
        public double? ColGameLeftWidth { get; set; }

        private static string FilePath
        {
            get
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                return Path.Combine(dir, "EdiAppSettings.json");
            }
        }

        public static AppLocalSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppLocalSettings>(File.ReadAllText(FilePath));
                    if (loaded != null) return loaded;
                }
            }
            catch { }
            return new AppLocalSettings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}

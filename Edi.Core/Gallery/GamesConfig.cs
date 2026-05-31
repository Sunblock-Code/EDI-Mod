using PropertyChanged;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Edi.Core.Services;

namespace Edi.Core
{
    [AddINotifyPropertyChangedInterface]
    [UserConfig]
    public class GamesConfig
    {
        public GameInfo SelectedGameinfo { get; set; }
        public ObservableCollection<GameInfo> GamesInfo { get; set; } = new();
    }

    public record GameInfo(string Name, string Path)
    {
        public string? ExePath { get; init; }
        public string? GalleryPath { get; init; }
        public string? InfoPath { get; init; }
        public string? ImagePath { get; init; }
        // Optional manually-set or auto-fetched icon (separate from ImagePath, which is the banner).
        // When non-null and the file exists, the card resolver uses this BEFORE any folder/exe icon.
        public string? IconPath { get; init; }
        public string? GameType { get; init; }   // "EDI" (default) or "ScriptPlayer"

        // Extra named launch targets shown in the Launch button's dropdown (the main ExePath is the
        // default entry). Each can point at a different exe/command with its own arguments — e.g.
        // "Mod loader", "Config tool", "Game (windowed)".
        public List<LaunchOption>? LaunchOptions { get; init; }
    }

    // A single named entry in a game's Launch dropdown.
    [AddINotifyPropertyChangedInterface]
    public class LaunchOption
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";   // exe, file or URL to start
        public string Args { get; set; } = "";   // optional command-line arguments
    }
}

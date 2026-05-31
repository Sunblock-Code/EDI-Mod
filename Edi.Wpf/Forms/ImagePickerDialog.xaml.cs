using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Edi.Forms
{
    // Simple image-grid picker. Caller supplies a list of (filePath, label) items; the dialog
    // shows them as thumbnails in a WrapPanel and returns the picked file path via SelectedPath.
    // Used for the right-click "Browse cached icons / banners" actions — and the "Fetch more"
    // button hands control back to the caller to download new variants then refresh the grid.
    public partial class ImagePickerDialog : Window
    {
        public sealed class Item
        {
            public string FilePath { get; set; } = "";
            public string Label { get; set; } = "";
            public ImageSource? Thumb { get; set; }
        }

        public ObservableCollection<Item> Items { get; } = new();
        public string? SelectedPath { get; private set; }

        // Caller hook: when set, the "Fetch more" button calls this and then we re-read
        // the directory the items came from to pick up any newly-added files.
        public Func<Task>? FetchMoreAsync { get; set; }
        public string? CacheDir { get; set; }   // optional — used to rescan after FetchMoreAsync
        public string? ExtraLabelPrefix { get; set; }   // optional prefix for labels of cache rescans

        public ImagePickerDialog(string title, string hint, IEnumerable<Item> initial)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            Title = title;
            lblHint.Text = hint;
            foreach (var it in initial)
            {
                if (it.Thumb == null && !string.IsNullOrWhiteSpace(it.FilePath) && File.Exists(it.FilePath))
                    it.Thumb = LoadThumb(it.FilePath);
                Items.Add(it);
            }
            lstItems.ItemsSource = Items;
            if (FetchMoreAsync == null) btnFetchMore.Visibility = Visibility.Collapsed;
        }

        // Helper for callers: build a thumbnail from a file path with safe decode + freeze.
        public static ImageSource? LoadThumb(string file)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(file);
                bi.DecodePixelWidth = 280;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        private void Items_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            btnUse.IsEnabled = lstItems.SelectedItem is Item;
        }

        private void Items_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lstItems.SelectedItem is Item it) Accept(it);
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (lstItems.SelectedItem is Item it) Accept(it);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Accept(Item it)
        {
            SelectedPath = it.FilePath;
            DialogResult = true;
            Close();
        }

        private async void FetchMore_Click(object sender, RoutedEventArgs e)
        {
            if (FetchMoreAsync == null) return;
            btnFetchMore.IsEnabled = false;
            lblStatus.Text = "Searching for more variants…";
            try
            {
                await FetchMoreAsync();
                RescanCacheDir();
                lblStatus.Text = $"Cache now has {Items.Count} item(s).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Fetch failed: " + ex.Message;
            }
            finally { btnFetchMore.IsEnabled = true; }
        }

        // Re-read the cache directory and append any files we haven't already shown.
        private void RescanCacheDir()
        {
            if (string.IsNullOrWhiteSpace(CacheDir) || !Directory.Exists(CacheDir)) return;
            var have = new HashSet<string>(Items.Select(i => i.FilePath ?? ""), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var f in Directory.EnumerateFiles(CacheDir).OrderBy(x => x))
            {
                if (have.Contains(f)) continue;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp" && ext != ".bmp" && ext != ".gif" && ext != ".ico") continue;
                var label = (ExtraLabelPrefix ?? "") + Path.GetFileNameWithoutExtension(f);
                Items.Add(new Item { FilePath = f, Label = label, Thumb = LoadThumb(f) });
                added++;
            }
        }
    }
}

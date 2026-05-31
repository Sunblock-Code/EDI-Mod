using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Edi.Forms
{
    public class CoverResult : INotifyPropertyChanged
    {
        public string Label { get; set; } = "";
        public string ImageUrl { get; set; } = "";   // full-res image used when the cover is chosen
        public string ThumbUrl { get; set; } = "";    // smaller image for the result tile (falls back to ImageUrl)
        public string PageUrl { get; set; } = "";      // game's thread/product page — clicking drills in to all its images

        private ImageSource? _thumb;
        public ImageSource? Thumb
        {
            get => _thumb;
            set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class CoverSearchDialog : Window
    {
        public string? SelectedImageUrl { get; private set; }

        public ObservableCollection<CoverResult> Results { get; } = new();

        private static readonly HttpClient _http = CreateHttp();
        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return h;
        }

        // Download an image with our own client (browser UA + referer) and decode it to a
        // frozen bitmap. WPF's built-in remote-image loader uses a bare UA that some CDNs
        // (Cloudflare on F95) block, which is why result tiles looked empty. Handles AVIF/WebP
        // via the OS codecs. Returns null on failure (tile just shows blank, still selectable).
        private static async Task<ImageSource?> LoadImageAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (url.IndexOf("f95zone", StringComparison.OrdinalIgnoreCase) >= 0)
                    req.Headers.TryAddWithoutValidation("Referer", "https://f95zone.to/");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync();

                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.DecodePixelWidth = 340;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // GET a page as HTML. DLsite's adult section needs an age-confirmation cookie,
        // otherwise it answers 403. Returns null on any non-success / failure.
        private static async Task<string?> GetHtmlAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                if (url.IndexOf("dlsite.com", StringComparison.OrdinalIgnoreCase) >= 0)
                    req.Headers.TryAddWithoutValidation("Cookie", "adultchecked=1; locale=en_US");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        public CoverSearchDialog(string gameName)
        {
            DarkTitleBar.Apply(this);
            InitializeComponent();
            lstResults.ItemsSource = Results;
            txtQuery.Text = gameName ?? "";
            Loaded += async (_, _) => { txtQuery.Focus(); txtQuery.SelectAll(); await RunSearch(); };
        }

        private void Query_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _ = RunSearch(); e.Handled = true; }
        }

        private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch();

        private async Task RunSearch()
        {
            var term = txtQuery.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(term)) return;

            btnSearch.IsEnabled = false;
            _savedMatches = null;
            btnBack.Visibility = Visibility.Collapsed;
            Results.Clear();
            btnUse.IsEnabled = false;
            lblStatus.Text = "Searching…";

            try
            {
                // Accept a link pasted on its own OR alongside text (grab the first URL).
                var urlMatch = Regex.Match(term, @"https?://\S+", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                {
                    var pageUrl = urlMatch.Value.TrimEnd('.', ',', ')', ']');
                    // Pull every image posted on the page (cover first) so the user can pick.
                    foreach (var img in await ExtractAllImagesFromUrlAsync(pageUrl))
                        Results.Add(new CoverResult { Label = "From link", ImageUrl = img });

                    async Task LoadLink(CoverResult r) =>
                        r.Thumb = await LoadImageAsync(string.IsNullOrEmpty(r.ThumbUrl) ? r.ImageUrl : r.ThumbUrl);
                    await Task.WhenAll(Results.Select(LoadLink));

                    lblStatus.Text = Results.Count > 0
                        ? $"{Results.Count} image(s) from that page. Click the one you want, then Use."
                        : "Couldn't find images on that page. Make sure it's the thread/product page (not a search page).";
                }
                else
                {
                    // SteamGridDB first when an API key is set (curated, high-res art). Then the
                    // adult-game sources (F95 → DLsite → itch) as fallbacks if nothing came back.
                    foreach (var r in await SearchSteamGridDbAsync(term)) Results.Add(r);
                    if (Results.Count == 0)
                        foreach (var r in await SearchF95Async(term)) Results.Add(r);
                    if (Results.Count == 0)
                        foreach (var r in await SearchDlsiteAsync(term)) Results.Add(r);
                    if (Results.Count == 0)
                        foreach (var r in await SearchItchAsync(term)) Results.Add(r);

                    // Fill in thumbnails concurrently (frozen bitmaps update via INotifyPropertyChanged).
                    async Task Load(CoverResult r) =>
                        r.Thumb = await LoadImageAsync(string.IsNullOrEmpty(r.ThumbUrl) ? r.ImageUrl : r.ThumbUrl);
                    await Task.WhenAll(Results.Select(Load));

                    lblStatus.Text = Results.Count > 0
                        ? $"{Results.Count} result(s). Click one to see all its images, then pick & Use."
                        : "No matches. Try simpler words, or paste the game's F95 / DLsite / itch.io page link above.";
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                btnSearch.IsEnabled = true;
            }
        }

        // When the matches list is showing, clicking a result drills into that game's page and
        // loads every image posted there (same view as pasting its link). _savedMatches lets the
        // Back button restore the match list; _navigating suppresses re-entrancy while we swap items.
        private List<CoverResult>? _savedMatches;
        private bool _navigating;

        private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (lstResults.SelectedItem is CoverResult r && !string.IsNullOrEmpty(r.PageUrl))
            {
                await DrillInAsync(r);
                return;
            }
            btnUse.IsEnabled = lstResults.SelectedItem is CoverResult;
        }

        private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Only the terminal image tiles (no PageUrl) confirm on double-click; match tiles drill in.
            if (lstResults.SelectedItem is CoverResult r && string.IsNullOrEmpty(r.PageUrl))
                Use_Click(sender, e);
        }

        private async Task DrillInAsync(CoverResult match)
        {
            btnSearch.IsEnabled = false;
            lblStatus.Text = "Opening images…";

            List<string> imgs;
            try { imgs = await ExtractAllImagesFromUrlAsync(match.PageUrl); }
            catch { imgs = new List<string>(); }

            _savedMatches ??= Results.ToList();

            _navigating = true;
            Results.Clear();
            if (imgs.Count == 0)
            {
                // Couldn't scrape the page — keep the cover so the user can still pick it.
                Results.Add(new CoverResult { Label = "Cover", ImageUrl = match.ImageUrl, ThumbUrl = match.ThumbUrl, Thumb = match.Thumb });
            }
            else
            {
                foreach (var img in imgs)
                    Results.Add(new CoverResult { Label = "From match", ImageUrl = img });
            }
            _navigating = false;

            async Task Load(CoverResult x)
            {
                if (x.Thumb == null)
                    x.Thumb = await LoadImageAsync(string.IsNullOrEmpty(x.ThumbUrl) ? x.ImageUrl : x.ThumbUrl);
            }
            await Task.WhenAll(Results.Select(Load));

            btnUse.IsEnabled = false;
            btnBack.Visibility = Visibility.Visible;
            btnSearch.IsEnabled = true;
            lblStatus.Text = imgs.Count > 0
                ? $"{Results.Count} image(s) from “{match.Label}”. Click the one you want, then Use."
                : "Couldn't load more images for this one — you can still Use its cover.";
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_savedMatches == null) return;
            _navigating = true;
            Results.Clear();
            foreach (var m in _savedMatches) Results.Add(m);
            _navigating = false;
            _savedMatches = null;

            btnBack.Visibility = Visibility.Collapsed;
            btnUse.IsEnabled = false;
            lblStatus.Text = $"{Results.Count} result(s). Click one to see all its images, then pick & Use.";
        }

        private void Use_Click(object sender, RoutedEventArgs e)
        {
            if (lstResults.SelectedItem is not CoverResult r) return;
            SelectedImageUrl = r.ImageUrl;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        // ===================== shared lookup helpers =====================

        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        { "of", "the", "a", "an", "in", "on", "and", "to", "for", "with", "my", "at", "is", "by" };

        // Search F95zone by name via its public "Latest Updates" API (no login, returns covers).
        // The API matches ALL query words against the title, so punctuation and stop-words
        // ("of", "the"…) cause misses. Try progressively looser queries until one hits.
        public static async Task<List<CoverResult>> SearchF95Async(string term)
        {
            foreach (var q in BuildQueryVariants(term))
            {
                var res = await F95QueryAsync(q);
                if (res.Count > 0) return res;
            }
            return new List<CoverResult>();
        }

        private static List<string> BuildQueryVariants(string term)
        {
            var variants = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string s)
            {
                s = s.Trim();
                if (s.Length > 0 && seen.Add(s)) variants.Add(s);
            }

            // Strip punctuation to spaces and collapse runs of whitespace.
            var norm = Regex.Replace(term ?? "", @"[^A-Za-z0-9]+", " ");
            norm = Regex.Replace(norm, @"\s+", " ").Trim();
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sig = words.Where(w => !_stopWords.Contains(w)).ToArray();

            Add(norm);                                   // full (cleaned)
            Add(string.Join(" ", sig));                  // drop stop-words
            if (sig.Length > 3) Add(string.Join(" ", sig.Take(3)));
            if (sig.Length > 2) Add(string.Join(" ", sig.Take(2)));
            if (sig.Length > 0) Add(sig[0]);             // last resort: most distinctive word
            return variants;
        }

        private static async Task<List<CoverResult>> F95QueryAsync(string query)
        {
            var results = new List<CoverResult>();
            var q = Uri.EscapeDataString(query);
            var url = $"https://f95zone.to/sam/latest_alpha/latest_data.php?cmd=list&cat=games&page=1&sort=likes&rows=30&search={q}";

            string json;
            try { json = await _http.GetStringAsync(url); }
            catch { return results; }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("msg", out var msg) ||
                    !msg.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var item in data.EnumerateArray())
                {
                    var cover = item.TryGetProperty("cover", out var c) ? c.GetString() : null;
                    if (string.IsNullOrWhiteSpace(cover)) continue;
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var creator = item.TryGetProperty("creator", out var cr) ? cr.GetString() : null;
                    var label = string.IsNullOrWhiteSpace(creator) ? (title ?? "F95") : $"{title} · {creator}";

                    // The API returns a downscaled preview.f95zone.to URL; the full-resolution
                    // image lives at the same path on attachments.f95zone.to.
                    var full = cover.Contains("preview.f95zone.to", StringComparison.OrdinalIgnoreCase)
                        ? cover.Replace("preview.f95zone.to", "attachments.f95zone.to")
                        : cover;

                    // Thread id lets clicking the result open the thread and pull every posted image.
                    long tid = 0;
                    if (item.TryGetProperty("thread_id", out var tidEl))
                        tid = tidEl.ValueKind == JsonValueKind.Number ? tidEl.GetInt64()
                            : long.TryParse(tidEl.GetString(), out var p1) ? p1 : 0;
                    if (tid == 0 && item.TryGetProperty("id", out var idEl))
                        tid = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64()
                            : long.TryParse(idEl.GetString(), out var p2) ? p2 : 0;
                    var page = tid > 0 ? $"https://f95zone.to/threads/{tid}/" : "";

                    results.Add(new CoverResult { Label = label, ImageUrl = full, ThumbUrl = cover, PageUrl = page });
                    if (results.Count >= 24) break;
                }
            }
            catch { }
            return results;
        }

        // Best-effort auto-find by name: F95 API first (matches the user's libraries), then DLsite.
        public static async Task<string?> AutoFindAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            try
            {
                var f = await SearchF95Async(gameName);
                if (f.Count > 0) return f[0].ImageUrl;
            }
            catch { }
            try
            {
                var dl = await SearchDlsiteAsync(gameName);
                if (dl.Count > 0) return dl[0].ImageUrl;
            }
            catch { }
            try
            {
                var it = await SearchItchAsync(gameName);
                if (it.Count > 0) return it[0].ImageUrl;
            }
            catch { }
            try
            {
                var sg = await SearchSteamGridDbAsync(gameName);
                if (sg.Count > 0) return sg[0].ImageUrl;
            }
            catch { }
            return null;
        }

        // Pull the single best cover image from a thread/product page (first of the set below).
        public static async Task<string?> ExtractCoverFromUrlAsync(string url)
        {
            var all = await ExtractAllImagesFromUrlAsync(url);
            return all.Count > 0 ? all[0] : null;
        }

        // Pull EVERY candidate image from a thread/product page — the cover first, then the
        // other images posted alongside it — so the user can pick among them. Falls back to a
        // single og:image when the page isn't a known forum/store layout.
        public static async Task<List<string>> ExtractAllImagesFromUrlAsync(string url)
        {
            var images = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? u)
            {
                if (string.IsNullOrWhiteSpace(u)) return;
                u = u.Replace("\\/", "/").Trim();
                if (u.StartsWith("//")) u = "https:" + u;
                if (seen.Add(u)) images.Add(u);
            }

            var html = await GetHtmlAsync(url);
            if (html == null) return images;

            // 1) F95 (and similar forums): full-size attachments posted in the thread (skip /thumb/).
            foreach (Match m in Regex.Matches(html,
                         @"https://attachments\.f95zone\.to/[^\s""'\\]+?\.(?:jpg|jpeg|png|webp|gif)",
                         RegexOptions.IgnoreCase))
                if (m.Value.IndexOf("/thumb/", StringComparison.OrdinalIgnoreCase) < 0) Add(m.Value);

            // 2) DLsite work + sample images.
            foreach (Match m in Regex.Matches(html,
                         @"(?:https?:)?//img\.dlsite\.jp/[^""'\s\\]*?_img_(?:main|sam|smp\d*)\.(?:jpg|jpeg|webp|png)",
                         RegexOptions.IgnoreCase))
                Add(m.Value);

            // 3) itch.io page images (cover + screenshots); upgrade thumbs to the original.
            foreach (Match m in Regex.Matches(html,
                         @"https://img\.itch\.zone/[^\s""'\\]+?\.(?:png|jpg|jpeg|gif|webp)",
                         RegexOptions.IgnoreCase))
                Add(Regex.Replace(m.Value, @"/\d+x\d+(?:%23\w+)?/", "/original/"));

            // 4) Fall back to og:image — but skip site chrome (favicons / asset images).
            if (images.Count == 0)
            {
                var og = Regex.Match(html,
                    @"<meta[^>]+property=[""']og:image[""'][^>]+content=[""']([^""']+)[""']",
                    RegexOptions.IgnoreCase);
                if (!og.Success)
                    og = Regex.Match(html,
                        @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image[""']",
                        RegexOptions.IgnoreCase);
                if (og.Success)
                {
                    var u = WebUtility.HtmlDecode(og.Groups[1].Value).Trim();
                    if (u.IndexOf("favicon", StringComparison.OrdinalIgnoreCase) < 0 &&
                        u.IndexOf("/assets/", StringComparison.OrdinalIgnoreCase) < 0)
                        Add(u);
                }
            }

            if (images.Count > 40) images = images.GetRange(0, 40);
            return images;
        }

        // Scrape the DLsite (maniax) keyword search for work thumbnails (needs the age cookie).
        public static async Task<List<CoverResult>> SearchDlsiteAsync(string term)
        {
            var results = new List<CoverResult>();
            var q = Uri.EscapeDataString(term.Trim());
            var html = await GetHtmlAsync($"https://www.dlsite.com/maniax/fsr/=/language/jp/keyword/{q}/");
            if (html == null) return results;

            var rx = new Regex(
                @"(?:https?:)?//img\.dlsite\.jp/[^""'\s\\]*?_img_(?:main|sam)\.(?:jpg|jpeg|webp|png)",
                RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in rx.Matches(html))
            {
                var u = m.Value.Replace("\\/", "/");
                if (u.StartsWith("//")) u = "https:" + u;
                if (!seen.Add(u)) continue;
                var rj = Regex.Match(u, @"RJ\d+", RegexOptions.IgnoreCase).Value.ToUpperInvariant();
                var page = string.IsNullOrEmpty(rj) ? "" : $"https://www.dlsite.com/maniax/work/=/product_id/{rj}.html";
                results.Add(new CoverResult { Label = string.IsNullOrEmpty(rj) ? "DLsite" : rj, ImageUrl = u, PageUrl = page });
                if (results.Count >= 12) break;
            }
            return results;
        }

        // Scrape itch.io's keyword search for game thumbnails. Within each result cell the
        // lazy-loaded cover comes right before its title link, so pairing image→next-title
        // keeps them aligned. The grid thumb is a cropped 315x250; swapping that path
        // segment for "original" yields the full-resolution cover used when one is chosen.
        public static async Task<List<CoverResult>> SearchItchAsync(string term)
        {
            var results = new List<CoverResult>();
            foreach (var query in BuildQueryVariants(term))
            {
                var html = await GetHtmlAsync($"https://itch.io/search?q={Uri.EscapeDataString(query)}");
                if (html == null) continue;

                var rx = new Regex(
                    @"data-lazy_src=""(https://img\.itch\.zone/[^""]+)""[\s\S]*?class=""title game_link"" href=""([^""]+)""[^>]*>([^<]+)</a>",
                    RegexOptions.IgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in rx.Matches(html))
                {
                    var thumb = m.Groups[1].Value;
                    var page  = m.Groups[2].Value;
                    if (!seen.Add(page)) continue;

                    var title = WebUtility.HtmlDecode(m.Groups[3].Value).Trim();
                    var full  = Regex.Replace(thumb, @"/\d+x\d+(?:%23\w+)?/", "/original/");

                    string author = "";
                    try
                    {
                        var host = new Uri(page).Host;
                        author = host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase) ? host[..^8] : host;
                    }
                    catch { }

                    var label = string.IsNullOrWhiteSpace(author)
                        ? (title.Length > 0 ? title : "itch.io")
                        : $"{title} · {author}";
                    results.Add(new CoverResult { Label = label, ImageUrl = full, ThumbUrl = thumb, PageUrl = page });
                    if (results.Count >= 24) break;
                }
                if (results.Count > 0) break;
            }
            return results;
        }

        // SteamGridDB (steamgriddb.com) — curated, high-res game art. Needs the user's free API key
        // (Settings → Data → SteamGridDB). Searches games by name, then pulls each top game's grids
        // (the cover/capsule art). Returns empty (silently) when no key is set.
        public static async Task<List<CoverResult>> SearchSteamGridDbAsync(string term)
        {
            var results = new List<CoverResult>();
            var key = Edi.Core.Services.AppLocalSettings.Load().SteamGridDbApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(key)) return results;

            async Task<string?> ApiGet(string url)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    using var resp = await _http.SendAsync(req);
                    return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
                }
                catch { return null; }
            }

            // 1) Find matching games by name.
            var games = new List<(int id, string name)>();
            var searchJson = await ApiGet($"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(term)}");
            if (searchJson == null) return results;
            try
            {
                using var doc = JsonDocument.Parse(searchJson);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var g in data.EnumerateArray())
                    {
                        if (g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        {
                            var nm = g.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                            games.Add((idEl.GetInt32(), nm));
                        }
                        if (games.Count >= 3) break;
                    }
            }
            catch { return results; }

            // 2) Pull each top game's grids (cover art).
            foreach (var (id, name) in games)
            {
                var gridsJson = await ApiGet($"https://www.steamgriddb.com/api/v2/grids/game/{id}?types=static&limit=12");
                if (gridsJson == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(gridsJson);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                    foreach (var grid in data.EnumerateArray())
                    {
                        var url = grid.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        var thumb = grid.TryGetProperty("thumb", out var th) ? th.GetString() : url;
                        results.Add(new CoverResult
                        {
                            Label = string.IsNullOrWhiteSpace(name) ? "SteamGridDB" : $"{name} · SteamGridDB",
                            ImageUrl = url!,
                            ThumbUrl = thumb ?? url!
                        });
                        if (results.Count >= 24) break;
                    }
                }
                catch { }
                if (results.Count >= 24) break;
            }
            return results;
        }

        // Icon-specific search (small square art). Mirrors SearchSteamGridDbAsync but hits the
        // /icons endpoint, which returns purpose-built game icons rather than capsule covers.
        // Used by the right-click "Icon → Fetch icon" menu and the library-wide icon back-fill.
        public static async Task<List<CoverResult>> SearchSteamGridDbIconsAsync(string term)
        {
            var results = new List<CoverResult>();
            var key = Edi.Core.Services.AppLocalSettings.Load().SteamGridDbApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(key)) return results;

            async Task<string?> ApiGet(string url)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    using var resp = await _http.SendAsync(req);
                    return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
                }
                catch { return null; }
            }

            // 1) Find matching games by name (same autocomplete endpoint as the cover search).
            var games = new List<(int id, string name)>();
            var searchJson = await ApiGet($"https://www.steamgriddb.com/api/v2/search/autocomplete/{Uri.EscapeDataString(term)}");
            if (searchJson == null) return results;
            try
            {
                using var doc = JsonDocument.Parse(searchJson);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var g in data.EnumerateArray())
                    {
                        if (g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        {
                            var nm = g.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
                            games.Add((idEl.GetInt32(), nm));
                        }
                        if (games.Count >= 3) break;
                    }
            }
            catch { return results; }

            // 2) Pull each top game's icons.
            foreach (var (id, name) in games)
            {
                var iconsJson = await ApiGet($"https://www.steamgriddb.com/api/v2/icons/game/{id}?limit=12");
                if (iconsJson == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(iconsJson);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                    foreach (var ic in data.EnumerateArray())
                    {
                        var url = ic.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        var thumb = ic.TryGetProperty("thumb", out var th) ? th.GetString() : url;
                        results.Add(new CoverResult
                        {
                            Label = string.IsNullOrWhiteSpace(name) ? "SteamGridDB icon" : $"{name} · icon",
                            ImageUrl = url!,
                            ThumbUrl = thumb ?? url!
                        });
                        if (results.Count >= 24) break;
                    }
                }
                catch { }
                if (results.Count >= 24) break;
            }
            return results;
        }

        // Result row for a found video preview/trailer. Url is the direct .mp4 link the
        // caller will download; ThumbUrl is the poster image for the picker; Label is for UI.
        public sealed class VideoResult
        {
            public string Url      { get; set; } = "";
            public string ThumbUrl { get; set; } = "";
            public string Label    { get; set; } = "";
            public string PageUrl  { get; set; } = "";
        }

        // Walks a DLsite product page for the user's term and pulls any <video> / "trial" /
        // sample .mp4 URLs it can see. DLsite serves preview videos under media.dlsite.jp or
        // img.dlsite.jp; multiple match shapes are caught so localized markup still works.
        public static async Task<List<VideoResult>> SearchDlsiteVideosAsync(string term)
        {
            var results = new List<VideoResult>();
            // Reuse the existing image search so we know which product page to fetch — its
            // PageUrl points at the product's full HTML where the video markup lives.
            List<CoverResult> hits;
            try { hits = await SearchDlsiteAsync(term); } catch { return results; }

            var rxVid = new Regex(@"(https?:)?//(?:media|img)\.dlsite\.jp[^""'\s<>\\]+?\.mp4", RegexOptions.IgnoreCase);
            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits.Take(4))
            {
                if (string.IsNullOrWhiteSpace(h.PageUrl)) continue;
                var html = await GetHtmlAsync(h.PageUrl);
                if (html == null) continue;
                foreach (Match m in rxVid.Matches(html))
                {
                    var u = m.Value.Replace("\\/", "/");
                    if (u.StartsWith("//")) u = "https:" + u;
                    if (!seen.Add(u)) continue;
                    results.Add(new VideoResult { Url = u, ThumbUrl = h.ImageUrl, PageUrl = h.PageUrl, Label = $"{h.Label} · DLsite" });
                    if (results.Count >= 12) break;
                }
                if (results.Count >= 12) break;
            }
            return results;
        }

        // itch.io games embed previews as <video src="https://img.itch.zone/.../*.mp4"> tags
        // (sometimes lazy-loaded via data-src). Walks the top game pages from the existing
        // text search and collects every preview URL it sees.
        public static async Task<List<VideoResult>> SearchItchVideosAsync(string term)
        {
            var results = new List<VideoResult>();
            List<CoverResult> hits;
            try { hits = await SearchItchAsync(term); } catch { return results; }

            var rxVid = new Regex(@"https://img\.itch\.zone/[^""'\s<>\\]+?\.mp4", RegexOptions.IgnoreCase);
            var rxDataSrc = new Regex(@"data-src=""(https://img\.itch\.zone/[^""]+\.mp4)""", RegexOptions.IgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits.Take(4))
            {
                if (string.IsNullOrWhiteSpace(h.PageUrl)) continue;
                var html = await GetHtmlAsync(h.PageUrl);
                if (html == null) continue;
                foreach (Match m in rxVid.Matches(html))
                {
                    if (!seen.Add(m.Value)) continue;
                    results.Add(new VideoResult { Url = m.Value, ThumbUrl = h.ImageUrl, PageUrl = h.PageUrl, Label = $"{h.Label} · itch.io" });
                    if (results.Count >= 12) break;
                }
                foreach (Match m in rxDataSrc.Matches(html))
                {
                    if (!seen.Add(m.Groups[1].Value)) continue;
                    results.Add(new VideoResult { Url = m.Groups[1].Value, ThumbUrl = h.ImageUrl, PageUrl = h.PageUrl, Label = $"{h.Label} · itch.io" });
                    if (results.Count >= 12) break;
                }
                if (results.Count >= 12) break;
            }
            return results;
        }

        // Auto-find: DLsite first (usually higher-quality trailers), then itch.io fallback.
        // Returns the first URL it can find, or null if neither source has a preview clip.
        public static async Task<string?> AutoFindVideoAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            try
            {
                var dl = await SearchDlsiteVideosAsync(gameName);
                if (dl.Count > 0) return dl[0].Url;
            }
            catch { }
            try
            {
                var it = await SearchItchVideosAsync(gameName);
                if (it.Count > 0) return it[0].Url;
            }
            catch { }
            return null;
        }

        // Combined picker: DLsite + itch.io results in one list so the picker dialog can
        // show every available preview at once.
        public static async Task<List<VideoResult>> SearchAllVideosAsync(string gameName)
        {
            var all = new List<VideoResult>();
            try { all.AddRange(await SearchDlsiteVideosAsync(gameName)); } catch { }
            try { all.AddRange(await SearchItchVideosAsync(gameName));   } catch { }
            return all;
        }

        // Auto-find for an icon. Tries SteamGridDB's /icons endpoint first (real icon assets),
        // then falls back to the same cover-source chain AutoFindAsync uses (DLsite → F95 →
        // itch.io → SteamGridDB grids). Cover-art images work fine as icon stand-ins when the
        // game has no purpose-built icon online.
        public static async Task<string?> AutoFindIconAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName)) return null;
            // Prefer real icon assets when available.
            try
            {
                var iconHits = await SearchSteamGridDbIconsAsync(gameName);
                var icon = iconHits.FirstOrDefault()?.ImageUrl;
                if (!string.IsNullOrWhiteSpace(icon)) return icon;
            }
            catch { }
            // Fall back to the multi-source cover chain — same one banners use.
            try { return await AutoFindAsync(gameName); }
            catch { return null; }
        }
    }
}

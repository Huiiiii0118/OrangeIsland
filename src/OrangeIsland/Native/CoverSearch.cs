using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrangeIsland.Native;

// A last resort for a song whose media session and player log supply no artwork: video platforms and browser players
// often publish a title but no thumbnail. The title is looked up on the platform itself when it carries a video id,
// otherwise in Apple's public search catalogue (no key, no account). Nothing here can hold up the panel: every request
// is short, failures are silent, and both hits and misses are remembered for this run; hits are also kept on disk.
internal static class CoverSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(7) };
    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrangeIsland", "covers");
    private const long MaxImage = 8_388_608;
    private const int KeepRecent = 48;
    // Bytes for a hit, null for a miss. A miss is cached too, so an unrecognised track does not hit the network every tick.
    private static readonly Dictionary<string, byte[]?> Memory = new();
    private static readonly object Gate = new();

    public static async Task<byte[]?> FindAsync(string title, string artist, string album, CancellationToken cancellation = default)
    {
        title = (title ?? "").Trim();
        if (title.Length < 2) return null;
        string key = title + "\n" + artist;
        lock (Gate) if (Memory.TryGetValue(key, out var remembered)) return remembered;
        string file = CacheFile(key);
        if (File.Exists(file))
        {
            try
            {
                var saved = await File.ReadAllBytesAsync(file, cancellation);
                if (saved.Length > 0 && saved.Length <= MaxImage) { Remember(key, saved); return saved; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        byte[]? found = null;
        try
        {
            var url = await PlatformAsync(title, artist, album, cancellation).ConfigureAwait(false)
                      ?? await ITunesAsync(title, artist, cancellation).ConfigureAwait(false);
            if (url is not null) found = await ImageAsync(url, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* No artwork is not an error. */ }
        Remember(key, found);
        if (found is not null)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                await File.WriteAllBytesAsync(file + ".tmp", found, cancellation);
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        return found;
    }

    // A title that names a Bilibili video carries its id (BV1..), and that video's own poster is the right cover.
    // Other platforms expose no anonymous endpoint here; their titles fall through to the music catalogue below.
    private static async Task<string?> PlatformAsync(string title, string artist, string album, CancellationToken cancellation)
    {
        var match = Regex.Match($"{title} {artist} {album}", @"BV[0-9A-Za-z]{10}");
        if (!match.Success) return null;
        using var response = await Http.GetAsync($"https://api.bilibili.com/x/web-interface/view?bvid={match.Value}", cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        if (json.RootElement.GetProperty("code").GetInt32() != 0) return null;
        var pic = json.RootElement.GetProperty("data").GetProperty("pic").GetString();
        if (string.IsNullOrWhiteSpace(pic)) return null;
        return pic.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + pic[7..] : pic;
    }

    // iTunes Search API: free, keyless and returns square artwork. The best-matching result wins, and a weak match is
    // refused rather than showing another song's cover.
    private static async Task<string?> ITunesAsync(string title, string artist, CancellationToken cancellation)
    {
        string term = Uri.EscapeDataString($"{title} {artist}".Trim()[..Math.Min(120, $"{title} {artist}".Trim().Length)]);
        using var response = await Http.GetAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=10", cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("results", out var results)) return null;
        string? best = null; double score = 0;
        foreach (var item in results.EnumerateArray())
        {
            string name = item.TryGetProperty("trackName", out var t) ? t.GetString() ?? "" : "";
            string by = item.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "";
            double rank = Similar(name, title) * 2 + (artist.Length > 0 ? Similar(by, artist) : 0);
            if (rank <= score || !item.TryGetProperty("artworkUrl100", out var art)) continue;
            var url = art.GetString();
            if (string.IsNullOrWhiteSpace(url)) continue;
            score = rank; best = url;
        }
        if (best is null || score < .35) return null;
        return Regex.Replace(best, @"\d+x\d+bb", "600x600bb");
    }

    private static async Task<byte[]?> ImageAsync(string url, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImage) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384]; int read;
        while ((read = await stream.ReadAsync(bytes, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImage) return null;
            buffer.Write(bytes, 0, read);
        }
        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    // Letter/digit bigram overlap, after stripping punctuation. Titles from video platforms are noisy; an exact or
    // contained match is worth much more than a loose one.
    private static double Similar(string a, string b)
    {
        a = Normalize(a); b = Normalize(b);
        if (a.Length < 2 || b.Length < 2) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return .9;
        var one = Grams(a); var two = Grams(b);
        int shared = one.Count(g => two.Contains(g));
        return (double)shared / Math.Max(1, one.Count + two.Count - shared);
    }
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    private static HashSet<string> Grams(string value)
    {
        var set = new HashSet<string>();
        for (int i = 0; i + 1 < value.Length; i++) set.Add(value.Substring(i, 2));
        return set;
    }

    private static void Remember(string key, byte[]? bytes)
    {
        lock (Gate)
        {
            if (Memory.Count > KeepRecent) Memory.Clear();
            Memory[key] = bytes;
        }
    }
    private static string CacheFile(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(CacheFolder, Convert.ToHexString(hash.AsSpan(0, 12)) + ".img");
    }
}

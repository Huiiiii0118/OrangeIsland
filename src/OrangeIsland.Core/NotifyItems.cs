using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrangeIsland.Core;

// Where a notice came from. QQ, WeChat and the mailbox are named apart for their colour and their symbol; every other chat or
// office program the page listens to is App, and is told apart by its own name.
public enum NotifySource { Qq, WeChat, Mail, App }

// One program's waiting notices, as the collapsed capsule counts them: its newest one (whose sender names the page) and the
// newest one carrying a one-time code, which the capsule shows instead of sender and count.
public sealed record NotifyGroup(string Program, NotifySource Source, string App, int Count, NotifyItem Latest, string? Code);

// One notice kept by the notification page. It stays unread, and the island keeps showing it, until the page is opened.
public sealed class NotifyItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NotifySource Source { get; set; }
    // The program that announced it: its process name (NotifyPrograms), so its icon and its window can be found again.
    public string Program { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    // A mail's sender, kept apart from the body the mail carries; older notices and every chat notice leave it empty.
    public string Sender { get; set; } = "";
    public string App { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public bool Read { get; set; }
    // What makes two notices the same one, so a re-read of the same message does not pile up: source, app, title and body.
    public string Key { get; set; } = "";

    [JsonIgnore] public string SourceName => Source switch
    {
        NotifySource.Qq => "QQ", NotifySource.WeChat => Loc.T("微信", "WeChat"), NotifySource.Mail => Loc.T("邮箱", "Mail"),
        _ => App.Length > 0 ? App : Loc.T("程序", "Program")
    };
    [JsonIgnore] public string Line => Body.Length > 0 ? Body : Title;
    [JsonIgnore] public string Age => At.LocalDateTime.Date == DateTime.Today ? At.ToString("HH:mm") : At.ToString("MM-dd HH:mm");
}

// The notices the panel keeps: the newest first, unread ones counted, the whole list capped. Saved beside the settings.
public sealed class NotifyStore
{
    public const string FileName = "notifications.json";
    public const int Limit = 200;
    private readonly string _path;
    private readonly List<NotifyItem> _items = [];

    public NotifyStore(string folder) { _path = Path.Combine(folder, FileName); Load(); }

    public IReadOnlyList<NotifyItem> Items => _items;
    public int Count => _items.Count;
    public int Unread => _items.Count(item => !item.Read);
    public NotifyItem? LatestUnread => _items.FirstOrDefault(item => !item.Read);
    // The unread notices of one program, and of the programs in the order their newest notice arrived: the collapsed capsule
    // counts by program.
    public int UnreadOf(string program) => _items.Count(item => !item.Read && item.Program == program);
    public IReadOnlyList<NotifyGroup> UnreadGroups()
    {
        var groups = new List<NotifyGroup>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            if (item.Read) continue;
            string? code = VerifyCodes.Of(item.Title, item.Body);
            if (at.TryGetValue(item.Program, out int known))
            {
                // A code is what the reader needs from that program, even when a later message pushed it down the list.
                if (code is not null && groups[known].Code is null) groups[known] = groups[known] with { Code = code };
                continue;
            }
            at[item.Program] = groups.Count;
            groups.Add(new NotifyGroup(item.Program, item.Source, item.App, UnreadOf(item.Program), item, code));
        }
        return groups;
    }
    // Raised when a notice arrives or is read, so the panel and the island draw again.
    public event Action? Changed;

    // Adds a notice unless the same one is already kept. Returns the item added, or null when it was a repeat. fresh: this
    // occurrence is a new message even when it reads exactly as one already kept (a second flash of the same program says
    // "another message" and nothing more, so its text is always the same).
    public NotifyItem? Add(NotifySource source, string program, string title, string body, string app = "", DateTimeOffset? at = null, bool fresh = false, string sender = "")
    {
        title = (title ?? "").Trim(); body = (body ?? "").Trim(); app = (app ?? "").Trim(); program = (program ?? "").Trim();
        if (title.Length == 0 && body.Length == 0) return null;
        string key = Key(source, app, title, body);
        // The same message arriving again within a few minutes (a re-read of a mailbox, a window renamed twice) is one notice.
        if (!fresh && _items.FirstOrDefault(item => item.Key == key && DateTimeOffset.Now - item.At < TimeSpan.FromMinutes(10)) is { } known)
        {
            if (!known.Read) return null;
            known.Read = false; known.At = at ?? DateTimeOffset.Now; known.Program = program;
            Save(); Changed?.Invoke();
            return known;
        }
        var item = new NotifyItem { Source = source, Program = program, Title = title, Body = body, App = app, Sender = sender.Trim(), At = at ?? DateTimeOffset.Now, Key = key };
        _items.Insert(0, item);
        while (_items.Count > Limit) _items.RemoveAt(_items.Count - 1);
        Save(); Changed?.Invoke();
        return item;
    }

    public void MarkRead(Guid id)
    {
        if (_items.FirstOrDefault(item => item.Id == id) is not { } item || item.Read) return;
        item.Read = true; Save(); Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        bool any = false;
        foreach (var item in _items) { if (!item.Read) { item.Read = true; any = true; } }
        if (!any) return;
        Save(); Changed?.Invoke();
    }

    // Everything one program announced is read: its icon stopped flashing, or its window was brought up.
    public void MarkProgramRead(string program)
    {
        bool any = false;
        foreach (var item in _items) { if (!item.Read && item.Program == program) { item.Read = true; any = true; } }
        if (!any) return;
        Save(); Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        if (_items.RemoveAll(item => item.Id == id) == 0) return;
        Save(); Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear(); Save(); Changed?.Invoke();
    }

    public static string Key(NotifySource source, string app, string title, string body) => $"{source}|{app}|{title}|{body}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<NotifyItem>>(File.ReadAllText(_path));
            // Only notices that name the program that announced them are kept: a list written before programs were watched
            // loses the rest.
            if (items is not null) _items.AddRange(items.Where(item => item is not null && item.Program.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _items.Clear(); }
        // A mail saved by an earlier build could hold its quoted-printable HTML raw, un-decoded. Repair such bodies on
        // load so the history shows readable text without waiting for the mail to arrive again.
        foreach (var item in _items)
            if (item.Source == NotifySource.Mail && NeedsMailRepair(item.Body))
                item.Body = RepairMail(item.Body);
        CollapseFlashes();
    }

    // A taskbar flash carries no message text, so every pulse of the same program adds an identical notice (Add is called
    // with fresh:true on purpose). When a window is misattributed — a shared WeChatAppEx container that belongs to another
    // app, fixed in NotifyPrograms — that floods the store with hundreds of identical read entries and pushes the real
    // notices out of the capped list. Keep only the newest of any identical, already-read run that grew past a small
    // threshold. Untouched: unread notices (they still count on the capsule), distinct messages, and every mail.
    private const int FlashRunThreshold = 5;
    private void CollapseFlashes()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in _items)
            if (item.Read && item.Key.Length > 0) counts[item.Key] = counts.GetValueOrDefault(item.Key) + 1;
        var flooded = counts.Where(pair => pair.Value > FlashRunThreshold).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (flooded.Count == 0) return;
        // _items is newest-first: the first entry of a flooded, read key is the newest of that run.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var keptItems = new List<NotifyItem>(_items.Count);
        foreach (var item in _items)
        {
            if (item.Read && flooded.Contains(item.Key) && !kept.Add(item.Key)) continue;
            keptItems.Add(item);
        }
        _items.Clear();
        _items.AddRange(keptItems);
        // Load runs from the constructor after _path is set; persisting here clears the flood for good. No Changed is
        // raised because nothing is being displayed yet.
        Save();
    }

    // A body is a raw quoted-printable MIME fragment when it carries many "=XX" escapes, or HTML tags left in the text.
    // Chat notices never do, and a normal mail body has at most a stray "=", so a threshold keeps healthy text intact.
    private static readonly Regex QpEscape = new(@"=[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

    // GB18030 / Big5 aliases need the code-pages provider; registering twice is harmless.
    static NotifyStore() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static bool NeedsMailRepair(string body)
        => QpEscape.Matches(body).Count >= 3 || HtmlTag.Matches(body).Count >= 2;

    // Undo the one encoding an old mail notice could be saved with: quoted-printable bytes, then the bytes as their
    // charset, then HTML stripped to words. If none of that reads better than the original, keep the original.
    private static string RepairMail(string body)
    {
        byte[] bytes;
        try { bytes = Encoding.Latin1.GetBytes(body); }
        catch (ArgumentException) { return body; }
        var decoded = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'=') { decoded.Add(bytes[i]); continue; }
            if (i + 2 < bytes.Length && Hex(bytes[i + 1]) >= 0 && Hex(bytes[i + 2]) >= 0)
            {
                decoded.Add((byte)(Hex(bytes[i + 1]) * 16 + Hex(bytes[i + 2]))); i += 2;
            }
            else if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') { /* soft line break */ }
            else if (i + 2 < bytes.Length && bytes[i + 1] == (byte)'\r' && bytes[i + 2] == (byte)'\n') i += 2;
            else decoded.Add((byte)'=');
        }
        string? best = null;
        foreach (var charset in new[] { "utf-8", "gb18030", "big5" })
        {
            string candidate;
            try { candidate = Encoding.GetEncoding(charset).GetString([.. decoded]); }
            catch (ArgumentException) { continue; }
            int damaged = candidate.Count(c => c == '\uFFFD');
            // Prefer the first charset (UTF-8) on a tie: only a strictly cleaner decode displaces the current best.
            if (best is null || damaged < best.Count(c => c == '\uFFFD')) best = candidate;
            if (damaged == 0) break;
        }
        if (best is null) return body;
        string text = HtmlTag.Replace(best, " ").Replace("&nbsp;", " ").Replace("&amp;", "&")
            .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        // Some old bodies were stored as plain text that only needed its HTML tags removed; stripping alone can then
        // read better than a charset round-trip. Compare with a quality score that penalises leftover "=XX" escapes:
        // comparing raw readable counts would favour the undecoded body, whose hex digits look like letters.
        string strippedOnly = HtmlTag.Replace(body, " ").Replace("&nbsp;", " ");
        while (strippedOnly.Contains("  ", StringComparison.Ordinal)) strippedOnly = strippedOnly.Replace("  ", " ", StringComparison.Ordinal);
        strippedOnly = strippedOnly.Trim();
        return Quality(text) >= Quality(strippedOnly) ? text : strippedOnly;
    }

    // How readable a repaired body is: words count, decoding damage and leftover quoted-printable escapes count against.
    private static double Quality(string s)
    {
        int cjk = 0, letters = 0, damaged = 0;
        foreach (char c in s)
        {
            if (c == '\uFFFD') damaged++;
            else if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else if (char.IsLetterOrDigit(c)) letters++;
        }
        return cjk * 3.0 + letters - damaged * 12.0 - QpEscape.Matches(s).Count * 6.0;
    }

    private static int Hex(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0', >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10, _ => -1
    };

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_items)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

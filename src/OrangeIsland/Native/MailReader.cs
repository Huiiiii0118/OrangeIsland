using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OrangeIsland.Core;

namespace OrangeIsland.Native;

// The unread mail a mailbox holds, asked over IMAP: the first look only notes which messages are already there, so turning the
// mailbox on does not replay the past. Only headers are asked for, never a body or an attachment, and a message is not marked
// read on the server.
internal sealed partial class MailReader
{
    static MailReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly HashSet<uint> _seen = [];
    private bool _seeded;

    public bool Seeded => _seeded;
    public string? LastError { get; private set; }

    // The messages that are new since the last look. Throws when the mailbox cannot be reached; the caller reports it.
    public async Task<IReadOnlyList<NotifyArrival>> PollAsync(string host, int port, string user, string password, CancellationToken token)
    {
        var found = new List<NotifyArrival>();
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await client.ConnectAsync(host, port, timeout.Token);
        using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        var session = new Session(ssl);
        await session.ReadLineAsync(timeout.Token); // the server's greeting
        await session.CommandAsync("LOGIN " + Quote(user) + " " + Quote(password), timeout.Token);
        await session.CommandAsync("SELECT \"INBOX\"", timeout.Token);
        var search = await session.CommandAsync("UID SEARCH UNSEEN", timeout.Token);
        var uids = ParseSearch(search);
        bool first = !_seeded;
        var fresh = uids.Where(uid => !_seen.Contains(uid)).OrderBy(uid => uid).ToList();
        // At most three at a time: a mailbox with fifty unread messages should not open fifty notifications.
        foreach (var uid in fresh.Take(3))
        {
            var header = await session.CommandAsync($"UID FETCH {uid} (BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)])", timeout.Token);
            var (from, subject) = ParseHeader(header);
            if (first) continue;
            // The readable text comes with the headers, so a mail shows its whole content on the page's left half rather
            // than a subject alone. Only the first 128 KB are asked for; a part the server will not give costs the mail
            // its text but not its announcement.
            string text = "";
            try
            {
                var body = await session.CommandAsync($"UID FETCH {uid} (BODY.PEEK[TEXT]<0.131072>)", timeout.Token);
                text = Body(session.Literals.FirstOrDefault([]));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            found.Add(new NotifyArrival(NotifySource.Mail, NotifyPrograms.MailKey, subject.Length > 0 ? subject : Loc.T("新邮件", "New mail"), text, Loc.T("邮箱", "Mail"), from));
        }
        foreach (var uid in fresh) _seen.Add(uid);
        _seen.IntersectWith(uids);
        _seeded = true;
        try { await session.CommandAsync("LOGOUT", timeout.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return found;
    }

    private static List<uint> ParseSearch(string response)
    {
        var uids = new List<uint>();
        foreach (var line in response.Split('\n'))
        {
            if (!line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in line["* SEARCH".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (uint.TryParse(part, out var uid)) uids.Add(uid);
        }
        return uids;
    }

    // From and Subject out of a header block, decoded from any of the encodings a mailbox may use.
    private static (string From, string Subject) ParseHeader(string block)
    {
        string from = "", subject = "";
        var unfolded = new StringBuilder();
        foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) { if (unfolded.Length > 0) break; continue; }
            if (line[0] is ' ' or '\t' && unfolded.Length > 0) { unfolded.Append(' ').Append(line.Trim()); continue; }
            if (unfolded.Length > 0) { Take(unfolded.ToString()); unfolded.Clear(); }
            unfolded.Append(line);
        }
        if (unfolded.Length > 0) Take(unfolded.ToString());
        void Take(string line)
        {
            if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase)) subject = Decode(line["Subject:".Length..].Trim());
            else if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase)) from = Short(Decode(line["From:".Length..].Trim()));
        }
        return (from, subject);
    }

    // "张三 <a@b.c>" becomes "张三"; an address alone keeps its name part.
    private static string Short(string from)
    {
        int angle = from.IndexOf('<');
        if (angle > 0) return from[..angle].Trim().Trim('"');
        if (angle == 0) return from.Trim('<', '>', ' ');
        int at = from.IndexOf('@');
        return at > 0 ? from[..at] : from;
    }

    // The readable text of a message body: the first text part of a MIME tree, its transfer encoding and charset undone,
    // HTML stripped to its words. A body that cannot be understood yields what decodes plainly, so the notice still
    // carries something; nothing here marks the message read on the server.
    internal static string Body(byte[] bytes) => Cap(PartText(bytes, out _).Trim());

    private const int BodyLimit = 8000;

    private static string Cap(string text) => text.Length > BodyLimit ? text[..BodyLimit] + "…" : text;

    private static string PartText(byte[] bytes, out bool html)
    {
        html = false;
        var (type, charset, boundary, transfer, content) = Part(bytes);
        if (boundary.Length > 0)
        {
            // The first plain part is the mail as its sender wrote it; an HTML-only mail has its tags stripped instead.
            string fallback = "";
            foreach (var piece in Parts(content, boundary))
            {
                string text = PartText(piece, out bool pieceHtml);
                if (text.Length == 0) continue;
                if (!pieceHtml) return text;
                fallback ??= text;
            }
            return fallback;
        }
        byte[] decoded = transfer switch
        {
            "base64" => FromBase64(content),
            "quoted-printable" => QuotedPrintable(content),
            _ => content
        };
        string result = Decode(decoded, charset);
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase)) { html = true; result = StripTags(result); }
        return result;
    }

    // A part's headers, its content, and the four header facts the decoding needs.
    private static (string Type, string Charset, string Boundary, string Transfer, byte[] Content) Part(byte[] bytes)
    {
        int split = IndexOfEmptyLine(bytes);
        byte[] head = split >= 0 ? bytes[..split] : bytes;
        byte[] content = split >= 0 ? bytes[(split + 4)..] : [];
        string text = Encoding.Latin1.GetString(head);
        string Type() => Match(text, @"(?im)^content-type\s*:\s*([^;\r\n]+)") ?? "text/plain";
        string Value(string name, string pattern) => Match(text, $"(?im)^{name}\\s*:\\s*{pattern}") ?? "";
        return (Type().Trim(),
            Value("content-type", "charset=\"?([^\";\\r\\n]+)\"?") ?? "",
            Value("content-type", "boundary=\"?([^\";\\r\\n]+)\"?") ?? "",
            (Value("content-transfer-encoding", "([^\\s;\\r\\n]+)") ?? "").ToLowerInvariant(),
            content);
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : match.Success ? match.Value : null;
    }

    // The pieces of a multipart body: everything between the boundary lines, decoded by the caller.
    private static List<byte[]> Parts(byte[] content, string boundary)
    {
        var result = new List<byte[]>();
        byte[] marker = Encoding.Latin1.GetBytes("--" + boundary);
        int start = -1, at = 0;
        while (at <= content.Length)
        {
            int line = IndexOfFrom(content, marker, at);
            if (line < 0) { if (start >= 0) result.Add(content[start..]); break; }
            if (start >= 0 && line >= start + 2) result.Add(content[start..(line - 2)]);
            int after = line + marker.Length;
            bool done = after + 1 < content.Length && content[after] == (byte)'-' && content[after + 1] == (byte)'-';
            int eol = IndexOfFrom(content, Crlf, after);
            if (eol < 0) break;
            at = eol + 2;
            if (done) break;
            start = at;
        }
        return result;
    }

    private static readonly byte[] Crlf = [(byte)'\r', (byte)'\n'];

    private static int IndexOfEmptyLine(byte[] bytes)
    {
        for (int i = 0; i + 3 < bytes.Length; i++)
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
        return -1;
    }

    private static int IndexOfFrom(byte[] bytes, byte[] pattern, int from)
    {
        if (pattern.Length == 0 || bytes.Length < pattern.Length) return -1;
        for (int i = Math.Max(0, from); i <= bytes.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && bytes[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    private static byte[] FromBase64(byte[] bytes)
    {
        var text = new List<char>(bytes.Length);
        foreach (var b in bytes)
            if (b is not (0x20 or 0x09 or 0x0D or 0x0A)) text.Add((char)b);
        try { return Convert.FromBase64String(new string([.. text])); }
        catch (FormatException) { return bytes; }
    }

    // "=41" becomes 'A'; a "=" at a line's end is the sender's line break, and is dropped.
    private static byte[] QuotedPrintable(byte[] bytes)
    {
        var result = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != '=')
            {
                result.Add(bytes[i]);
                continue;
            }
            if (i + 1 < bytes.Length && bytes[i + 1] == '\r' && i + 2 < bytes.Length && bytes[i + 2] == '\n') { i += 2; continue; }
            if (bytes[i + 1] == '\n') { i += 1; continue; }
            if (i + 2 < bytes.Length && Hex(bytes[i + 1]) >= 0 && Hex(bytes[i + 2]) >= 0)
            {
                result.Add((byte)(Hex(bytes[i + 1]) * 16 + Hex(bytes[i + 2])));
                i += 2;
            }
            else result.Add((byte)'=');
        }
        return [.. result];
    }

    // The bytes as their charset says; a charset .NET does not know falls back to UTF-8.
    private static string Decode(byte[] bytes, string charset)
    {
        if (bytes.Length == 0) return "";
        try { return Encoding.GetEncoding(charset.Length > 0 ? charset : "utf-8").GetString(bytes); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes); }
    }

    private static string StripTags(string html) => Collapse(Tags().Replace(html, " ")
        .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'"));

    private static string Collapse(string text)
    {
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text;
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    // A header value as its sender wrote it: "=?UTF-8?B?...?=" and "=?GBK?Q?...?=" are decoded.
    internal static string Decode(string value)
    {
        if (!value.Contains("=?", StringComparison.Ordinal)) return value;
        try
        {
            return EncodedWord().Replace(value, match =>
            {
                string charset = match.Groups[1].Value.Split('*')[0];
                string text = match.Groups[3].Value;
                Encoding encoding;
                try { encoding = Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = Encoding.UTF8; }
                try
                {
                    return match.Groups[2].Value.ToUpperInvariant() switch
                    {
                        "B" => encoding.GetString(Convert.FromBase64String(text)),
                        _ => encoding.GetString(Unquote(Encoding.ASCII.GetBytes(text.Replace('_', ' '))))
                    };
                }
                catch (FormatException) { return match.Value; }
            }).Replace("?= =?", "");
        }
        catch (ArgumentException) { return value; }
    }

    private static byte[] Unquote(byte[] text)
    {
        var result = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length && Hex(text[i + 1]) is int high && high >= 0 && Hex(text[i + 2]) is int low && low >= 0)
            {
                result.Add((byte)(high * 16 + low)); i += 2;
            }
            else result.Add(text[i]);
        }
        return [.. result];
    }

    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => -1
    };

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=")]
    private static partial Regex EncodedWord();

    // One IMAP session: numbered commands, responses read up to the tag that answered them, literals included.
    private sealed class Session(SslStream stream)
    {
        private readonly List<byte> _buffer = [];
        private int _counter;
        // The literal blocks the last command's answer carried, raw: a mail body is decoded with its own charset,
        // not with the UTF-8 the response text is read as.
        public List<byte[]> Literals { get; } = [];

        public async Task<string> CommandAsync(string command, CancellationToken token)
        {
            Literals.Clear();
            string tag = "A" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bytes = Encoding.UTF8.GetBytes($"{tag} {command}\r\n");
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);
            return await ReadResponseAsync(tag, token);
        }

        private async Task<string> ReadResponseAsync(string tag, CancellationToken token)
        {
            var text = new StringBuilder();
            while (true)
            {
                string line = await ReadLineAsync(token);
                if (line.Length == 0) continue;
                int literal = LiteralLength(line);
                text.AppendLine(line);
                if (literal > 0)
                {
                    var block = await ReadBlockAsync(literal, token);
                    Literals.Add(block);
                    text.Append(Encoding.UTF8.GetString(block));
                }
                if (line.StartsWith(tag + " ", StringComparison.Ordinal)) return text.ToString();
            }
        }

        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            while (true)
            {
                for (int i = 0; i < _buffer.Count - 1; i++)
                {
                    if (_buffer[i] != '\r' || _buffer[i + 1] != '\n') continue;
                    string line = Encoding.UTF8.GetString([.. _buffer.GetRange(0, i)]);
                    _buffer.RemoveRange(0, i + 2);
                    return line;
                }
                await FillAsync(token);
            }
        }

        private async Task<byte[]> ReadBlockAsync(int length, CancellationToken token)
        {
            while (_buffer.Count < length) await FillAsync(token);
            var block = _buffer.GetRange(0, length).ToArray();
            _buffer.RemoveRange(0, length);
            return block;
        }

        private async Task FillAsync(CancellationToken token)
        {
            var chunk = new byte[8192];
            int read = await stream.ReadAsync(chunk, token);
            if (read <= 0) throw new IOException("the mailbox closed the connection");
            _buffer.AddRange(chunk.AsSpan(0, read).ToArray());
        }

        private static int LiteralLength(string line)
        {
            int open = line.LastIndexOf('{');
            int close = line.LastIndexOf('}');
            if (open < 0 || close != line.Length - 1 || close - open is < 2 or > 12) return 0;
            return int.TryParse(line[(open + 1)..close], out int length) ? length : 0;
        }
    }
}

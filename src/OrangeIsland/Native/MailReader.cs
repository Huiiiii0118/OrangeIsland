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
            // The whole MIME entity is asked for, headers and body together. BODY[TEXT] alone drops the top-level
            // Content-Type and Content-Transfer-Encoding, without which a base64 or quoted-printable body cannot be
            // decoded. Marketing mail often places a large inline image ahead of its text; reading in slices and
            // stopping as soon as a readable body turns up keeps that image from pushing the text out of the budget.
            string text = "";
            try
            {
                var mime = new List<byte>();
                for (int start = 0; start < MaxMimeBytes; start += SliceBytes)
                {
                    int end = Math.Min(start + SliceBytes, MaxMimeBytes) - 1;
                    await session.CommandAsync($"UID FETCH {uid} (BODY.PEEK[]<{start}.{end}>)", timeout.Token);
                    var slice = session.Literals.FirstOrDefault([]);
                    if (slice.Length == 0) break;
                    mime.AddRange(slice);
                    text = Body([.. mime]);
                    // A slice shorter than asked means the mail ended here. Otherwise, once a part decodes to readable
                    // words there is no reason to pull more of a mail whose rest is likely an image or an attachment.
                    if (slice.Length < SliceBytes || Quality(text) >= GoodBodyScore) break;
                }
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

    // The readable text of a message body: of every text part in the MIME tree, the one that decodes to the most
    // readable words wins. A marketing mail often puts an inline image or a short plain-text stub ahead of the real
    // HTML body, so taking the first part loses the message; each part is decoded and scored, and the fullest, cleanest
    // text is kept. Nothing here marks the message read on the server.
    internal static string Body(byte[] bytes) => Cap(Best(bytes).Text);

    // The best decoded part and its quality. A caller reading a large mail in slices uses the score to tell whether it
    // has reached readable text yet or must read on: a big inline image can sit ahead of the real message body.
    private static (string Text, double Score) Best(byte[] bytes)
    {
        var candidates = new List<string>();
        Collect(bytes, candidates, 0);
        string best = "";
        double bestScore = double.NegativeInfinity;
        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0) continue;
            double score = Quality(candidate);
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return (best.Trim(), bestScore);
    }

    private const int BodyLimit = 8000;

    // How much of a mail's MIME entity is read to find its text: 256 KB a slice, up to 1 MB. A short mail costs one
    // small slice; the rest is only pulled when an inline image or attachment is crowding the body out of the first.
    private const int SliceBytes = 262144, MaxMimeBytes = 1048576;
    // A decoded part scoring this high reads like a real message (tens of readable words), so the slice loop can stop.
    private const double GoodBodyScore = 120;

    private static string Cap(string text) => text.Length > BodyLimit ? text[..BodyLimit] + "…" : text;

    // A MIME tree is shallow; the guard only stops a malformed message from recursing without end.
    private const int MaxDepth = 8;

    // Decode every text part into a candidate. Images, audio, files and explicit attachments are skipped: decoding a
    // binary part only yields replacement characters, which would otherwise crowd out the real message.
    private static void Collect(byte[] bytes, List<string> found, int depth)
    {
        if (depth > MaxDepth || bytes.Length == 0) return;
        var (type, charset, boundary, transfer, content, attachment) = Part(bytes);
        if (attachment || IsBinary(type)) return;
        if (boundary.Length > 0)
        {
            foreach (var piece in Parts(content, boundary)) Collect(piece, found, depth + 1);
            return;
        }
        byte[] decoded = transfer switch
        {
            "base64" => FromBase64(content),
            "quoted-printable" => QuotedPrintable(content),
            _ => content
        };
        string result = Decode(decoded, charset);
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase)) result = StripTags(result);
        result = result.Trim();
        if (result.Length > 0) found.Add(result);
    }

    // Media types that are never the message text. text/* and multipart/* are kept; so is an unlabelled part (content
    // type defaults to text/plain), which a few senders produce.
    private static bool IsBinary(string type)
    {
        string t = type.Trim().ToLowerInvariant();
        return t.Length > 0 && !t.StartsWith("text/") && !t.StartsWith("multipart/") && !t.StartsWith("message/");
    }

    // How much a decoded part reads like a real message: readable words count, decoding damage counts against it. This
    // lets a full HTML body outrank a one-line plain-text stub, and a clean decode outrank binary or half-decoded noise.
    private static double Quality(string text)
    {
        int cjk = 0, readable = 0, damaged = 0;
        foreach (char c in text)
        {
            if (c == '\uFFFD') damaged++;
            else if (c is >= '\u4e00' and <= '\u9fff') cjk++;
            else if (char.IsLetterOrDigit(c)) readable++;
            else if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) damaged++;
        }
        double score = cjk * 3.0 + readable - damaged * 12.0;
        // Leftover quoted-printable escapes or HTML tags mean the part was not fully decoded or stripped.
        score -= Escapes().Matches(text).Count * 6.0;
        score -= Tags().Matches(text).Count * 8.0;
        return score;
    }

    // A part's headers, its content, and the facts the decoding needs. A header may fold onto lines starting with a
    // space or tab (a long Content-Type with its boundary on the next line), and the line ending may be CRLF or a bare
    // LF; both are normalised before the parameters are read.
    private static (string Type, string Charset, string Boundary, string Transfer, byte[] Content, bool Attachment) Part(byte[] bytes)
    {
        var (headEnd, bodyStart) = SplitHeader(bytes);
        byte[] head = headEnd >= 0 ? bytes[..headEnd] : bytes;
        byte[] content = bodyStart >= 0 ? bytes[bodyStart..] : [];
        string text = Unfold(Encoding.Latin1.GetString(head));
        // The media type comes first, its parameters ("; charset=", "; boundary=") follow. Read the whole Content-Type
        // value once, then pick parameters out of it: looking for "charset=" right after the colon never matches a
        // real header such as "text/plain; charset=GBK".
        string contentType = Match(text, @"(?im)^content-type\s*:\s*([^\r\n]+)") ?? "text/plain";
        string Type() => contentType.Split(';')[0].Trim();
        string Param(string name) => Match(contentType, $@"(?i)(?:^|;)\s*{name}\s*=\s*""?([^"";\r\n]+)""?") ?? "";
        string transfer = (Match(text, @"(?im)^content-transfer-encoding\s*:\s*([^\s;\r\n]+)") ?? "").ToLowerInvariant();
        string disposition = (Match(text, @"(?im)^content-disposition\s*:\s*([^\r\n;]+)") ?? "").Trim().ToLowerInvariant();
        return (Type(), Param("charset"), Param("boundary"), transfer, content, disposition is "attachment" or "inline");
    }

    // Join folded header lines ("\r\n " / "\n\t") into one line each, so a parameter split across lines is found.
    private static string Unfold(string header)
    {
        if (!header.Contains('\n', StringComparison.Ordinal)) return header;
        var lines = header.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var joined = new StringBuilder(header.Length);
        foreach (var line in lines)
        {
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                // A continuation line: drop its leading whitespace and append to the previous value.
                if (joined.Length > 0) joined.Append(' ');
                joined.Append(line.TrimStart(' ', '\t'));
            }
            else
            {
                if (joined.Length > 0) joined.Append('\n');
                joined.Append(line);
            }
        }
        return joined.ToString();
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : match.Success ? match.Value : null;
    }

    // The pieces of a multipart body: everything between the boundary lines, decoded by the caller. Boundary lines end
    // in CRLF or a bare LF, and a piece ends just before the CRLF/LF that precedes the next boundary.
    private static List<byte[]> Parts(byte[] content, string boundary)
    {
        var result = new List<byte[]>();
        byte[] marker = Encoding.Latin1.GetBytes("--" + boundary);
        int start = -1, at = 0;
        while (at <= content.Length)
        {
            int line = IndexOfFrom(content, marker, at);
            if (line < 0) { if (start >= 0) result.Add(content[start..]); break; }
            // A boundary marker only counts at the start of a line, not inside base64 or text.
            if (line > 0 && content[line - 1] != (byte)'\n') { at = line + marker.Length; continue; }
            if (start >= 0 && line >= start + 2)
            {
                int end = line;
                if (end >= 2 && content[end - 2] == (byte)'\r' && content[end - 1] == (byte)'\n') end -= 2;
                else if (end >= 1 && content[end - 1] == (byte)'\n') end -= 1;
                result.Add(content[start..end]);
            }
            int after = line + marker.Length;
            bool done = after + 1 < content.Length && content[after] == (byte)'-' && content[after + 1] == (byte)'-';
            int eol = IndexOfFrom(content, [(byte)'\n'], after);
            if (eol < 0) break;
            at = eol + 1;
            if (done) break;
            start = at;
        }
        return result;
    }

    // Where a part's headers end and its body begins, across the four blank-line forms (CRLF CRLF, LF LF, and the two
    // mixed ones). The head excludes the terminating line break; the body starts just past the blank line.
    private static (int HeadEnd, int BodyStart) SplitHeader(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
            {
                int b = i + 2;                                     // just past the first CRLF
                if (b < bytes.Length && bytes[b] == (byte)'\n') return (i, b + 1);
                if (b + 1 < bytes.Length && bytes[b] == (byte)'\r' && bytes[b + 1] == (byte)'\n') return (i, b + 2);
            }
            else if (bytes[i] == (byte)'\n')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') return (i, i + 2);
                if (i + 2 < bytes.Length && bytes[i + 1] == (byte)'\r' && bytes[i + 2] == (byte)'\n') return (i, i + 3);
            }
        }
        return (-1, -1);
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

    private static string StripTags(string html) => Collapse(
        Tags().Replace(Hidden().Replace(html, " "), " ")
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
            .Replace("&#160;", " ").Replace("&#32;", " ")
            .Replace("\r", " ").Replace("\n", " "));

    private static string Collapse(string text)
    {
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Trim();
    }

    // Tags (including their whole contents) that carry no readable message.
    [GeneratedRegex(@"(?is)<(script|style|head|title)\b[^>]*>.*?</\1>")]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    // A leftover quoted-printable escape ("=E5"): a strong sign a part was shown without being decoded.
    [GeneratedRegex(@"=[0-9A-Fa-f]{2}")]
    private static partial Regex Escapes();

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

using System.Text;
using MimeKit;
using MimeKit.Utils;
using MsgReader.Outlook;

// ---- one-shot MSG -> EML probe (fidelity check) ----
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length < 2) { Console.Error.WriteLine("usage: msgconv <in.msg> <out.eml>"); return 2; }

using var msg = new Storage.Message(args[0], FileAccess.Read);
var mime = Build(msg);

mime.Prepare(EncodingConstraint.SevenBit);   // assigns a valid CTE to every part

var fmt = FormatOptions.Default.Clone();
fmt.International = false;
fmt.NewLineFormat = NewLineFormat.Dos;
using (var fs = File.Create(args[1]))
    mime.WriteTo(fmt, fs);

Report(msg, args[1]);
return 0;

// ------------------------------------------------------------------
static MimeMessage Build(Storage.Message msg)
{
    MimeMessage m;

    // 1) if Outlook preserved the original transport headers, start from those:
    //    load them as a message and keep the whole set (Received, DKIM/ARC, Message-ID,
    //    Thread-Index, X-*, ...), removing only the MIME framing of the old body.
    var raw = msg.TransportMessageHeaders;
    if (!string.IsNullOrWhiteSpace(raw))
    {
        var norm = raw.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n").TrimEnd();
        var bytes = Encoding.UTF8.GetBytes(norm + "\r\n\r\n");
        m = MimeMessage.Load(new MemoryStream(bytes));
        foreach (var h in m.Headers.Where(h =>
                     h.Id is HeaderId.ContentType or HeaderId.ContentTransferEncoding or HeaderId.MimeVersion
                         or HeaderId.ContentDisposition or HeaderId.ContentId or HeaderId.ContentDescription
                         or HeaderId.ContentLocation or HeaderId.ContentMd5 or HeaderId.ContentLanguage
                     || h.Field.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToList())
            m.Headers.Remove(h);
    }
    else
    {
        m = new MimeMessage();
        m.Headers.Remove(HeaderId.From);   // MimeKit pre-creates an empty From
        m.Headers.Remove(HeaderId.To);
    }

    // 2) fill in from the MAPI properties only what is missing
    if (string.IsNullOrEmpty(m.Subject) && !string.IsNullOrEmpty(msg.Subject))
        m.Subject = msg.Subject;

    if (m.From.Count == 0) AddAddr(m.From, msg.Sender?.DisplayName, msg.Sender?.Email);
    if (m.From.Count == 0 && msg.SenderRepresenting != null)
        AddAddr(m.From, msg.SenderRepresenting.DisplayName, msg.SenderRepresenting.Email);

    if (m.To.Count == 0) FillRecipients(m.To, msg, RecipientType.To);
    if (m.Cc.Count == 0) FillRecipients(m.Cc, msg, RecipientType.Cc);
    if (m.Bcc.Count == 0) FillRecipients(m.Bcc, msg, RecipientType.Bcc);

    if (!m.Headers.Contains(HeaderId.Date))
        m.Date = msg.SentOn ?? msg.ReceivedOn ?? msg.CreationTime ?? DateTimeOffset.Now;

    if (!m.Headers.Contains(HeaderId.MessageId))
        m.MessageId = MimeUtils.GenerateMessageId();

    // 3) body + attachments, rebuilt from the MSG
    var html = NullIfEmpty(msg.BodyHtml);
    var text = NullIfEmpty(msg.BodyText);
    var rtf = NullIfEmpty(msg.BodyRtf);

    var bb = new BodyBuilder();
    if (text != null) bb.TextBody = text;
    if (html != null) bb.HtmlBody = html;
    if (text == null && html == null && rtf != null)
    {
        bb.TextBody = "[RTF-only message: body preserved as application/rtf attachment]";
        var rtfPart = new MimePart("application", "rtf")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(rtf))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "body.rtf" },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "body.rtf"
        };
        bb.Attachments.Add(rtfPart);
    }

    foreach (var a in msg.Attachments)
    {
        switch (a)
        {
            case Storage.Attachment att:
                AddAttachment(bb, att, html);
                break;
            case Storage.Message sub:
                bb.Attachments.Add(new MessagePart
                {
                    Message = Build(sub),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
                    {
                        FileName = Safe(sub.FileName ?? (sub.Subject ?? "message") + ".eml")
                    }
                });
                break;
        }
    }

    m.Body = bb.ToMessageBody();
    return m;
}

static void AddAttachment(BodyBuilder bb, Storage.Attachment att, string html)
{
    var data = att.Data ?? Array.Empty<byte>();
    var name = Safe(att.FileName ?? "attachment.bin");
    var ct = ParseCt(att.MimeType, name);
    var cid = att.ContentId?.Trim('<', '>');

    var referenced = cid != null && html != null &&
                     html.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase);
    var inline = referenced || (att.IsInline && cid != null);

    var entity = inline
        ? bb.LinkedResources.Add(name, data, ct)
        : bb.Attachments.Add(name, data, ct);

    if (cid != null) entity.ContentId = cid;
    if (entity is MimePart mp)
    {
        mp.ContentDisposition = new ContentDisposition(inline ? ContentDisposition.Inline : ContentDisposition.Attachment)
        {
            FileName = name,
            CreationDate = att.CreationTime,
            ModificationDate = att.LastModificationTime
        };
        mp.ContentTransferEncoding = ContentEncoding.Base64;
    }
}

static ContentType ParseCt(string mimeType, string fileName)
{
    if (!string.IsNullOrWhiteSpace(mimeType) && ContentType.TryParse(mimeType, out var ct)) return ct;
    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    return ext switch
    {
        ".pdf" => new ContentType("application", "pdf"),
        ".png" => new ContentType("image", "png"),
        ".jpg" or ".jpeg" => new ContentType("image", "jpeg"),
        ".gif" => new ContentType("image", "gif"),
        ".txt" => new ContentType("text", "plain"),
        ".htm" or ".html" => new ContentType("text", "html"),
        ".msg" => new ContentType("application", "vnd.ms-outlook"),
        _ => new ContentType("application", "octet-stream")
    };
}

static void FillRecipients(InternetAddressList list, Storage.Message msg, RecipientType type)
{
    foreach (var r in msg.Recipients.Where(r => r.Type == type))
        AddAddr(list, r.DisplayName, r.Email);
}

static void AddAddr(InternetAddressList list, string display, string email)
{
    if (string.IsNullOrWhiteSpace(email))
    {
        if (string.IsNullOrWhiteSpace(display)) return;
        // Exchange/X500 only: keep the name, no fake address
        list.Add(new MailboxAddress(display, "unknown@invalid"));
        return;
    }
    list.Add(new MailboxAddress(string.IsNullOrWhiteSpace(display) ? email : display, email));
}

static string Safe(string n)
{
    foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
    return n.Trim();
}
static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

// ------------------------------------------------------------------
static void Report(Storage.Message msg, string eml)
{
    void H(string s) => Console.WriteLine("\n== " + s + " " + new string('=', Math.Max(0, 60 - s.Length)));

    H("MSG (source)");
    Console.WriteLine($"Type                : {msg.Type}");
    Console.WriteLine($"Subject             : {msg.Subject}");
    Console.WriteLine($"Sender              : {msg.Sender?.DisplayName} <{msg.Sender?.Email}> raw={msg.Sender?.Raw}");
    Console.WriteLine($"SentOn / ReceivedOn : {msg.SentOn:o} / {msg.ReceivedOn:o}");
    Console.WriteLine($"BodyText            : {msg.BodyText?.Length ?? -1} chars");
    Console.WriteLine($"BodyHtml            : {msg.BodyHtml?.Length ?? -1} chars");
    Console.WriteLine($"BodyRtf             : {msg.BodyRtf?.Length ?? -1} chars");
    Console.WriteLine($"TransportHeaders    : {msg.TransportMessageHeaders?.Length ?? -1} chars");
    Console.WriteLine($"Recipients          : {msg.Recipients.Count}");
    foreach (var r in msg.Recipients)
        Console.WriteLine($"   [{r.Type}] {r.DisplayName} <{r.Email}> addrType={r.AddressType}");
    Console.WriteLine($"Attachments         : {msg.Attachments.Count}");
    foreach (var a in msg.Attachments)
        Console.WriteLine(a switch
        {
            Storage.Attachment at => $"   FILE  {at.FileName}  {at.Data?.Length ?? 0} B  mime={at.MimeType}  cid={at.ContentId}  inline={at.IsInline}  hidden={at.Hidden}  pos={at.RenderingPosition}",
            Storage.Message sm => $"   EMBED .msg  subject={sm.Subject}  attachments={sm.Attachments.Count}",
            _ => "   ??? " + a.GetType()
        });

    H("EML (re-read + validation)");
    var fi = new FileInfo(eml);
    Console.WriteLine($"File                : {fi.Name}  {fi.Length} B");
    var re = MimeMessage.Load(eml);
    Console.WriteLine($"Subject             : {re.Subject}");
    Console.WriteLine($"From                : {re.From}");
    Console.WriteLine($"To                  : {re.To}");
    Console.WriteLine($"Cc                  : {re.Cc}");
    Console.WriteLine($"Date                : {re.Date:o}");
    Console.WriteLine($"Message-Id          : {re.MessageId}");
    Console.WriteLine($"TextBody            : {re.TextBody?.Length ?? -1} chars");
    Console.WriteLine($"HtmlBody            : {re.HtmlBody?.Length ?? -1} chars");
    Console.WriteLine($"Attachments         : {re.Attachments.Count()}");
    Console.WriteLine($"BodyParts           : {re.BodyParts.Count()}");

    Console.WriteLine("\nMIME structure:");
    Dump(re.Body, 1);

    Console.WriteLine("\nHeaders of the produced EML:");
    foreach (var h in re.Headers) Console.WriteLine($"   {h.Field}: {Trunc(h.Value)}");

    // CID cross-check
    var htmlOut = re.HtmlBody;
    if (htmlOut != null)
    {
        var cids = System.Text.RegularExpressions.Regex.Matches(htmlOut, @"cid:([^""'\s>\)]+)")
            .Select(x => x.Groups[1].Value).Distinct().ToList();
        var present = re.BodyParts.OfType<MimePart>().Select(p => p.ContentId?.Trim('<', '>')).Where(x => x != null).ToHashSet();
        Console.WriteLine($"\nCIDs referenced in HTML: {cids.Count}");
        foreach (var c in cids)
            Console.WriteLine($"   cid:{c} -> {(present.Contains(c) ? "OK resolved" : "!! MISSING")}");
    }
}

static void Dump(MimeEntity e, int lvl)
{
    var pad = new string(' ', lvl * 3);
    var extra = e switch
    {
        MimePart p => $" name={p.FileName} enc={p.ContentTransferEncoding} cid={p.ContentId} disp={p.ContentDisposition?.Disposition} size={SizeOf(p)}",
        _ => ""
    };
    Console.WriteLine($"{pad}{e.ContentType}{extra}");
    if (e is Multipart mult) foreach (var c in mult) Dump(c, lvl + 1);
    if (e is MessagePart msgp && msgp.Message != null) Dump(msgp.Message.Body, lvl + 1);
}

static long SizeOf(MimePart p)
{
    if (p.Content == null) return 0;
    using var ms = new MemoryStream();
    p.Content.DecodeTo(ms);
    return ms.Length;
}
static string Trunc(string s) => s == null ? "" : (s.Length > 160 ? s[..160] + " …" : s).Replace("\r", "").Replace("\n", " ");

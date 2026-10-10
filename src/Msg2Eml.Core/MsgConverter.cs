using System.Text;
using MimeKit;
using MimeKit.Utils;
using MsgReader.Outlook;

namespace Msg2Eml.Core;

/// <summary>
/// Converts an Outlook <c>.msg</c> file into a MIME message (<c>.eml</c>).
/// </summary>
/// <remarks>
/// Strategy (plan, sections 1-3): if Outlook preserved the original transport headers,
/// the EML starts from those and only the body is rebuilt from the MAPI properties. The MAPI
/// properties are the fallback for messages without transport headers (drafts, Sent Items).
/// The conversion happens entirely in memory.
/// </remarks>
public sealed class MsgConverter
{
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly string[] UnsupportedTypePrefixes =
        ["Appointment", "Task", "Contact", "StickyNote", "Schedule", "WorkFlow", "Workflow"];

    private readonly ConversionOptions _options;

    static MsgConverter()
    {
        EnsureEncodings();
    }

    /// <summary>
    /// Registers the Windows code pages (requirement 1 of the plan). It must be called explicitly
    /// at application startup: without it, on Linux, MsgReader.Rtf fails with
    /// "'Windows-1252' is not a supported encoding name" on every RTF-encapsulated body.
    /// The static constructor calls it as a safety net, but that only fires on first use of the converter.
    /// </summary>
    public static void EnsureEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MsgConverter(ConversionOptions? options = null)
    {
        _options = options ?? new ConversionOptions();
    }

    /// <summary>Converts the content of <paramref name="input"/> and returns the EML bytes.</summary>
    /// <exception cref="ConversionException">For every application-level error; see <see cref="ConversionErrorCode"/>.</exception>
    public ConversionResult Convert(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var data = ReadAll(input, cancellationToken);
        if (data.Length < OleMagic.Length || !data.AsSpan(0, OleMagic.Length).SequenceEqual(OleMagic))
            throw new ConversionException(ConversionErrorCode.InvalidMsgFile,
                "The input is not an OLE compound file (.msg).");

        Storage.Message msg;
        try
        {
            msg = new Storage.Message(new MemoryStream(data, writable: false));
        }
        catch (Exception ex)
        {
            throw new ConversionException(ConversionErrorCode.InvalidMsgFile,
                "The input could not be opened as an Outlook message.", ex);
        }

        using (msg)
        {
            EnsureSupportedType(msg);

            var ctx = new BuildContext();
            MimeMessage mime;
            try
            {
                mime = Build(msg, depth: 0, ctx, cancellationToken);
            }
            catch (ConversionException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ConversionException(ConversionErrorCode.ConversionError,
                    "The message could not be converted.", ex);
            }

            byte[] eml;
            try
            {
                // Requirement 3: MimeKit does not assign a Content-Transfer-Encoding on its own.
                mime.Prepare(EncodingConstraint.SevenBit);

                // Requirement 4: CRLF and 7-bit headers.
                var fmt = FormatOptions.Default.Clone();
                fmt.International = false;
                fmt.NewLineFormat = NewLineFormat.Dos;

                using var ms = new MemoryStream();
                mime.WriteTo(fmt, ms, cancellationToken);
                eml = ms.ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ConversionException(ConversionErrorCode.MimeGenerationError,
                    "The MIME message could not be serialized.", ex);
            }

            // Requirement 5: re-parse self-check.
            try
            {
                using var check = new MemoryStream(eml, writable: false);
                _ = MimeMessage.Load(check, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new ConversionException(ConversionErrorCode.MimeGenerationError,
                    "The generated EML failed the re-parse self-check.", ex);
            }

            return new ConversionResult(eml, ctx.ToStats());
        }
    }

    // ------------------------------------------------------------------ building

    private MimeMessage Build(Storage.Message msg, int depth, BuildContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (depth > _options.MaxNestingDepth)
            throw new ConversionException(ConversionErrorCode.NestingTooDeep,
                $"Nested messages exceed the maximum depth of {_options.MaxNestingDepth}.");
        ctx.MaxNestedDepth = Math.Max(ctx.MaxNestedDepth, depth);

        // 1) Headers: passthrough of the original transport headers, loaded as a message
        //    (requirement 2: never `new MimeMessage()` + copy, which pre-creates an empty From).
        var raw = msg.TransportMessageHeaders;
        var hasTransport = !string.IsNullOrWhiteSpace(raw);
        if (depth == 0) ctx.HasTransportHeaders = hasTransport;

        var m = LoadHeaders(hasTransport ? raw! : string.Empty);
        var originalContentType = m.Headers[HeaderId.ContentType];
        RemoveBodyHeaders(m);

        // 2) MAPI properties only for what is missing.
        if (string.IsNullOrEmpty(m.Subject) && !string.IsNullOrEmpty(msg.Subject))
            m.Subject = msg.Subject;

        if (m.From.Count == 0) AddAddress(m.From, msg.Sender?.DisplayName, msg.Sender?.Email, ctx);
        if (m.From.Count == 0 && msg.SenderRepresenting is { } rep)
            AddAddress(m.From, rep.DisplayName, rep.Email, ctx);

        if (m.To.Count == 0) FillRecipients(m.To, msg, RecipientType.To, ctx);
        if (m.Cc.Count == 0) FillRecipients(m.Cc, msg, RecipientType.Cc, ctx);
        if (m.Bcc.Count == 0) FillRecipients(m.Bcc, msg, RecipientType.Bcc, ctx);

        if (!m.Headers.Contains(HeaderId.Date))
        {
            // Requirement 8: never DateTimeOffset.Now. If no date is available, the header stays absent.
            var date = msg.SentOn ?? msg.ReceivedOn ?? msg.CreationTime;
            if (date is { } d) m.Date = d;
        }

        if (!m.Headers.Contains(HeaderId.MessageId))
            m.MessageId = MimeUtils.GenerateMessageId(_options.GeneratedMessageIdDomain);

        // 3) Body and attachments, rebuilt from the MSG.
        var attachments = msg.Attachments.ToList();

        if (TryBuildSmimeOpaque(originalContentType, attachments, msg, ctx, depth, out var smimeBody))
        {
            m.Body = smimeBody;
            return m;
        }

        var html = NullIfEmpty(msg.BodyHtml);
        var text = NullIfEmpty(msg.BodyText);
        var rtf = NullIfEmpty(msg.BodyRtf);

        var bb = new BodyBuilder();
        if (text != null) { bb.TextBody = text; ctx.AddDecoded(text.Length, _options); }
        if (html != null) { bb.HtmlBody = html; ctx.AddDecoded(html.Length, _options); }

        var kind = (text, html) switch
        {
            (not null, not null) => BodyKind.TextAndHtml,
            (null, not null) => BodyKind.Html,
            (not null, null) => BodyKind.Text,
            _ => BodyKind.None,
        };

        if (text == null && html == null && rtf != null)
        {
            // RTF-only policy: the body is preserved as an attachment, not discarded.
            kind = BodyKind.RtfOnly;
            bb.TextBody = "[RTF-only message: the original body is preserved as the attachment body.rtf]";
            var rtfBytes = Encoding.UTF8.GetBytes(rtf);
            ctx.AddDecoded(rtfBytes.Length, _options);
            bb.Attachments.Add(new MimePart("application", "rtf")
            {
                Content = new MimeContent(new MemoryStream(rtfBytes, writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "body.rtf" },
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = "body.rtf",
            });
        }
        if (depth == 0) ctx.BodyKind = kind;

        foreach (var item in attachments)
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case Storage.Attachment att:
                    AddAttachment(bb, att, html, ctx);
                    break;

                case Storage.Message sub:
                    ctx.NestedMessages++;
                    var subName = ChangeExtension(SafeFileName(sub.FileName ?? sub.Subject ?? "message"), ".eml");
                    bb.Attachments.Add(new MessagePart
                    {
                        Message = Build(sub, depth + 1, ctx, ct),
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = subName },
                    });
                    break;
            }
        }

        m.Body = bb.ToMessageBody();
        return m;
    }

    private static MimeMessage LoadHeaders(string rawHeaders)
    {
        var norm = rawHeaders.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n").TrimEnd();
        var bytes = Encoding.UTF8.GetBytes(norm + "\r\n\r\n");
        using var ms = new MemoryStream(bytes, writable: false);
        return MimeMessage.Load(ms);
    }

    /// <summary>
    /// Removes the MIME framing of the old body (requirement 9). <c>Content-Language</c> is a
    /// message header and is preserved.
    /// </summary>
    private static void RemoveBodyHeaders(MimeMessage m)
    {
        var toRemove = m.Headers.Where(h =>
                h.Id is HeaderId.ContentType or HeaderId.ContentTransferEncoding or HeaderId.MimeVersion
                    or HeaderId.ContentDisposition or HeaderId.ContentId or HeaderId.ContentDescription
                    or HeaderId.ContentLocation or HeaderId.ContentMd5
                || h.Field.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var h in toRemove) m.Headers.Remove(h);
    }

    /// <summary>
    /// S/MIME policy (plan, section 3): an opaque message (<c>application/pkcs7-mime</c>) is
    /// returned with the <c>smime.p7m</c> as its body, byte-identical, so it stays decryptable and
    /// verifiable. Clear-signed messages (<c>multipart/signed</c>) follow the generic path:
    /// the signature cannot be rebuilt in a verifiable way from a <c>.msg</c>.
    /// </summary>
    private bool TryBuildSmimeOpaque(string? originalContentType, List<object> attachments,
        Storage.Message msg, BuildContext ctx, int depth, out MimeEntity? body)
    {
        body = null;
        if (originalContentType == null) return false;
        if (!ContentType.TryParse(originalContentType, out var ct)) return false;
        if (!ct.MediaType.Equals("application", StringComparison.OrdinalIgnoreCase)) return false;
        if (!(ct.MediaSubtype.Equals("pkcs7-mime", StringComparison.OrdinalIgnoreCase)
              || ct.MediaSubtype.Equals("x-pkcs7-mime", StringComparison.OrdinalIgnoreCase))) return false;

        var p7m = attachments.OfType<Storage.Attachment>()
            .Where(a => (a.FileName ?? "").EndsWith(".p7m", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (p7m.Count != 1 || attachments.Count != 1) return false;

        var data = p7m[0].Data ?? [];
        ctx.AddDecoded(data.Length, _options);
        ctx.Attachments++;
        if (depth == 0) ctx.BodyKind = BodyKind.SmimeOpaque;

        body = new MimePart(ct)
        {
            Content = new MimeContent(new MemoryStream(data, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "smime.p7m" },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "smime.p7m",
        };
        return true;
    }

    private void AddAttachment(BodyBuilder bb, Storage.Attachment att, string? html, BuildContext ctx)
    {
        byte[] data;
        try
        {
            data = att.Data ?? [];
        }
        catch (Exception ex)
        {
            throw new ConversionException(ConversionErrorCode.AttachmentReadError,
                "An attachment could not be read.", ex);
        }
        ctx.AddDecoded(data.Length, _options);

        var name = SafeFileName(att.FileName ?? "attachment.bin");
        var contentType = ParseContentType(att.MimeType, name);
        var cid = att.ContentId?.Trim('<', '>');
        if (string.IsNullOrWhiteSpace(cid)) cid = null;

        // Inline only if the cid is actually referenced in the HTML; otherwise it is a regular attachment,
        // even if Outlook marks it IsInline/Hidden.
        var referenced = cid != null && html != null &&
                         html.Contains("cid:" + cid, StringComparison.OrdinalIgnoreCase);
        var inline = referenced || (att.IsInline && cid != null);

        var entity = inline
            ? bb.LinkedResources.Add(name, data, contentType)
            : bb.Attachments.Add(name, data, contentType);

        if (inline) ctx.InlineAttachments++; else ctx.Attachments++;

        if (cid != null) entity.ContentId = cid;
        if (entity is MimePart part)
        {
            part.ContentDisposition = new ContentDisposition(inline ? ContentDisposition.Inline : ContentDisposition.Attachment)
            {
                FileName = name,
                CreationDate = att.CreationTime,
                ModificationDate = att.LastModificationTime,
            };
            part.ContentTransferEncoding = ContentEncoding.Base64;
        }
    }

    private static ContentType ParseContentType(string? mimeType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) && ContentType.TryParse(mimeType, out var ct)) return ct;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => new ContentType("application", "pdf"),
            ".png" => new ContentType("image", "png"),
            ".jpg" or ".jpeg" => new ContentType("image", "jpeg"),
            ".gif" => new ContentType("image", "gif"),
            ".bmp" => new ContentType("image", "bmp"),
            ".webp" => new ContentType("image", "webp"),
            ".svg" => new ContentType("image", "svg+xml"),
            ".txt" => new ContentType("text", "plain"),
            ".csv" => new ContentType("text", "csv"),
            ".htm" or ".html" => new ContentType("text", "html"),
            ".xml" => new ContentType("application", "xml"),
            ".json" => new ContentType("application", "json"),
            ".zip" => new ContentType("application", "zip"),
            ".doc" => new ContentType("application", "msword"),
            ".docx" => new ContentType("application", "vnd.openxmlformats-officedocument.wordprocessingml.document"),
            ".xls" => new ContentType("application", "vnd.ms-excel"),
            ".xlsx" => new ContentType("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ".ppt" => new ContentType("application", "vnd.ms-powerpoint"),
            ".pptx" => new ContentType("application", "vnd.openxmlformats-officedocument.presentationml.presentation"),
            ".msg" => new ContentType("application", "vnd.ms-outlook"),
            ".eml" => new ContentType("message", "rfc822"),
            ".p7m" => new ContentType("application", "pkcs7-mime"),
            ".p7s" => new ContentType("application", "pkcs7-signature"),
            ".ics" => new ContentType("text", "calendar"),
            ".vcf" => new ContentType("text", "vcard"),
            ".rtf" => new ContentType("application", "rtf"),
            _ => new ContentType("application", "octet-stream"),
        };
    }

    private void FillRecipients(InternetAddressList list, Storage.Message msg, RecipientType type, BuildContext ctx)
    {
        foreach (var r in msg.Recipients.Where(r => r.Type == type))
            AddAddress(list, r.DisplayName, r.Email, ctx);
    }

    private void AddAddress(InternetAddressList list, string? display, string? email, BuildContext ctx)
    {
        var resolved = !string.IsNullOrWhiteSpace(email) && email.Contains('@') && !email.StartsWith('/');
        if (!resolved)
        {
            if (string.IsNullOrWhiteSpace(display)) return;
            // Policy: X500/Exchange-only recipient, name preserved, placeholder address.
            ctx.UnresolvedRecipients++;
            list.Add(new MailboxAddress(display, _options.UnresolvedAddressPlaceholder));
            return;
        }
        list.Add(new MailboxAddress(string.IsNullOrWhiteSpace(display) ? email : display, email));
    }

    private static void EnsureSupportedType(Storage.Message msg)
    {
        var type = msg.Type.ToString();
        if (UnsupportedTypePrefixes.Any(p => type.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            throw new ConversionException(ConversionErrorCode.UnsupportedMessageType,
                $"Message type '{type}' is not an e-mail and cannot be converted.");
    }

    // ------------------------------------------------------------------ utilities

    private static byte[] ReadAll(Stream input, CancellationToken ct)
    {
        if (input is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == ms.Length)
            return seg.Array!.Length == seg.Count ? seg.Array! : ms.ToArray();

        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        ct.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }

    /// <summary>
    /// Safe file name for MIME headers and downloads: path separators and characters invalid on
    /// Windows and Unix are replaced with <c>_</c>, regardless of the system the service runs on.
    /// </summary>
    public static string SafeFileName(string? name, string fallback = "attachment")
    {
        if (string.IsNullOrEmpty(name)) return fallback;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (InvalidFileNameChars.Contains(chars[i]) || char.IsControl(chars[i])) chars[i] = '_';
        var result = new string(chars).Trim().Trim('.').Trim();
        if (result.Length > 200) result = result[..200];
        return result.Length == 0 ? fallback : result;
    }

    private static readonly HashSet<char> InvalidFileNameChars =
        [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static string ChangeExtension(string name, string ext)
    {
        var baseName = name.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return baseName + ext;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed class BuildContext
    {
        public bool HasTransportHeaders;
        public BodyKind BodyKind;
        public int Attachments;
        public int InlineAttachments;
        public int NestedMessages;
        public int MaxNestedDepth;
        public int UnresolvedRecipients;
        public long DecodedBytes;

        public void AddDecoded(long bytes, ConversionOptions options)
        {
            DecodedBytes += bytes;
            if (DecodedBytes > options.MaxDecodedBytes)
                throw new ConversionException(ConversionErrorCode.FileTooLarge,
                    $"Decoded content exceeds the limit of {options.MaxDecodedBytes} bytes.");
        }

        public ConversionStats ToStats() => new()
        {
            HasTransportHeaders = HasTransportHeaders,
            BodyKind = BodyKind,
            Attachments = Attachments,
            InlineAttachments = InlineAttachments,
            NestedMessages = NestedMessages,
            MaxNestedDepth = MaxNestedDepth,
            UnresolvedRecipients = UnresolvedRecipients,
            DecodedBytes = DecodedBytes,
        };
    }
}

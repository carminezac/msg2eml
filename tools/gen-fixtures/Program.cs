// Generates the synthetic .msg fixtures used by the test suite (plan, section 9).
//
//   dotnet run --project tools/gen-fixtures -- <output-dir>
//
// Every fixture is built with MsgKit from invented data: no real names, addresses or
// content. The files are committed so the tests do not depend on MsgKit; re-run this
// tool to regenerate them after changing a case.
using System.Text;
using MsgKit;
using MsgKit.Enums;

if (args.Length != 1) { Console.Error.WriteLine("usage: gen-fixtures <output-dir>"); return 2; }
var outDir = args[0];
Directory.CreateDirectory(outDir);

var sentOn = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

// ------------------------------------------------------------------ helpers

static Sender Sender(string email = "alice.sender@example.org", string name = "Alice Sender") =>
    new(email, name, AddressType.Smtp, MessageFormat.TextAndHtml, true, true);

static void To(Email m, string email, string name, AddressType type = AddressType.Smtp) =>
    m.Recipients.AddTo(email, name, type, MapiObjectType.MAPI_MAILUSER, RecipientRowDisplayType.MessagingUser);

static void Cc(Email m, string email, string name) =>
    m.Recipients.AddCc(email, name, AddressType.Smtp, MapiObjectType.MAPI_MAILUSER, RecipientRowDisplayType.MessagingUser);

static void Bcc(Email m, string email, string name) =>
    m.Recipients.AddBcc(email, name, AddressType.Smtp, MapiObjectType.MAPI_MAILUSER, RecipientRowDisplayType.MessagingUser);

static void Attach(Email m, string fileName, byte[] data, bool inline = false, string? contentId = null) =>
    m.Attachments.Add(new MemoryStream(data), fileName, -1, inline, contentId!);

static string Headers(string subject, string messageId, string contentType, params string[] extra)
{
    var sb = new StringBuilder();
    sb.Append("Received: from mail.example.org (mail.example.org [192.0.2.10])\r\n");
    sb.Append(" by mx.example.net with ESMTPS id abc123; Sun, 15 Mar 2026 10:30:05 +0000\r\n");
    sb.Append("From: Alice Sender <alice.sender@example.org>\r\n");
    sb.Append("To: Bob Recipient <bob.recipient@example.net>\r\n");
    sb.Append("Subject: ").Append(subject).Append("\r\n");
    sb.Append("Thread-Topic: ").Append(subject).Append("\r\n");
    sb.Append("Date: Sun, 15 Mar 2026 10:30:00 +0000\r\n");
    sb.Append("Message-ID: <").Append(messageId).Append(">\r\n");
    sb.Append("Content-Language: en-US\r\n");
    sb.Append("X-Test-Fixture: msg2eml\r\n");
    foreach (var e in extra) sb.Append(e).Append("\r\n");
    sb.Append("MIME-Version: 1.0\r\n");
    sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
    return sb.ToString();
}

// A 1x1 PNG (67 bytes) and a 1x1 GIF, enough to be real images.
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");
var fakePdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

void Save(Email m, string name)
{
    var path = Path.Combine(outDir, name);
    m.Save(path);
    Console.WriteLine($"{name,-32} {new FileInfo(path).Length,7} B");
    m.Dispose();
}

// ------------------------------------------------------------------ fixtures

// 1. Plain text, no transport headers: the MAPI fallback is the main path.
{
    var m = new Email(Sender(), "Plain text message", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    Cc(m, "carol@example.net", "Carol Copy");
    Bcc(m, "dave@example.net", "Dave Blind");
    m.SentOn = sentOn;
    m.BodyText = "Hello Bob,\r\n\r\nthis is a plain text message with accents: è é à ù and a euro sign €.\r\n\r\nRegards,\r\nAlice\r\n";
    Save(m, "plain-text.msg");
}

// 2. Native HTML + text, with transport headers.
{
    var m = new Email(Sender(), "Native HTML message", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyText = "Hello Bob, this is the text alternative. Price: 12,50 €.";
    m.BodyHtml = "<html><body><p>Hello <b>Bob</b>, this is the <i>HTML</i> body. Price: 12,50 &euro;.</p></body></html>";
    m.TransportMessageHeadersText = Headers("Native HTML message", "native-html-0001@example.org",
        "multipart/alternative; boundary=\"b1\"");
    Save(m, "native-html.msg");
}

// 3. Inline images referenced by cid, one image attached but not referenced.
{
    var m = new Email(Sender(), "Inline images", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyText = "Logo and banner are inline images.";
    m.BodyHtml = "<html><body><p>Logo: <img src=\"cid:logo.png@fixture\"> Banner: <img src=\"cid:banner.gif@fixture\"></p></body></html>";
    Attach(m, "logo.png", png, inline: true, contentId: "logo.png@fixture");
    Attach(m, "banner.gif", gif, inline: true, contentId: "banner.gif@fixture");
    Attach(m, "unreferenced.png", png, inline: true, contentId: "unreferenced.png@fixture");
    m.TransportMessageHeadersText = Headers("Inline images", "inline-images-0001@example.org",
        "multipart/related; boundary=\"b2\"; type=\"text/html\"");
    Save(m, "inline-images.msg");
}

// 4. Regular attachments: PDF, Unicode file name, no extension (unknown MIME type), empty file.
{
    var m = new Email(Sender(), "Attachments", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyText = "Three attachments.";
    m.BodyHtml = "<html><body><p>Three attachments.</p></body></html>";
    Attach(m, "report.pdf", fakePdf);
    Attach(m, "relazione-è-ü-日本.txt", Encoding.UTF8.GetBytes("Testo con àccenti.\r\n"));
    Attach(m, "README", Encoding.ASCII.GetBytes("no extension\n"));
    Attach(m, "empty.bin", Array.Empty<byte>());
    Save(m, "attachments.msg");
}

// 5. Unicode subject and body, no transport headers (subject must come from MAPI).
{
    var m = new Email(Sender("zoe.sender@example.org", "Zoë Sénder"), "Oggetto con àccenti, € e 日本語 🙂", false, false, false);
    To(m, "bjorn@example.net", "Björn Ünicode");
    m.SentOn = sentOn;
    m.BodyText = "Corpo con àccenti, € e 日本語 🙂";
    m.BodyHtml = "<html><body><p>Corpo con àccenti, &euro; e 日本語 🙂</p></body></html>";
    Save(m, "unicode.msg");
}

// 6. Exchange (X500) recipient without an SMTP address, no transport headers.
{
    var m = new Email(Sender(), "Exchange recipient", false, false, false);
    To(m, "/o=ExchangeLabs/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=0123456789abcdef-bob", "Bob Exchange", AddressType.Ex);
    Cc(m, "carol@example.net", "Carol Copy");
    m.SentOn = sentOn;
    m.BodyText = "Recipient in EX format.";
    Save(m, "ex-recipient.msg");
}

// 7. HTML only inside RTF (encapsulated), no PR_HTML: the de-encapsulation path.
{
    var m = new Email(Sender(), "RTF encapsulated HTML", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyText = "Hello from RTF-encapsulated HTML. Accents: è à ù €";
    m.BodyRtf = "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\deff0{\\fonttbl{\\f0\\fmodern Courier New;}}" +
                "{\\*\\htmltag1 <html>}{\\*\\htmltag2 <head>}{\\*\\htmltag3 </head>}{\\*\\htmltag50 <body>}" +
                "{\\*\\htmltag96 <p>}\\htmlrtf \\f0\\fs24 \\htmlrtf0 Hello from {\\*\\htmltag84 <b>}RTF-encapsulated{\\*\\htmltag92 </b>} HTML. Accents: \\'e8 \\'e0 \\'f9 \\'80" +
                "{\\*\\htmltag104 </p>}{\\*\\htmltag58 </body>}{\\*\\htmltag4 </html>}}";
    m.BodyRtfCompressed = false;
    m.TransportMessageHeadersText = Headers("RTF encapsulated HTML", "rtf-html-0001@example.org",
        "multipart/alternative; boundary=\"b3\"");
    Save(m, "rtf-encapsulated-html.msg");
}

// 8. RTF only: no text, no HTML, plain (non-encapsulated) RTF.
{
    var m = new Email(Sender(), "RTF only", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyRtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 This body exists only as RTF.\\par}";
    m.BodyRtfCompressed = false;
    Save(m, "rtf-only.msg");
}

// 9. A .msg attached as a plain file (not an embedded message).
{
    var inner = new Email(Sender("inner@example.org", "Inner Sender"), "Inner message", false, false, false);
    To(inner, "bob.recipient@example.net", "Bob Recipient");
    inner.SentOn = sentOn;
    inner.BodyText = "I am the inner message.";
    using var ms = new MemoryStream();
    inner.Save(ms);
    inner.Dispose();

    var m = new Email(Sender(), "Message with .msg attachment", false, false, false);
    To(m, "bob.recipient@example.net", "Bob Recipient");
    m.SentOn = sentOn;
    m.BodyText = "See the attached message.";
    Attach(m, "forwarded.msg", ms.ToArray());
    Save(m, "msg-as-attachment.msg");
}

// 10. Non-mail types: appointment, task, contact.
{
    var a = new Appointment(Sender(), "Project meeting", false)
    {
        MeetingStart = sentOn, MeetingEnd = sentOn.AddHours(1), Location = "Room 1", BodyText = "Agenda.",
    };
    a.Recipients.AddTo("bob.recipient@example.net", "Bob Recipient", AddressType.Smtp, MapiObjectType.MAPI_MAILUSER, RecipientRowDisplayType.MessagingUser);
    a.Save(Path.Combine(outDir, "appointment.msg"));
    Console.WriteLine($"{"appointment.msg",-32} {new FileInfo(Path.Combine(outDir, "appointment.msg")).Length,7} B");

    var t = new MsgKit.Task(Sender(), "Write the report", false) { BodyText = "Due soon.", DueDate = sentOn.AddDays(7) };
    t.Save(Path.Combine(outDir, "task.msg"));
    Console.WriteLine($"{"task.msg",-32} {new FileInfo(Path.Combine(outDir, "task.msg")).Length,7} B");

    var c = new Contact(Sender(), "Bob Recipient", false, false) { GivenName = "Bob", SurName = "Recipient" };
    c.Save(Path.Combine(outDir, "contact.msg"));
    Console.WriteLine($"{"contact.msg",-32} {new FileInfo(Path.Combine(outDir, "contact.msg")).Length,7} B");
}

// 11. Health-check fixture: the smallest useful message (embedded in the web project).
{
    var m = new Email(Sender("health@msg2eml.invalid", "Health Check"), "health", false, false, false);
    To(m, "probe@msg2eml.invalid", "Probe");
    m.SentOn = sentOn;
    m.BodyText = "ok";
    Save(m, "health.msg");
}

return 0;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Msg2Eml.Tests;

/// <summary>
/// Fidelity comparison between a reference EML (e.g. the Outlook Web export of the same
/// mail) and a produced EML. C# port of <c>tools/oneshot-msgconv/compare-eml.py</c>, limited
/// to the criteria that must always hold (plan, section 9).
/// </summary>
public static class EmlFidelity
{
    public sealed record Difference(string Category, string Detail)
    {
        public override string ToString() => $"[{Category}] {Detail}";
    }

    private static readonly string[] ComparedHeaders =
        ["From", "To", "Cc", "Bcc", "Subject", "Date", "Message-ID", "Reply-To", "In-Reply-To", "References", "Thread-Index"];

    public static IReadOnlyList<Difference> Compare(MimeMessage reference, MimeMessage produced)
    {
        var diffs = new List<Difference>();

        foreach (var name in ComparedHeaders)
        {
            var a = Squash(reference.Headers[name]);
            var b = Squash(produced.Headers[name]);
            if (a.Length == 0 && b.Length == 0) continue;
            if (!string.Equals(a, b, StringComparison.Ordinal))
                diffs.Add(new Difference("header", $"{name}: reference='{Trunc(a)}' produced='{Trunc(b)}'"));
        }

        foreach (var ctype in new[] { "text/plain", "text/html" })
        {
            var a = Body(reference, ctype);
            var b = Body(produced, ctype);
            if (a == null && b == null) continue;
            if (a == null || b == null)
            {
                diffs.Add(new Difference("body", $"{ctype} present only in {(a == null ? "produced" : "reference")}"));
                continue;
            }
            if (Visible(a) != Visible(b))
                diffs.Add(new Difference("body", $"{ctype}: visible text differs"));
        }

        var ba = Binaries(reference);
        var bb = Binaries(produced);
        foreach (var key in ba.Keys.Union(bb.Keys).Order())
        {
            ba.TryGetValue(key, out var x);
            bb.TryGetValue(key, out var y);
            if (x != y) diffs.Add(new Difference("attachment", $"{key}: reference={x ?? "missing"} produced={y ?? "missing"}"));
        }

        diffs.AddRange(Validate(produced).Select(d => new Difference("mime", d)));
        return diffs;
    }

    /// <summary>Checks that must hold for any produced EML, with or without a reference.</summary>
    public static IReadOnlyList<string> Validate(MimeMessage produced)
    {
        var problems = new List<string>();

        var html = Body(produced, "text/html");
        if (html != null)
        {
            var present = produced.BodyParts.OfType<MimePart>().Select(p => p.ContentId).Where(c => c != null).ToHashSet();
            foreach (var cid in Cids(html))
                if (!present.Contains(cid)) problems.Add($"cid:{cid} referenced in HTML but no part has that Content-ID");
        }

        foreach (var part in produced.BodyParts.OfType<TextPart>())
            if (part.ContentTransferEncoding == ContentEncoding.Default)
                problems.Add($"{part.ContentType.MimeType} has no Content-Transfer-Encoding");

        return problems;
    }

    public static IEnumerable<string> Cids(string html) =>
        Regex.Matches(html, @"cid:([^""'\s>)]+)").Select(m => m.Groups[1].Value).Distinct();

    public static string? Body(MimeMessage m, string ctype) =>
        m.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.ContentType.MimeType.Equals(ctype, StringComparison.OrdinalIgnoreCase))?.Text;

    /// <summary>Visible text of an HTML or plain body: tags removed, entities decoded, whitespace squashed.</summary>
    public static string Visible(string s)
    {
        s = Regex.Replace(s, @"(?is)<(script|style)\b.*?</\1>", " ");
        s = Regex.Replace(s, @"(?s)<!--.*?-->", " ");
        s = Regex.Replace(s, @"<[^>]+>", " ");
        return Squash(WebUtility.HtmlDecode(s));
    }

    public static string Squash(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static Dictionary<string, string> Binaries(MimeMessage m)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in m.BodyParts.OfType<MimePart>())
        {
            if (part is TextPart && part.FileName == null) continue;
            var key = part.FileName ?? part.ContentId ?? "?";
            if (part.Content == null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            result[key] = $"{ms.Length}B/{Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16]}";
        }
        return result;
    }

    private static string Trunc(string s) => s.Length > 120 ? s[..120] + "…" : s;
}

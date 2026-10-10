using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using Msg2Eml.Core;

namespace Msg2Eml.Tests;

public class MsgConverterTests
{
    private readonly MsgConverter _converter = new();

    [Fact]
    public void RandomBytes_AreRejectedAsInvalidMsgFile()
    {
        var ex = Assert.Throws<ConversionException>(() => _converter.Convert(new MemoryStream(Encoding.ASCII.GetBytes("this is not a msg file"))));
        Assert.Equal(ConversionErrorCode.InvalidMsgFile, ex.Code);
    }

    [Fact]
    public void EmptyStream_IsRejectedAsInvalidMsgFile()
    {
        var ex = Assert.Throws<ConversionException>(() => _converter.Convert(new MemoryStream()));
        Assert.Equal(ConversionErrorCode.InvalidMsgFile, ex.Code);
    }

    [Fact]
    public void EmlRenamedAsMsg_IsRejectedAsInvalidMsgFile()
    {
        var eml = "From: a@b.c\r\nTo: d@e.f\r\nSubject: x\r\n\r\nbody\r\n";
        var ex = Assert.Throws<ConversionException>(() => _converter.Convert(new MemoryStream(Encoding.ASCII.GetBytes(eml))));
        Assert.Equal(ConversionErrorCode.InvalidMsgFile, ex.Code);
    }

    [Fact]
    public void TruncatedOleContainer_IsRejectedAsInvalidMsgFile()
    {
        var bytes = new byte[512];
        Fixtures.OleMagic.CopyTo(bytes, 0);
        var ex = Assert.Throws<ConversionException>(() => _converter.Convert(new MemoryStream(bytes)));
        Assert.Equal(ConversionErrorCode.InvalidMsgFile, ex.Code);
    }

    [Fact]
    public void TruncatedRealSample_IsRejectedWithApplicationError()
    {
        if (Fixtures.FirstSample is not { } path) return;

        var full = File.ReadAllBytes(path);
        var truncated = full[..(full.Length / 3)];
        var ex = Assert.Throws<ConversionException>(() => _converter.Convert(new MemoryStream(truncated)));
        Assert.True(ex.Code is ConversionErrorCode.InvalidMsgFile or ConversionErrorCode.ConversionError
                                                                   or ConversionErrorCode.AttachmentReadError,
            $"unexpected code {ex.Code}");
    }

    [Fact]
    public void RealSamples_ConvertToValidEml()
    {
        var files = Fixtures.SampleMsgFiles;
        if (files.Count == 0) return;

        foreach (var path in files)
        {
            using var fs = File.OpenRead(path);
            var result = _converter.Convert(fs);

            Assert.NotEmpty(result.Eml);

            using var ms = new MemoryStream(result.Eml);
            var mime = MimeMessage.Load(ms);

            Assert.False(string.IsNullOrEmpty(mime.MessageId), $"{Path.GetFileName(path)}: Message-ID missing");
            Assert.True(mime.From.Count > 0, $"{Path.GetFileName(path)}: From missing");
            Assert.True(mime.Headers.Contains(HeaderId.Date), $"{Path.GetFileName(path)}: Date missing");
            Assert.True(mime.Headers.Count(h => h.Id == HeaderId.From) == 1, $"{Path.GetFileName(path)}: duplicated From");
            Assert.True(mime.Headers.Count(h => h.Id == HeaderId.Date) == 1, $"{Path.GetFileName(path)}: duplicated Date");

            // CRLF and CTE: no line longer than 998 bytes and no bare 8-bit byte.
            Assert.DoesNotContain((byte)0x80, result.Eml.Where(b => b >= 0x80).Take(1));
            AssertCrlfOnly(result.Eml, Path.GetFileName(path));

            // Every cid referenced in the HTML must exist as a part.
            if (mime.HtmlBody is { } html)
            {
                var cids = Regex.Matches(html, @"cid:([^""'\s>)]+)").Select(x => x.Groups[1].Value).Distinct();
                var present = mime.BodyParts.OfType<MimePart>().Select(p => p.ContentId).Where(x => x != null).ToHashSet();
                foreach (var cid in cids)
                    Assert.True(present.Contains(cid), $"{Path.GetFileName(path)}: cid {cid} not resolved");
            }

            // Text parts: explicit CTE.
            foreach (var part in mime.BodyParts.OfType<TextPart>())
                Assert.NotEqual(ContentEncoding.Default, part.ContentTransferEncoding);
        }
    }

    [Fact]
    public void NestingLimit_IsEnforced()
    {
        if (Fixtures.FirstSample is not { } path) return;

        // With a maximum depth of -1 even the main message (depth 0) exceeds the limit.
        var strict = new MsgConverter(new ConversionOptions { MaxNestingDepth = -1 });
        using var fs = File.OpenRead(path);
        var ex = Assert.Throws<ConversionException>(() => strict.Convert(fs));
        Assert.Equal(ConversionErrorCode.NestingTooDeep, ex.Code);
    }

    [Fact]
    public void DecodedBytesLimit_IsEnforced()
    {
        if (Fixtures.FirstSample is not { } path) return;

        var strict = new MsgConverter(new ConversionOptions { MaxDecodedBytes = 10 });
        using var fs = File.OpenRead(path);
        var ex = Assert.Throws<ConversionException>(() => strict.Convert(fs));
        Assert.Equal(ConversionErrorCode.FileTooLarge, ex.Code);
    }

    [Theory]
    [InlineData("a/b\\c:d.pdf", "a_b_c_d.pdf")]
    [InlineData("  ..hidden..  ", "hidden")]
    [InlineData("", "attachment")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    public void SafeFileName_StripsDangerousCharacters(string input, string expected)
    {
        Assert.Equal(expected, MsgConverter.SafeFileName(input));
    }

    private static void AssertCrlfOnly(byte[] eml, string name)
    {
        for (var i = 0; i < eml.Length; i++)
        {
            if (eml[i] == (byte)'\n')
                Assert.True(i > 0 && eml[i - 1] == (byte)'\r', $"{name}: bare LF at offset {i}");
            if (eml[i] == (byte)'\r')
                Assert.True(i + 1 < eml.Length && eml[i + 1] == (byte)'\n', $"{name}: bare CR at offset {i}");
        }
    }
}

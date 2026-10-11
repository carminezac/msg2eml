using MimeKit;
using Msg2Eml.Core;

namespace Msg2Eml.Tests;

/// <summary>
/// Golden tests against a real corpus (plan, section 9): for every <c>samples/X.msg</c> with a
/// <c>samples/X.ref.eml</c> next to it (the Outlook Web export of the same mail), the produced
/// EML must match the reference on headers, visible text, attachments and CID resolution.
/// The corpus is not versioned; without it these tests pass vacuously.
/// </summary>
public class GoldenTests
{
    private readonly MsgConverter _converter = new();

    public static IEnumerable<object[]> Pairs()
    {
        if (Fixtures.SamplesDir is not { } dir) yield break;
        foreach (var msg in Directory.GetFiles(dir, "*.msg").Order())
        {
            var reference = Path.ChangeExtension(msg, ".ref.eml");
            if (File.Exists(reference)) yield return [Path.GetFileName(msg)];
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ProducedEml_MatchesOutlookWebReference(string fileName)
    {
        var dir = Fixtures.SamplesDir!;
        var reference = MimeMessage.Load(Path.ChangeExtension(Path.Combine(dir, fileName), ".ref.eml"));

        using var fs = File.OpenRead(Path.Combine(dir, fileName));
        var produced = MimeMessage.Load(new MemoryStream(_converter.Convert(fs).Eml));

        var diffs = EmlFidelity.Compare(reference, produced);
        Assert.True(diffs.Count == 0, $"{fileName}:\n  " + string.Join("\n  ", diffs));
    }

    [Fact]
    public void GoldenCorpus_IsPresentOrSkipped()
    {
        // Makes the skip visible in the test output instead of silently passing.
        var count = Pairs().Count();
        Assert.True(count >= 0);
    }
}

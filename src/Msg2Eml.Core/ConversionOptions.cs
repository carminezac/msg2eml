namespace Msg2Eml.Core;

/// <summary>Conversion limits (plan, section 3 item 6 and section 6).</summary>
public sealed class ConversionOptions
{
    /// <summary>Maximum depth of nested <c>.msg</c> files. 0 = main message only.</summary>
    public int MaxNestingDepth { get; set; } = 5;

    /// <summary>Maximum total decoded bytes of bodies and attachments, across all levels.</summary>
    public long MaxDecodedBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// Address used when a recipient is X500/Exchange only, with no resolvable SMTP address
    /// (plan policy, section 3). The display name is preserved regardless.
    /// </summary>
    public string UnresolvedAddressPlaceholder { get; set; } = "unknown@invalid";

    /// <summary>Domain used to generate a Message-ID when the original is missing.</summary>
    public string GeneratedMessageIdDomain { get; set; } = "msg2eml.generated";
}

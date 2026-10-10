namespace Msg2Eml.Core;

/// <summary>How the body of the main message was reconstructed.</summary>
public enum BodyKind
{
    None,
    Text,
    Html,
    TextAndHtml,
    RtfOnly,
    SmimeOpaque,
}

/// <summary>
/// Conversion statistics, intended for structured logging: no field contains
/// message content.
/// </summary>
public sealed record ConversionStats
{
    public bool HasTransportHeaders { get; init; }
    public BodyKind BodyKind { get; init; }
    public int Attachments { get; init; }
    public int InlineAttachments { get; init; }
    public int NestedMessages { get; init; }
    public int MaxNestedDepth { get; init; }
    public int UnresolvedRecipients { get; init; }
    public long DecodedBytes { get; init; }
}

public sealed record ConversionResult(byte[] Eml, ConversionStats Stats);

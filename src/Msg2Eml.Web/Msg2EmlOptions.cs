namespace Msg2Eml.Web;

/// <summary>Service configuration, <c>Msg2Eml</c> section of appsettings or <c>Msg2Eml__*</c> variables.</summary>
public sealed class Msg2EmlOptions
{
    public const string SectionName = "Msg2Eml";

    /// <summary>Maximum size of the uploaded file. Default 50 MB.</summary>
    public long MaxUploadBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Maximum depth of nested .msg files.</summary>
    public int MaxNestingDepth { get; set; } = 5;

    /// <summary>Maximum total decoded bytes per conversion. Default 256 MB.</summary>
    public long MaxDecodedBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Concurrent conversions per instance. Beyond that, requests wait.</summary>
    public int MaxConcurrentConversions { get; set; } = 4;

    /// <summary>Conversion requests per minute per client address. 0 = no limit.</summary>
    public int RateLimitPerMinute { get; set; } = 0;

    /// <summary>Logs in JSON format on stdout (default) or as text.</summary>
    public bool JsonLogs { get; set; } = true;
}

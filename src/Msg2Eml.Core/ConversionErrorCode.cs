namespace Msg2Eml.Core;

/// <summary>Application-level conversion error codes (plan, section 5).</summary>
public enum ConversionErrorCode
{
    /// <summary>The file is not a valid OLE/CFBF container or MsgReader cannot open it.</summary>
    InvalidMsgFile,

    /// <summary>Input over the limit, or decoded content over the limit.</summary>
    FileTooLarge,

    /// <summary>Appointment, task, contact, note: not an e-mail.</summary>
    UnsupportedMessageType,

    /// <summary>Nested messages beyond the maximum depth.</summary>
    NestingTooDeep,

    /// <summary>An attachment cannot be read.</summary>
    AttachmentReadError,

    /// <summary>MIME serialization or re-parse self-check failed.</summary>
    MimeGenerationError,

    /// <summary>Any other error during conversion.</summary>
    ConversionError,
}

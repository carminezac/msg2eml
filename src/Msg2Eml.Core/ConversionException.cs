namespace Msg2Eml.Core;

/// <summary>
/// Application-level conversion error. <see cref="Message"/> is generic by construction:
/// it never contains the subject, addresses, attachment names or other message content.
/// </summary>
public sealed class ConversionException : Exception
{
    public ConversionErrorCode Code { get; }

    public ConversionException(ConversionErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

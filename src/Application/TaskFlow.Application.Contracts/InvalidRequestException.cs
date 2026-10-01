namespace TaskFlow.Application.Contracts;

/// <summary>
/// The caller sent input TaskFlow's own checks reject (a page size out of range; a bad cursor is the codec's
/// InvalidCursorException). The Api maps it to 400 and gRPC to InvalidArgument, with the message as the detail.
/// Deliberately not an <see cref="ArgumentException"/>: those stay unmapped (500), because a framework or
/// library ArgumentException is a server bug, not the caller's mistake.
/// </summary>
public sealed class InvalidRequestException : Exception
{
    /// <summary>Creates the exception with the message the caller sees.</summary>
    public InvalidRequestException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the message the caller sees and the parse failure behind it.</summary>
    public InvalidRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

namespace SazViewer.Core;

public static class SazPasswordLimits
{
    public const int MaximumCharacters = 1024;
}

public sealed record SazPasswordRequest(
    int Attempt,
    int MaximumAttempts,
    bool PreviousPasswordRejected);

public interface ISazPasswordProvider
{
    int MaximumAttempts { get; }

    char[]? GetPassword(SazPasswordRequest request);
}

public abstract class SazArchiveException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class SazPasswordRequiredException()
    : SazArchiveException("This SAZ archive is encrypted and requires a password.");

public sealed class SazPasswordCancelledException()
    : SazArchiveException("Password input was cancelled or reached end of input.");

public sealed class SazPasswordUnavailableException()
    : SazArchiveException(
        "Secure interactive password input is unavailable because standard input is redirected. Use --password-stdin.");

public sealed class SazPasswordTooLongException(int maximumLength)
    : SazArchiveException($"Password input exceeds the {maximumLength:N0}-character safety limit.");

public sealed class SazAuthenticationException()
    : SazArchiveException("The password is incorrect.");

public sealed class SazUnsupportedEncryptionException(string detail)
    : SazArchiveException($"The SAZ archive uses unsupported encryption: {detail}");

public sealed class SazArchiveCorruptException(string detail, Exception? innerException = null)
    : SazArchiveException($"The encrypted SAZ archive is corrupt or failed integrity validation: {detail}", innerException);

public sealed class SazArchiveLimitException(string detail)
    : SazArchiveException($"The SAZ archive exceeds a safety limit: {detail}");

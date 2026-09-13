using BitosX.Core.Domain;
using BitosX.Crypto;

namespace BitosX.App.ViewModels;

/// <summary>
/// Maps exceptions to safe, user-facing messages. Never exposes stack traces or cryptographic
/// internals, and never reveals which cryptographic component failed on an unlock attempt.
/// </summary>
internal static class UserFacingError
{
    public static string Describe(Exception exception) => exception switch
    {
        // Wrong password and tampering are intentionally indistinguishable.
        VaultAuthenticationException => "Incorrect master password, or the vault file has been modified.",
        UnsupportedVaultFormatException => "This vault file is not compatible with this version of BitosX.",
        VaultValidationException validation => validation.Message,
        ArgumentException argument => argument.Message,
        FileNotFoundException => "No vault file was found.",
        UnauthorizedAccessException => "BitosX does not have permission to access the vault file.",
        IOException => "A file system error occurred while accessing the vault.",
        OperationCanceledException => "The operation was cancelled.",
        _ => "An unexpected error occurred. Please try again.",
    };
}

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

        // About the device, not about the vault or the password; the message names the memory needed.
        KeyDerivationResourceException resource => resource.Message,
        WeakerKeyDerivationException weaker => weaker.Message,

        // These carry a fixed, non-secret message that tells the user what to do next.
        VaultChangedExternallyException changed => changed.Message,
        VaultInconsistentStateException inconsistent => inconsistent.Message,
        VaultTooLargeException tooLarge => tooLarge.Message,

        VaultValidationException validation => validation.Message,
        VaultException => "That item is no longer available. It may have been changed or removed.",
        ArgumentException argument => argument.Message,
        FileNotFoundException => "No vault file was found.",
        UnauthorizedAccessException => "BitosX does not have permission to access the vault file.",
        IOException => "A file system error occurred while accessing the vault.",
        OperationCanceledException => "The operation was cancelled.",
        _ => "An unexpected error occurred. Please try again.",
    };
}

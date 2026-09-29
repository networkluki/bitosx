using BitosX.Core.Domain;

namespace BitosX.Core.Validation;

/// <summary>
/// Centralized input validation for vault items. All limits fail closed by throwing
/// <see cref="VaultValidationException"/>. These bounds guard against corrupt input and
/// unbounded resource use; they are not security guarantees on their own.
/// </summary>
public static class VaultValidation
{
    public const int MaxNameLength = 200;
    public const int MaxUsernameLength = 320;   // RFC 5321 email upper bound
    public const int MaxPasswordLength = 4096;  // generous; passwords are never truncated
    public const int MaxNotesLength = 16384;
    public const int MaxUrlLength = 2048;
    public const int MaxUrlCount = 64;

    /// <summary>Validates any vault item, dispatching to type-specific rules.</summary>
    public static void Validate(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Id == Guid.Empty)
        {
            throw new VaultValidationException("Item id must not be empty.");
        }

        ValidateName(item.Name);
        ValidateOptionalLength(item.Notes, MaxNotesLength, "Notes");

        if (item.UpdatedAt < item.CreatedAt)
        {
            throw new VaultValidationException("UpdatedAt must not be earlier than CreatedAt.");
        }

        switch (item)
        {
            case LoginItem login:
                ValidateLogin(login);
                break;
            default:
                throw new VaultValidationException($"Unsupported item type '{item.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Repairs harmless metadata on an item read from a stored vault, so that an anomaly which is
    /// not a security or integrity concern can never make a vault or backup impossible to open.
    /// Currently: an <see cref="VaultItem.UpdatedAt"/> earlier than <see cref="VaultItem.CreatedAt"/>
    /// (written by earlier versions after the system clock was moved back) is raised to CreatedAt.
    /// Everything else is left untouched and must pass <see cref="Validate"/>.
    /// </summary>
    public static VaultItem NormalizeLoaded(VaultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.UpdatedAt < item.CreatedAt ? item with { UpdatedAt = item.CreatedAt } : item;
    }

    private static void ValidateLogin(LoginItem login)
    {
        ValidateOptionalLength(login.Username, MaxUsernameLength, "Username");
        ValidateOptionalLength(login.Password, MaxPasswordLength, "Password");

        // Not reachable through the editor, but a stored document can carry an explicit null.
        if (login.Urls is null)
        {
            throw new VaultValidationException("The URL list must not be null.");
        }

        if (login.Urls.Count > MaxUrlCount)
        {
            throw new VaultValidationException($"An item may have at most {MaxUrlCount} URLs.");
        }

        foreach (string url in login.Urls)
        {
            ValidateUrl(url);
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new VaultValidationException("Name is required.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new VaultValidationException($"Name must be at most {MaxNameLength} characters.");
        }
    }

    private static void ValidateOptionalLength(string? value, int maxLength, string fieldName)
    {
        if (value is not null && value.Length > maxLength)
        {
            throw new VaultValidationException($"{fieldName} must be at most {maxLength} characters.");
        }
    }

    private static void ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new VaultValidationException("A URL must not be blank.");
        }

        if (url.Length > MaxUrlLength)
        {
            throw new VaultValidationException($"A URL must be at most {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new VaultValidationException($"'{url}' is not a valid absolute http(s) URL.");
        }
    }
}

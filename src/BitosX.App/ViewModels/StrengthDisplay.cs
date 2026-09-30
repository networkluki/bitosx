using BitosX.Core.Security;

namespace BitosX.App.ViewModels;

/// <summary>How a password rating is written on screen.</summary>
public static class StrengthDisplay
{
    /// <summary>The name of <paramref name="level"/> as a person writes it.</summary>
    public static string Name(PasswordStrengthLevel level) => level switch
    {
        PasswordStrengthLevel.VeryWeak => "Very weak",
        PasswordStrengthLevel.Weak => "Weak",
        PasswordStrengthLevel.Fair => "Fair",
        PasswordStrengthLevel.Strong => "Strong",
        PasswordStrengthLevel.VeryStrong => "Very strong",
        _ => level.ToString(),
    };

    /// <summary>The name of <paramref name="level"/> as it is written inside a sentence.</summary>
    public static string Phrase(PasswordStrengthLevel level) => level switch
    {
        PasswordStrengthLevel.VeryWeak => "very weak",
        PasswordStrengthLevel.Weak => "weak",
        PasswordStrengthLevel.Fair => "fair",
        PasswordStrengthLevel.Strong => "strong",
        PasswordStrengthLevel.VeryStrong => "very strong",
        _ => level.ToString(),
    };

    /// <summary>The line shown under a password that is being typed; empty for no password.</summary>
    public static string Rating(string? password)
        => string.IsNullOrEmpty(password)
            ? string.Empty
            : $"Strength: {Name(PasswordStrength.Estimate(password))}";
}

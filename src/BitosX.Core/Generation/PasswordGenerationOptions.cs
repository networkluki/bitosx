namespace BitosX.Core.Generation;

/// <summary>Configuration for <see cref="PasswordGenerator"/>.</summary>
public sealed record PasswordGenerationOptions
{
    public const int MinLength = 4;
    public const int MaxLength = 256;

    /// <summary>Desired password length.</summary>
    public int Length { get; init; } = 20;

    public bool IncludeLowercase { get; init; } = true;
    public bool IncludeUppercase { get; init; } = true;
    public bool IncludeDigits { get; init; } = true;
    public bool IncludeSymbols { get; init; } = true;

    /// <summary>Exclude visually ambiguous characters (I, l, 1, O, 0, o).</summary>
    public bool ExcludeAmbiguous { get; init; }

    /// <summary>Validates the options, throwing <see cref="ArgumentException"/> for impossible configurations.</summary>
    public void Validate()
    {
        if (Length is < MinLength or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(Length), Length,
                $"Password length must be between {MinLength} and {MaxLength}.");
        }

        if (!IncludeLowercase && !IncludeUppercase && !IncludeDigits && !IncludeSymbols)
        {
            throw new ArgumentException("At least one character class must be selected.");
        }
    }
}

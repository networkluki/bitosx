namespace BitosX.Core.Security;

/// <summary>Coarse password-strength category.</summary>
public enum PasswordStrengthLevel
{
    VeryWeak = 0,
    Weak = 1,
    Fair = 2,
    Strong = 3,
    VeryStrong = 4,
}

/// <summary>
/// Heuristic password-strength estimation. Starts from length and character-class diversity and
/// then discounts what an attacker would try first: repeated characters, runs such as "abcd",
/// "4321" or "qwerty", a block repeated several times, and the most common passwords (also when
/// written with the usual substitutions, such as "P@ssw0rd").
/// </summary>
/// <remarks>
/// This is a rough local heuristic, NOT a guarantee of resistance to any particular attacker. It
/// errs on the side of a lower rating for the patterns it knows, but it has no dictionary: it cannot
/// tell that a password is made of ordinary words, names or dates, and it does not know about
/// breached passwords. A good rating means "no obvious weakness found", nothing more. It never
/// leaves the device.
/// </remarks>
public static class PasswordStrength
{
    // What a character is worth when it merely continues a pattern the previous one started.
    private const double PatternContinuationBits = 1.0;

    // What a whole common password is worth: one guess out of a short, well-known list.
    private const double CommonPasswordBits = 7.0;

    private static readonly string[] KeyboardRows =
    [
        "1234567890",
        "qwertyuiopå",
        "asdfghjklöä",
        "zxcvbnm",
    ];

    // Lower case; letters only where the usual substitutions apply (see Normalize). Longest first,
    // so that "qwertyuiop" is matched before "qwerty".
    private static readonly string[] CommonPasswords =
    [
        "administrator", "qwertyuiop", "1q2w3e4r5t", "password", "iloveyou", "sunshine", "princess",
        "football", "baseball", "superman", "starwars", "whatever", "trustno1", "changeme", "zaq12wsx",
        "1q2w3e4r", "letmein", "welcome", "freedom", "bitosx", "qwerty", "azerty", "monkey", "dragon",
        "master", "shadow", "secret", "hunter", "batman", "summer", "winter", "asdfgh", "zxcvbn",
        "abc123", "admin", "login", "hello",
    ];

    /// <summary>Estimates the strength category of a password.</summary>
    public static PasswordStrengthLevel Estimate(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return PasswordStrengthLevel.VeryWeak;
        }

        double bits = EstimateEntropyBits(password);
        return bits switch
        {
            < 40 => PasswordStrengthLevel.VeryWeak,
            < 60 => PasswordStrengthLevel.Weak,
            < 80 => PasswordStrengthLevel.Fair,
            < 100 => PasswordStrengthLevel.Strong,
            _ => PasswordStrengthLevel.VeryStrong,
        };
    }

    /// <summary>
    /// Approximate guessing entropy in bits: random selection from the observed character pool,
    /// discounted for the patterns described on <see cref="PasswordStrength"/>.
    /// </summary>
    public static double EstimateEntropyBits(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            return 0;
        }

        double bitsPerCharacter = Math.Log2(PoolSize(password));

        // "abcabcabc" is "abc" and a repeat count.
        int period = SmallestPeriod(password);
        if (period < password.Length)
        {
            return EstimateSequence(password.AsSpan(0, period), bitsPerCharacter)
                   + Math.Log2((double)password.Length / period);
        }

        return EstimateSequence(password, bitsPerCharacter);
    }

    private static double EstimateSequence(ReadOnlySpan<char> text, double bitsPerCharacter)
    {
        // Normalized character by character, so positions line up with the original.
        Span<char> normalized = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            normalized[i] = Normalize(text[i]);
        }

        double bits = 0;
        int position = 0;
        while (position < text.Length)
        {
            int common = CommonPasswordLengthAt(text[position..], normalized[position..]);
            if (common > 0)
            {
                bits += CommonPasswordBits;
                position += common;
                continue;
            }

            bool continuesPattern = position > 0 && ContinuesPattern(text[position - 1], text[position]);
            bits += continuesPattern ? PatternContinuationBits : bitsPerCharacter;
            position++;
        }

        return bits;
    }

    private static int PoolSize(string password)
    {
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in password)
        {
            if (char.IsLower(c)) { lower = true; }
            else if (char.IsUpper(c)) { upper = true; }
            else if (char.IsDigit(c)) { digit = true; }
            else { symbol = true; }
        }

        int pool = 0;
        if (lower) { pool += 26; }
        if (upper) { pool += 26; }
        if (digit) { pool += 10; }
        if (symbol) { pool += 32; } // conservative estimate of the symbol space
        return pool;
    }

    /// <summary>The length of the shortest block the text is a whole number of repetitions of.</summary>
    private static int SmallestPeriod(string text)
    {
        for (int period = 1; period <= text.Length / 2; period++)
        {
            if (text.Length % period != 0)
            {
                continue;
            }

            bool repeats = true;
            for (int i = period; i < text.Length && repeats; i++)
            {
                repeats = text[i] == text[i - period];
            }

            if (repeats)
            {
                return period;
            }
        }

        return text.Length;
    }

    private static int CommonPasswordLengthAt(ReadOnlySpan<char> text, ReadOnlySpan<char> normalized)
    {
        foreach (string common in CommonPasswords)
        {
            // Either as typed ("trustno1") or with substitutions undone ("P@ssw0rd" -> "password").
            if (text.StartsWith(common, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(common, StringComparison.Ordinal))
            {
                return common.Length;
            }
        }

        return 0;
    }

    /// <summary>Lower case, with the substitutions people use in place of letters undone.</summary>
    private static char Normalize(char c) => c switch
    {
        '0' => 'o',
        '1' or '!' or '|' => 'i',
        '3' => 'e',
        '4' or '@' => 'a',
        '5' or '$' => 's',
        '7' => 't',
        _ => char.ToLowerInvariant(c),
    };

    private static bool ContinuesPattern(char previous, char current)
    {
        if (previous == current)
        {
            return true; // "aaa"
        }

        bool sameKind = (char.IsLetter(previous) && char.IsLetter(current))
                        || (char.IsDigit(previous) && char.IsDigit(current));
        if (sameKind && Math.Abs(current - previous) == 1)
        {
            return true; // "abc", "321"
        }

        char a = char.ToLowerInvariant(previous);
        char b = char.ToLowerInvariant(current);
        foreach (string row in KeyboardRows)
        {
            int index = row.IndexOf(a, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (index > 0 && row[index - 1] == b) || (index < row.Length - 1 && row[index + 1] == b); // "qwerty"
            }
        }

        return false;
    }
}

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
/// Heuristic password-strength estimation based on length and character-class diversity.
/// </summary>
/// <remarks>
/// This is a rough local heuristic (an approximate entropy estimate), NOT a guarantee of
/// resistance to any particular attacker. It does not detect dictionary words, keyboard
/// patterns, or known-breached passwords. It never leaves the device.
/// </remarks>
public static class PasswordStrength
{
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

    /// <summary>Approximate Shannon entropy in bits assuming random selection from the observed character pool.</summary>
    public static double EstimateEntropyBits(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            return 0;
        }

        int pool = 0;
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (char c in password)
        {
            if (char.IsLower(c)) { lower = true; }
            else if (char.IsUpper(c)) { upper = true; }
            else if (char.IsDigit(c)) { digit = true; }
            else { symbol = true; }
        }

        if (lower) { pool += 26; }
        if (upper) { pool += 26; }
        if (digit) { pool += 10; }
        if (symbol) { pool += 32; } // conservative estimate of the symbol space

        if (pool == 0)
        {
            return 0;
        }

        return password.Length * Math.Log2(pool);
    }
}

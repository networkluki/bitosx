using BitosX.Crypto;

namespace BitosX.Core.Generation;

/// <summary>
/// Cryptographically secure password generator. Uses <see cref="SecureRandom.GetInt32(int)"/>
/// for uniform, modulo-bias-free selection from the chosen alphabet. When the length allows,
/// at least one character from every selected class is guaranteed.
/// </summary>
public sealed class PasswordGenerator : IPasswordGenerator
{
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Symbols = "!@#$%^&*()-_=+[]{};:,.?/";

    // Visually ambiguous characters removed when ExcludeAmbiguous is set.
    private static readonly char[] Ambiguous = ['I', 'l', '1', 'O', '0', 'o'];

    public string Generate(PasswordGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<string> classes = BuildSelectedClasses(options);

        // Validate() guarantees at least one class, but ExcludeAmbiguous could in theory empty one.
        string combined = string.Concat(classes);
        if (combined.Length == 0)
        {
            throw new ArgumentException("The selected options produce an empty character set.");
        }

        char[] result = new char[options.Length];
        int position = 0;

        // Guarantee coverage of each class when the length permits.
        if (options.Length >= classes.Count)
        {
            foreach (string cls in classes)
            {
                result[position++] = cls[SecureRandom.GetInt32(cls.Length)];
            }
        }

        // Fill the remainder from the combined alphabet.
        for (; position < result.Length; position++)
        {
            result[position] = combined[SecureRandom.GetInt32(combined.Length)];
        }

        Shuffle(result);
        return new string(result);
    }

    private static List<string> BuildSelectedClasses(PasswordGenerationOptions options)
    {
        var classes = new List<string>(4);
        if (options.IncludeLowercase)
        {
            classes.Add(Filter(Lowercase, options.ExcludeAmbiguous));
        }

        if (options.IncludeUppercase)
        {
            classes.Add(Filter(Uppercase, options.ExcludeAmbiguous));
        }

        if (options.IncludeDigits)
        {
            classes.Add(Filter(Digits, options.ExcludeAmbiguous));
        }

        if (options.IncludeSymbols)
        {
            classes.Add(Filter(Symbols, options.ExcludeAmbiguous));
        }

        // Drop any class that became empty after filtering.
        classes.RemoveAll(string.IsNullOrEmpty);
        return classes;
    }

    private static string Filter(string set, bool excludeAmbiguous)
    {
        if (!excludeAmbiguous)
        {
            return set;
        }

        return new string(set.Where(c => Array.IndexOf(Ambiguous, c) < 0).ToArray());
    }

    /// <summary>Fisher–Yates shuffle using the CSPRNG (unbiased).</summary>
    private static void Shuffle(char[] buffer)
    {
        for (int i = buffer.Length - 1; i > 0; i--)
        {
            int j = SecureRandom.GetInt32(i + 1);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }
    }
}

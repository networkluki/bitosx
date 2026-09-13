namespace BitosX.Core.Generation;

/// <summary>Generates random passwords using a cryptographically secure RNG.</summary>
public interface IPasswordGenerator
{
    /// <summary>Generates a password matching <paramref name="options"/>. Throws if the options are invalid.</summary>
    string Generate(PasswordGenerationOptions options);
}

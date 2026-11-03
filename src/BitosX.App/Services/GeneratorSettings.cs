using BitosX.Core.Generation;

namespace BitosX.App.Services;

/// <summary>
/// The generator settings of this run: what the Generator page is set to, which the login editor's
/// Generate button uses too, so that a site that refuses symbols is set up once. Kept in memory;
/// nothing about it is secret, and nothing is written.
/// </summary>
public sealed class GeneratorSettings
{
    private PasswordGenerationOptions _current = new();

    /// <summary>The settings in effect. Always valid: a setting the generator cannot use is not taken.</summary>
    public PasswordGenerationOptions Current
    {
        get => _current;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            _current = value;
        }
    }
}

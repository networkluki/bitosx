using BitosX.Core.Security;

namespace BitosX.App.ViewModels;

/// <summary>A key-derivation profile as it is offered in Settings.</summary>
/// <param name="Profile">The profile.</param>
public sealed record KeyDerivationChoice(KeyDerivationProfile Profile)
{
    /// <summary>What the user sees: the name and what every unlock will need.</summary>
    public string Display =>
        $"{Profile.Name} — {Profile.MemoryMiB} MiB of memory, {Profile.Parameters.Iterations} iterations";

    public override string ToString() => Display;
}

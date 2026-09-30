using BitosX.Crypto;

namespace BitosX.Core.Security;

/// <summary>
/// A named set of key-derivation settings a user can move a vault to.
/// </summary>
/// <remarks>
/// The profiles differ in memory only. Memory is what makes Argon2 expensive to attack: each guess
/// needs that much of it, on whatever hardware the attacker uses. There are deliberately few of
/// them, and an existing vault is never moved to another one by itself — what a device can afford
/// is for its user to decide, with a measurement from that device in front of them (see
/// <see cref="IKeyDerivationBenchmark"/>).
/// </remarks>
/// <param name="Name">Short name shown to the user.</param>
/// <param name="Parameters">The settings.</param>
public sealed record KeyDerivationProfile(string Name, Argon2Parameters Parameters)
{
    /// <summary>
    /// What vaults were created with before 0.7.0, and what a vault is created with on a device that
    /// cannot spare the memory of <see cref="ForNewVaults"/>.
    /// </summary>
    public static KeyDerivationProfile Standard { get; } = new("Standard", Argon2Parameters.Default);

    /// <summary>Four times the memory of <see cref="Standard"/>. What new vaults are created with.</summary>
    public static KeyDerivationProfile Strong { get; } = new("Strong", Argon2Parameters.Strong);

    /// <summary>The most the vault format allows.</summary>
    public static KeyDerivationProfile Maximum { get; } = new("Maximum", Argon2Parameters.Maximum);

    /// <summary>
    /// What a new vault is created with. Raised from <see cref="Standard"/> in 0.7.0, once the
    /// derivation had been measured on four machines instead of one; see docs/KEY_DERIVATION.md.
    /// </summary>
    public static KeyDerivationProfile ForNewVaults => Strong;

    /// <summary>All profiles, weakest first.</summary>
    public static IReadOnlyList<KeyDerivationProfile> All { get; } = [Standard, Strong, Maximum];

    /// <summary>The memory every unlock needs, in mebibytes.</summary>
    public int MemoryMiB => Parameters.MemoryKiB / 1024;

    /// <summary>The profiles a vault with <paramref name="current"/> settings can move to.</summary>
    public static IReadOnlyList<KeyDerivationProfile> StrongerThan(Argon2Parameters current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return [.. All.Where(profile => profile.Parameters.IsStrongerThan(current))];
    }
}

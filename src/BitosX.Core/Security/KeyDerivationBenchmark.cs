using System.Diagnostics;
using BitosX.Crypto;

namespace BitosX.Core.Security;

/// <summary>
/// Measures how long deriving a key takes on this device, so that stronger settings can be judged
/// before a vault is moved to them.
/// </summary>
public interface IKeyDerivationBenchmark
{
    /// <summary>
    /// Derives a key with <paramref name="parameters"/> from a fixed dummy password and returns how
    /// long it took. This is what every unlock will cost on this device with these settings.
    /// </summary>
    /// <exception cref="KeyDerivationResourceException">
    /// The device does not have the memory these settings ask for.
    /// </exception>
    Task<TimeSpan> MeasureAsync(Argon2Parameters parameters, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IKeyDerivationBenchmark"/>: runs the real key derivation, off the calling
/// thread. Nothing secret is involved — the password and salt are constants and the derived key is
/// discarded.
/// </summary>
public sealed class KeyDerivationBenchmark : IKeyDerivationBenchmark
{
    private static readonly byte[] DummyPassword = "BitosX key-derivation measurement"u8.ToArray();
    private static readonly byte[] DummySalt = new byte[CryptoConstants.SaltSizeBytes];

    private readonly IKeyDerivation _keyDerivation;

    public KeyDerivationBenchmark(IKeyDerivation keyDerivation)
    {
        _keyDerivation = keyDerivation ?? throw new ArgumentNullException(nameof(keyDerivation));
    }

    public Task<TimeSpan> MeasureAsync(Argon2Parameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stopwatch = Stopwatch.StartNew();
                byte[] key = _keyDerivation.DeriveKey(DummyPassword, DummySalt, parameters);
                stopwatch.Stop();

                CryptoBuffers.Wipe(key);
                return stopwatch.Elapsed;
            },
            cancellationToken);
    }
}

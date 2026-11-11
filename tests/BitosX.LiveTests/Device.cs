using BitosX.ApiClient;
using BitosX.ApiClient.Sync;
using BitosX.App;
using BitosX.Core.Domain;
using BitosX.Core.Services;
using BitosX.Core.Storage;
using BitosX.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BitosX.LiveTests;

/// <summary>
/// One installation of BitosX: a data directory of its own (vault, settings, sync record) and the
/// application's services as its composition root wires them (<see cref="ServiceConfiguration"/>),
/// pointed at the live API. Two of them are two devices of the same user.
/// </summary>
internal sealed class Device : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _services;

    public Device(TimeProvider? clock = null)
    {
        _directory = Directory.CreateTempSubdirectory("bitosx-live-").FullName;
        Location = AppLocation.In(_directory);
        Server = ApiServer.Parse(LiveStack.Api.ToString());
        _services = ServiceConfiguration.Build(Location, Server, services =>
        {
            if (clock is not null)
            {
                services.AddSingleton(clock);
            }
        });
    }

    public AppLocation Location { get; }

    public ApiServer Server { get; }

    public IVaultService Vault => _services.GetRequiredService<IVaultService>();

    public IAccountSession Account => _services.GetRequiredService<IAccountSession>();

    public IVaultSyncService Sync => _services.GetRequiredService<IVaultSyncService>();

    /// <summary>Importing logins from a file, as the Settings page does it.</summary>
    public IVaultImportService Import => _services.GetRequiredService<IVaultImportService>();

    /// <summary>The application's own HTTP client of the API, for the calls a test makes past the session.</summary>
    public IBitosXApiClient Api => _services.GetRequiredService<IBitosXApiClient>();

    public ISyncStateStore SyncRecord => _services.GetRequiredService<ISyncStateStore>();

    public async Task CreateVaultAsync(string masterPassword)
    {
        await Vault.RefreshStateAsync().ConfigureAwait(false);
        await Vault.CreateVaultAsync(masterPassword).ConfigureAwait(false);
    }

    public Task<LoginItem> AddLoginAsync(string name)
        => Vault.AddLoginAsync(new LoginItem { Name = name, Username = "someone@example.test", Password = LiveStack.NewPassword() });

    /// <summary>The vault file as the bytes a sync uploads.</summary>
    public async Task<byte[]> EnvelopeBytesAsync()
        => EncryptedVaultSerializer.SerializeToUtf8Bytes(
            await _services.GetRequiredService<ILocalVaultStorage>().ReadAsync().ConfigureAwait(false));

    /// <summary>Signs this device in with the password alone; fails if the account asks for a second factor.</summary>
    public async Task SignInAsync(string email, string password)
    {
        SignInOutcome outcome = await Account.SignInAsync(email, password).ConfigureAwait(false);
        Assert.Equal(SignInOutcome.SignedIn, outcome);
        Assert.True(Account.IsSignedIn);
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

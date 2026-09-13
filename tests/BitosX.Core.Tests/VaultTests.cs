using BitosX.Core.Domain;

namespace BitosX.Core.Tests;

public sealed class VaultTests
{
    private static LoginItem Login(string name = "Example", string? username = "user", string? password = "password123!")
        => new() { Name = name, Username = username, Password = password };

    [Fact]
    public void Add_StoresItem()
    {
        var vault = new Vault();
        LoginItem item = Login();

        vault.Add(item);

        Assert.Single(vault.Items);
        Assert.Same(item, vault.Items[0]);
        Assert.NotNull(vault.Find(item.Id));
    }

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        var vault = new Vault();
        LoginItem item = Login();
        vault.Add(item);

        LoginItem duplicate = item with { Name = "Other" };
        Assert.Throws<DuplicateItemException>(() => vault.Add(duplicate));
    }

    [Fact]
    public void Add_InvalidItem_Throws()
    {
        var vault = new Vault();
        Assert.Throws<VaultValidationException>(() => vault.Add(Login(name: "  ")));
    }

    [Fact]
    public void Update_ReplacesItem_AndStampsUpdatedAt()
    {
        var vault = new Vault();
        DateTimeOffset yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        LoginItem item = Login() with { CreatedAt = yesterday, UpdatedAt = yesterday };
        vault.Add(item);

        VaultItem stored = vault.Update(item with { Name = "Renamed" });

        Assert.Equal("Renamed", ((LoginItem)vault.Items[0]).Name);
        Assert.True(stored.UpdatedAt > item.UpdatedAt);
    }

    [Fact]
    public void Update_MissingItem_Throws()
    {
        var vault = new Vault();
        Assert.Throws<ItemNotFoundException>(() => vault.Update(Login()));
    }

    [Fact]
    public void Remove_DeletesItem()
    {
        var vault = new Vault();
        LoginItem item = Login();
        vault.Add(item);

        Assert.True(vault.Remove(item.Id));
        Assert.Empty(vault.Items);
        Assert.False(vault.Remove(item.Id));
    }
}

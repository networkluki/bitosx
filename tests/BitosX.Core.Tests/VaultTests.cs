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

    // ---- Clock moved back since the item was created (0.5.1) ----

    [Fact]
    public void Update_WhenTheClockIsBehindCreatedAt_StampsCreatedAt_NotAnEarlierTime()
    {
        var vault = new Vault();
        DateTimeOffset createdWhileClockWasAhead = DateTimeOffset.UtcNow.AddDays(2);
        LoginItem item = Login() with { CreatedAt = createdWhileClockWasAhead, UpdatedAt = createdWhileClockWasAhead };
        vault.Add(item);

        VaultItem stored = vault.Update(item with { Favorite = true });

        // Before the fix this stored UpdatedAt = now < CreatedAt, an item that failed its own validation.
        Assert.Equal(createdWhileClockWasAhead, stored.UpdatedAt);
        BitosX.Core.Validation.VaultValidation.Validate(stored);
    }

    [Fact]
    public void Update_WhenTheClockIsBehindCreatedAt_TheItemCanStillBeEditedRepeatedly()
    {
        var vault = new Vault();
        DateTimeOffset createdWhileClockWasAhead = DateTimeOffset.UtcNow.AddDays(2);
        LoginItem item = Login() with { CreatedAt = createdWhileClockWasAhead, UpdatedAt = createdWhileClockWasAhead };
        vault.Add(item);

        // The editor stamps UpdatedAt with the current (earlier) time before handing the item over.
        VaultItem first = vault.Update(item with { Name = "Renamed", UpdatedAt = DateTimeOffset.UtcNow });
        VaultItem second = vault.Update((LoginItem)first with { Name = "Renamed again", UpdatedAt = DateTimeOffset.UtcNow });

        Assert.Equal("Renamed again", ((LoginItem)vault.Items[0]).Name);
        Assert.True(second.UpdatedAt >= second.CreatedAt);
    }

    [Fact]
    public void Update_InvalidItem_Throws_AndKeepsTheStoredItem()
    {
        var vault = new Vault();
        LoginItem item = Login();
        vault.Add(item);

        Assert.Throws<VaultValidationException>(() => vault.Update(item with { Name = " " }));

        Assert.Same(item, vault.Items[0]);
    }

    [Fact]
    public void Add_NullUrlList_Throws()
    {
        var vault = new Vault();
        Assert.Throws<VaultValidationException>(() => vault.Add(Login() with { Urls = null! }));
    }
}

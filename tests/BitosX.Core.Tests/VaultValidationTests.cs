using BitosX.Core.Domain;
using BitosX.Core.Validation;

namespace BitosX.Core.Tests;

public sealed class VaultValidationTests
{
    [Fact]
    public void Validate_ValidLogin_Passes()
    {
        var item = new LoginItem { Name = "Site", Username = "u", Password = "p", Urls = ["https://example.com"] };
        VaultValidation.Validate(item); // should not throw
    }

    [Fact]
    public void Validate_EmptyName_Throws()
    {
        Assert.Throws<VaultValidationException>(() => VaultValidation.Validate(new LoginItem { Name = "" }));
    }

    [Fact]
    public void Validate_EmptyId_Throws()
    {
        Assert.Throws<VaultValidationException>(() => VaultValidation.Validate(new LoginItem { Id = Guid.Empty, Name = "Site" }));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    public void Validate_InvalidUrl_Throws(string url)
    {
        var item = new LoginItem { Name = "Site", Urls = [url] };
        Assert.Throws<VaultValidationException>(() => VaultValidation.Validate(item));
    }

    [Fact]
    public void Validate_TooLongName_Throws()
    {
        var item = new LoginItem { Name = new string('x', VaultValidation.MaxNameLength + 1) };
        Assert.Throws<VaultValidationException>(() => VaultValidation.Validate(item));
    }

    [Fact]
    public void Validate_MaxLengthPassword_IsAccepted_NotTruncated()
    {
        var item = new LoginItem { Name = "Site", Password = new string('p', VaultValidation.MaxPasswordLength) };
        VaultValidation.Validate(item); // exactly at the limit is allowed
    }

    [Fact]
    public void Validate_UpdatedBeforeCreated_Throws()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var item = new LoginItem { Name = "Site", CreatedAt = now, UpdatedAt = now.AddMinutes(-5) };
        Assert.Throws<VaultValidationException>(() => VaultValidation.Validate(item));
    }
}

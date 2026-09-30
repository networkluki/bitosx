using BitosX.App.ViewModels;
using BitosX.Core.Generation;

namespace BitosX.App.Tests;

/// <summary>
/// The generator page. The password it shows always belongs to the settings it shows: before
/// 0.7.0, changing the length or the character classes left the old password standing — "Length
/// 128" above a password of 20 characters — and "Copy" copied that one.
/// </summary>
public sealed class GeneratorPageViewModelTests
{
    private static GeneratorPageViewModel NewPage(FakeClipboardService? clipboard = null)
        => new(new PasswordGenerator(), clipboard ?? new FakeClipboardService());

    [Fact]
    public void ThePage_OpensWithAPassword()
    {
        GeneratorPageViewModel page = NewPage();

        Assert.Equal(20, page.GeneratedPassword.Length);
        Assert.StartsWith("Strength: ", page.StrengthText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(128)]
    public void ChangingTheLength_MakesAPasswordOfThatLength(int length)
    {
        GeneratorPageViewModel page = NewPage();

        page.Length = length;

        Assert.Equal(length, page.GeneratedPassword.Length);
    }

    [Fact]
    public void TurningACharacterClassOff_MakesAPasswordWithoutIt()
    {
        GeneratorPageViewModel page = NewPage();
        page.Length = 64;

        page.IncludeSymbols = false;
        Assert.All(page.GeneratedPassword, c => Assert.True(char.IsAsciiLetterOrDigit(c)));

        page.IncludeDigits = false;
        Assert.All(page.GeneratedPassword, c => Assert.True(char.IsAsciiLetter(c)));

        page.IncludeUppercase = false;
        Assert.All(page.GeneratedPassword, c => Assert.True(char.IsAsciiLetterLower(c)));
    }

    [Fact]
    public void ExcludingAmbiguousCharacters_MakesAPasswordWithoutThem()
    {
        GeneratorPageViewModel page = NewPage();
        page.Length = 128;

        page.ExcludeAmbiguous = true;

        Assert.DoesNotContain(page.GeneratedPassword, c => "Il1O0o".Contains(c, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsThatCannotMakeAPassword_AreReported_AndNoPasswordIsShown()
    {
        GeneratorPageViewModel page = NewPage();
        page.IncludeSymbols = false;
        page.IncludeDigits = false;
        page.IncludeUppercase = false;

        page.IncludeLowercase = false;

        Assert.Equal(string.Empty, page.GeneratedPassword);
        Assert.Equal(string.Empty, page.StrengthText);
        Assert.False(string.IsNullOrWhiteSpace(page.ErrorMessage));

        page.IncludeLowercase = true;
        Assert.Equal(20, page.GeneratedPassword.Length);
        Assert.Null(page.ErrorMessage);
    }

    [Fact]
    public async Task Copy_CopiesThePasswordThatIsShown()
    {
        var clipboard = new FakeClipboardService();
        GeneratorPageViewModel page = NewPage(clipboard);
        page.Length = 48;

        await page.CopyCommand.ExecuteAsync(null);

        Assert.Equal(page.GeneratedPassword, clipboard.LastCopied);
        Assert.Equal(48, clipboard.LastCopied!.Length);
    }

    [Fact]
    public async Task ChangingASetting_TakesBackTheNoteThatThePasswordWasCopied()
    {
        GeneratorPageViewModel page = NewPage();
        await page.CopyCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(page.StatusMessage));

        page.Length = 21;

        // What was copied is no longer what is shown.
        Assert.True(string.IsNullOrEmpty(page.StatusMessage));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa", "Strength: Very weak")]
    [InlineData("", "")]
    public void TheRating_IsWrittenInWords(string password, string expected)
    {
        Assert.Equal(expected, StrengthDisplay.Rating(password));
    }

    [Fact]
    public void EveryRating_HasAName()
    {
        foreach (BitosX.Core.Security.PasswordStrengthLevel level in Enum.GetValues<BitosX.Core.Security.PasswordStrengthLevel>())
        {
            string name = StrengthDisplay.Name(level);
            Assert.DoesNotContain(name, c => char.IsUpper(c) && name.IndexOf(c, StringComparison.Ordinal) > 0);
            Assert.Equal(name.ToUpperInvariant(), StrengthDisplay.Phrase(level).ToUpperInvariant());
        }
    }
}

using BitosX.Core.Generation;

namespace BitosX.Core.Tests;

public sealed class PasswordGeneratorTests
{
    private readonly PasswordGenerator _generator = new();

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    public void Generate_ProducesRequestedLength(int length)
    {
        string password = _generator.Generate(new PasswordGenerationOptions { Length = length });
        Assert.Equal(length, password.Length);
    }

    [Fact]
    public void Generate_OnlyDigits_ContainsOnlyDigits()
    {
        var options = new PasswordGenerationOptions
        {
            Length = 32,
            IncludeLowercase = false,
            IncludeUppercase = false,
            IncludeDigits = true,
            IncludeSymbols = false,
        };

        string password = _generator.Generate(options);
        Assert.All(password, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void Generate_AllClasses_LongPassword_ContainsEachClass()
    {
        var options = new PasswordGenerationOptions { Length = 64 };
        string password = _generator.Generate(options);

        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, c => !char.IsLetterOrDigit(c));
    }

    [Fact]
    public void Generate_ExcludeAmbiguous_OmitsAmbiguousCharacters()
    {
        var options = new PasswordGenerationOptions { Length = 200, IncludeSymbols = false, ExcludeAmbiguous = true };
        string password = _generator.Generate(options);

        foreach (char ambiguous in "Il1O0o")
        {
            Assert.DoesNotContain(ambiguous, password);
        }
    }

    [Fact]
    public void Generate_NoClassSelected_Throws()
    {
        var options = new PasswordGenerationOptions
        {
            IncludeLowercase = false,
            IncludeUppercase = false,
            IncludeDigits = false,
            IncludeSymbols = false,
        };

        Assert.Throws<ArgumentException>(() => _generator.Generate(options));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(257)]
    public void Generate_InvalidLength_Throws(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _generator.Generate(new PasswordGenerationOptions { Length = length }));
    }

    [Fact]
    public void Generate_ProducesVaryingOutput()
    {
        var options = new PasswordGenerationOptions { Length = 32 };
        var results = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 50; i++)
        {
            results.Add(_generator.Generate(options));
        }

        // 50 random 32-char passwords should effectively never collide.
        Assert.Equal(50, results.Count);
    }
}

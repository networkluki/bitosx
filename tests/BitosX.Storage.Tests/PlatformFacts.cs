namespace BitosX.Storage.Tests;

/// <summary>
/// A test of something only Unix has. On Windows it is reported as skipped — not as passed, which
/// would say that something was checked.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Applies to Unix only.";
        }
    }
}

/// <summary>A test of something only Windows has. Elsewhere it is reported as skipped.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Applies to Windows only.";
        }
    }
}

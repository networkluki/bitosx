namespace BitosX.Platform.Files;

/// <summary>
/// The user chose a location in a file dialog that is not a file on this computer (a virtual or
/// sandboxed location without a local path). Told apart from a cancel, so the caller can say why
/// nothing happened instead of silently doing nothing.
/// </summary>
public sealed class FileLocationNotLocalException : Exception
{
    public FileLocationNotLocalException()
        : base("The chosen location is not a file on this computer.")
    {
    }

    public FileLocationNotLocalException(string message)
        : base(message)
    {
    }

    public FileLocationNotLocalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace BitosX.Core.ImportExport;

/// <summary>
/// Reads an import file: bytes in, the logins it holds out. This is the entry point of the import
/// library; it touches no file and no vault. The rules it follows are written down, with their
/// sources, in docs/IMPORT_EXPORT.md, which the web vault follows as well.
/// </summary>
public static class ImportParser
{
    /// <summary>
    /// Parses the content of a file. The format is detected from the content, never from a file
    /// name: the first character that is not a space, a tab or a line end decides. <c>{</c> or
    /// <c>[</c> is JSON, <c>&lt;</c> is XML, and anything else is a comma-separated file.
    /// </summary>
    /// <exception cref="ImportRefusedException">
    /// The file is not read at all: too large, not UTF-8, damaged, encrypted, not a format that is
    /// read, or over a limit.
    /// </exception>
    public static ParsedImport Parse(ReadOnlyMemory<byte> file)
    {
        if (file.Length > ImportLimits.MaxFileBytes)
        {
            throw new ImportRefusedException(ImportRefusal.TooLarge);
        }

        string text = ImportText.Decode(file, out ReadOnlyMemory<byte> utf8);

        int first = text.AsSpan().IndexOfAnyExcept(" \t\r\n");
        if (first < 0)
        {
            throw new ImportRefusedException(ImportRefusal.Empty);
        }

        return text[first] switch
        {
            '{' or '[' => JsonImportReader.Read(utf8),
            '<' => KeePassXmlImportReader.Read(text),
            _ => CsvImportReader.Read(text),
        };
    }
}

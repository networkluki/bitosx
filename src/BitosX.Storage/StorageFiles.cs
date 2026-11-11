using System.Buffers;
using BitosX.Crypto;

namespace BitosX.Storage;

/// <summary>
/// File primitives shared by the storage classes: a size-bounded read and an atomic, durable write.
/// </summary>
internal static class StorageFiles
{
    private const string TempSuffix = ".tmp";
    private const int TempTokenLength = 32; // Guid "N" format
    private static readonly SearchValues<char> LowerHexDigits = SearchValues.Create("0123456789abcdef");

    // A temporary file this old cannot belong to a write that is still in progress.
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reads a whole file, refusing anything larger than <paramref name="maxBytes"/>. The limit is
    /// enforced on the bytes actually read from the open handle, not only on a length reported before
    /// reading, so a file that grows after the check cannot get past it.
    /// </summary>
    /// <exception cref="UnsupportedVaultFormatException">The file is larger than the limit.</exception>
    public static async Task<byte[]> ReadBoundedAsync(
        string path, long maxBytes, string description, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, ReadOptions(FileOptions.Asynchronous | FileOptions.SequentialScan));
        await using (stream.ConfigureAwait(false))
        {
            ThrowIfTooLarge(stream.Length, maxBytes, description);

            using var content = new MemoryStream(capacity: (int)stream.Length);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                ThrowIfTooLarge(content.Length + read, maxBytes, description);
                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
    }

    /// <summary>Synchronous counterpart of <see cref="ReadBoundedAsync"/>.</summary>
    public static byte[] ReadBounded(string path, long maxBytes, string description)
    {
        using var stream = new FileStream(path, ReadOptions(FileOptions.SequentialScan));
        ThrowIfTooLarge(stream.Length, maxBytes, description);

        using var content = new MemoryStream(capacity: (int)stream.Length);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ThrowIfTooLarge(content.Length + read, maxBytes, description);
            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    /// <summary>Returns <paramref name="utf8"/> without a leading UTF-8 byte order mark, if it has one.</summary>
    public static ReadOnlySpan<byte> WithoutByteOrderMark(ReadOnlySpan<byte> utf8)
        => utf8.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? utf8[3..] : utf8;

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/> so that, whatever happens —
    /// an exception, a crash, or power loss — the path holds either the complete previous file or the
    /// complete new one:
    /// <list type="number">
    /// <item>The content is written to a new, uniquely named file in the same directory. It is
    /// created exclusively, so it can neither follow a link planted at a predictable name nor be
    /// shared with another writer, and is readable by the current user only.</item>
    /// <item>It is flushed to the physical disk, so the rename below can never expose a file whose
    /// data is still only in the operating system's cache.</item>
    /// <item>It is renamed over the target in a single step.</item>
    /// <item>On Unix the directory is flushed as well, so that the rename itself survives a power
    /// loss (see <see cref="UnixDirectory"/>).</item>
    /// </list>
    /// </summary>
    public static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveStaleTempFiles(path);

        string tempPath = NewTempPath(path);
        try
        {
            var stream = new FileStream(tempPath, WriteOptions(FileOptions.Asynchronous));
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(tempPath, path);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Synchronous counterpart of <see cref="WriteAtomicAsync"/>.</summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        RemoveStaleTempFiles(path);

        string tempPath = NewTempPath(path);
        try
        {
            using (var stream = new FileStream(tempPath, WriteOptions(FileOptions.None)))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(tempPath, path);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Creates a directory that, on Unix, only the current user can enter.</summary>
    public static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory); // inherits the per-user ACL of its parent
        }
        else
        {
            Directory.CreateDirectory(
                directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Deletes a file if it exists, ignoring failures: a leftover file is harmless.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// How long, in total, replacing a file on Windows waits for another program to let go of it.
    /// Windows refuses to rename over a file that is open; virus scanners, search indexers and backup
    /// tools open freshly written files for a moment. Without the retry a save fails whenever one of
    /// them happens to be looking (found by the Windows CI run of the settings checklist, where the
    /// test itself reads the file while it is being replaced).
    /// <para>The budget is counted in the pauses asked for, not on the clock, so the number of attempts
    /// is the same on a slow machine as on a fast one.</para>
    /// </summary>
    internal static readonly TimeSpan WindowsReplaceRetryBudget = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan FirstRetryPause = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LongestRetryPause = TimeSpan.FromMilliseconds(200);

    private static void MoveIntoPlace(string tempPath, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ReplaceRetryingWhileHeld(tempPath, path, static pause => Thread.Sleep(pause));
        }
        else
        {
            Replace(tempPath, path);
        }

        // On Unix the rename is a change to the directory, which is flushed on its own.
        UnixDirectory.TryFlush(Path.GetDirectoryName(path));
    }

    /// <summary>
    /// Renames <paramref name="tempPath"/> over <paramref name="path"/> in one step of the file system:
    /// <c>rename(2)</c> on Unix, <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c> on Windows (both
    /// files are in the same directory, so it is never a copy). If it fails, both files are what and
    /// where they were, which is what lets a caller try again, or give up and delete the temporary file.
    /// <para>Deliberately not <see cref="File.Replace(string, string, string?)"/>. On Windows that is
    /// <c>ReplaceFile</c>, which carries out several steps in one call, and whose documented failures
    /// include two (<c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c> and <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c>)
    /// after which the file that was to be replaced is no longer under its name: with no backup name
    /// given it is gone, and the new content exists only as the temporary file - the one a failed write
    /// deletes. A vault must never be able to end up there.</para>
    /// </summary>
    private static void Replace(string tempPath, string path) => File.Move(tempPath, path, overwrite: true);

    /// <summary>
    /// <see cref="Replace"/>, tried again after a growing pause while the failure is the kind an open
    /// handle of another program causes, until the pauses would add up to more than
    /// <see cref="WindowsReplaceRetryBudget"/>. A failed attempt changes nothing, so trying again cannot
    /// lose the file; the last failure is thrown. <paramref name="pause"/> does the waiting (tests pass
    /// their own, so that they depend neither on the clock nor on how busy the machine is).
    /// </summary>
    internal static void ReplaceRetryingWhileHeld(string tempPath, string path, Action<TimeSpan> pause)
    {
        TimeSpan waited = TimeSpan.Zero;
        TimeSpan next = FirstRetryPause;
        while (true)
        {
            try
            {
                Replace(tempPath, path);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && waited + next <= WindowsReplaceRetryBudget
                                       && File.Exists(tempPath))
            {
                pause(next);
                waited += next;
                next = next + next < LongestRetryPause ? next + next : LongestRetryPause;
            }
        }
    }

    private static string NewTempPath(string path) => $"{path}.{Guid.NewGuid():N}{TempSuffix}";

    /// <summary>
    /// Removes temporary files left behind by a write that was interrupted by a crash, so that old
    /// generations of the (encrypted) file do not accumulate next to it. Only files that carry this
    /// class's own naming pattern are touched, and only when they are too old to be in use.
    /// </summary>
    private static void RemoveStaleTempFiles(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            string fileName = Path.GetFileName(path);
            DateTime cutoff = DateTime.UtcNow - StaleTempAge;
            foreach (string candidate in Directory.EnumerateFiles(directory, fileName + ".*" + TempSuffix))
            {
                if (IsOwnTempFile(Path.GetFileName(candidate), fileName)
                    && File.GetLastWriteTimeUtc(candidate) < cutoff)
                {
                    TryDelete(candidate);
                }
            }

            // The fixed name used by versions before 0.5.1.
            string legacy = path + TempSuffix;
            if (File.Exists(legacy) && File.GetLastWriteTimeUtc(legacy) < cutoff)
            {
                TryDelete(legacy);
            }
        }
        catch (IOException)
        {
            // Housekeeping only; never a reason to fail a write.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsOwnTempFile(string candidateName, string fileName)
    {
        // "<fileName>.<32 hex digits>.tmp"
        if (candidateName.Length != fileName.Length + 1 + TempTokenLength + TempSuffix.Length
            || !candidateName.StartsWith(fileName + ".", StringComparison.Ordinal)
            || !candidateName.EndsWith(TempSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        return candidateName.AsSpan(fileName.Length + 1, TempTokenLength).IndexOfAnyExcept(LowerHexDigits) < 0;
    }

    private static FileStreamOptions ReadOptions(FileOptions options) => new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        Options = options,
    };

    private static FileStreamOptions WriteOptions(FileOptions options)
    {
        var result = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = options,
        };

        if (!OperatingSystem.IsWindows())
        {
            result.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return result;
    }

    private static void ThrowIfTooLarge(long size, long maxBytes, string description)
    {
        if (size > maxBytes)
        {
            throw new UnsupportedVaultFormatException(
                $"The {description} is too large ({size} bytes; limit {maxBytes}).");
        }
    }
}

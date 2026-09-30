using System.Runtime.InteropServices;
using System.Text;

namespace BitosX.Storage;

/// <summary>
/// Flushes a directory to the disk on Unix. Renaming a file over another changes the directory,
/// not the file, and a directory has a cache of its own: until it is flushed, a power loss can
/// bring back the name as it was before — pointing at the previous file. The file's content is
/// flushed separately, before the rename.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no call for this: it refuses to open a directory as a file. The three calls are made
/// directly to the C library.
/// </para>
/// <para>
/// Best effort. When this runs the new file is complete and in place; a flush that fails, or a
/// system where the calls are not available, leaves the write as durable as it was before 0.7.0
/// and is not a reason to report the write as failed.
/// </para>
/// </remarks>
internal static class UnixDirectory
{
    private const int ReadOnly = 0; // O_RDONLY, the same on Linux, macOS and the BSDs

    /// <summary>
    /// Flushes <paramref name="directory"/> to the disk. Returns whether it was done; never throws.
    /// Does nothing, and returns <see langword="false"/>, on Windows.
    /// </summary>
    public static bool TryFlush(string? directory)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            // The path as the C library wants it: UTF-8, ending in a zero.
            byte[] path = Encoding.UTF8.GetBytes(directory + '\0');
            int descriptor = NativeMethods.Open(path, ReadOnly);
            if (descriptor < 0)
            {
                return false;
            }

            try
            {
                return NativeMethods.Flush(descriptor) == 0;
            }
            finally
            {
                _ = NativeMethods.Close(descriptor);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern int Open(byte[] path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern int Flush(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static extern int Close(int descriptor);
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace L5xCommands;

/// <summary>
/// Serializes work on a path across processes, sessions and users by holding an exclusive
/// (<see cref="FileShare.None"/>) handle on a lock file inside <c>lockDirectory</c>.
/// Unlike a machine-wide named kernel object, only accounts that can write to that directory
/// (normally the repository's git directory) can create the lock file or open it to hold the lock.
/// </summary>
internal sealed class PathGuard : IDisposable
{
    private const int RetryDelayMilliseconds = 100;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly FileStream lockFile;

    private PathGuard(FileStream lockFile)
    {
        this.lockFile = lockFile;
    }

    public void Dispose() => lockFile.Dispose();

    internal static string GetLockFilePath(string path, string lockDirectory)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Path.Combine(Path.GetFullPath(lockDirectory), $"l5xgit-{Convert.ToHexString(hashBytes)}.lock");
    }

    private static PathGuard Acquire(string path, string lockDirectory, int millisecondsTimeout, string timeoutExceptionText)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);

        var lockFilePath = GetLockFilePath(path, lockDirectory);
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                // The lock is the exclusive open handle, not the file's existence, so the file left behind after a
                // run (or by a crashed process) does not block later runs. Write access is requested so that users
                // who can merely read the repository cannot open the leftover file and squat on the lock.
                var lockFile = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                    ShareWithGroupIfDirectoryIs(lockFile, lockDirectory);
                return new PathGuard(lockFile);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                var remaining = millisecondsTimeout == Timeout.Infinite
                    ? RetryDelayMilliseconds
                    : millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds;

                if (remaining <= 0)
                    throw new TimeoutException(timeoutExceptionText, ex);

                Thread.Sleep(Math.Min(RetryDelayMilliseconds, remaining));
            }
        }
    }

    /// <summary>
    /// The lock file's mode comes from the creator's umask, not git's shared-repository settings, so a
    /// 0644 file would lock other members of a group-writable repository out. Mirror the directory's
    /// group-write bit (never "other" access) when this process owns the file.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ShareWithGroupIfDirectoryIs(FileStream lockFile, string lockDirectory)
    {
        const UnixFileMode groupReadWrite = UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        try
        {
            if (!File.GetUnixFileMode(lockDirectory).HasFlag(UnixFileMode.GroupWrite))
                return;

            var mode = File.GetUnixFileMode(lockFile.SafeFileHandle);
            if ((mode & groupReadWrite) != groupReadWrite)
                File.SetUnixFileMode(lockFile.SafeFileHandle, mode | groupReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Not the owner; the owner widens the mode on its next run.
        }
    }

    private static bool IsLockContention(IOException ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
            return false;

        if (!OperatingSystem.IsWindows())
            return true;

        var win32Error = ex.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation;
    }

    public static async Task Guard(string path, string lockDirectory, int millisecondsTimeout, string timeoutExceptionText, Func<Task> action)
    {
        using var guard = Acquire(path, lockDirectory, millisecondsTimeout, timeoutExceptionText);
        await action();
    }

    public static async Task<T> Guard<T>(string path, string lockDirectory, int millisecondsTimeout, string timeoutExceptionText, Func<Task<T>> func)
    {
        using var guard = Acquire(path, lockDirectory, millisecondsTimeout, timeoutExceptionText);
        return await func();
    }

    public static void Guard(string path, string lockDirectory, int millisecondsTimeout, string timeoutExceptionText, Action action)
    {
        using var guard = Acquire(path, lockDirectory, millisecondsTimeout, timeoutExceptionText);
        action();
    }

    public static T Guard<T>(string path, string lockDirectory, int millisecondsTimeout, string timeoutExceptionText, Func<T> func)
    {
        using var guard = Acquire(path, lockDirectory, millisecondsTimeout, timeoutExceptionText);
        return func();
    }
}

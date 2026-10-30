using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace L5xCommands;

/// <summary>
/// Serializes work on a path across processes, sessions and users by holding an exclusive handle
/// on a lock file in <c>lockDirectory</c>. Only accounts with access to that directory can create
/// or hold the lock, unlike a machine-wide named kernel object.
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
        return Path.Combine(lockDirectory, $"l5xgit-{Convert.ToHexString(hashBytes)}.lock");
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
                // The file is intentionally left behind: the lock is the open handle, not the file's existence.
                return new PathGuard(new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
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

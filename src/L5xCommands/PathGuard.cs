using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace L5xCommands;

internal sealed class PathGuard : IDisposable
{
    private readonly Semaphore semaphore;

    // Session-local and current-user-only, so another account cannot pre-create or hold the lock.
    private static readonly NamedWaitHandleOptions SemaphoreOptions = new()
    {
        CurrentUserOnly = true,
        CurrentSessionOnly = true,
    };

    private PathGuard(string path)
    {
        try
        {
            semaphore = new Semaphore(1, 1, GetSemaphoreName(path), SemaphoreOptions);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Unable to create lock for \"{Path.GetFullPath(path)}\": a lock object with the same name exists but is not owned by the current user.", ex);
        }
    }

    private bool Acquire(int millisecondsTimeout) => semaphore.WaitOne(millisecondsTimeout);
    private void Release() => semaphore.Release();
    public void Dispose() => semaphore.Dispose();

    private static string GetSemaphoreName(string path)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        var uniqueString = Convert.ToHexString(hashBytes);
        return $"L5xSemaphore_{uniqueString}";
    }

    public static async Task Guard(string path, int millisecondsTimeout, string timeoutExceptionText, Func<Task> action)
    {
        using var mutex = new PathGuard(path);
        bool acquired = mutex.Acquire(millisecondsTimeout);

        if (!acquired)
            throw new TimeoutException(timeoutExceptionText);

        try
        {
            await action();
        }
        finally
        {
            mutex.Release();
        }
    }

    public static async Task<T> Guard<T>(string path, int millisecondsTimeout, string timeoutExceptionText, Func<Task<T>> func)
    {
        using var mutex = new PathGuard(path);
        bool acquired = mutex.Acquire(millisecondsTimeout);

        if (!acquired)
            throw new TimeoutException(timeoutExceptionText);

        try
        {
            return await func();
        }
        finally
        {
            mutex.Release();
        }
    }

    public static void Guard(string path, int millisecondsTimeout, string timeoutExceptionText, Action action)
    {
        using var mutex = new PathGuard(path);
        bool acquired = mutex.Acquire(millisecondsTimeout);

        if (!acquired)
            throw new TimeoutException(timeoutExceptionText);

        try
        {
            action();
        }
        finally
        {
            mutex.Release();
        }
    }

    public static T Guard<T>(string path, int millisecondsTimeout, string timeoutExceptionText, Func<T> func)
    {
        using var mutex = new PathGuard(path);
        bool acquired = mutex.Acquire(millisecondsTimeout);

        if (!acquired)
            throw new TimeoutException(timeoutExceptionText);

        try
        {
            return func();
        }
        finally
        {
            mutex.Release();
        }
    }
}
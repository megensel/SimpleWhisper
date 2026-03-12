using System.Threading;

namespace SimpleWhisper.Helpers;

/// <summary>
/// Ensures only a single instance of the application is running at any time
/// by using a system-wide named <see cref="Mutex"/>.
/// </summary>
public static class SingleInstanceHelper
{
    private const string MutexName = "SimpleWhisper_SingleInstance";

    private static Mutex? _mutex;

    /// <summary>
    /// Attempts to acquire the single-instance mutex.
    /// Returns <c>true</c> if another instance already owns the mutex (i.e., the app is already running).
    /// Returns <c>false</c> if this instance successfully acquired ownership.
    /// </summary>
    /// <returns>
    /// <c>true</c> if another instance is already running; <c>false</c> if this is the first instance.
    /// </returns>
    public static bool IsAlreadyRunning()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (createdNew)
        {
            // This instance owns the mutex; it is the first (and only) instance.
            return false;
        }

        // Another instance already owns the mutex.
        _mutex.Dispose();
        _mutex = null;
        return true;
    }

    /// <summary>
    /// Releases the single-instance mutex. Call this during application shutdown
    /// to allow a new instance to start cleanly.
    /// </summary>
    public static void Release()
    {
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex was not owned by the calling thread; safe to ignore.
            }
            finally
            {
                _mutex.Dispose();
                _mutex = null;
            }
        }
    }
}

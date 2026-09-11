namespace VoiceTranscript.Core.Audio;

/// <summary>Retries an optional capture mode once, including failures during stream startup.</summary>
public static class CaptureStartup
{
    public static async Task<T> StartAsync<T>(
        bool tryPreferred,
        Func<bool, Task<T>> create,
        Func<T, Task> start,
        Action<Exception> onFallback,
        CancellationToken cancellationToken = default) where T : class, IDisposable
    {
        async Task<T> Attempt(bool preferred)
        {
            T? resource = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                resource = await create(preferred);
                cancellationToken.ThrowIfCancellationRequested();
                await start(resource);
                cancellationToken.ThrowIfCancellationRequested();
                return resource;
            }
            catch
            {
                // Preserve the startup error even if a disconnected device also rejects cleanup.
                try { resource?.Dispose(); }
                catch (Exception) { }
                throw;
            }
        }

        if (tryPreferred)
        {
            try { return await Attempt(true); }
            catch (Exception e) when (e is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                onFallback(e);
            }
        }

        return await Attempt(false);
    }
}

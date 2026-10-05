namespace VMixPlayerController;

// Called on the UI thread. Pending work is cancelled when its target changes;
// failed idempotent Play/Pause commands are retried on subsequent snapshots.
internal sealed class AtemPlaybackAutomation : IDisposable
{
    private string identity = "";
    private bool? desired;
    private bool? applied;
    private bool hasBeenOnAir;
    private bool running;
    private long generation;
    private CancellationTokenSource pending = new();
    private DateTime retryAfter;
    public string LastError { get; private set; } = "";
    public bool IsPending => desired.HasValue && applied != desired;

    public void Reset()
    {
        pending.Cancel();
        pending.Dispose();
        pending = new();
        generation++;
        identity = "";
        desired = applied = null;
        hasBeenOnAir = false;
        LastError = "";
        retryAfter = DateTime.MinValue;
    }

    public async Task ReconcileAsync(string targetIdentity, bool? onAir,
        Func<bool, CancellationToken, Task> send, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        if (identity != targetIdentity || desired != onAir)
        {
            // Preserve a failed Pause across reconnections, but not across input changes (Reset).
            identity = targetIdentity;
            desired = onAir;
            pending.Cancel();
            pending.Dispose();
            pending = new();
            generation++;
            applied = null;
            retryAfter = DateTime.MinValue;
        }
        if (onAir == true) hasBeenOnAir = true;
        if (onAir == false && !hasBeenOnAir) applied = false;
        if (!onAir.HasValue || applied == onAir || running || time < retryAfter) return;
        var attemptGeneration = generation;
        var token = pending.Token;
        running = true;
        try
        {
            await send(onAir.Value, token);
            if (attemptGeneration != generation) return;
            applied = onAir;
            LastError = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (attemptGeneration != generation) return;
            LastError = ex.Message;
            retryAfter = time.AddSeconds(2);
            LogService.Write("WARN", $"ATEM AUTO: orden pendiente; se reintentará: {ex.Message}");
        }
        finally { running = false; }
    }

    public void Dispose()
    {
        pending.Cancel();
        pending.Dispose();
        generation++;
    }
}

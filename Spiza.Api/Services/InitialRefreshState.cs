namespace Spiza.Api.Services;

public sealed class InitialRefreshState
{
    private int completed;

    public bool IsCompleted => Volatile.Read(ref completed) == 1;

    public void MarkCompleted() => Interlocked.Exchange(ref completed, 1);
}

namespace LAM.Core.Riot;

/// <summary>
/// Keeps request rates under Riot's published development-key limits: 20 per second and 100 per two
/// minutes, both enforced at once.
///
/// Staying under them matters more than it looks. Exceeding a limit earns a 429 with a penalty
/// window, and the age estimate deliberately issues a burst of a dozen requests per account — run
/// that across a large collection without pacing and every later refresh starts failing.
/// </summary>
public sealed class TokenBucket
{
    private readonly int _shortLimit;
    private readonly TimeSpan _shortWindow;
    private readonly int _longLimit;
    private readonly TimeSpan _longWindow;

    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public TokenBucket(
        int shortLimit = 20,
        TimeSpan? shortWindow = null,
        int longLimit = 100,
        TimeSpan? longWindow = null)
    {
        _shortLimit = shortLimit;
        _shortWindow = shortWindow ?? TimeSpan.FromSeconds(1);
        _longLimit = longLimit;
        _longWindow = longWindow ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Blocks until another request may be sent, then records it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                Trim(now);

                if (now < _pausedUntil)
                {
                    wait = _pausedUntil - now;
                }
                else if (CountSince(now - _shortWindow) >= _shortLimit)
                {
                    wait = _shortWindow / 4;
                }
                else if (_recent.Count >= _longLimit)
                {
                    // Wait exactly until the oldest request falls out of the long window, rather
                    // than polling — this branch can otherwise busy-wait for a minute or more.
                    wait = _recent.Peek() + _longWindow - now;
                }
                else
                {
                    _recent.Enqueue(now);
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
        }
    }

    /// <summary>Honours a 429's Retry-After by refusing to send anything until it elapses.</summary>
    public void PauseFor(TimeSpan duration)
    {
        var until = DateTimeOffset.UtcNow + duration;
        if (until > _pausedUntil) _pausedUntil = until;
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _longWindow;
        while (_recent.Count > 0 && _recent.Peek() < cutoff) _recent.Dequeue();
    }

    private int CountSince(DateTimeOffset since) => _recent.Count(timestamp => timestamp >= since);
}

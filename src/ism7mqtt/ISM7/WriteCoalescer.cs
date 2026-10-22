using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ism7mqtt
{
    /// <summary>
    /// Holds a write until its target has been quiet for a moment, so that a burst of commands for the same parameter
    /// (a "−" button pressed thirty times) reaches the ISM7 as one write with the last value. Each key is a parameter;
    /// a newer command for the same key replaces the one still waiting.
    /// </summary>
    public sealed class WriteCoalescer
    {
        private readonly TimeSpan _quiet;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new ConcurrentDictionary<string, CancellationTokenSource>();

        public WriteCoalescer(TimeSpan quiet)
        {
            _quiet = quiet;
        }

        /// <summary>
        /// Schedules <paramref name="send"/> for <paramref name="key"/> after the quiet period, replacing whatever was
        /// waiting for the same key. The returned task completes when this command was sent or replaced; callers
        /// that must not wait for the quiet period need not await it.
        /// </summary>
        public Task ScheduleAsync(string key, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
        {
            var mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pending.AddOrUpdate(key, mine, (_, previous) =>
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // It finished waiting at this same moment; it is sent or dropped by its own run.
                }
                return mine;
            });
            return RunAsync(key, mine, send, cancellationToken);
        }

        private async Task RunAsync(string key, CancellationTokenSource mine, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_quiet, mine.Token);
            }
            catch (OperationCanceledException)
            {
                // Replaced, or the client is stopping: in the second case the entry is still ours to remove.
                _pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, mine));
                mine.Dispose();
                return;
            }
            // Only the newest command for the key is sent; one replaced in the meantime has been cancelled above.
            if (!_pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, mine)))
            {
                mine.Dispose();
                return;
            }
            mine.Dispose();
            await send(cancellationToken);
        }
    }
}

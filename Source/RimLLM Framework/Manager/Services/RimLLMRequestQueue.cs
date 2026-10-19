using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager;

/// <summary>
/// 管理 API 請求的優先權佇列與並行限流。
/// 依據優先級（數值大者先行）與先進先出（FIFO）規則調度執行。
/// </summary>
/// <remarks>
/// 核心是「取得一個併發名額」而非「代為執行一段工作」。
/// 串流請求必須在整個列舉期間持有名額，而列舉是由呼叫端驅動的，
/// 佇列無從得知它何時結束——把名額交給呼叫端以 using 持有，是唯一能同時
/// 涵蓋串流與非串流的形狀。
/// </remarks>
internal class RimLLMRequestQueue
{
    private readonly IRimLLMSettings _settings;
    private readonly object _queueLock = new();
    private readonly List<QueueEntry> _waitingQueue = [];
    private int _activeRequests;
    private long _enqueueSequence;

    /// <summary>
    /// 佇列實體定義。
    /// </summary>
    private class QueueEntry : IComparable<QueueEntry>
    {
        public int Priority { get; set; }
        public TaskCompletionSource<IDisposable> Tcs { get; set; }

        /// <summary>
        /// 入列序號。先前以 DateTime.UtcNow 判先後，同一個時鐘刻度內入列的請求鍵值相等，
        /// BinarySearch 命中後會插在既有項目前面，突發時同優先級變成後進先出。
        /// </summary>
        public long Sequence { get; set; }

        public int CompareTo(QueueEntry other)
        {
            // 優先級高（數值大）的排在前面
            int cmp = other.Priority.CompareTo(this.Priority);
            if (cmp == 0)
            {
                // 優先級相同時，先入列的排在前面（FIFO）
                return this.Sequence.CompareTo(other.Sequence);
            }
            return cmp;
        }
    }

    /// <summary>釋放時歸還名額並重新抽取佇列。</summary>
    private sealed class Slot : IDisposable
    {
        private RimLLMRequestQueue _queue;

        public Slot(RimLLMRequestQueue queue)
        {
            _queue = queue;
        }

        public void Dispose()
        {
            RimLLMRequestQueue queue = Interlocked.Exchange(ref _queue, null);
            if (queue == null) return; // 重複釋放不可重複歸還名額

            lock (queue._queueLock)
            {
                queue._activeRequests--;
            }
            queue.ProcessQueue();
        }
    }

    public RimLLMRequestQueue(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 排隊取得一個併發名額。名額在回傳值被 Dispose 時歸還，
    /// 因此呼叫端必須以 using 包住整段需要佔用名額的工作。
    /// </summary>
    public async Task<IDisposable> AcquireSlotAsync(int priority, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new QueueEntry
        {
            Priority = priority,
            Tcs = tcs,
            Sequence = Interlocked.Increment(ref _enqueueSequence)
        };

        // 一開始就已取消：沿用「await 一個已取消的 Task」的形狀，
        // 讓呼叫端拿到的仍是 TaskCanceledException 而非裸的 OperationCanceledException。
        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetCanceled(cancellationToken);
            return await tcs.Task.ConfigureAwait(false);
        }

        // 必須先入列再註冊取消：反過來的話，取消若發生在註冊與入列之間，
        // 回呼會找不到項目而不做任何事，隨後入列的項目就再也不會被取消。
        lock (_queueLock)
        {
            int index = _waitingQueue.BinarySearch(entry);
            if (index < 0)
            {
                index = ~index;
            }
            _waitingQueue.Insert(index, entry);
        }

        CancellationTokenRegistration registration = default;
        if (cancellationToken != default)
        {
            registration = cancellationToken.Register(() =>
            {
                lock (_queueLock)
                {
                    // 只有還在等待中才由取消端結束；已經拿到名額的交由 Dispose 歸還。
                    if (!_waitingQueue.Remove(entry)) return;
                }
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        ProcessQueue();

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    private void ProcessQueue()
    {
        List<QueueEntry> toGrant = null;
        lock (_queueLock)
        {
            int limit = Math.Max(1, _settings.MaxConcurrentRequests);
            while (_activeRequests < limit && _waitingQueue.Count > 0)
            {
                var entry = _waitingQueue[0];
                _waitingQueue.RemoveAt(0);
                _activeRequests++;
                if (toGrant == null) toGrant = [];
                toGrant.Add(entry);
            }
        }

        if (toGrant == null) return;

        // 於鎖外發放名額，大幅縮減全域鎖持有時間。
        foreach (var entry in toGrant)
        {
            var slot = new Slot(this);
            if (!entry.Tcs.TrySetResult(slot))
            {
                // 等待端已經被取消，發不出去的名額必須立刻歸還，否則名額會永久流失。
                slot.Dispose();
            }
        }
    }
}

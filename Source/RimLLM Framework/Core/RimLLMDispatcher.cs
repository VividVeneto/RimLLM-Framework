using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace RimLLM_Framework.Core;

/// <summary>
/// Unity 主線程派遣器 (Main Thread Dispatcher)。
/// 確保所有在背景線程（如 API 請求完成）的 Callback 能夠安全地回到 Unity 主線程執行，防止 TPS 劇烈震盪與 Unity API 非線程安全崩潰。
/// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
public class RimLLMDispatcher : MonoBehaviour
{
    /// <summary>佇列長度上限。超過後丟棄最舊的項目，避免高 chunk 率時無限成長。</summary>
    private const int MaxQueuedActions = 4096;

    /// <summary>單一幀最多執行的項目數。</summary>
    private const int MaxActionsPerFrame = 128;

    /// <summary>單一幀執行的時間預算（毫秒）。</summary>
    private const long FrameBudgetMs = 2;

    private static RimLLMDispatcher _instance;
    private static readonly object InstanceLock = new();
    private static readonly ConcurrentQueue<QueuedAction> ExecutionQueue = new();
    private static int _queuedCount;
    private static int _droppedCount;

    /// <summary>
    /// 是否已有可運作的主線程 pump（即元件已 Awake 且尚未 OnDestroy）。
    /// </summary>
    private static volatile bool _hasPump;

    /// <summary>
    /// 於主線程建立派遣器單例。必須由 Mod 進入點在主線程呼叫。
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_instance != null) return;

        lock (InstanceLock)
        {
            if (_instance != null) return;

            GameObject go = new GameObject("RimLLMDispatcher");
            _instance = go.AddComponent<RimLLMDispatcher>();
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    /// <summary>
    /// 將 Action 排入佇列，以便在下一次 Unity Update 週期中於主線程執行。
    /// 無可運作的 pump 時（單元測試、headless）改為同步執行。
    /// </summary>
    public static void EnqueueOnMainThread(Action action)
    {
        if (action == null) return;

        if (_hasPump)
        {
            TryEnqueueBounded(action);
            return;
        }

        // 沒有可運作的 pump（單元測試、headless 反射執行，或元件尚未建立）。
        // 此時同步執行是唯一選擇，但這是明確的分支而非以例外控制流程。
        action.Invoke();
    }

    /// <summary>
    /// 將非同步 Func 排入佇列，並以非同步方式等待其在主線程執行後的結果。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(Func<System.Threading.Tasks.Task<T>> func)
    {
        return EnqueueOnMainThreadAsync(func, CancellationToken.None);
    }

    /// <summary>
    /// 將非同步 Func 排入佇列；尚未派送時取消會取消等待並略過委派，已開始的委派則自行處理取消。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(
        Func<System.Threading.Tasks.Task<T>> func,
        CancellationToken cancellationToken)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled<T>(cancellationToken);
        }

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0; // 0 = 排隊中、1 = 已派送、2 = 派送前取消
        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    registration.Dispose();
                }
            });
        }

        // Register 可能在 token 已取消時同步執行 callback。
        if (Volatile.Read(ref state) == 2)
        {
            registration.Dispose();
            return tcs.Task;
        }

        // 內層刻意不加 ConfigureAwait(false)：本委派由 Update 在主線程上啟動，
        // 保留 SynchronizationContext 才能讓 func 的續行回到主線程。
        EnqueueWaitable(async () =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            {
                registration.Dispose();
                return;
            }

            try
            {
                T result = await func();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        }, tcs, () => registration.Dispose());

        return tcs.Task;
    }

    /// <summary>
    /// 將同步 Func 排入佇列，並以非同步方式等待其在主線程執行後的結果。
    /// </summary>
    public static System.Threading.Tasks.Task<T> EnqueueOnMainThreadAsync<T>(Func<T> func)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

        EnqueueWaitable(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, tcs);

        return tcs.Task;
    }

    /// <summary>
    /// 排入一個「有人在等結果」的委派。與 <see cref="EnqueueOnMainThread"/> 的差別在於：
    /// 佇列溢位而被丟棄時會讓對應的 <paramref name="tcs"/> 失敗，避免等待端永遠掛住。
    /// </summary>
    private static void EnqueueWaitable<T>(
        Action action,
        System.Threading.Tasks.TaskCompletionSource<T> tcs,
        Action onDropped = null)
    {
        if (_hasPump)
        {
            TryEnqueueBounded(new QueuedAction(action, () =>
            {
                tcs.TrySetException(
                    new InvalidOperationException($"[RimLLM] 主線程佇列已滿（上限 {MaxQueuedActions}），待執行的主線程工作被丟棄。"));
                onDropped?.Invoke();
            }));
            return;
        }

        action.Invoke();
    }

    /// <summary>
    /// 有界入列。超過上限時丟棄最舊項目並計數，回傳是否發生丟棄。
    /// </summary>
    /// <remarks>
    /// 遙測寫入也是透過本派遣器排入的；丟棄最舊項目理論上可能丟掉一次遙測寫入請求。
    /// 因為遙測本身有 15 秒節流且關閉遊戲時會強制 flush，此風險可接受。
    /// </remarks>
    internal static bool TryEnqueueBounded(Action action)
    {
        if (action == null) return false;
        return TryEnqueueBounded(new QueuedAction(action, null));
    }

    private static bool TryEnqueueBounded(QueuedAction item)
    {
        ExecutionQueue.Enqueue(item);
        int count = Interlocked.Increment(ref _queuedCount);

        bool dropped = false;
        while (count > MaxQueuedActions && ExecutionQueue.TryDequeue(out QueuedAction discarded))
        {
            count = Interlocked.Decrement(ref _queuedCount);
            dropped = true;

            // 被丟棄的項目若有人在等它的結果，必須通知，否則對方會永遠掛在 await 上。
            discarded.NotifyDropped();

            int totalDropped = Interlocked.Increment(ref _droppedCount);
            if (totalDropped % 100 == 1)
            {
                RimLLMLog.Warning($"[RimLLM] Main-thread queue reached its limit of {MaxQueuedActions}; {totalDropped} callbacks dropped so far.");
            }
        }

        return !dropped;
    }

    /// <summary>佇列項目：待執行的委派，以及（可選的）被丟棄時要通知的等待者。</summary>
    private struct QueuedAction
    {
        private readonly Action _run;
        private readonly Action _onDropped;

        public QueuedAction(Action run, Action onDropped)
        {
            _run = run;
            _onDropped = onDropped;
        }

        public void Run()
        {
            _run?.Invoke();
        }

        public void NotifyDropped()
        {
            _onDropped?.Invoke();
        }
    }

    /// <summary>
    /// 依項目數與時間預算清空佇列，回傳實際執行的項目數。
    /// 佇列為空時直接回傳，不啟動 Stopwatch，避免每幀一次的計時器配置。
    /// 時間比較使用 ElapsedTicks（高解析度）：ElapsedMilliseconds 在 Windows 上粒度約 15ms，
    /// 2ms 預算形同虛設。
    /// </summary>
    internal static int DrainWithBudget(int maxActions, long budgetMs)
    {
        if (ExecutionQueue.IsEmpty || maxActions <= 0 || budgetMs <= 0)
        {
            return 0;
        }

        // OnDestroy 以 long.MaxValue 表示「不設時間上限」；直接跳過時間比較以免換算溢位。
        bool checkTime = budgetMs != long.MaxValue;
        long budgetTicks = checkTime ? budgetMs * Stopwatch.Frequency / 1000L : 0L;
        var stopwatch = Stopwatch.StartNew();
        int processed = 0;

        while (processed < maxActions &&
               (!checkTime || stopwatch.ElapsedTicks < budgetTicks) &&
               ExecutionQueue.TryDequeue(out QueuedAction action))
        {
            Interlocked.Decrement(ref _queuedCount);
            processed++;

            try
            {
                action.Run();
            }
            catch (Exception ex)
            {
                RimLLMLog.Error($"[RimLLM] Main-thread callback failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        return processed;
    }

    /// <summary>
    /// 測試用：清空佇列與計數。
    /// </summary>
    internal static void ResetQueueForTests()
    {
#pragma warning disable S108 // reason: 測試用清空佇列，空區塊為設計意圖，僅需出列副作用
        while (ExecutionQueue.TryDequeue(out _))
        {
            // 刻意清空佇列，僅需出列副作用，無需額外處理。
        }
#pragma warning restore S108
        Interlocked.Exchange(ref _queuedCount, 0);
        Interlocked.Exchange(ref _droppedCount, 0);
    }

    internal static int QueuedCount => Volatile.Read(ref _queuedCount);

    /// <summary>主線程 pump 是否運作中（遊戲內整合測試用）。</summary>
    internal static bool HasPump => _hasPump;

#pragma warning disable S2325 // reason: Unity Message 必須為實例方法，無法 static
#pragma warning disable S2696 // reason: Unity 單例在實例生命週期中設定靜態實例
    private void Awake()
    {
        _hasPump = true;
    }

    private void Update()
    {
        // 每幀受項目數與時間預算雙重限制，避免長回應的大量 chunk 在單一幀內全部執行造成卡頓。
        DrainWithBudget(MaxActionsPerFrame, FrameBudgetMs);
    }

    private void OnDestroy()
    {
        _hasPump = false;

        // 先把剩餘項目排乾，避免關閉時遺失遙測寫入等重要回呼。
        int drained = DrainWithBudget(int.MaxValue, long.MaxValue);
        if (drained > 0)
        {
            RimLLMLog.Message($"[RimLLM] Dispatcher drained {drained} pending callbacks before shutdown.");
        }

        if (_instance == this)
        {
            _instance = null;
        }
    }
#pragma warning restore S2696
#pragma warning restore S2325
}
#pragma warning restore S101

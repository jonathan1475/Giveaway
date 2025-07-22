using PKHeX.Core;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SysBot.Pokemon;

/// <summary>
/// Manages multiple trade queues and handles trade distribution logic.
/// </summary>
public class TradeQueueManager<T> where T : PKM, new()
{
    public readonly PokeTradeQueue<T>[] AllQueues;
    public readonly List<Action<PokeRoutineExecutorBase, PokeTradeDetail<T>>> Forwarders = [];
    public readonly TradeQueueInfo<T> Info;

    public BatchTradeTracker<T> BatchTracker => BatchTradeTracker<T>.Instance;
    private readonly PokeTradeHub<T> Hub;

    // Individual queues for different trade types
    private readonly PokeTradeQueue<T> Batch = new(PokeTradeType.Batch);
    private readonly PokeTradeQueue<T> Clone = new(PokeTradeType.Clone);
    private readonly PokeTradeQueue<T> Dump = new(PokeTradeType.Dump);
    private readonly PokeTradeQueue<T> FixOT = new(PokeTradeType.FixOT);
    private readonly PokeTradeQueue<T> Seed = new(PokeTradeType.Seed);
    private readonly PokeTradeQueue<T> Trade = new(PokeTradeType.Specific);

    private readonly PokeRoutineType[] _flexDequeueOrder =
    [
        PokeRoutineType.SeedCheck,
        PokeRoutineType.Clone,
        PokeRoutineType.Dump,
        PokeRoutineType.FixOT,
        PokeRoutineType.LinkTrade,
        PokeRoutineType.Batch
    ];

    public TradeQueueManager(PokeTradeHub<T> hub)
    {
        Hub = hub;
        Info = new(hub);
        AllQueues = [Seed, Dump, Clone, FixOT, Trade, Batch];

        foreach (var q in AllQueues)
            q.Queue.Settings = hub.Config.Favoritism;
    }

    public void ClearAll()
    {
        foreach (var q in AllQueues)
            q.Clear();
    }

    public void Enqueue(PokeRoutineType type, PokeTradeDetail<T> detail, uint priority) =>
        GetQueue(type).Enqueue(detail, priority);

    public PokeTradeQueue<T> GetQueue(PokeRoutineType type) => type switch
    {
        PokeRoutineType.SeedCheck => Seed,
        PokeRoutineType.Clone => Clone,
        PokeRoutineType.Dump => Dump,
        PokeRoutineType.FixOT => FixOT,
        PokeRoutineType.Batch => Batch,
        _ => Trade,
    };

    public void StartTrade(PokeRoutineExecutorBase b, PokeTradeDetail<T> detail)
    {
        foreach (var f in Forwarders)
            f.Invoke(b, detail);
    }

    public bool TryDequeue(PokeRoutineType type, out PokeTradeDetail<T> detail, out uint priority, string botName)
    {
        // Handle FlexTrade differently
        if (type == PokeRoutineType.FlexTrade)
            return GetFlexDequeue(out detail, out priority, botName);

        var queue = GetQueue(type);
        if (!queue.TryPeek(out detail, out priority))
            return false;

        bool isBatchTrade = detail.TotalBatchTrades > 1;

        // For batch trades, check if we can process it
        if (isBatchTrade && !BatchTracker.CanProcessBatchTrade(detail))
            return false;

        // Try to dequeue the trade
        if (!queue.TryDequeue(out detail, out priority))
            return false;

        // For batch trades, try to claim it
        if (isBatchTrade && !BatchTracker.TryClaimBatchTrade(detail, botName))
        {
            queue.Enqueue(detail, priority);
            return false;
        }

        return true;
    }

    public void CompleteTrade(PokeRoutineExecutorBase b, PokeTradeDetail<T> detail)
    {
        BatchTracker.CompleteBatchTrade(detail);
    }

    private bool GetFlexDequeue(out PokeTradeDetail<T> detail, out uint priority, string botName)
    {
        var cfg = Hub.Config.Queues;
        return cfg.FlexMode == FlexYieldMode.LessCheatyFirst
            ? GetFlexDequeueOld(out detail, out priority, botName)
            : GetFlexDequeueWeighted(cfg, out detail, out priority, botName);
    }

    private bool GetFlexDequeueOld(out PokeTradeDetail<T> detail, out uint priority, string botName)
    {
        foreach (var type in _flexDequeueOrder)
        {
            if (TryDequeue(type, out detail, out priority, botName))
                return true;
        }

        detail = default!;
        priority = default;
        return false;
    }

    private bool GetFlexDequeueWeighted(QueueSettings cfg, out PokeTradeDetail<T> detail, out uint priority, string botName)
    {
        PokeTradeQueue<T>? preferredQueue = null;
        long bestWeight = 0;
        uint bestPriority = uint.MaxValue;
        PokeTradeDetail<T>? bestDetail = null;

        foreach (var q in AllQueues)
        {
            if (!q.TryPeek(out var qDetail, out var qPriority))
                continue;

            if (qDetail.TotalBatchTrades > 1 && !BatchTracker.CanProcessBatchTrade(qDetail))
                continue;

            if (qPriority > bestPriority)
                continue;

            var weight = cfg.GetWeight(q.Count, qDetail.Time, q.Type);
            if (qPriority >= bestPriority && weight <= bestWeight)
                continue;

            bestWeight = weight;
            bestPriority = qPriority;
            preferredQueue = q;
            bestDetail = qDetail;
        }

        if (preferredQueue is null || bestDetail is null)
        {
            detail = default!;
            priority = default;
            return false;
        }

        if (!preferredQueue.TryDequeue(out detail, out priority))
            return false;

        if (detail.TotalBatchTrades > 1 && !BatchTracker.TryClaimBatchTrade(detail, botName))
        {
            preferredQueue.Enqueue(detail, priority);
            return false;
        }

        return true;
    }

    public bool TryDequeueLedy(out PokeTradeDetail<T> detail, bool force = false)
    {
        detail = default!;
        var cfg = Hub.Config.Distribution;

        if ((!cfg.DistributeWhileIdle && !force) || Hub.Ledy.Pool.Count == 0)
            return false;

        var random = Hub.Ledy.Pool.GetRandomPoke();
        var code = cfg.RandomCode ? Hub.Config.Trade.GetRandomTradeCode() : cfg.TradeCode;
        var lgcode = GetDefaultLGCode();

        var trainer = new PokeTradeTrainerInfo("Random Distribution");
        detail = new(random, trainer, PokeTradeHub<T>.LogNotifier, PokeTradeType.Random, code, false, lgcode);
        return true;
    }

    private static List<Pictocodes> GetDefaultLGCode() =>
        [Pictocodes.Pikachu, Pictocodes.Pikachu, Pictocodes.Pikachu];
}

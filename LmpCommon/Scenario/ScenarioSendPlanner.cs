using System;
using System.Collections.Generic;
using System.Threading;

namespace LmpCommon.Scenario
{
    /// <summary>
    /// One scenario upload: module names and their bytes in parallel lists owned by this batch alone (never shared with
    /// another send), plus the hash of each, to be recorded only once the batch was handed to the network sender.
    /// </summary>
    public sealed class ScenarioSendBatch
    {
        public readonly List<string> Names = new List<string>();
        public readonly List<byte[]> Data = new List<byte[]>();
        public readonly List<string> Hashes = new List<string>();
        public int Count => Names.Count;
    }

    /// <summary>
    /// Pure part of the client's scenario upload (LmpClient ScenarioSystem). The original code built every send in
    /// static lists shared by all sends, while sends run on background tasks and are started from several places
    /// (30 s routine, facility and vessel events); two overlapping sends could throw, or pair one module's name with
    /// another module's bytes. Here each send gets its own batch, and the "already sent" hashes are only updated by
    /// <see cref="Commit"/> after the batch went to the sender.
    /// </summary>
    public static class ScenarioSendPlanner
    {
        /// <summary>
        /// Builds a batch from serialized modules (name, bytes), skipping empty data and modules whose hash equals the
        /// last one sent. <paramref name="lastSentHashes"/> is only read. <paramref name="onEmpty"/> is told about empty data.
        /// </summary>
        public static ScenarioSendBatch Plan(IEnumerable<KeyValuePair<string, byte[]>> serialized, IDictionary<string, string> lastSentHashes,
            Action<string> onEmpty = null)
        {
            var batch = new ScenarioSendBatch();
            if (serialized == null)
                return batch;
            foreach (var module in serialized)
            {
                if (module.Key == null)
                    continue;
                if (module.Value == null || module.Value.Length == 0)
                {
                    onEmpty?.Invoke(module.Key);
                    continue;
                }

                var hash = Common.CalculateSha256Hash(module.Value);
                if (lastSentHashes != null && lastSentHashes.TryGetValue(module.Key, out var last) && last == hash)
                    continue;

                batch.Names.Add(module.Key);
                batch.Data.Add(module.Value);
                batch.Hashes.Add(hash);
            }
            return batch;
        }

        /// <summary>Records the batch as sent. Call only after the batch was handed to the network sender.</summary>
        public static void Commit(ScenarioSendBatch batch, IDictionary<string, string> lastSentHashes)
        {
            if (batch == null || lastSentHashes == null)
                return;
            for (var i = 0; i < batch.Count; i++)
                lastSentHashes[batch.Names[i]] = batch.Hashes[i];
        }
    }

    /// <summary>
    /// Allows one scenario send at a time. A full send asked for while another send runs is remembered and started by
    /// the next <see cref="Tick"/>; a single-module flush (Kerbal Konstructs shared statics) is only taken while nothing
    /// runs, so a flush that cannot start stays requested and is retried at the next tick.
    /// </summary>
    public sealed class ScenarioSendCoordinator
    {
        public enum TickAction { None, Full, SingleModule }

        private int _inFlight;
        private int _fullPending;

        public bool IsBusy => Volatile.Read(ref _inFlight) != 0;
        public bool FullPending => Volatile.Read(ref _fullPending) != 0;

        /// <summary>For a full send. True: the caller owns the send and must call <see cref="End"/>. False: queued for the next tick.</summary>
        public bool TryBeginFull()
        {
            if (TryBegin())
            {
                Interlocked.Exchange(ref _fullPending, 0);
                return true;
            }
            Interlocked.Exchange(ref _fullPending, 1);
            return false;
        }

        /// <summary>
        /// Called every second. Returns what to send now; for anything but None the caller owns the send and must call
        /// <see cref="End"/>. <paramref name="takeSingleModuleRequest"/> is only called when nothing else runs, and consumes the request.
        /// </summary>
        public TickAction Tick(Func<bool> takeSingleModuleRequest)
        {
            if (!TryBegin())
                return TickAction.None;
            if (Interlocked.Exchange(ref _fullPending, 0) != 0)
                return TickAction.Full;
            if (takeSingleModuleRequest != null && takeSingleModuleRequest())
                return TickAction.SingleModule;
            End();
            return TickAction.None;
        }

        public void End() => Interlocked.Exchange(ref _inFlight, 0);

        public void Reset()
        {
            Interlocked.Exchange(ref _inFlight, 0);
            Interlocked.Exchange(ref _fullPending, 0);
        }

        private bool TryBegin() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;
    }
}

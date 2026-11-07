using LmpCommon.Enums;
using LmpCommon.PersistentSync;
using Server.Log;
using Server.System.PersistentSync;
using System;
using System.Globalization;

namespace Server.Admin
{
    public enum CareerCurrency
    {
        Funds,
        Science,
    }

    public enum CareerChangeMode
    {
        Add,
        Set,
    }

    public enum CareerChangeStatus
    {
        Changed,
        /// <summary>The currency does not exist in the current game mode, or the backend does not support the change (409).</summary>
        Unavailable,
        /// <summary>The persistent-sync registry is not initialised yet (409).</summary>
        NotReady,
        /// <summary>The resulting value is outside the allowed range; nothing was changed (400).</summary>
        OutOfBounds,
    }

    public sealed class CareerChangeResult
    {
        public CareerChangeStatus Status { get; set; }
        /// <summary>Plain-English reason for every status other than <see cref="CareerChangeStatus.Changed"/>.</summary>
        public string Message { get; set; }
        public double Previous { get; set; }
        public double Current { get; set; }

        public static CareerChangeResult Refused(CareerChangeStatus status, string message) => new CareerChangeResult { Status = status, Message = message };
    }

    public sealed class CareerCurrencyInfo
    {
        public bool Available { get; set; }
        /// <summary>The current value; null when the currency is not available.</summary>
        public double? Value { get; set; }
    }

    /// <summary>GET /api/v1/career.</summary>
    public sealed class CareerInfo
    {
        public string GameMode { get; set; } = "Sandbox";
        public CareerCurrencyInfo Funds { get; set; } = new CareerCurrencyInfo();
        public CareerCurrencyInfo Science { get; set; } = new CareerCurrencyInfo();
    }

    /// <summary>Body of POST /api/v1/career/funds and /api/v1/career/science.</summary>
    public sealed class CareerChangeRequest
    {
        public string Mode { get; set; }
        public double? Amount { get; set; }
        public bool? Announce { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Reads and changes the shared Career funds and science through the persistent-sync registry, the same way the setfunds and setscience
    /// console commands do. The rules (which game mode has which currency, the bounds) live here so the handler and the backend agree.
    /// </summary>
    public static class CareerAdmin
    {
        public const double MaxFundsAmount = 1e12;
        public const double MaxScienceAmount = 1e9;
        public const int MaxNoteChars = 80;
        public const string Reason = "Admin API";
        public const string NotReadyMessage = "The server is still starting and its shared game data is not ready yet. Try again in a moment.";

        // One admin change at a time. The registry has no read-modify-write of its own, so this only keeps two admin calls from
        // interleaving; a client intent can still land between the read and the write (see the remarks on Change).
        private static readonly object ChangeGate = new object();

        public static string Name(CareerCurrency currency) => currency == CareerCurrency.Funds ? "funds" : "science";

        public static bool IsAvailable(CareerCurrency currency, GameMode gameMode)
        {
            return currency == CareerCurrency.Funds ? gameMode == GameMode.Career : gameMode == GameMode.Career || gameMode == GameMode.Science;
        }

        public static string UnavailableMessage(CareerCurrency currency, GameMode gameMode)
        {
            var exists = currency == CareerCurrency.Funds ? "Career mode" : "Career and Science mode";
            return "There are no " + Name(currency) + " in " + gameMode + " mode; they exist only in " + exists + ".";
        }

        /// <summary>A float read back as the shortest decimal that identifies it (0.1f gives 0.1, not 0.10000000149011612).</summary>
        private static double WidenFloat(float value) => double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        public static CareerInfo Read(GameMode gameMode)
        {
            return new CareerInfo
            {
                GameMode = gameMode.ToString(),
                Funds = ReadCurrency(CareerCurrency.Funds, gameMode),
                Science = ReadCurrency(CareerCurrency.Science, gameMode),
            };
        }

        private static CareerCurrencyInfo ReadCurrency(CareerCurrency currency, GameMode gameMode)
        {
            if (!IsAvailable(currency, gameMode)) return new CareerCurrencyInfo { Available = false, Value = null };
            return TryReadCurrent(currency, out var value, out _) ? new CareerCurrencyInfo { Available = true, Value = value } : new CareerCurrencyInfo { Available = false, Value = null };
        }

        private static PersistentSyncDomainId DomainOf(CareerCurrency currency) => currency == CareerCurrency.Funds ? PersistentSyncDomainId.Funds : PersistentSyncDomainId.Science;

        private static bool TryDecode(CareerCurrency currency, PersistentSyncDomainSnapshot snapshot, out double value)
        {
            value = 0;
            if (snapshot == null || snapshot.DomainId != DomainOf(currency)) return false;
            if (snapshot.NumBytes < (currency == CareerCurrency.Funds ? sizeof(double) : sizeof(float))) return false;

            value = currency == CareerCurrency.Funds
                ? FundsSnapshotPayloadSerializer.Deserialize(snapshot.Payload, snapshot.NumBytes)
                : WidenFloat(ScienceSnapshotPayloadSerializer.Deserialize(snapshot.Payload, snapshot.NumBytes));
            return true;
        }

        private static bool TryReadCurrent(CareerCurrency currency, out double value, out long revision)
        {
            value = 0;
            revision = 0;
            if (!PersistentSyncRegistry.IsPersistentSyncInitialized) return false;

            foreach (var snapshot in PersistentSyncRegistry.GetSnapshots(new[] { DomainOf(currency) }))
            {
                if (!TryDecode(currency, snapshot, out value)) continue;
                revision = snapshot.Revision;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Adds to or replaces the current value and broadcasts it to every player, like the console commands. The new value is worked out from
        /// the value read a moment earlier. A player's game could send its own funds or science in that instant, and the registry offers no
        /// compare-and-set, so such a change would be overwritten; a warning goes to the server log when the revision shows it happened.
        /// </summary>
        public static CareerChangeResult Change(GameMode gameMode, CareerCurrency currency, CareerChangeMode mode, double amount)
        {
            if (!IsAvailable(currency, gameMode)) return CareerChangeResult.Refused(CareerChangeStatus.Unavailable, UnavailableMessage(currency, gameMode));

            lock (ChangeGate)
            {
                if (!TryReadCurrent(currency, out var previous, out var revisionBefore)) return CareerChangeResult.Refused(CareerChangeStatus.NotReady, NotReadyMessage);

                var target = mode == CareerChangeMode.Add ? previous + amount : amount;
                var bounds = CheckBounds(currency, target);
                if (bounds != null) return CareerChangeResult.Refused(CareerChangeStatus.OutOfBounds, bounds);

                var payload = currency == CareerCurrency.Funds
                    ? FundsIntentPayloadSerializer.Serialize(target, Reason)
                    : ScienceIntentPayloadSerializer.Serialize((float)target, Reason);
                var applied = PersistentSyncRegistry.ApplyServerMutation(DomainOf(currency), payload, payload.Length, Reason);
                if (!applied.Accepted || !TryDecode(currency, applied.Snapshot, out var current)) return CareerChangeResult.Refused(CareerChangeStatus.NotReady, NotReadyMessage);

                if (applied.Changed && applied.Snapshot.Revision != revisionBefore + 1)
                {
                    LunaLog.Warning("[AdminApi] a player's game changed " + Name(currency) + " at the same moment as the admin change; the admin value replaced it");
                }

                return new CareerChangeResult { Status = CareerChangeStatus.Changed, Previous = previous, Current = current };
            }
        }

        /// <summary>Null when <paramref name="target"/> is allowed, else the reason. Rejects rather than clamps.</summary>
        public static string CheckBounds(CareerCurrency currency, double target)
        {
            var shown = target.ToString("R", CultureInfo.InvariantCulture);
            if (currency == CareerCurrency.Funds)
            {
                return double.IsNaN(target) || Math.Abs(target) > MaxFundsAmount
                    ? "funds would end up at " + shown + ", outside the allowed range of -1000000000000 to 1000000000000; nothing was changed"
                    : null;
            }

            if (double.IsNaN(target) || target < 0) return "science cannot go below 0 (it would end up at " + shown + "); nothing was changed";
            if (float.IsInfinity((float)target)) return "science would end up at " + shown + ", which is too large; nothing was changed";
            return null;
        }
    }
}

using System;
using System.Collections.Generic;

namespace LmpCommon.Compression
{
    /// <summary>
    /// A bounded least-recently-used cache of compressed payloads, keyed by (message type, content hash, raw length, level), so a
    /// vessel, kerbal or scenario that goes out again (a player joins, a re-send) is not compressed again. A changed content has a
    /// different hash and simply misses; nothing has to be invalidated by hand. Thread-safe. <see cref="TryGet"/> hands out a private
    /// copy because message objects are recycled and their arrays reused.
    /// </summary>
    public sealed class CompressedPayloadCache
    {
        /// <summary>Counted per entry on top of its bytes (dictionary slot, list node, key).</summary>
        public const int EntryOverheadBytes = 96;

        private struct Key : IEquatable<Key>
        {
            public string Type;
            public ulong Hash;
            public int Length;
            public int Level;

            public bool Equals(Key other)
            {
                return Hash == other.Hash && Length == other.Length && Level == other.Level && string.Equals(Type, other.Type, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is Key && Equals((Key)obj);
            }

            public override int GetHashCode()
            {
                return (int)(Hash ^ (Hash >> 32)) ^ (Length * 31) ^ (Level * 997);
            }
        }

        private sealed class Entry
        {
            public Key Key;
            public byte[] Data;
            public long Cost;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new Dictionary<Key, LinkedListNode<Entry>>();
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private long _bytes;
        private long _evictions;

        public CompressedPayloadCache(long maxBytes)
        {
            MaxBytes = Math.Max(1, maxBytes);
        }

        public long MaxBytes { get; }

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        /// <summary>Bytes held, including the per-entry overhead; never above <see cref="MaxBytes"/>.</summary>
        public long Bytes
        {
            get { lock (_lock) return _bytes; }
        }

        public long Evictions
        {
            get { lock (_lock) return _evictions; }
        }

        /// <summary>A private copy of the cached compressed bytes, and the entry becomes the most recently used one.</summary>
        public bool TryGet(string type, ulong hash, int rawLength, int level, out byte[] copy)
        {
            copy = null;
            var key = new Key { Type = type ?? string.Empty, Hash = hash, Length = rawLength, Level = level };
            lock (_lock)
            {
                LinkedListNode<Entry> node;
                if (!_map.TryGetValue(key, out node)) return false;
                _lru.Remove(node);
                _lru.AddFirst(node);
                copy = new byte[node.Value.Data.Length];
                Buffer.BlockCopy(node.Value.Data, 0, copy, 0, copy.Length);
                return true;
            }
        }

        /// <summary>
        /// Stores <paramref name="compressed"/> (exactly the compressed bytes, no spare capacity; the cache keeps the array, so the
        /// caller must not use it afterwards). Older entries are evicted until it fits; an entry that alone is more than a quarter of
        /// the cache is not stored.
        /// </summary>
        public void Add(string type, ulong hash, int rawLength, int level, byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0) return;
            var cost = compressed.Length + EntryOverheadBytes;
            if (cost > MaxBytes / 4) return;

            var key = new Key { Type = type ?? string.Empty, Hash = hash, Length = rawLength, Level = level };
            lock (_lock)
            {
                LinkedListNode<Entry> existing;
                if (_map.TryGetValue(key, out existing)) RemoveNode(existing);

                while (_bytes + cost > MaxBytes && _lru.Last != null)
                {
                    RemoveNode(_lru.Last);
                    _evictions++;
                }

                var node = _lru.AddFirst(new Entry { Key = key, Data = compressed, Cost = cost });
                _map[key] = node;
                _bytes += cost;
            }
        }

        /// <summary>Drops every entry of one message type (for example when its store was cleared). Returns how many were removed.</summary>
        public int Invalidate(string type)
        {
            var name = type ?? string.Empty;
            lock (_lock)
            {
                var removed = 0;
                var node = _lru.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.Key.Type, name, StringComparison.Ordinal))
                    {
                        RemoveNode(node);
                        removed++;
                    }

                    node = next;
                }

                return removed;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        private void RemoveNode(LinkedListNode<Entry> node)
        {
            _lru.Remove(node);
            _map.Remove(node.Value.Key);
            _bytes -= node.Value.Cost;
        }

        /// <summary>64-bit hash of the first <paramref name="length"/> bytes (eight bytes per step; far cheaper than compressing them).</summary>
        public static ulong HashOf(byte[] data, int length)
        {
            const ulong multiplier = 0x9FB21C651E98DF25UL;
            unchecked
            {
                ulong hash = 0x9E3779B97F4A7C15UL ^ (ulong)length;
                var i = 0;
                for (; i + 8 <= length; i += 8)
                {
                    var block = BitConverter.ToUInt64(data, i);
                    hash ^= block * 0xC2B2AE3D27D4EB4FUL;
                    hash = ((hash << 27) | (hash >> 37)) * multiplier + 0x165667B19E3779F9UL;
                }

                ulong tail = 0;
                var shift = 0;
                for (; i < length; i++)
                {
                    tail |= (ulong)data[i] << shift;
                    shift += 8;
                }

                hash ^= tail * 0xC2B2AE3D27D4EB4FUL;
                hash = ((hash << 27) | (hash >> 37)) * multiplier;
                hash ^= hash >> 33;
                hash *= 0xFF51AFD7ED558CCDUL;
                hash ^= hash >> 33;
                return hash;
            }
        }
    }
}

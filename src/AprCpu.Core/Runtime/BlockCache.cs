namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 7 A.4 — PC-keyed cache mapping a block's start PC to its
/// JIT'd block function pointer. Backs the block-JIT dispatch path:
/// before fetch+decode, the executor first checks here; on hit it
/// jumps directly to the block function, skipping per-instruction
/// dispatch entirely.
///
/// LRU eviction at capacity bound — bounded memory is important
/// because every distinct block start PC gets compiled into its own
/// LLVM module that ORC retains for its lifetime. Without eviction a
/// long-running ROM would accumulate unbounded JIT memory.
///
/// Single-threaded by design — emulator's hot loop is single-threaded
/// and adding locking on every cache hit would dwarf the rest of the
/// dispatch cost. Re-entrant safety is the caller's job (the only
/// re-entrancy path is exception handling, which we don't currently
/// take through cache code).
///
/// Eviction granularity is one entry. A full block bin (the
/// LLVMModuleRef + its compiled native code) is owned by ORC LLJIT
/// internally; the cache only loses the lookup, not the underlying
/// memory. A separate code-memory reclamation pass (resource trackers)
/// would be needed to actually free the JIT memory — Phase 7 deferred
/// item; usually irrelevant for short benchmark runs.
/// </summary>
public sealed class BlockCache
{
    /// <summary>Default capacity — chosen empirically for GBA homebrew
    /// (typically &lt; 1000 distinct block heads in active code) with
    /// margin for larger commercial ROMs.</summary>
    public const int DefaultCapacity = 4096;

    private readonly int _capacity;
    private readonly Dictionary<uint, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru;   // head = MRU, tail = LRU

    private struct Entry
    {
        public uint    Pc;
        public IntPtr  Fn;
    }

    public BlockCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be > 0");
        _capacity = capacity;
        _map = new Dictionary<uint, LinkedListNode<Entry>>(capacity);
        _lru = new LinkedList<Entry>();
    }

    /// <summary>Number of currently-cached entries.</summary>
    public int Count => _map.Count;

    /// <summary>Maximum number of entries before LRU eviction kicks in.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Look up a cached block fn by start PC. On hit, marks the entry
    /// as most-recently-used (so it survives subsequent eviction passes).
    /// </summary>
    public bool TryGet(uint pc, out IntPtr fn)
    {
        if (_map.TryGetValue(pc, out var node))
        {
            // Promote to MRU. LinkedList<T> Remove + AddFirst is O(1).
            _lru.Remove(node);
            _lru.AddFirst(node);
            fn = node.Value.Fn;
            return true;
        }
        fn = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Insert (or update) a cache entry. If the cache is already at
    /// capacity and this is a new PC, the least-recently-used entry
    /// is evicted.
    /// </summary>
    public void Add(uint pc, IntPtr fn)
    {
        if (_map.TryGetValue(pc, out var existing))
        {
            // Update fn pointer + promote to MRU.
            _lru.Remove(existing);
            existing.Value = new Entry { Pc = pc, Fn = fn };
            _lru.AddFirst(existing);
            return;
        }
        var node = _lru.AddFirst(new Entry { Pc = pc, Fn = fn });
        _map[pc] = node;
        if (_map.Count > _capacity)
        {
            var tail = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(tail.Value.Pc);
        }
    }

    /// <summary>
    /// Drop an entry by PC (e.g. for SMC invalidation in Phase 7 A.5).
    /// Returns true if an entry was actually removed.
    /// </summary>
    public bool Invalidate(uint pc)
    {
        if (_map.TryGetValue(pc, out var node))
        {
            _lru.Remove(node);
            _map.Remove(pc);
            return true;
        }
        return false;
    }

    /// <summary>Drop all cached entries.</summary>
    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}

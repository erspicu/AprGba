namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 7 A.4 — one cached compiled block: native fn pointer +
/// metadata used by the executor for cycle accounting / stats.
/// </summary>
public readonly struct CachedBlock
{
    /// <summary>Native entry point of the compiled block fn (signature
    /// <c>void(byte*)</c>).</summary>
    public IntPtr Fn { get; }

    /// <summary>Number of instructions the detector found in this block —
    /// used by the runner to scale cycle accounting / instruction
    /// counters per block execution.</summary>
    public int InstructionCount { get; }

    /// <summary>
    /// Total byte length of the block (sum of per-instruction lengths).
    /// For fixed-width sets equals <c>InstructionCount × InstrSizeBytes</c>;
    /// for variable-width sets (LR35902) it must be the sum of the
    /// per-instruction <see cref="DecodedBlockInstruction.LengthBytes"/>.
    /// Used by the runner to advance PC after a fall-through (no branch,
    /// no budget exit) block execution. Phase 7 GB block-JIT P0.4.
    ///
    /// <para>For cross-jump-followed blocks (P1 #6) the byte length sum
    /// no longer equals "next-PC after block" because instructions are
    /// non-sequential in memory. Use <see cref="NextPcAfterLastInstr"/>
    /// for the fall-through PC instead.</para>
    /// </summary>
    public int TotalByteLength { get; }

    /// <summary>
    /// Phase 7 GB block-JIT P1 #6 — PC immediately after the LAST
    /// instruction in the block (= lastBi.Pc + lastBi.LengthBytes).
    /// Equals <c>StartPc + TotalByteLength</c> for sequential blocks,
    /// but DIFFERENT for cross-jump-followed blocks where instructions
    /// span multiple PC ranges. Runner uses this for the fall-through
    /// path (PcWritten=0 + budget OK) — sequential PC advance from
    /// block-start would be wrong after cross-jump.
    /// </summary>
    public uint NextPcAfterLastInstr { get; }

    /// <summary>
    /// Phase 7 A.5 SMC support — lowest memory address covered by any
    /// instruction in this block. For sequential blocks equals StartPc;
    /// for cross-jump blocks (P1 #6) may be lower if a followed JP
    /// went backward.
    /// </summary>
    public uint CoverageStartPc { get; }

    /// <summary>
    /// Phase 7 A.5 SMC support — exclusive upper bound of memory
    /// covered by any instruction in this block (= max(instr.Pc +
    /// instr.LengthBytes)). Used by SMC detection: a memory write to
    /// any address in [CoverageStartPc, CoverageEndPcExclusive) MAY
    /// invalidate this block (conservative: covers gaps in cross-jump
    /// blocks; over-invalidates by a few bytes max).
    /// </summary>
    public uint CoverageEndPcExclusive { get; }

    public CachedBlock(IntPtr fn, int instructionCount, int totalByteLength, uint nextPcAfterLastInstr,
                       uint coverageStartPc, uint coverageEndPcExclusive)
    {
        Fn = fn;
        InstructionCount = instructionCount;
        TotalByteLength = totalByteLength;
        NextPcAfterLastInstr = nextPcAfterLastInstr;
        CoverageStartPc = coverageStartPc;
        CoverageEndPcExclusive = coverageEndPcExclusive;
    }
}

/// <summary>
/// Phase 7 A.4 — PC-keyed cache mapping a block's start PC to its
/// JIT'd block function pointer (and instruction count). Backs the
/// block-JIT dispatch path: before fetch+decode, the executor first
/// checks here; on hit it jumps directly to the block function,
/// skipping per-instruction dispatch entirely.
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

    // Phase 7 A.5 SMC support — per-byte coverage count. Each entry
    // is the number of cached blocks whose CoverageStartPc..CoverageEndPcExclusive
    // range includes this address. Bus.WriteByte calls NotifyMemoryWrite
    // which checks this counter; if >0, scans cached blocks for any
    // overlapping the address and invalidates them. Memory-bounded by
    // the addressable space (e.g. LR35902 = 64KB → 64KB counter array).
    // Per-write fast-path = 1 byte read + branch (~1ns).
    private readonly byte[] _coverageCount;
    private readonly uint _addressSpaceBytes;

    private struct Entry
    {
        public uint        Pc;
        public CachedBlock Block;
    }

    public BlockCache(int capacity = DefaultCapacity, uint addressSpaceBytes = 0x10000)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be > 0");
        _capacity = capacity;
        _map = new Dictionary<uint, LinkedListNode<Entry>>(capacity);
        _lru = new LinkedList<Entry>();
        _addressSpaceBytes = addressSpaceBytes;
        _coverageCount = new byte[addressSpaceBytes];
    }

    /// <summary>Number of currently-cached entries.</summary>
    public int Count => _map.Count;

    /// <summary>Maximum number of entries before LRU eviction kicks in.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Look up a cached block by start PC. On hit, marks the entry
    /// as most-recently-used (so it survives subsequent eviction passes).
    /// </summary>
    public bool TryGet(uint pc, out CachedBlock entry)
    {
        if (_map.TryGetValue(pc, out var node))
        {
            // Promote to MRU. LinkedList<T> Remove + AddFirst is O(1).
            _lru.Remove(node);
            _lru.AddFirst(node);
            entry = node.Value.Block;
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>
    /// Insert (or update) a cache entry. If the cache is already at
    /// capacity and this is a new PC, the least-recently-used entry
    /// is evicted.
    /// </summary>
    public void Add(uint pc, CachedBlock block)
    {
        if (_map.TryGetValue(pc, out var existing))
        {
            // Update entry + promote to MRU. Decrement old block's
            // coverage; increment new block's.
            DecrementCoverage(existing.Value.Block);
            _lru.Remove(existing);
            existing.Value = new Entry { Pc = pc, Block = block };
            _lru.AddFirst(existing);
            IncrementCoverage(block);
            return;
        }
        var node = _lru.AddFirst(new Entry { Pc = pc, Block = block });
        _map[pc] = node;
        IncrementCoverage(block);
        if (_map.Count > _capacity)
        {
            var tail = _lru.Last!;
            _lru.RemoveLast();
            DecrementCoverage(tail.Value.Block);
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
            DecrementCoverage(node.Value.Block);
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
        Array.Clear(_coverageCount, 0, _coverageCount.Length);
    }

    /// <summary>
    /// Phase 7 A.5 SMC — call this on every memory write. Fast path:
    /// 1-byte read + branch when no cached block covers the address.
    /// Slow path: scan cached blocks, invalidate any whose coverage
    /// range includes the address. Returns true if any block was
    /// invalidated (mainly for diagnostics).
    /// </summary>
    public bool NotifyMemoryWrite(uint addr)
    {
        if (addr >= _addressSpaceBytes) return false;
        if (_coverageCount[addr] == 0) return false;

        // Slow path: linear scan to find blocks covering this addr.
        // Cached blocks at this point: typically 100s-1000s in steady
        // state. This is the rare "RAM write hits cached block code"
        // path that fires only occasionally (test framework loading
        // new code, JIT warmup, etc.). Invalidating triggers recompile
        // on next dispatch.
        List<uint>? toRemove = null;
        foreach (var kvp in _map)
        {
            var blk = kvp.Value.Value.Block;
            if (addr >= blk.CoverageStartPc && addr < blk.CoverageEndPcExclusive)
            {
                (toRemove ??= new List<uint>()).Add(kvp.Key);
            }
        }
        if (toRemove is null) return false;
        foreach (var pc in toRemove) Invalidate(pc);
        return true;
    }

    private void IncrementCoverage(CachedBlock blk)
    {
        for (uint a = blk.CoverageStartPc; a < blk.CoverageEndPcExclusive && a < _addressSpaceBytes; a++)
        {
            if (_coverageCount[a] < byte.MaxValue) _coverageCount[a]++;
        }
    }

    private void DecrementCoverage(CachedBlock blk)
    {
        for (uint a = blk.CoverageStartPc; a < blk.CoverageEndPcExclusive && a < _addressSpaceBytes; a++)
        {
            if (_coverageCount[a] > 0) _coverageCount[a]--;
        }
    }
}

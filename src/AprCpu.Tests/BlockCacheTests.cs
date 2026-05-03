using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 7 A.4 — BlockCache LRU semantics:
/// - Add/TryGet round-trip
/// - Miss returns false + IntPtr.Zero
/// - Capacity overflow evicts the LRU entry
/// - TryGet promotes an entry to MRU (saving it from being evicted next)
/// - Invalidate removes an entry
/// </summary>
public class BlockCacheTests
{
    [Fact]
    public void Add_Then_TryGet_Returns_Stored_FnPtr()
    {
        var cache = new BlockCache(capacity: 8);
        cache.Add(0x100u, (IntPtr)0x5EADBEEF);
        Assert.True(cache.TryGet(0x100u, out var fn));
        Assert.Equal((IntPtr)0x5EADBEEF, fn);
    }

    [Fact]
    public void Miss_Returns_False_And_Zero()
    {
        var cache = new BlockCache(capacity: 8);
        cache.Add(0x100u, (IntPtr)0x1);
        Assert.False(cache.TryGet(0x999u, out var fn));
        Assert.Equal(IntPtr.Zero, fn);
    }

    [Fact]
    public void Add_Same_Pc_Twice_Updates_FnPtr_Without_Growing()
    {
        var cache = new BlockCache(capacity: 8);
        cache.Add(0x100u, (IntPtr)0xAA);
        cache.Add(0x100u, (IntPtr)0xBB);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(0x100u, out var fn));
        Assert.Equal((IntPtr)0xBB, fn);
    }

    [Fact]
    public void Add_Past_Capacity_Evicts_Lru_Entry()
    {
        var cache = new BlockCache(capacity: 3);
        cache.Add(0x100u, (IntPtr)0x1);
        cache.Add(0x200u, (IntPtr)0x2);
        cache.Add(0x300u, (IntPtr)0x3);
        // 0x100 is the LRU at this point.
        cache.Add(0x400u, (IntPtr)0x4);

        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet(0x100u, out _));     // evicted
        Assert.True (cache.TryGet(0x200u, out _));
        Assert.True (cache.TryGet(0x300u, out _));
        Assert.True (cache.TryGet(0x400u, out _));
    }

    [Fact]
    public void TryGet_Promotes_Entry_To_Mru()
    {
        var cache = new BlockCache(capacity: 3);
        cache.Add(0x100u, (IntPtr)0x1);
        cache.Add(0x200u, (IntPtr)0x2);
        cache.Add(0x300u, (IntPtr)0x3);

        // Touch 0x100 — should now be MRU; 0x200 becomes LRU.
        Assert.True(cache.TryGet(0x100u, out _));

        // Insert a fourth entry; LRU (now 0x200) should be evicted.
        cache.Add(0x400u, (IntPtr)0x4);

        Assert.True (cache.TryGet(0x100u, out _));    // saved by recent touch
        Assert.False(cache.TryGet(0x200u, out _));    // evicted
        Assert.True (cache.TryGet(0x300u, out _));
        Assert.True (cache.TryGet(0x400u, out _));
    }

    [Fact]
    public void Invalidate_Removes_Entry()
    {
        var cache = new BlockCache(capacity: 8);
        cache.Add(0x100u, (IntPtr)0x1);
        cache.Add(0x200u, (IntPtr)0x2);
        Assert.True(cache.Invalidate(0x100u));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(0x100u, out _));
        Assert.True (cache.TryGet(0x200u, out _));

        // Invalidating non-existent PC returns false, doesn't throw.
        Assert.False(cache.Invalidate(0xCAFEu));
    }

    [Fact]
    public void Clear_Removes_All_Entries()
    {
        var cache = new BlockCache(capacity: 8);
        for (uint pc = 0x100; pc < 0x110; pc += 4)
            cache.Add(pc, (IntPtr)pc);
        Assert.Equal(4, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(0x100u, out _));
    }

    [Fact]
    public void Constructor_Rejects_NonPositive_Capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlockCache(-1));
    }
}

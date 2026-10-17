using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SLVZ.CoreDb.Storage;

internal static class PageType
{
    public const byte Leaf = 1, Internal = 2, Overflow = 3, Free = 4;
}

/// <summary>
/// Fixed-size page file. Pages are XOR-ed on disk. Every commit is first written to a
/// write-ahead log (.wal), then applied to the main file, so a crash never leaves a half-written state.
/// Not thread-safe for writers: callers (CoreTable) serialize writers and allow parallel readers.
/// </summary>
internal sealed class Pager : IDisposable
{
    public const int PageSize = 4096;
    const int MetaOffset = 32;
    const ulong FnvOffset = 14695981039346656037UL;

    static readonly byte[] Magic = "SLVZCDB1"u8.ToArray();
    static readonly byte[] WalMagic = "SLVZWAL1"u8.ToArray();
    static readonly byte[] WalEnd = "WALEND!!"u8.ToArray();
    static readonly byte[] DefaultKey = "SLVZ.CoreDb::xor-key::7f3a91c2e5b8d406"u8.ToArray();

    // test hook: simulates a crash right after the WAL became durable
    internal static volatile bool TestFailAfterWal;

    readonly bool _durable;
    readonly int _cacheLimit;
    readonly byte[] _key;
    readonly SafeFileHandle _file;
    readonly FileStream _wal;
    readonly Dictionary<uint, byte[]> _cache = new();
    readonly Dictionary<uint, byte[]> _dirty = new();
    readonly object _cacheLock = new();
    bool _faulted;

    public uint PageCount { get; private set; }
    public uint FreeHead { get; private set; }
    public byte[] Meta { get; private set; } = Array.Empty<byte>();

    public Pager(string path, CoreDbOptions options)
    {
        _durable = options.Durable;
        _cacheLimit = Math.Max(64, options.CachePages);
        _key = options.XorKey is { Length: > 0 } k ? (byte[])k.Clone() : DefaultKey;

        try
        {
            _file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new CoreDbException($"Can't open '{path}'. Is it used by another process?", ex);
        }

        try
        {
            _wal = new FileStream(path + ".wal", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            Recover();

            if (RandomAccess.GetLength(_file) == 0)
            {
                PageCount = 1;
                FreeHead = 0;
                Commit(Array.Empty<byte>(), force: true);
            }
            else LoadHeader();
        }
        catch
        {
            _wal?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    // ---------- reading / writing pages ----------

    /// <summary>Returns a page for READING. Never mutate the returned array.</summary>
    public byte[] Read(uint id)
    {
        lock (_cacheLock)
        {
            if (_dirty.TryGetValue(id, out var d)) return d;
            if (_cache.TryGetValue(id, out var c)) return c;
        }

        if (id != 0 && id >= PageCount)
            throw new CoreDbException($"Corrupt file: page {id} is out of range.");

        var buf = new byte[PageSize];
        int n = RandomAccess.Read(_file, buf, (long)id * PageSize);
        if (n != PageSize) throw new CoreDbException($"Corrupt file: page {id} is truncated.");
        Xor(buf, id);

        lock (_cacheLock)
        {
            if (_cache.Count >= _cacheLimit) _cache.Clear();
            _cache[id] = buf;
        }
        return buf;
    }

    /// <summary>Returns a private, mutable copy of the page that becomes part of the current transaction.</summary>
    public byte[] Write(uint id)
    {
        lock (_cacheLock)
        {
            if (_dirty.TryGetValue(id, out var d)) return d;
        }
        var copy = (byte[])Read(id).Clone();
        lock (_cacheLock) _dirty[id] = copy;
        return copy;
    }

    public uint Allocate()
    {
        if (FreeHead != 0)
        {
            uint id = FreeHead;
            var pg = Read(id);
            if (pg[0] != PageType.Free) throw new CoreDbException("Corrupt file: free list is damaged.");
            FreeHead = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(4));
            Array.Clear(Write(id));
            return id;
        }

        uint nid = PageCount++;
        lock (_cacheLock) _dirty[nid] = new byte[PageSize];
        return nid;
    }

    public void Free(uint id)
    {
        var pg = Write(id);
        Array.Clear(pg);
        pg[0] = PageType.Free;
        BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(4), FreeHead);
        FreeHead = id;
    }

    // ---------- transactions ----------

    public void Commit(byte[] meta, bool force = false)
    {
        if (_faulted)
            throw new CoreDbException("The database hit an I/O error while committing. Dispose it and open it again.");
        if (_dirty.Count == 0 && !force) return;
        if (meta.Length > PageSize - MetaOffset)
            throw new CoreDbException("Table metadata is too large (too many indexes).");

        var p0 = new byte[PageSize];
        Magic.CopyTo(p0, 0);
        BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(12), PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(p0.AsSpan(16), PageCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p0.AsSpan(20), FreeHead);
        BinaryPrimitives.WriteUInt16LittleEndian(p0.AsSpan(24), (ushort)meta.Length);
        meta.CopyTo(p0, MetaOffset);
        lock (_cacheLock) _dirty[0] = p0;

        var ids = _dirty.Keys.ToArray();
        Array.Sort(ids);

        try
        {
            // 1) write-ahead log
            _wal.Position = 0;
            ulong hash = FnvOffset;
            var head = new byte[12];
            WalMagic.CopyTo(head, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)ids.Length);
            hash = Fnv(hash, head);
            _wal.Write(head);

            var enc = new byte[4 + PageSize];
            foreach (var id in ids)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(enc, id);
                Buffer.BlockCopy(_dirty[id], 0, enc, 4, PageSize);
                Xor(enc.AsSpan(4), id);
                hash = Fnv(hash, enc);
                _wal.Write(enc);
            }

            var tail = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(tail, hash);
            WalEnd.CopyTo(tail, 8);
            _wal.Write(tail);
            _wal.Flush(_durable);

            if (TestFailAfterWal) throw new IOException("Simulated crash after WAL.");

            // 2) apply to the main file
            var page = new byte[PageSize];
            foreach (var id in ids)
            {
                Buffer.BlockCopy(_dirty[id], 0, page, 0, PageSize);
                Xor(page, id);
                RandomAccess.Write(_file, page, (long)id * PageSize);
            }
            if (_durable) RandomAccess.FlushToDisk(_file);

            // 3) the log is no longer needed
            _wal.SetLength(0);
            _wal.Flush(_durable);
        }
        catch
        {
            _faulted = true;
            throw;
        }

        lock (_cacheLock)
        {
            if (_cache.Count + _dirty.Count > _cacheLimit) _cache.Clear();
            foreach (var kv in _dirty) _cache[kv.Key] = kv.Value;
            _dirty.Clear();
        }
        Meta = (byte[])meta.Clone();
    }

    public void Rollback()
    {
        lock (_cacheLock) _dirty.Clear();
        LoadHeader();
    }

    public void Sync() => RandomAccess.FlushToDisk(_file);

    public void Dispose()
    {
        _wal.Dispose();
        _file.Dispose();
    }

    // ---------- internals ----------

    void LoadHeader()
    {
        var p = Read(0);
        if (!p.AsSpan(0, 8).SequenceEqual(Magic))
            throw new CoreDbException("Not a SLVZ.CoreDb file (or the XOR key is wrong).");
        if (BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(12)) != PageSize)
            throw new CoreDbException("Unsupported page size.");

        PageCount = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16));
        FreeHead = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(20));
        int ml = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(24));
        Meta = p.AsSpan(MetaOffset, ml).ToArray();
    }

    void Recover()
    {
        if (_wal.Length == 0) return;

        if (TryValidateWal(out uint count))
        {
            var enc = new byte[4 + PageSize];
            _wal.Position = 12;
            for (uint i = 0; i < count; i++)
            {
                _wal.ReadExactly(enc);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(enc);
                RandomAccess.Write(_file, enc.AsSpan(4), (long)id * PageSize);
            }
            RandomAccess.FlushToDisk(_file);
        }
        // an incomplete log means the commit never finished: the main file is still the old, consistent state
        _wal.SetLength(0);
        _wal.Flush(true);
    }

    bool TryValidateWal(out uint count)
    {
        count = 0;
        try
        {
            long len = _wal.Length;
            if (len < 12 + 16) return false;

            _wal.Position = 0;
            var head = new byte[12];
            _wal.ReadExactly(head);
            if (!head.AsSpan(0, 8).SequenceEqual(WalMagic)) return false;

            uint c = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            if (12 + (long)c * (4 + PageSize) + 16 != len) return false;

            ulong hash = Fnv(FnvOffset, head);
            var enc = new byte[4 + PageSize];
            for (uint i = 0; i < c; i++)
            {
                _wal.ReadExactly(enc);
                hash = Fnv(hash, enc);
            }

            var tail = new byte[16];
            _wal.ReadExactly(tail);
            if (BinaryPrimitives.ReadUInt64LittleEndian(tail) != hash) return false;
            if (!tail.AsSpan(8).SequenceEqual(WalEnd)) return false;

            count = c;
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    void Xor(Span<byte> buf, uint pageId)
    {
        var key = _key;
        int j = (int)(pageId * 131u % (uint)key.Length);
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] ^= key[j];
            if (++j == key.Length) j = 0;
        }
    }

    static ulong Fnv(ulong h, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}

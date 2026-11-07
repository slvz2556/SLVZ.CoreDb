using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace SLVZ.CoreDb.Storage;

internal static class PageType
{
    public const byte Leaf = 1, Internal = 2, Overflow = 3, Free = 4;
}

/// <summary>
/// Fixed-size page file. Pages are XOR-ed on disk. A commit is: (1) Prepare = write all changed pages to a
/// write-ahead log (.wal) and flush it, (2) Apply = copy them into the main file. A crash between the two is repaired
/// on the next open by replaying the log, so the file never stays half-written.
/// A log written for a multi-table transaction (txId != empty) is only replayed if the database has a commit decision for it.
/// Not thread-safe for writers: callers (DataSet) serialize writers and keep readers away from the Apply step.
/// </summary>
internal sealed class Pager : IDisposable
{
    public const int PageSize = 4096;
    const int MetaOffset = 32;
    const int WalHeader = 8 + 4 + 16;
    const ulong FnvOffset = 14695981039346656037UL;

    static readonly byte[] Magic = "SLVZCDB1"u8.ToArray();
    static readonly byte[] WalMagic = "SLVZWAL2"u8.ToArray();
    static readonly byte[] WalEnd = "WALEND!!"u8.ToArray();
    static readonly byte[] DefaultKey = "SLVZ.CoreDb::xor-key::7f3a91c2e5b8d406"u8.ToArray();

    // test hook: simulates a crash right after the WAL became durable (single-table commit)
    internal static volatile bool TestFailAfterWal;

    readonly bool _durable;
    readonly int _cacheLimit;
    readonly byte[] _key;
    readonly Func<Guid, bool>? _isCommitted;
    readonly SafeFileHandle _file;
    readonly FileStream _wal;
    readonly Dictionary<uint, byte[]> _cache = new();
    readonly Dictionary<uint, byte[]> _dirty = new();
    readonly object _cacheLock = new();
    bool _faulted;
    bool _prepared;
    uint[]? _preparedIds;
    byte[] _preparedMeta = Array.Empty<byte>();

    public uint PageCount { get; private set; }
    public uint FreeHead { get; private set; }
    public byte[] Meta { get; private set; } = Array.Empty<byte>();

    public Pager(string path, CoreDbOptions options, Func<Guid, bool>? isCommitted = null)
    {
        _durable = options.Durable;
        _cacheLimit = Math.Max(64, options.CachePages);
        _key = options.XorKey is { Length: > 0 } k ? (byte[])k.Clone() : DefaultKey;
        _isCommitted = isCommitted;

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

    /// <summary>Returns a page for READING (includes the current transaction's changes). Never mutate the returned array.</summary>
    public byte[] Read(uint id) => ReadPage(id, committedOnly: false);

    /// <summary>Returns a page as of the last commit, ignoring the transaction in progress. Used by readers.</summary>
    public byte[] ReadCommitted(uint id) => ReadPage(id, committedOnly: true);

    byte[] ReadPage(uint id, bool committedOnly)
    {
        lock (_cacheLock)
        {
            if (!committedOnly && _dirty.TryGetValue(id, out var d)) return d;
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

    /// <summary>Single-table commit: Prepare + Apply.</summary>
    public void Commit(byte[] meta, bool force = false)
    {
        if (!Prepare(meta, Guid.Empty, force)) return;
        if (TestFailAfterWal)
        {
            _faulted = true;
            _prepared = false;      // leave the log on disk, like a real crash would
            throw new IOException("Simulated crash after WAL.");
        }
        Apply();
    }

    /// <summary>Writes the transaction to the WAL (durably). Nothing in the main file changes yet.</summary>
    /// <returns>false if there was nothing to write.</returns>
    public bool Prepare(byte[] meta, Guid txId, bool force = false)
    {
        if (_faulted)
            throw new CoreDbException("The database hit an I/O error while committing. Dispose it and open it again.");
        if (_prepared) throw new InvalidOperationException("Transaction is already prepared.");
        if (_dirty.Count == 0 && !force) return false;
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
            _wal.Position = 0;
            ulong hash = FnvOffset;
            var head = new byte[WalHeader];
            WalMagic.CopyTo(head, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)ids.Length);
            txId.TryWriteBytes(head.AsSpan(12));
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
        }
        catch
        {
            try { _wal.SetLength(0); } catch { /* best effort */ }
            throw;
        }

        _preparedIds = ids;
        _preparedMeta = meta;
        _prepared = true;
        return true;
    }

    /// <summary>Copies the prepared pages into the main file and discards the WAL. After this the commit is final.</summary>
    public void Apply()
    {
        if (!_prepared || _preparedIds == null) throw new InvalidOperationException("Nothing is prepared.");
        var ids = _preparedIds;

        try
        {
            var page = new byte[PageSize];
            foreach (var id in ids)
            {
                Buffer.BlockCopy(_dirty[id], 0, page, 0, PageSize);
                Xor(page, id);
                RandomAccess.Write(_file, page, (long)id * PageSize);
            }
            if (_durable) RandomAccess.FlushToDisk(_file);

            _wal.SetLength(0);
            _wal.Flush(_durable);
        }
        catch
        {
            // the WAL must stay on disk: it is what repairs a half-applied commit on the next open
            _faulted = true;
            _prepared = false;
            throw;
        }

        lock (_cacheLock)
        {
            if (_cache.Count + _dirty.Count > _cacheLimit) _cache.Clear();
            foreach (var kv in _dirty) _cache[kv.Key] = kv.Value;
            _dirty.Clear();
        }
        Meta = (byte[])_preparedMeta.Clone();
        _prepared = false;
        _preparedIds = null;
    }

    /// <summary>Throws away the current transaction (prepared or not).</summary>
    public void Abort()
    {
        if (_prepared)
        {
            try
            {
                _wal.SetLength(0);
                _wal.Flush(_durable);
            }
            catch { /* best effort */ }
            _prepared = false;
            _preparedIds = null;
        }
        lock (_cacheLock) _dirty.Clear();
        LoadHeader();
    }

    public void Rollback() => Abort();

    /// <summary>After a failure that may have left a half-applied commit: refuse further commits; the WAL stays for recovery.</summary>
    public void MarkFaulted()
    {
        _faulted = true;
        _prepared = false;
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

        if (TryValidateWal(out uint count, out Guid txId) && (txId == Guid.Empty || (_isCommitted?.Invoke(txId) ?? false)))
        {
            var enc = new byte[4 + PageSize];
            _wal.Position = WalHeader;
            for (uint i = 0; i < count; i++)
            {
                _wal.ReadExactly(enc);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(enc);
                RandomAccess.Write(_file, enc.AsSpan(4), (long)id * PageSize);
            }
            RandomAccess.FlushToDisk(_file);
        }
        // an incomplete log (or a multi-table log without a commit decision) means the commit never happened:
        // the main file is still the old, consistent state
        _wal.SetLength(0);
        _wal.Flush(true);
    }

    bool TryValidateWal(out uint count, out Guid txId)
    {
        count = 0;
        txId = Guid.Empty;
        try
        {
            long len = _wal.Length;
            if (len < WalHeader + 16) return false;

            _wal.Position = 0;
            var head = new byte[WalHeader];
            _wal.ReadExactly(head);
            if (!head.AsSpan(0, 8).SequenceEqual(WalMagic)) return false;

            uint c = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            if (WalHeader + (long)c * (4 + PageSize) + 16 != len) return false;

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
            txId = new Guid(head.AsSpan(12, 16));
            return true;
        }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>Reads the transaction id of a WAL file without opening the table (used to clean up decision files).</summary>
    internal static bool TryReadWalTxId(string walPath, out Guid txId)
    {
        txId = Guid.Empty;
        try
        {
            using var fs = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < WalHeader) return false;
            var head = new byte[WalHeader];
            fs.ReadExactly(head);
            if (!head.AsSpan(0, 8).SequenceEqual(WalMagic)) return false;
            txId = new Guid(head.AsSpan(12, 16));
            return true;
        }
        catch (IOException) { throw; }
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

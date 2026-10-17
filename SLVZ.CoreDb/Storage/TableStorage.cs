namespace SLVZ.CoreDb.Storage;

/// <summary>One table file: the primary B+Tree + one B+Tree per index, plus counters.</summary>
internal sealed class TableStorage : IDisposable
{
    readonly string _path;
    readonly CoreDbOptions _options;

    public Pager Pager { get; private set; }
    public BPlusTree Primary { get; private set; } = null!;
    public Dictionary<string, BPlusTree> Indexes { get; } = new();
    public long AutoIncrement;
    public long Count;

    public TableStorage(string path, CoreDbOptions options)
    {
        _path = path;
        _options = options;
        Pager = new Pager(path, options);
        try
        {
            if (Pager.Meta.Length == 0)
            {
                Primary = BPlusTree.Create(Pager);
                Commit();
            }
            else LoadMeta();
        }
        catch
        {
            Pager.Dispose();
            throw;
        }
    }

    void LoadMeta()
    {
        using var r = new BinaryReader(new MemoryStream(Pager.Meta));
        r.ReadByte(); // meta version
        Primary = new BPlusTree(Pager, r.ReadUInt32());
        AutoIncrement = r.ReadInt64();
        Count = r.ReadInt64();
        Indexes.Clear();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++)
        {
            string name = r.ReadString();
            Indexes[name] = new BPlusTree(Pager, r.ReadUInt32());
        }
    }

    byte[] SerializeMeta()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)1);
        w.Write(Primary.Root);
        w.Write(AutoIncrement);
        w.Write(Count);
        w.Write((byte)Indexes.Count);
        foreach (var kv in Indexes)
        {
            w.Write(kv.Key);
            w.Write(kv.Value.Root);
        }
        w.Flush();
        return ms.ToArray();
    }

    public void Commit(bool force = false) => Pager.Commit(SerializeMeta(), force);

    public void Rollback()
    {
        Pager.Rollback();
        LoadMeta();
    }

    /// <summary>Makes the set of index trees match <paramref name="wanted"/>. Returns the newly created (empty) ones.</summary>
    public List<string> SyncIndexes(IEnumerable<string> wanted)
    {
        var want = new HashSet<string>(wanted);
        foreach (var name in Indexes.Keys.Where(k => !want.Contains(k)).ToList())
        {
            Indexes[name].FreeAll();
            Indexes.Remove(name);
        }

        var created = new List<string>();
        foreach (var name in want)
        {
            if (Indexes.ContainsKey(name)) continue;
            Indexes[name] = BPlusTree.Create(Pager);
            created.Add(name);
        }
        return created;
    }

    public void Clear()
    {
        var names = Indexes.Keys.ToList();
        Primary.FreeAll();
        foreach (var t in Indexes.Values) t.FreeAll();
        Indexes.Clear();
        Primary = BPlusTree.Create(Pager);
        foreach (var n in names) Indexes[n] = BPlusTree.Create(Pager);
        Count = 0;
    }

    /// <summary>Rebuilds the file without dead space. Must not run inside a transaction.</summary>
    public void Compact()
    {
        var tmp = _path + ".compact";
        File.Delete(tmp);
        File.Delete(tmp + ".wal");

        var fast = new CoreDbOptions { XorKey = _options.XorKey, Durable = false, CachePages = _options.CachePages };
        using (var dst = new TableStorage(tmp, fast))
        {
            dst.SyncIndexes(Indexes.Keys);
            CopyTree(Primary, dst.Primary, dst);
            foreach (var kv in Indexes) CopyTree(kv.Value, dst.Indexes[kv.Key], dst);
            dst.AutoIncrement = AutoIncrement;
            dst.Count = Count;
            dst.Commit(force: true);
            dst.Pager.Sync();
        }
        File.Delete(tmp + ".wal");

        Pager.Dispose();
        try
        {
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            Pager = new Pager(_path, _options);
            LoadMeta();
        }
    }

    static void CopyTree(BPlusTree src, BPlusTree dst, TableStorage owner)
    {
        int n = 0;
        src.Scan(null, null, (k, v) =>
        {
            dst.Upsert(k, v);
            if (++n % 2000 == 0) owner.Commit();
            return true;
        });
    }

    public void Dispose() => Pager.Dispose();
}

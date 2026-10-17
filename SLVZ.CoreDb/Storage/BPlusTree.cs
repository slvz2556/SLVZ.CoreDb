using System.Buffers.Binary;

namespace SLVZ.CoreDb.Storage;

internal sealed class Entry
{
    public byte[] Key = null!;
    public byte[]? Value;          // inline value; null => stored in an overflow chain
    public uint OverflowPage;
    public int OverflowLength;

    public int Size => 2 + Key.Length + 1 + (Value != null ? 2 + Value.Length : 8);
}

internal readonly record struct Split(byte[] Key, uint Page);

/// <summary>
/// B+Tree over pages. Keys and values are byte arrays (keys compare bytewise).
/// Big values go to overflow pages. Deletes don't merge nodes (Compact() rebuilds the file).
/// </summary>
internal sealed class BPlusTree
{
    public const int MaxKey = 256;
    const int MaxInline = 640;
    const int LeafHeader = 12, InternalHeader = 8, OverflowHeader = 12;
    const int LeafCapacity = Pager.PageSize - LeafHeader;
    const int InternalCapacity = Pager.PageSize - InternalHeader;
    const int OverflowCapacity = Pager.PageSize - OverflowHeader;

    readonly Pager _p;
    public uint Root { get; private set; }

    public BPlusTree(Pager pager, uint root)
    {
        _p = pager;
        Root = root;
    }

    public static BPlusTree Create(Pager pager)
    {
        uint id = pager.Allocate();
        new Leaf { Id = id }.Save(pager);
        return new BPlusTree(pager, id);
    }

    // ================= nodes =================

    sealed class Leaf
    {
        public uint Id, Next, Prev;
        public List<Entry> Entries = new();

        public int Size
        {
            get { int s = 0; foreach (var e in Entries) s += e.Size; return s; }
        }

        public static Leaf Load(uint id, byte[] pg)
        {
            var n = new Leaf { Id = id };
            int count = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(1));
            n.Next = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(4));
            n.Prev = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(8));
            int pos = LeafHeader;
            for (int i = 0; i < count; i++)
            {
                var e = new Entry();
                int kl = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(pos)); pos += 2;
                e.Key = pg.AsSpan(pos, kl).ToArray(); pos += kl;
                byte flag = pg[pos++];
                if (flag == 0)
                {
                    int vl = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(pos)); pos += 2;
                    e.Value = pg.AsSpan(pos, vl).ToArray(); pos += vl;
                }
                else
                {
                    e.OverflowLength = BinaryPrimitives.ReadInt32LittleEndian(pg.AsSpan(pos)); pos += 4;
                    e.OverflowPage = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(pos)); pos += 4;
                }
                n.Entries.Add(e);
            }
            return n;
        }

        public void Save(Pager p)
        {
            var pg = p.Write(Id);
            Array.Clear(pg);
            pg[0] = PageType.Leaf;
            BinaryPrimitives.WriteUInt16LittleEndian(pg.AsSpan(1), (ushort)Entries.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(4), Next);
            BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(8), Prev);
            int pos = LeafHeader;
            foreach (var e in Entries)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(pg.AsSpan(pos), (ushort)e.Key.Length); pos += 2;
                e.Key.CopyTo(pg, pos); pos += e.Key.Length;
                if (e.Value != null)
                {
                    pg[pos++] = 0;
                    BinaryPrimitives.WriteUInt16LittleEndian(pg.AsSpan(pos), (ushort)e.Value.Length); pos += 2;
                    e.Value.CopyTo(pg, pos); pos += e.Value.Length;
                }
                else
                {
                    pg[pos++] = 1;
                    BinaryPrimitives.WriteInt32LittleEndian(pg.AsSpan(pos), e.OverflowLength); pos += 4;
                    BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(pos), e.OverflowPage); pos += 4;
                }
            }
        }
    }

    sealed class Inner
    {
        public uint Id;
        public List<byte[]> Keys = new();
        public List<uint> Children = new();   // Children.Count == Keys.Count + 1

        public int Size
        {
            get { int s = 0; foreach (var k in Keys) s += 2 + k.Length + 4; return s; }
        }

        public static Inner Load(uint id, byte[] pg)
        {
            var n = new Inner { Id = id };
            int count = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(1));
            n.Children.Add(BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(4)));
            int pos = InternalHeader;
            for (int i = 0; i < count; i++)
            {
                int kl = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(pos)); pos += 2;
                n.Keys.Add(pg.AsSpan(pos, kl).ToArray()); pos += kl;
                n.Children.Add(BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(pos))); pos += 4;
            }
            return n;
        }

        public void Save(Pager p)
        {
            var pg = p.Write(Id);
            Array.Clear(pg);
            pg[0] = PageType.Internal;
            BinaryPrimitives.WriteUInt16LittleEndian(pg.AsSpan(1), (ushort)Keys.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(4), Children[0]);
            int pos = InternalHeader;
            for (int i = 0; i < Keys.Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(pg.AsSpan(pos), (ushort)Keys[i].Length); pos += 2;
                Keys[i].CopyTo(pg, pos); pos += Keys[i].Length;
                BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(pos), Children[i + 1]); pos += 4;
            }
        }
    }

    // ================= helpers =================

    static int Cmp(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    static int LowerBound(List<Entry> entries, byte[] key, out bool found)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Cmp(entries[mid].Key, key) < 0) lo = mid + 1; else hi = mid;
        }
        found = lo < entries.Count && Cmp(entries[lo].Key, key) == 0;
        return lo;
    }

    /// <summary>Index of the child to follow = number of separator keys that are &lt;= key.</summary>
    static int ChildIndex(List<byte[]> keys, byte[] key)
    {
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Cmp(keys[mid], key) <= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    uint FindLeafId(byte[] key)
    {
        uint id = Root;
        while (true)
        {
            var pg = _p.Read(id);
            if (pg[0] == PageType.Leaf) return id;
            var n = Inner.Load(id, pg);
            id = n.Children[ChildIndex(n.Keys, key)];
        }
    }

    uint LeftmostLeafId()
    {
        uint id = Root;
        while (true)
        {
            var pg = _p.Read(id);
            if (pg[0] == PageType.Leaf) return id;
            id = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(4));
        }
    }

    // ================= values / overflow =================

    Entry MakeEntry(byte[] key, byte[] value)
    {
        var e = new Entry { Key = key };
        if (value.Length <= MaxInline)
        {
            e.Value = value;
            return e;
        }

        int chunks = (value.Length + OverflowCapacity - 1) / OverflowCapacity;
        uint next = 0;
        for (int c = chunks - 1; c >= 0; c--)
        {
            int off = c * OverflowCapacity;
            int len = Math.Min(OverflowCapacity, value.Length - off);
            uint id = _p.Allocate();
            var pg = _p.Write(id);
            Array.Clear(pg);
            pg[0] = PageType.Overflow;
            BinaryPrimitives.WriteUInt32LittleEndian(pg.AsSpan(4), next);
            BinaryPrimitives.WriteInt32LittleEndian(pg.AsSpan(8), len);
            Buffer.BlockCopy(value, off, pg, OverflowHeader, len);
            next = id;
        }
        e.OverflowPage = next;
        e.OverflowLength = value.Length;
        return e;
    }

    byte[] ReadValue(Entry e)
    {
        if (e.Value != null) return e.Value;
        var buf = new byte[e.OverflowLength];
        int off = 0;
        uint id = e.OverflowPage;
        while (id != 0)
        {
            var pg = _p.Read(id);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(pg.AsSpan(4));
            int len = BinaryPrimitives.ReadInt32LittleEndian(pg.AsSpan(8));
            Buffer.BlockCopy(pg, OverflowHeader, buf, off, len);
            off += len;
            id = next;
        }
        return buf;
    }

    void FreeOverflow(Entry e)
    {
        if (e.Value != null) return;
        uint id = e.OverflowPage;
        while (id != 0)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(_p.Read(id).AsSpan(4));
            _p.Free(id);
            id = next;
        }
    }

    // ================= public operations =================

    public byte[]? Get(byte[] key)
    {
        uint id = FindLeafId(key);
        var leaf = Leaf.Load(id, _p.Read(id));
        int i = LowerBound(leaf.Entries, key, out bool found);
        return found ? ReadValue(leaf.Entries[i]) : null;
    }

    public bool Contains(byte[] key)
    {
        uint id = FindLeafId(key);
        var leaf = Leaf.Load(id, _p.Read(id));
        LowerBound(leaf.Entries, key, out bool found);
        return found;
    }

    /// <returns>true if an existing value was replaced.</returns>
    public bool Upsert(byte[] key, byte[] value)
    {
        if (key.Length > MaxKey) throw new CoreDbException($"Key is too long ({key.Length} bytes, max {MaxKey}).");

        var split = InsertRec(Root, key, value, out bool replaced);
        if (split is { } s)
        {
            uint newRoot = _p.Allocate();
            var n = new Inner { Id = newRoot };
            n.Children.Add(Root);
            n.Keys.Add(s.Key);
            n.Children.Add(s.Page);
            n.Save(_p);
            Root = newRoot;
        }
        return replaced;
    }

    public bool Delete(byte[] key)
    {
        uint id = FindLeafId(key);
        var leaf = Leaf.Load(id, _p.Read(id));
        int i = LowerBound(leaf.Entries, key, out bool found);
        if (!found) return false;
        FreeOverflow(leaf.Entries[i]);
        leaf.Entries.RemoveAt(i);
        leaf.Save(_p);
        return true;
    }

    /// <summary>Visits entries with from &lt;= key &lt; to in key order. Return false from the visitor to stop.</summary>
    public void Scan(byte[]? from, byte[]? to, Func<byte[], byte[], bool> visit)
    {
        uint id = from == null ? LeftmostLeafId() : FindLeafId(from);
        while (id != 0)
        {
            var leaf = Leaf.Load(id, _p.Read(id));
            int start = from == null ? 0 : LowerBound(leaf.Entries, from, out _);
            from = null;
            for (int i = start; i < leaf.Entries.Count; i++)
            {
                var e = leaf.Entries[i];
                if (to != null && Cmp(e.Key, to) >= 0) return;
                if (!visit(e.Key, ReadValue(e))) return;
            }
            id = leaf.Next;
        }
    }

    /// <summary>Frees every page of this tree (the tree must not be used afterwards).</summary>
    public void FreeAll()
    {
        var stack = new Stack<uint>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            uint id = stack.Pop();
            var pg = _p.Read(id);
            if (pg[0] == PageType.Leaf)
            {
                foreach (var e in Leaf.Load(id, pg).Entries) FreeOverflow(e);
            }
            else
            {
                foreach (var c in Inner.Load(id, pg).Children) stack.Push(c);
            }
            _p.Free(id);
        }
        Root = 0;
    }

    // ================= insertion =================

    Split? InsertRec(uint id, byte[] key, byte[] value, out bool replaced)
    {
        var pg = _p.Read(id);

        if (pg[0] == PageType.Leaf)
        {
            var leaf = Leaf.Load(id, pg);
            int i = LowerBound(leaf.Entries, key, out bool found);
            var entry = MakeEntry(key, value);
            if (found)
            {
                FreeOverflow(leaf.Entries[i]);
                leaf.Entries[i] = entry;
                replaced = true;
            }
            else
            {
                leaf.Entries.Insert(i, entry);
                replaced = false;
            }

            if (leaf.Size <= LeafCapacity)
            {
                leaf.Save(_p);
                return null;
            }
            bool appendOnly = !found && i == leaf.Entries.Count - 1 && leaf.Next == 0;
            return SplitLeaf(leaf, appendOnly);
        }

        var node = Inner.Load(id, pg);
        int ci = ChildIndex(node.Keys, key);
        var child = InsertRec(node.Children[ci], key, value, out replaced);
        if (child is not { } cs) return null;

        node.Keys.Insert(ci, cs.Key);
        node.Children.Insert(ci + 1, cs.Page);
        if (node.Size <= InternalCapacity)
        {
            node.Save(_p);
            return null;
        }
        return SplitInner(node);
    }

    Split SplitLeaf(Leaf left, bool appendOnly)
    {
        int n = left.Entries.Count;
        int at;
        if (appendOnly) at = n - 1;     // sequential inserts keep pages full
        else
        {
            int total = left.Size, acc = 0;
            at = n - 1;
            for (int i = 0; i < n; i++)
            {
                acc += left.Entries[i].Size;
                if (acc >= total / 2) { at = i + 1; break; }
            }
            at = Math.Clamp(at, 1, n - 1);
        }

        var right = new Leaf { Id = _p.Allocate() };
        right.Entries.AddRange(left.Entries.GetRange(at, n - at));
        left.Entries.RemoveRange(at, n - at);

        right.Next = left.Next;
        right.Prev = left.Id;
        if (left.Next != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(_p.Write(left.Next).AsSpan(8), right.Id);
        left.Next = right.Id;

        left.Save(_p);
        right.Save(_p);
        return new Split(right.Entries[0].Key, right.Id);
    }

    Split SplitInner(Inner left)
    {
        int n = left.Keys.Count;
        int total = left.Size, acc = 0, m = n / 2;
        for (int i = 0; i < n; i++)
        {
            acc += 2 + left.Keys[i].Length + 4;
            if (acc >= total / 2) { m = i; break; }
        }
        m = Math.Clamp(m, 1, n - 2);

        var promote = left.Keys[m];
        var right = new Inner { Id = _p.Allocate() };
        right.Keys.AddRange(left.Keys.GetRange(m + 1, n - m - 1));
        right.Children.AddRange(left.Children.GetRange(m + 1, n - m));
        left.Keys.RemoveRange(m, n - m);
        left.Children.RemoveRange(m + 1, n - m);

        left.Save(_p);
        right.Save(_p);
        return new Split(promote, right.Id);
    }
}

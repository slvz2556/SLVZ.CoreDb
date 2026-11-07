using System.Linq.Expressions;
using System.Reflection;
using SLVZ.CoreDb.Storage;

namespace SLVZ.CoreDb;

/// <summary>
/// A table of <typeparamref name="T"/> rows (one file). Thread-safe.
/// Readers never wait for a writer's transaction: they see the last committed state and only pause for the moment the
/// writer applies its commit. Every write method is one atomic, crash-safe transaction. Use <see cref="Transaction"/> to group several
/// writes of this table and <see cref="CoreDatabase.Transaction"/> to group writes of several tables.
/// </summary>
public sealed partial class DataSet<T> : ITable where T : class, new()
{
    readonly TableStorage _s;
    readonly EntityInfo<T> _e = EntityInfo<T>.Instance;
    readonly string _name = typeof(T).Name;
    readonly object _txLock = new();                                              // one writer transaction at a time
    readonly ReaderWriterLockSlim _view = new(LockRecursionPolicy.SupportsRecursion); // readers vs. the short "apply commit" step
    int _depth;
    bool _failed;

    internal DataSet(string path, CoreDbOptions options, Func<Guid, bool>? decision)
    {
        _s = new TableStorage(path, options, decision);
        try
        {
            RunWrite(() =>
            {
                var created = _s.SyncIndexes(_e.Indexes.Select(i => i.Prop.Name));
                if (created.Count > 0 && _s.Count > 0) BuildIndexes(created);
                return 0;
            });
        }
        catch
        {
            _s.Dispose();
            throw;
        }
    }

    // ===================== reads =====================

    /// <summary>Number of rows in the table (instant, no scan).</summary>
    public long Count => RunRead(v => v.Count);

    /// <summary>Returns the row with this primary key, or null if there is none. Very fast (one B+Tree lookup).</summary>
    /// <param name="key">The primary key value (an int, string, Guid... matching the key property).</param>
    public T? Find(object key) => RunRead(v =>
    {
        var data = v.Primary.Get(PkBytes(key));
        return data == null ? null : _e.Deserialize(data);
    });

    /// <summary>Checks whether a row with this primary key exists, without loading it.</summary>
    public bool Exists(object key) => RunRead(v => v.Primary.Contains(PkBytes(key)));

    /// <summary>Returns every row ordered by primary key. It loads the whole table into memory, so on big tables prefer Where / Range / OrderedBy(take: ...).</summary>
    public List<T> GetAll() => FullScan(_ => true);

    /// <summary>Always scans every row (no index). Takes a plain delegate, so it can run any C# code.</summary>
    public List<T> FullScan(Func<T, bool> predicate) => RunRead(v =>
    {
        var list = new List<T>();
        v.Primary.Scan(null, null, (_, val) =>
        {
            var e = _e.Deserialize(val);
            if (predicate(e)) list.Add(e);
            return true;
        });
        return list;
    });

    /// <summary>Finds all rows whose [Index] property equals <paramref name="value"/>. Throws if the property has no [Index]. For simple conditions this is the lightest way to query.</summary>
    /// <param name="property">The indexed property, e.g. x =&gt; x.Slug.</param>
    /// <param name="value">The value to look for (null is allowed).</param>
    public List<T> FindBy<TValue>(Expression<Func<T, TValue>> property, TValue value) => RunRead(v =>
    {
        var ix = GetIndex(property);
        var prefix = KeyEncoder.Trunc(KeyEncoder.Encode(value, ix.Prop.Type));
        var list = new List<T>();
        v.Indexes[ix.Prop.Name].Scan(prefix, KeyEncoder.Successor(prefix), (_, pk) =>
        {
            var data = v.Primary.Get(pk);
            if (data == null) return true;
            var e = _e.Deserialize(data);
            if (Equals(ix.Prop.Get(e), value)) list.Add(e);
            return true;
        });
        return list;
    });

    /// <summary>Rows whose [Index] property is between <paramref name="from"/> and <paramref name="to"/> (both included), ordered by that property.
    /// Only the index entries and rows that are really returned are read, so <paramref name="skip"/> / <paramref name="take"/> make paging cheap.
    /// (For strings longer than 128 bytes the bounds are approximate.)</summary>
    /// <param name="property">The indexed property, e.g. x =&gt; x.Date.</param>
    /// <param name="from">Lowest value.</param>
    /// <param name="to">Highest value.</param>
    /// <param name="descending">true = from the highest value down.</param>
    /// <param name="skip">How many matching rows to skip first.</param>
    /// <param name="take">Maximum number of rows to return (null = all).</param>
    public List<T> Range<TValue>(Expression<Func<T, TValue>> property, TValue from, TValue to,
        bool descending = false, int skip = 0, int? take = null) => RunRead(v =>
    {
        var ix = GetIndex(property);
        var start = KeyEncoder.Trunc(KeyEncoder.Encode(from, ix.Prop.Type));
        var end = KeyEncoder.Successor(KeyEncoder.Trunc(KeyEncoder.Encode(to, ix.Prop.Type)));
        return ReadIndex(v, ix, start, end, descending, skip, take);
    });

    /// <summary>Rows ordered by an [Index] property, with paging. Reads only what it returns (plus the skipped index entries), so
    /// <c>OrderedBy(x =&gt; x.Date, descending: true, take: 20)</c> is fast on millions of rows.</summary>
    /// <param name="property">The indexed property, e.g. x =&gt; x.Date.</param>
    /// <param name="descending">true = highest value first.</param>
    /// <param name="skip">How many rows to skip first.</param>
    /// <param name="take">Maximum number of rows to return (null = all).</param>
    public List<T> OrderedBy<TValue>(Expression<Func<T, TValue>> property, bool descending = false, int skip = 0, int? take = null) =>
        RunRead(v => ReadIndex(v, GetIndex(property), null, null, descending, skip, take));

    /// <summary>The smallest value of an [Index] or [PrimaryKey] property (null values are ignored), read straight from the index. Throws if there is no such row.</summary>
    public TValue Min<TValue>(Expression<Func<T, TValue>> property) =>
        TryMin(property, out var value) ? value : throw new InvalidOperationException("The table has no rows with a value for this property.");

    /// <summary>The largest value of an [Index] or [PrimaryKey] property (null values are ignored), read straight from the index. Throws if there is no such row.</summary>
    public TValue Max<TValue>(Expression<Func<T, TValue>> property) =>
        TryMax(property, out var value) ? value : throw new InvalidOperationException("The table has no rows with a value for this property.");

    /// <summary>Like <see cref="Min{TValue}"/> but returns false instead of throwing when there are no rows.</summary>
    public bool TryMin<TValue>(Expression<Func<T, TValue>> property, out TValue value) => TryEdge(property, false, out value);

    /// <summary>Like <see cref="Max{TValue}"/> but returns false instead of throwing when there are no rows.</summary>
    public bool TryMax<TValue>(Expression<Func<T, TValue>> property, out TValue value) => TryEdge(property, true, out value);

    // ===================== writes =====================

    /// <summary>Adds a new row (one atomic transaction). If the key is [PrimaryKey(AutoIncrement = true)] and 0, the new id is generated and written back into <paramref name="entity"/>.
    /// Throws <see cref="DuplicateKeyException"/> if the key exists and <see cref="UniqueConstraintException"/> if a unique index value is taken; nothing is changed in that case.</summary>
    public void Insert(T entity) => RunWrite(() => { InsertCore(entity); return 0; });

    /// <summary>Adds many rows in ONE transaction: all are saved or none (if one fails). Much faster than calling Insert in a loop because the disk is synced once.
    /// For huge imports (millions of rows) call it in batches of about 10,000 rows: a transaction keeps its changed pages in memory until it commits.</summary>
    public void InsertMany(IEnumerable<T> entities) => RunWrite(() =>
    {
        foreach (var e in entities) InsertCore(e);
        return 0;
    });

    /// <summary>Replaces the stored row that has the same primary key as <paramref name="entity"/> and keeps the indexes in sync.</summary>
    /// <returns>false if no row with this key exists (nothing is inserted).</returns>
    public bool Update(T entity) => RunWrite(() => UpdateCore(entity));

    /// <summary>Updates the row if its key exists, otherwise inserts it.</summary>
    public void Upsert(T entity) => RunWrite(() =>
    {
        if (!IsAutoZero(entity) && UpdateCore(entity)) return 0;
        InsertCore(entity);
        return 0;
    });

    /// <summary>Deletes the row with this primary key and its index entries.</summary>
    /// <returns>true if a row was deleted, false if there was none.</returns>
    public bool Delete(object key) => RunWrite(() => DeleteCore(PkBytes(key)));

    /// <summary>Deletes the row that has the same primary key as <paramref name="entity"/>.</summary>
    /// <returns>true if a row was deleted, false if there was none.</returns>
    public bool Delete(T entity) => RunWrite(() => DeleteCore(PkBytes(_e.Pk.Get(entity))));

    /// <summary>Adds the rows that don't exist yet and updates the ones that do, all in ONE transaction.</summary>
    public void UpsertMany(IEnumerable<T> entities) => RunWrite(() =>
    {
        foreach (var e in entities) Upsert(e);
        return 0;
    });

    /// <summary>Updates many rows (matched by primary key) in ONE transaction: all or none. Rows that don't exist are skipped, not inserted.</summary>
    /// <returns>How many rows were actually updated.</returns>
    public int UpdateMany(IEnumerable<T> entities) => RunWrite(() =>
    {
        int n = 0;
        foreach (var e in entities)
            if (UpdateCore(e)) n++;
        return n;
    });

    /// <summary>Finds the rows matching <paramref name="predicate"/> (indexes are used like in <see cref="Where"/>), applies <paramref name="change"/> to each and saves them, all in ONE transaction.
    /// <example><c>users.UpdateMany(u =&gt; u.IsActive == false, u =&gt; u.Name = "-");</c></example>
    /// The primary key must not be changed inside <paramref name="change"/>.</summary>
    /// <returns>How many rows were updated.</returns>
    public int UpdateMany(Expression<Func<T, bool>> predicate, Action<T> change) => RunWrite(() =>
    {
        int n = 0;
        foreach (var row in Where(predicate))
        {
            var before = PkBytes(_e.Pk.Get(row));
            change(row);
            if (!before.AsSpan().SequenceEqual(PkBytes(_e.Pk.Get(row))))
                throw new InvalidOperationException("UpdateMany: the primary key can't be changed.");
            if (UpdateCore(row)) n++;
        }
        return n;
    });

    /// <summary>Deletes all the given rows (matched by primary key) in ONE transaction. Missing rows are skipped.</summary>
    /// <returns>How many rows were deleted.</returns>
    public int DeleteMany(IEnumerable<T> entities) => RunWrite(() =>
    {
        int n = 0;
        foreach (var e in entities)
            if (DeleteCore(PkBytes(_e.Pk.Get(e)))) n++;
        return n;
    });

    /// <summary>Deletes the rows with these primary keys (e.g. a list of ids) in ONE transaction. Missing keys are skipped.</summary>
    /// <returns>How many rows were deleted.</returns>
    public int DeleteMany<TKey>(IEnumerable<TKey> keys) => RunWrite(() =>
    {
        int n = 0;
        foreach (var k in keys)
            if (DeleteCore(PkBytes(k))) n++;
        return n;
    });

    /// <summary>Deletes every row matching <paramref name="predicate"/> (indexes are used like in <see cref="Where"/>) in ONE transaction.
    /// <example><c>notes.DeleteMany(n =&gt; n.Date &lt; DateTime.Now.AddYears(-1));</c></example></summary>
    /// <returns>How many rows were deleted.</returns>
    public int DeleteMany(Expression<Func<T, bool>> predicate) => RunWrite(() =>
    {
        int n = 0;
        foreach (var row in Where(predicate))
            if (DeleteCore(PkBytes(_e.Pk.Get(row)))) n++;
        return n;
    });

    /// <summary>Runs several operations as ONE atomic transaction: if anything inside throws, every change made inside is rolled back. Calls to this table's methods inside the action join the transaction,
    /// and reads inside it see the changes made so far. Other threads keep seeing the old data until the transaction commits.
    /// To make several TABLES atomic together use <see cref="CoreDatabase.Transaction"/>.</summary>
    public void Transaction(Action action) => RunWrite(() => { action(); return 0; });

    /// <summary>Deletes ALL rows of the table (and its indexes). The file keeps its size until you call <see cref="Compact"/>.</summary>
    public void Clear() => RunWrite(() => { _s.Clear(); return 0; });

    /// <summary>Rewrites the file without dead space (after many deletes). Blocks other threads while running.</summary>
    public void Compact()
    {
        Monitor.Enter(_txLock);
        try
        {
            _view.EnterWriteLock();
            try
            {
                if (_depth > 0) throw new InvalidOperationException("Compact can't run inside a transaction.");
                _s.Compact();
            }
            finally { _view.ExitWriteLock(); }
        }
        finally { Monitor.Exit(_txLock); }
    }

    /// <summary>Closes the table files. Normally you dispose the CoreDatabase / CoreDbContext instead.</summary>
    public void Dispose()
    {
        Monitor.Enter(_txLock);
        try
        {
            _view.EnterWriteLock();
            try { _s.Dispose(); }
            finally { _view.ExitWriteLock(); }
        }
        finally { Monitor.Exit(_txLock); }
    }

    // ===================== write internals =====================

    void InsertCore(T entity)
    {
        var pkProp = _e.Pk;
        object? pkVal = pkProp.Get(entity);

        if (_e.AutoIncrement)
        {
            long cur = pkVal is null ? 0 : Convert.ToInt64(pkVal);
            if (cur == 0)
            {
                long next = _s.AutoIncrement + 1;
                pkVal = Convert.ChangeType(next, pkProp.Type);
                pkProp.Set(entity, pkVal);
                _s.AutoIncrement = next;
            }
            else if (cur > _s.AutoIncrement) _s.AutoIncrement = cur;
        }

        var pk = PkBytes(pkVal);
        if (_s.Primary.Contains(pk))
            throw new DuplicateKeyException($"{typeof(T).Name}: key '{pkVal}' already exists.");

        foreach (var ix in _e.Indexes)
            if (ix.Unique) CheckUnique(ix, entity, pk);

        _s.Primary.Upsert(pk, _e.Serialize(entity));
        foreach (var ix in _e.Indexes)
            _s.Indexes[ix.Prop.Name].Upsert(IndexKey(ix, entity, pk), pk);
        _s.Count++;
    }

    bool UpdateCore(T entity)
    {
        var pk = PkBytes(_e.Pk.Get(entity));
        var oldData = _s.Primary.Get(pk);
        if (oldData == null) return false;
        var old = _e.Deserialize(oldData);

        foreach (var ix in _e.Indexes)
        {
            var oldKey = IndexKey(ix, old, pk);
            var newKey = IndexKey(ix, entity, pk);
            if (oldKey.AsSpan().SequenceEqual(newKey)) continue;

            if (ix.Unique) CheckUnique(ix, entity, pk);
            var tree = _s.Indexes[ix.Prop.Name];
            tree.Delete(oldKey);
            tree.Upsert(newKey, pk);
        }
        _s.Primary.Upsert(pk, _e.Serialize(entity));
        return true;
    }

    bool DeleteCore(byte[] pk)
    {
        var data = _s.Primary.Get(pk);
        if (data == null) return false;
        var entity = _e.Deserialize(data);

        foreach (var ix in _e.Indexes)
            _s.Indexes[ix.Prop.Name].Delete(IndexKey(ix, entity, pk));
        _s.Primary.Delete(pk);
        _s.Count--;
        return true;
    }

    void CheckUnique(IndexMeta<T> ix, T entity, byte[] pk)
    {
        var value = ix.Prop.Get(entity);
        if (value is null) return;

        var enc = KeyEncoder.Encode(value, ix.Prop.Type);
        var prefix = KeyEncoder.Trunc(enc);
        bool truncated = enc.Length > KeyEncoder.MaxIndexValueBytes;
        bool conflict = false;

        _s.Indexes[ix.Prop.Name].Scan(prefix, KeyEncoder.Successor(prefix), (_, otherPk) =>
        {
            if (otherPk.AsSpan().SequenceEqual(pk)) return true;
            if (truncated)
            {
                var data = _s.Primary.Get(otherPk);
                if (data == null || !Equals(ix.Prop.Get(_e.Deserialize(data)), value)) return true;
            }
            conflict = true;
            return false;
        });

        if (conflict)
            throw new UniqueConstraintException($"{typeof(T).Name}.{ix.Prop.Name}: value '{value}' already exists.");
    }

    byte[] IndexKey(IndexMeta<T> ix, T entity, byte[] pk)
    {
        var v = KeyEncoder.Trunc(KeyEncoder.Encode(ix.Prop.Get(entity), ix.Prop.Type));
        var key = new byte[v.Length + pk.Length];
        v.CopyTo(key, 0);
        pk.CopyTo(key, v.Length);
        return key;
    }

    void BuildIndexes(List<string> names)
    {
        var metas = names.Select(n => _e.FindIndex(n)!).ToList();
        _s.Primary.Scan(null, null, (pk, data) =>
        {
            var e = _e.Deserialize(data);
            foreach (var ix in metas)
                _s.Indexes[ix.Prop.Name].Upsert(IndexKey(ix, e, pk), pk);
            return true;
        });
    }

    bool IsAutoZero(T entity)
    {
        if (!_e.AutoIncrement) return false;
        var v = _e.Pk.Get(entity);
        return v is null || Convert.ToInt64(v) == 0;
    }

    byte[] PkBytes(object? key) => KeyEncoder.EncodePk(key, _e.Pk.Type);

    // ===================== read internals =====================

    /// <summary>Walks an index (forwards or backwards), skips <paramref name="skip"/> entries and loads at most <paramref name="take"/> rows.</summary>
    List<T> ReadIndex(TableView v, IndexMeta<T> ix, byte[]? start, byte[]? end, bool descending, int skip, int? take)
    {
        var list = new List<T>();
        if (take is <= 0) return list;
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));

        int skipped = 0;
        bool Visit(byte[] _, byte[] pk)
        {
            if (skipped < skip) { skipped++; return true; }
            var data = v.Primary.Get(pk);
            if (data != null) list.Add(_e.Deserialize(data));
            return take == null || list.Count < take;
        }

        var tree = v.Indexes[ix.Prop.Name];
        if (descending) tree.ScanReverse(start, end, Visit);
        else tree.Scan(start, end, Visit);
        return list;
    }

    bool TryEdge<TValue>(Expression<Func<T, TValue>> property, bool descending, out TValue value)
    {
        var name = PropName(property);
        var row = RunRead(v => EdgeRow(v, name, descending));
        if (row == null)
        {
            value = default!;
            return false;
        }
        value = (TValue)_e.FindProp(name)!.Get(row)!;
        return true;
    }

    T? EdgeRow(TableView v, string name, bool descending)
    {
        var nonNull = new byte[] { 1 };   // keys of null values start with 0
        if (name == _e.Pk.Name)
        {
            T? found = null;
            bool Visit(byte[] _, byte[] val) { found = _e.Deserialize(val); return false; }
            if (descending) v.Primary.ScanReverse(nonNull, null, Visit);
            else v.Primary.Scan(nonNull, null, Visit);
            return found;
        }

        var ix = _e.FindIndex(name)
                 ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} has no [Index] attribute.");
        var rows = ReadIndex(v, ix, nonNull, null, descending, 0, 1);
        return rows.Count == 0 ? null : rows[0];
    }

    static string PropName<TValue>(Expression<Func<T, TValue>> selector)
    {
        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } u) body = u.Operand;
        if (body is MemberExpression { Member: PropertyInfo p }) return p.Name;
        throw new ArgumentException("Select a single property, e.g. x => x.Title", nameof(selector));
    }

    IndexMeta<T> GetIndex<TValue>(Expression<Func<T, TValue>> selector)
    {
        var name = PropName(selector);
        return _e.FindIndex(name)
               ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} has no [Index] attribute.");
    }

    // ===================== locking / transactions =====================

    /// <summary>Readers use the last committed state. A thread that is inside its own transaction on this table sees its own changes instead.</summary>
    TResult RunRead<TResult>(Func<TableView, TResult> f)
    {
        if (Monitor.IsEntered(_txLock)) return f(_s.Live);

        _view.EnterReadLock();
        try { return f(_s.Committed); }
        finally { _view.ExitReadLock(); }
    }

    void CommitLocal()
    {
        _view.EnterWriteLock();     // only for the short "apply" step: waits for readers that are mid-read, blocks new ones
        try { _s.Commit(); }
        finally { _view.ExitWriteLock(); }
    }

    TResult RunWrite<TResult>(Func<TResult> f)
    {
        Monitor.Enter(_txLock);
        try
        {
            _depth++;
            try
            {
                var result = f();
                if (_depth == 1)
                {
                    if (_failed)
                    {
                        _failed = false;
                        _s.Rollback();
                        throw new InvalidOperationException("The transaction was rolled back because an operation inside it failed.");
                    }
                    CommitLocal();
                }
                return result;
            }
            catch
            {
                _failed = true;
                if (_depth == 1)
                {
                    _failed = false;
                    _s.Rollback();
                }
                throw;
            }
            finally { _depth--; }
        }
        finally { Monitor.Exit(_txLock); }
    }

    // ----- multi-table transactions (driven by CoreDatabase.Transaction) -----

    string ITable.Name => _name;

    void ITable.BeginMulti()
    {
        Monitor.Enter(_txLock);
        if (_depth > 0)
        {
            Monitor.Exit(_txLock);
            throw new InvalidOperationException("A database transaction can't start inside a transaction of a single table.");
        }
        _depth = 1;          // inner operations see depth >= 2 and therefore never commit on their own
        _failed = false;
    }

    bool ITable.HasFailed => _failed;

    bool ITable.Prepare(Guid txId) => _s.Prepare(txId);

    void ITable.Apply()
    {
        _view.EnterWriteLock();
        try { _s.Apply(); }
        finally { _view.ExitWriteLock(); }
    }

    void ITable.Abort()
    {
        _failed = false;
        _s.Rollback();
    }

    void ITable.Fault() => _s.MarkFaulted();

    void ITable.EndMulti()
    {
        _depth = 0;
        _failed = false;
        Monitor.Exit(_txLock);
    }
}

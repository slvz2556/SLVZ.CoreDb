using System.Linq.Expressions;
using System.Reflection;
using SLVZ.CoreDb.Storage;

namespace SLVZ.CoreDb;

/// <summary>
/// A table of <typeparamref name="T"/> rows (one file). Thread-safe: many readers run in parallel; writers are exclusive.
/// Every write method is one atomic, crash-safe transaction. Use <see cref="Transaction"/> to group several.
/// </summary>
public sealed class DataSet<T> : IDisposable where T : class, new()
{
    readonly TableStorage _s;
    readonly EntityInfo<T> _e = EntityInfo<T>.Instance;
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    int _depth;
    bool _failed;

    internal DataSet(string path, CoreDbOptions options)
    {
        _s = new TableStorage(path, options);
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
    public long Count => RunRead(() => _s.Count);

    /// <summary>Returns the row with this primary key, or null if there is none. Very fast (one B+Tree lookup).</summary>
    /// <param name="key">The primary key value (an int, string, Guid... matching the key property).</param>
    public T? Find(object key) => RunRead(() =>
    {
        var data = _s.Primary.Get(PkBytes(key));
        return data == null ? null : _e.Deserialize(data);
    });

    /// <summary>Checks whether a row with this primary key exists, without loading it.</summary>
    public bool Exists(object key) => RunRead(() => _s.Primary.Contains(PkBytes(key)));

    /// <summary>Returns every row ordered by primary key. It loads the whole table into memory, so on big tables prefer Where / Range / OrderedBy(take: ...).</summary>
    public List<T> GetAll() => FullScan(_ => true);

    /// <summary>Always scans every row (no index). Takes a plain delegate, so it can run any C# code.</summary>
    public List<T> FullScan(Func<T, bool> predicate) => RunRead(() =>
    {
        var list = new List<T>();
        _s.Primary.Scan(null, null, (_, v) =>
        {
            var e = _e.Deserialize(v);
            if (predicate(e)) list.Add(e);
            return true;
        });
        return list;
    });

    /// <summary>
    /// Filters rows with a C# lambda. Conditions joined by &amp;&amp; that compare an [Index] / primary-key property
    /// with ==, &gt;, &gt;=, &lt;, &lt;= are answered through the B+Tree; the full lambda is then applied to the
    /// candidates, so any other condition still works. Without a usable condition (or with ||) it scans all rows.
    /// Rows come back in index order when an index was used.
    /// </summary>
    public List<T> Where(Expression<Func<T, bool>> predicate) => RunRead(() =>
    {
        var plan = Plan(predicate);
        // few candidates (index used): the interpreter starts instantly; full scan: real compiled IL is faster per row
        var test = predicate.Compile(preferInterpretation: plan != null);
        var list = new List<T>();
        void Visit(byte[] data)
        {
            var e = _e.Deserialize(data);
            if (test(e)) list.Add(e);
        }

        if (plan is not { } p)
        {
            _s.Primary.Scan(null, null, (_, v) => { Visit(v); return true; });
        }
        else if (p.Tree == null)
        {
            _s.Primary.Scan(p.Start, p.End, (_, v) => { Visit(v); return true; });
        }
        else
        {
            p.Tree.Scan(p.Start, p.End, (_, pk) =>
            {
                var data = _s.Primary.Get(pk);
                if (data != null) Visit(data);
                return true;
            });
        }
        return list;
    });

    /// <summary>Finds all rows whose [Index] property equals <paramref name="value"/>. Throws if the property has no [Index]. For simple conditions this is the lightest way to query.</summary>
    /// <param name="property">The indexed property, e.g. x =&gt; x.Slug.</param>
    /// <param name="value">The value to look for (null is allowed).</param>
    public List<T> FindBy<TValue>(Expression<Func<T, TValue>> property, TValue value) => RunRead(() =>
    {
        var ix = GetIndex(property);
        var prefix = KeyEncoder.Trunc(KeyEncoder.Encode(value, ix.Prop.Type));
        var list = new List<T>();
        _s.Indexes[ix.Prop.Name].Scan(prefix, KeyEncoder.Successor(prefix), (_, pk) =>
        {
            var data = _s.Primary.Get(pk);
            if (data == null) return true;
            var e = _e.Deserialize(data);
            if (Equals(ix.Prop.Get(e), value)) list.Add(e);
            return true;
        });
        return list;
    });

    /// <summary>from &lt;= value &lt;= to through an [Index] property, ordered by that property.
    /// (For strings longer than 128 bytes the bounds are approximate.)</summary>
    public List<T> Range<TValue>(Expression<Func<T, TValue>> property, TValue from, TValue to) => RunRead(() =>
    {
        var ix = GetIndex(property);
        var start = KeyEncoder.Trunc(KeyEncoder.Encode(from, ix.Prop.Type));
        var end = KeyEncoder.Successor(KeyEncoder.Trunc(KeyEncoder.Encode(to, ix.Prop.Type)));
        var list = new List<T>();
        _s.Indexes[ix.Prop.Name].Scan(start, end, (_, pk) =>
        {
            var data = _s.Primary.Get(pk);
            if (data != null) list.Add(_e.Deserialize(data));
            return true;
        });
        return list;
    });

    /// <summary>Rows ordered by an [Index] property, with optional paging.</summary>
    public List<T> OrderedBy<TValue>(Expression<Func<T, TValue>> property, bool descending = false, int skip = 0, int? take = null) =>
        RunRead(() =>
        {
            var ix = GetIndex(property);
            var pks = new List<byte[]>();
            _s.Indexes[ix.Prop.Name].Scan(null, null, (_, pk) => { pks.Add(pk); return true; });
            if (descending) pks.Reverse();

            var list = new List<T>();
            foreach (var pk in pks.Skip(skip).Take(take ?? int.MaxValue))
            {
                var data = _s.Primary.Get(pk);
                if (data != null) list.Add(_e.Deserialize(data));
            }
            return list;
        });

    // ===================== writes =====================

    /// <summary>Adds a new row (one atomic transaction). If the key is [PrimaryKey(AutoIncrement = true)] and 0, the new id is generated and written back into <paramref name="entity"/>.
    /// Throws <see cref="DuplicateKeyException"/> if the key exists and <see cref="UniqueConstraintException"/> if a unique index value is taken; nothing is changed in that case.</summary>
    public void Insert(T entity) => RunWrite(() => { InsertCore(entity); return 0; });

    /// <summary>Adds many rows in ONE transaction: all are saved or none (if one fails). Much faster than calling Insert in a loop because the disk is synced once.</summary>
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

    /// <summary>Runs several operations as ONE atomic transaction: if anything inside throws, every change made inside is rolled back. Calls to this table's methods inside the action join the transaction.</summary>
    public void Transaction(Action action) => RunWrite(() => { action(); return 0; });

    /// <summary>Deletes ALL rows of the table (and its indexes). The file keeps its size until you call <see cref="Compact"/>.</summary>
    public void Clear() => RunWrite(() => { _s.Clear(); return 0; });

    /// <summary>Rewrites the file without dead space (after many deletes). Blocks other threads while running.</summary>
    public void Compact()
    {
        _lock.EnterWriteLock();
        try
        {
            if (_depth > 0) throw new InvalidOperationException("Compact can't run inside a transaction.");
            _s.Compact();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Closes the table files. Normally you dispose the CoreDatabase / CoreDbContext instead.</summary>
    public void Dispose()
    {
        _lock.EnterWriteLock();
        try { _s.Dispose(); }
        finally { _lock.ExitWriteLock(); }
    }

    // ===================== internals =====================

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

    IndexMeta<T> GetIndex<TValue>(Expression<Func<T, TValue>> selector)
    {
        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } u) body = u.Operand;
        if (body is MemberExpression { Member: PropertyInfo p })
            return _e.FindIndex(p.Name)
                   ?? throw new InvalidOperationException($"{typeof(T).Name}.{p.Name} has no [Index] attribute.");
        throw new ArgumentException("Select a single property, e.g. x => x.Title", nameof(selector));
    }

    // ---------- query planning for Where ----------

    sealed class Bound
    {
        public bool HasEq, HasLo, HasHi;
        public object? Eq, Lo, Hi;
    }

    sealed class ParamFinder : ExpressionVisitor
    {
        readonly ParameterExpression _p;
        public bool Found;
        public ParamFinder(ParameterExpression p) => _p = p;
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == _p) Found = true;
            return node;
        }
    }

    (BPlusTree? Tree, byte[]? Start, byte[]? End)? Plan(Expression<Func<T, bool>> expr)
    {
        try
        {
            var param = expr.Parameters[0];
            var conjuncts = new List<Expression>();
            SplitAnd(expr.Body, conjuncts);

            var bounds = new Dictionary<string, Bound>();
            foreach (var c in conjuncts)
            {
                if (!TryMatch(c, param, out var name, out var op, out var value)) continue;
                if (name != _e.Pk.Name && _e.FindIndex(name) == null) continue;
                if (!bounds.TryGetValue(name, out var b)) bounds[name] = b = new Bound();

                switch (op)
                {
                    case ExpressionType.Equal: b.HasEq = true; b.Eq = value; break;
                    case ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual when value != null: b.HasLo = true; b.Lo = value; break;
                    case ExpressionType.LessThan or ExpressionType.LessThanOrEqual when value != null: b.HasHi = true; b.Hi = value; break;
                }
            }

            string? best = null;
            int bestScore = int.MaxValue;
            foreach (var (name, b) in bounds)
            {
                bool isPk = name == _e.Pk.Name;
                int score;
                if (b.HasEq) score = isPk ? 0 : _e.FindIndex(name)!.Unique ? 1 : 2;
                else if (b.HasLo || b.HasHi) score = (isPk ? 3 : 5) + (b.HasLo && b.HasHi ? 0 : 1);
                else continue;
                if (score < bestScore) { bestScore = score; best = name; }
            }
            if (best == null) return null;

            var bd = bounds[best];
            bool pk = best == _e.Pk.Name;
            var type = pk ? _e.Pk.Type : _e.FindIndex(best)!.Prop.Type;
            byte[] Enc(object? v)
            {
                var e = KeyEncoder.Encode(v, type);
                return pk ? e : KeyEncoder.Trunc(e);
            }

            var tree = pk ? null : _s.Indexes[best];
            if (bd.HasEq)
            {
                var prefix = Enc(bd.Eq);
                return (tree, prefix, KeyEncoder.Successor(prefix));
            }
            var start = bd.HasLo ? Enc(bd.Lo) : new byte[] { 1 };   // 0x00 = null values
            var end = bd.HasHi ? KeyEncoder.Successor(Enc(bd.Hi)) : null;
            return (tree, start, end);
        }
        catch (Exception)
        {
            return null;   // can't plan it: scan everything, the lambda decides (and reports its own errors)
        }
    }

    static void SplitAnd(Expression e, List<Expression> list)
    {
        if (e is BinaryExpression { NodeType: ExpressionType.AndAlso } b)
        {
            SplitAnd(b.Left, list);
            SplitAnd(b.Right, list);
        }
        else list.Add(e);
    }

    static Expression Unwrap(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert } u ? Unwrap(u.Operand) : e;

    static bool TryMatch(Expression e, ParameterExpression param, out string name, out ExpressionType op, out object? value)
    {
        name = ""; op = default; value = null;
        if (e is not BinaryExpression b) return false;
        if (b.NodeType is not (ExpressionType.Equal or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual)) return false;

        var left = Unwrap(b.Left);
        var right = Unwrap(b.Right);
        Expression valueExpr;
        if (IsParamProperty(left, param, out name) && !Uses(right, param)) { valueExpr = right; op = b.NodeType; }
        else if (IsParamProperty(right, param, out name) && !Uses(left, param)) { valueExpr = left; op = Flip(b.NodeType); }
        else return false;

        value = Eval(valueExpr);
        return true;
    }

    // constants and captured variables (closure fields) are read by reflection; anything else is interpreted
    static object? Eval(Expression e)
    {
        switch (e)
        {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression { Expression: null } m:
                return m.Member is FieldInfo sf ? sf.GetValue(null) : ((PropertyInfo)m.Member).GetValue(null);
            case MemberExpression { Expression: { } inner } m when !HasCall(inner):
                var target = Eval(inner);
                return m.Member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)m.Member).GetValue(target);
            default:
                return Expression.Lambda<Func<object?>>(Expression.Convert(e, typeof(object))).Compile(preferInterpretation: true)();
        }
    }

    static bool HasCall(Expression e) => e is not (ConstantExpression or MemberExpression { Expression: null })
        && !(e is MemberExpression me && !HasCall(me.Expression!));

    static bool IsParamProperty(Expression e, ParameterExpression param, out string name)
    {
        if (e is MemberExpression { Member: PropertyInfo p, Expression: ParameterExpression pe } && pe == param)
        {
            name = p.Name;
            return true;
        }
        name = "";
        return false;
    }

    static bool Uses(Expression e, ParameterExpression param)
    {
        var f = new ParamFinder(param);
        f.Visit(e);
        return f.Found;
    }

    static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t,
    };

    TResult RunRead<TResult>(Func<TResult> f)
    {
        _lock.EnterReadLock();
        try { return f(); }
        finally { _lock.ExitReadLock(); }
    }

    TResult RunWrite<TResult>(Func<TResult> f)
    {
        _lock.EnterWriteLock();
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
                    _s.Commit();
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
        finally { _lock.ExitWriteLock(); }
    }
}

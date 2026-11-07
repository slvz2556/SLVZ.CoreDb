using SLVZ.CoreDb.Storage;

namespace SLVZ.CoreDb;

/// <summary>Base class of all errors thrown by SLVZ.CoreDb.</summary>
public class CoreDbException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CoreDbException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Insert failed because a row with the same primary key already exists.</summary>
public sealed class DuplicateKeyException : CoreDbException
{
    /// <summary>Creates the exception.</summary>
    public DuplicateKeyException(string message) : base(message) { }
}

/// <summary>Insert/Update failed because another row already has the same value in a [Index(Unique = true)] property.</summary>
public sealed class UniqueConstraintException : CoreDbException
{
    /// <summary>Creates the exception.</summary>
    public UniqueConstraintException(string message) : base(message) { }
}

/// <summary>Optional settings for the database.</summary>
public sealed class CoreDbOptions
{
    /// <summary>Key used to XOR the file content. Null = built-in default key.</summary>
    public byte[]? XorKey { get; set; }

    /// <summary>true = fsync on every commit (survives power loss). false = faster, still safe against app crashes.</summary>
    public bool Durable { get; set; } = true;

    /// <summary>Max cached 4 KB pages per table.</summary>
    public int CachePages { get; set; } = 1024;

    /// <summary>Extension of the table files (default ".cdb").</summary>
    public string FileExtension { get; set; } = ".cdb";
}

/// <summary>
/// Entry point. Give it a directory; every entity type gets its own file inside it.
/// Create ONE instance per directory and keep it for the lifetime of the app.
/// </summary>
public sealed class CoreDatabase : IDisposable
{
    sealed class SimulatedCrash : IOException
    {
        public SimulatedCrash(string m) : base(m) { }
    }

    sealed class MultiTx
    {
        public int Owner;
        public readonly List<ITable> Entered = new();
    }

    // test hooks (see the Tests project): simulate a crash around the commit decision of a multi-table transaction
    internal static volatile bool TestFailBeforeDecision;
    internal static volatile bool TestFailAfterDecision;

    readonly string _dir;
    readonly CoreDbOptions _options;
    readonly object _gate = new();
    readonly object _multiLock = new();            // one multi-table transaction at a time
    readonly Dictionary<Type, ITable> _tables = new();
    MultiTx? _multi;
    bool _disposed;

    /// <summary>Opens (or creates) the database in this folder.</summary>
    /// <param name="directoryPath">Folder where the table files are created. It is created if missing.</param>
    /// <param name="options">Optional settings (XOR key, durability, cache size).</param>
    public CoreDatabase(string directoryPath, CoreDbOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path is required.", nameof(directoryPath));
        _dir = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(_dir);
        _options = options ?? new CoreDbOptions();
        CleanUpDecisionFiles();
    }

    /// <summary>The full path of the database folder.</summary>
    public string DirectoryPath => _dir;

    /// <summary>Returns the table (file) of <typeparamref name="T"/>, creating it on first use. Calling it again returns the same instance.</summary>
    public DataSet<T> Table<T>() where T : class, new()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            DataSet<T> table;
            if (_tables.TryGetValue(typeof(T), out var existing)) table = (DataSet<T>)existing;
            else
            {
                table = new DataSet<T>(Path.Combine(_dir, typeof(T).Name + _options.FileExtension), _options, IsCommitted);
                _tables[typeof(T)] = table;
            }

            // a table first used inside a database transaction joins that transaction
            if (_multi is { } m && m.Owner == Environment.CurrentManagedThreadId && !m.Entered.Contains(table))
            {
                ((ITable)table).BeginMulti();
                m.Entered.Add(table);
            }
            return table;
        }
    }

    /// <summary>
    /// Runs the action as ONE atomic transaction over ALL tables of this database: either every change in every table is saved or none.
    /// If anything inside throws, everything is rolled back. Other threads keep seeing the old data until the transaction commits,
    /// and after a crash the database comes back either entirely before or entirely after it.
    /// <example><c>db.Transaction(() =&gt; { users.Insert(u); posts.Insert(p); });</c></example>
    /// </summary>
    public void Transaction(Action action)
    {
        if (_multi is { } cur && cur.Owner == Environment.CurrentManagedThreadId)
        {
            action();      // already inside a database transaction: join it
            return;
        }

        lock (_multiLock)
        {
            var multi = new MultiTx { Owner = Environment.CurrentManagedThreadId };
            List<ITable> snapshot;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                snapshot = _tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                _multi = multi;
            }

            List<ITable>? prepared = null;
            var applied = new HashSet<ITable>();
            bool decided = false, crashed = false;
            var txId = Guid.NewGuid();
            try
            {
                foreach (var t in snapshot)
                {
                    lock (_gate)
                    {
                        if (multi.Entered.Contains(t)) continue;
                    }
                    t.BeginMulti();
                    lock (_gate) multi.Entered.Add(t);
                }

                action();

                if (multi.Entered.Any(t => t.HasFailed))
                    throw new InvalidOperationException("The transaction was rolled back because an operation inside it failed.");

                // phase 1: every table logs its changes durably (nothing visible, nothing applied)
                prepared = new List<ITable>();
                foreach (var t in multi.Entered)
                    if (t.Prepare(txId)) prepared.Add(t);

                if (prepared.Count > 0)
                {
                    if (TestFailBeforeDecision) throw new SimulatedCrash("Simulated crash before the commit decision.");

                    // the commit point: from now on a crash is repaired by finishing the transaction
                    WriteDecision(txId);
                    decided = true;
                    if (TestFailAfterDecision) throw new SimulatedCrash("Simulated crash after the commit decision.");

                    // phase 2: apply
                    foreach (var t in prepared)
                    {
                        t.Apply();
                        applied.Add(t);
                    }
                    DeleteDecision(txId);
                }
            }
            catch (Exception ex)
            {
                crashed = ex is SimulatedCrash;
                if (decided || crashed)
                {
                    // possibly half-applied (or a simulated crash): keep the logs, refuse writes until reopened (recovery finishes the job)
                    if (prepared != null)
                        foreach (var t in prepared)
                            if (!applied.Contains(t)) t.Fault();
                }
                else
                {
                    foreach (var t in multi.Entered)
                        try { t.Abort(); } catch { /* keep releasing */ }
                }
                throw;
            }
            finally
            {
                foreach (var t in multi.Entered) t.EndMulti();
                lock (_gate) _multi = null;
            }
        }
    }

    /// <summary>Async version of <see cref="Transaction"/>. The action itself is synchronous (a transaction belongs to one thread).</summary>
    public Task TransactionAsync(Action action, CancellationToken ct = default) => Task.Run(() => Transaction(action), ct);

    /// <summary>Closes all table files and releases the folder lock.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var t in _tables.Values) t.Dispose();
            _tables.Clear();
        }
    }

    // ----- commit decisions of multi-table transactions -----

    string DecisionPath(Guid txId) => Path.Combine(_dir, txId.ToString("N") + ".txn");

    bool IsCommitted(Guid txId) => File.Exists(DecisionPath(txId));

    void WriteDecision(Guid txId)
    {
        using (var fs = new FileStream(DecisionPath(txId), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(txId.ToByteArray());
            fs.Flush(_options.Durable);
        }

        if (!_options.Durable) return;
        try
        {
            // make the new file's directory entry durable too (works on Linux/Android; ignored where directories can't be opened)
            using var dir = File.OpenHandle(_dir, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.FlushToDisk(dir);
        }
        catch { /* best effort */ }
    }

    void DeleteDecision(Guid txId)
    {
        try { File.Delete(DecisionPath(txId)); } catch { /* a stale file is cleaned up on the next open */ }
    }

    /// <summary>Removes decision files that no table log refers to any more.</summary>
    void CleanUpDecisionFiles()
    {
        string[] decisions;
        try { decisions = Directory.GetFiles(_dir, "*.txn"); }
        catch { return; }
        if (decisions.Length == 0) return;

        var referenced = new HashSet<Guid>();
        foreach (var wal in Directory.GetFiles(_dir, "*.wal"))
        {
            try
            {
                if (Pager.TryReadWalTxId(wal, out var id)) referenced.Add(id);
            }
            catch (IOException)
            {
                return;      // a log is in use by another process: don't touch anything
            }
        }

        foreach (var file in decisions)
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && !referenced.Contains(id))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
    }
}

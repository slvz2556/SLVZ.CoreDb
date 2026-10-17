namespace SLVZ.CoreDb;

/// <summary>
/// Derive from it, call <see cref="Initialize"/> inside <see cref="Configuration"/> and expose your tables:
/// <code>
/// class MyDb : CoreDbContext
/// {
///     public override void Configuration()
///     {
///         Initialize(path);
///         Notes = DataSet&lt;Note&gt;();
///     }
///     public DataSet&lt;Note&gt;? Notes;
/// }
/// </code>
/// Keep one instance for the lifetime of the app (e.g. AddSingleton&lt;MyDb&gt;()).
/// </summary>
public abstract class CoreDbContext : IDisposable
{
    CoreDatabase? _db;

    /// <summary>Calls <see cref="Configuration"/> and checks that Initialize was called.</summary>
    protected CoreDbContext()
    {
        Configuration();
        if (_db == null)
            throw new InvalidOperationException("Call Initialize(path) inside Configuration().");
    }

    /// <summary>Called once by the constructor: call Initialize(...) and create your DataSets here.</summary>
    public abstract void Configuration();

    /// <summary>Folder where the table files are created (it is created if missing).</summary>
    protected void Initialize(string path, CoreDbOptions? options = null)
    {
        if (_db != null) throw new InvalidOperationException("Initialize was already called.");
        _db = new CoreDatabase(path, options);
    }

    /// <summary>Returns the table (file) of <typeparamref name="T"/>. Call it inside <see cref="Configuration"/>, or from a property: <c>public DataSet&lt;User&gt; Users =&gt; DataSet&lt;User&gt;();</c></summary>
    protected DataSet<T> DataSet<T>() where T : class, new()
    {
        if (_db == null) throw new InvalidOperationException("Call Initialize(path) before DataSet<T>().");
        return _db.Table<T>();
    }

    /// <summary>The full path of the database folder.</summary>
    public string? DirectoryPath => _db?.DirectoryPath;

    /// <summary>Closes all table files and releases the folder lock.</summary>
    public void Dispose()
    {
        _db?.Dispose();
        GC.SuppressFinalize(this);
    }
}

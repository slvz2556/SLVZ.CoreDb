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
    readonly string _dir;
    readonly CoreDbOptions _options;
    readonly object _gate = new();
    readonly Dictionary<Type, IDisposable> _tables = new();
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
    }

    /// <summary>The full path of the database folder.</summary>
    public string DirectoryPath => _dir;

    /// <summary>Returns the table (file) of <typeparamref name="T"/>, creating it on first use. Calling it again returns the same instance.</summary>
    public DataSet<T> Table<T>() where T : class, new()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tables.TryGetValue(typeof(T), out var existing))
                return (DataSet<T>)existing;

            var table = new DataSet<T>(Path.Combine(_dir, typeof(T).Name + _options.FileExtension), _options);
            _tables[typeof(T)] = table;
            return table;
        }
    }

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
}

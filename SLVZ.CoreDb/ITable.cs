namespace SLVZ.CoreDb;

/// <summary>What the database needs from every table to run a transaction that spans several tables.</summary>
internal interface ITable : IDisposable
{
    string Name { get; }

    /// <summary>Takes the table's write lock for the whole database transaction.</summary>
    void BeginMulti();

    /// <summary>true if an operation inside the transaction threw (even if the caller caught it).</summary>
    bool HasFailed { get; }

    /// <summary>Step 1 of the commit: log the changes durably. Returns false if the table has no changes.</summary>
    bool Prepare(Guid txId);

    /// <summary>Step 2 of the commit: apply the logged changes to the file and publish them to readers.</summary>
    void Apply();

    /// <summary>Throws the table's changes away.</summary>
    void Abort();

    /// <summary>The table may be half-committed: refuse further writes until the database is reopened (which repairs it).</summary>
    void Fault();

    /// <summary>Releases the lock taken by BeginMulti.</summary>
    void EndMulti();
}

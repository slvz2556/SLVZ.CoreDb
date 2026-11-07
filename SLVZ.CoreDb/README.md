# SLVZ.CoreDb

Embedded local database for .NET (net8.0+, Windows / Android / anything). No dependencies.
Pages + B+Tree storage, attribute-based indexes, XOR-ed files, crash-safe (write-ahead log), thread-safe.

## Quick start

```csharp
public class Note
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [Index]
    public DateTime Date { get; set; }

    [Index(Unique = true)]
    public string Slug { get; set; } = "";

    public string Title { get; set; } = "";

    [Ignore]
    public bool IsSelected { get; set; }      // not stored
}

// 1) context style
class MyDb : CoreDbContext
{
    public override void Configuration()
    {
        Initialize(Path.Combine(FileSystem.AppDataDirectory, "db"));   // a folder
    }

    public DataSet<Note> Notes => DataSet<Note>();   // never null, no warnings
}
builder.Services.AddSingleton<MyDb>();

// 2) or directly: the only thing you give it is a folder
using var db = new CoreDatabase(Path.Combine(FileSystem.AppDataDirectory, "db"));
var notes = db.Table<Note>();                           // file: Note.cdb
```

## API

```csharp
notes.Insert(new Note { Date = DateTime.Now, Slug = "hello" });
notes.Find(1);                                          // by primary key
notes.FindBy(x => x.Slug, "hello");                     // by index
notes.Range(x => x.Date, from, to, descending: false, skip: 0, take: 50);   // index range, ordered, paged
notes.Where(x => x.Date >= a && x.Date <= b && x.Title.Contains("trip")); // uses the Date index automatically
notes.FullScan(x => SomeCustomCode(x));                 // always scans every row
notes.OrderedBy(x => x.Date, descending: true, skip: 0, take: 20);
notes.Transaction(() => { notes.Insert(a); notes.Update(b); notes.Delete(3); });
notes.UpdateMany(n => n.Date < old, n => n.Title = "-");   // predicate uses the index
notes.DeleteMany(n => n.Date < old);   // also: DeleteMany(ids), DeleteMany(entities), UpsertMany, UpdateMany(entities)
notes.Compact();                                        // reclaim space after many deletes

// more queries
notes.Where(n => n.Slug.StartsWith("trip-"));            // prefix search through the index (ordinal)
notes.Where(n => n.Slug == "a" || n.Slug == "b");        // each || branch uses its own index
notes.CountWhere(n => n.Date >= a && n.Date <= b);       // counts index entries, loads no rows
notes.Any(n => n.Slug == "hello");                       // stops at the first match
notes.FirstOrDefault(n => n.Slug == "hello");
notes.Min(n => n.Date);  notes.Max(n => n.Date);         // straight from the index (TryMin / TryMax don't throw)

// async versions of everything (they run on the thread pool, the UI thread stays free)
var list = await notes.WhereAsync(n => n.Slug == "hello");
await notes.InsertAsync(new Note { Slug = "x" });

// one atomic transaction over SEVERAL tables
db.Transaction(() => { users.Insert(u); posts.Insert(p); });      // or: await db.TransactionAsync(...)
```

Keep one `CoreDatabase` / `CoreDbContext` instance per folder (e.g. a singleton): the folder is locked while it is open.

## How it works

* `Where` picks indexes by itself: conditions joined by `&&` that compare an [Index]/[PrimaryKey] property with `==  >  >=  <  <=` or `StartsWith` are answered through the B+Tree; branches joined by `||` each use their own index; the whole lambda is then applied to the candidates, so every other condition still works. A branch without an indexed condition means a full scan.
* Files: one `<Type>.cdb` per entity (+ a temporary `.wal`), content XOR-ed page by page (4 KB pages).
* Concurrency: readers never wait for a writer's transaction. They read the last committed state (the writer works on private copies of the pages) and only pause for the short moment the writer applies its commit. Writers of one table run one at a time. The folder is locked against other processes.
* Crash safety: every commit is first written to the WAL (and flushed), then applied to the data file. Incomplete commits are discarded and complete ones replayed on open.
* Transactions over several tables (`db.Transaction`): every table logs its changes, then a small commit-decision file is written, then the tables apply. After a crash the database comes back entirely before or entirely after the transaction.
* `CoreDbOptions`: `XorKey`, `Durable` (fsync per commit, default true), `CachePages` (default 1024 pages = 4 MB per table).
* Key/index types: int/long/…, string, bool, double/decimal, DateTime/DateTimeOffset/DateOnly/TimeOnly/TimeSpan, Guid, enums, byte[].
  Any other property type is stored as JSON (but can't be indexed).
* XOR hides content from casual viewing; it is NOT encryption.

## Benchmark

Measured on a Linux sandbox, .NET 8, `Durable = false`, 1,000,000 rows inserted in batches of 10,000 (one `InsertMany` per batch).
Model: `int` auto-increment key, unique `string` index (`Key`), non-unique `int` index (`Num`, 100,000 distinct values, ~10 rows each), one `string` title.
The benchmark project is `SLVZ.CoreDb.Bench`: `dotnet run -c Release -- 1000000`.
The table shows the range of two full runs with the current engine (numbers move by up to ~1.5x between runs, so read them as orders of magnitude).

| Operation | Result |
|---|---|
| Bulk insert (primary tree + 2 indexes per row) | 63–87 s, ~11,500–15,800 rows/s |
| File size | 147 MB |
| `Find` by primary key | 20–32 µs |
| `FindBy` on a unique string index | 41–76 µs |
| `FindBy` on a non-unique index (10 rows) | 0.3–0.35 ms |
| `OrderedBy(..., descending: true, take: 20)` | 0.4–0.5 ms |
| `OrderedBy(..., take: 20)` | 0.5–0.8 ms |
| `OrderedBy(..., skip: 500000, take: 20)` | 49–66 ms (walks 500k index entries, loads only 20 rows) |
| `Range` over 21 values (210 rows) | 3–4 ms |
| `Range` over the whole index, `descending: true, take: 20` | 0.4–0.5 ms |

Smaller test (20,000 rows): `Where(x => x.Slug == k)` ≈ 0.05–0.1 ms through the index vs ≈ 80–130 ms for a full scan; `Where(x => x.Slug == a || x.Slug == b)` ≈ 0.1 ms.

**Readers vs writers.** 4 writer threads updating rows non-stop and 4 reader threads doing `Find`, for 3 seconds (`Durable = false`): ~20,000 writes and ~320,000 reads. Before snapshot reads, the same kind of test left the readers almost completely blocked.

**Memory.** The database is *not* loaded into RAM. After inserting 1M rows (147 MB file) the live managed memory after a GC was ~4 MB.
The process peak for the whole run was 80–141 MB depending on the run: that is .NET garbage from the insert batches (the GC decides when to free it) plus the runtime itself.
Page cache is capped by `CachePages`.

**Durability cost.** With `Durable = true` every commit does an fsync. Measured single-row inserts: 0.6 ms and 10.7 ms on two different machines.
Group writes with `InsertMany` / `Transaction` to pay for one fsync.

**Not measured yet:** Android devices, comparison with SQLite, real power-loss testing, more than 1M rows.

## Limits and known weak spots

* Primary key <= 128 bytes; strings in indexes are compared on their first 128 bytes (`FindBy` and unique checks stay exact; `Range` on longer strings is approximate).
* `StartsWith` uses an ordinal (byte-wise) prefix search to find candidates; the lambda is applied afterwards. Prefer `StartsWith(prefix, StringComparison.Ordinal)` so both agree exactly. Rows whose property is null are skipped by the index (a plain scan would throw on them).
* `GetAll()`, `FullScan`, and `Where` without an indexed condition load rows into memory.
* `skip` in `OrderedBy` / `Range` still walks the skipped index entries (500,000 entries ≈ 50–65 ms). For deep paging use a key-based range: `Range(x => x.Date, lastSeenDate, max, take: 20)`.
* Deletes don't merge pages: the file doesn't shrink until `Compact()`.
* A transaction keeps its changed pages in RAM until commit: insert big imports in batches (~10,000 rows).
* `db.Transaction` (several tables) takes the write lock of every table for its whole duration, so other threads' writes wait until it ends (reads don't). While the tables apply their commits one after another, a concurrent reader can briefly see table A already committed and table B not yet: the transaction is atomic for crashes and rollbacks, not for the instant it becomes visible to other threads. Crash points covered by tests: before and after the commit decision.
* A transaction belongs to one thread: don't `await` inside the action. The `...Async` methods run the blocking work on the thread pool; they are not truly asynchronous I/O, and the `CancellationToken` is only checked before the work starts.
* The whole library uses reflection, expression trees and JSON: not tested yet with Android Release trimming/AOT.
* No SQL, no joins.



👨‍💻 **Author:** [SLVZ](https://slvz.dev)
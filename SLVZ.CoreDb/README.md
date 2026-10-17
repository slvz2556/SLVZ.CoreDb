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
notes.Range(x => x.Date, from, to);                     // index range, ordered
notes.Where(x => x.Date >= a && x.Date <= b && x.Title.Contains("trip")); // uses the Date index automatically
notes.FullScan(x => SomeCustomCode(x));                 // always scans every row
notes.OrderedBy(x => x.Date, descending: true, skip: 0, take: 20);
notes.Transaction(() => { notes.Insert(a); notes.Update(b); notes.Delete(3); });
notes.UpdateMany(n => n.Date < old, n => n.Title = "-");   // predicate uses the index
notes.DeleteMany(n => n.Date < old);   // also: DeleteMany(ids), DeleteMany(entities), UpsertMany, UpdateMany(entities)
notes.Compact();                                        // reclaim space after many deletes
```

Keep one `CoreDatabase` / `CoreDbContext` instance per folder (e.g. a singleton): the folder is locked while it is open.

## How it works

* `Where` picks an index by itself from `&&`-joined `==  >  >=  <  <=` conditions on [Index]/[PrimaryKey] properties; everything else is filtered on the candidates. `||` or no indexed condition = full scan.
* Files: one `<Type>.cdb` per entity (+ a temporary `.wal`), content XOR-ed page by page (4 KB pages).
* Concurrency: parallel readers, exclusive writers, per table. The folder is locked against other processes.
* Crash safety: every write is a transaction logged to the WAL before touching the data file; incomplete commits are discarded and complete ones are replayed on open.
* `CoreDbOptions`: `XorKey`, `Durable` (fsync per commit, default true), `CachePages` (default 1024 pages = 4 MB per table).
* Key/index types: int/long/…, string, bool, double/decimal, DateTime/DateTimeOffset/DateOnly/TimeOnly/TimeSpan, Guid, enums, byte[].
  Any other property type is stored as JSON (but can't be indexed).
* XOR hides content from casual viewing; it is NOT encryption.

## Benchmark

Measured on a Linux sandbox, .NET 8, `Durable = false`, one run, 1,000,000 rows inserted in batches of 10,000 (one `InsertMany` per batch).
Model: `int` auto-increment key, unique `string` index (`Key`), non-unique `int` index (`Num`, 100,000 distinct values, ~10 rows each), one `string` title.

| Operation | Result |
|---|---|
| Bulk insert (3 indexes' worth of work per row) | ~64 s, ~15,000–17,000 rows/s |
| File size | 147 MB |
| `Find` by primary key | ~24 µs |
| `FindBy` on a unique string index | ~38 µs |
| `FindBy` on a non-unique index (10 rows) | ~0.2 ms |
| `Range` over 21 values (210 rows) | ~103 ms once (cold) at 1M rows; ~2 ms warm at 300k rows |
| `OrderedBy(..., take: 20)` | ~194 ms (reads the whole index: O(n), see below) |

Smaller test (20,000 rows): `Where(x => x.Slug == k)` ≈ 0.05 ms through the index vs ≈ 80 ms for a full scan.

**Memory.** The database is *not* loaded into RAM. After inserting 1M rows (147 MB file) the live managed memory after a GC was ~4 MB.
The process peak was 139–160 MB, mostly .NET garbage from the insert batches, the 10,000-row transactions and (for the last 20 MB) `OrderedBy`.
Page cache is capped by `CachePages`.

**Durability cost.** With `Durable = true` every commit does an fsync. Measured single-row inserts: 0.6 ms and 10.7 ms on two different machines.
Group writes with `InsertMany` / `Transaction` to pay for one fsync.

**Not measured yet:** Android devices, comparison with SQLite, power-loss testing, more than 1M rows.

## Limits and known weak spots

* Primary key <= 128 bytes; strings in indexes are compared on their first 128 bytes (`FindBy` and unique checks stay exact; `Range` on longer strings is approximate).
* `GetAll()`, `FullScan`, and `Where` without an indexed condition load rows into memory.
* `OrderedBy` and `Range` currently read more of the index than they need (all keys for `OrderedBy`).
* Deletes don't merge pages: the file doesn't shrink until `Compact()`.
* A transaction keeps its changed pages in RAM until commit: insert big imports in batches (~10,000 rows).
* Under continuous heavy writing from many threads, readers can be starved (writers have priority).
* Transactions are per table, not across tables. No SQL, no joins.



👨‍💻 **Author:** [SLVZ](https://slvz.dev)
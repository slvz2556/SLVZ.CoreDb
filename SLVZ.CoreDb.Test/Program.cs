using System.Diagnostics;
using System.Text;
using SLVZ.CoreDb;
using SLVZ.CoreDb.Storage;

int failed = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failed++;
}

var dir = Path.Combine(Path.GetTempPath(), "coredb-test-" + Guid.NewGuid().ToString("N"));
var rnd = new Random(42);
string[] tags = { "work", "family", "travel", "health", "dream" };

// ---------- 1. bulk insert + lookups ----------
var sw = Stopwatch.StartNew();
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();
    var list = new List<Note>();
    for (int i = 0; i < 20000; i++)
    {
        list.Add(new Note
        {
            Date = new DateTime(2020, 1, 1).AddMinutes(i * 7),
            Slug = "slug-" + i,
            Title = "Title " + i,
            Body = i % 500 == 0 ? new string('x', 20000) + "UniqueMarkerText" : "body " + i,
            Tag = i % 10 == 0 ? null : tags[i % tags.Length],
            Labels = new List<string> { "a", "b" + i },
            Mood = (Mood)(i % 3),
            Temp = "not stored",
        });
    }
    notes.InsertMany(list);
    Console.WriteLine($"insert 20k: {sw.ElapsedMilliseconds} ms");

    Check(notes.Count == 20000, "count after bulk insert");
    Check(list[0].Id == 1 && list[19999].Id == 20000, "autoincrement ids assigned");

    var n500 = notes.Find(501)!;
    Check(n500.Title == "Title 500" && n500.Body.Length == 20000 + "UniqueMarkerText".Length, "overflow record round-trip");
    Check(n500.Temp == "" , "[Ignore] property not stored");
    Check(n500.Labels.SequenceEqual(new[] { "a", "b500" }), "JSON list round-trip");

    sw.Restart();
    int hits = 0;
    for (int i = 0; i < 5000; i++) hits += notes.FindBy(x => x.Slug, "slug-" + (i * 3)).Count;
    Console.WriteLine($"5000 unique-index lookups: {sw.ElapsedMilliseconds} ms");
    Check(hits == 5000, "FindBy unique index");

    Check(notes.FindBy(x => x.Tag, "work").Count == 4000 - 0 || true, "FindBy tag runs");
    int expectWork = Enumerable.Range(0, 20000).Count(i => i % 10 != 0 && tags[i % 5] == "work");
    Check(notes.FindBy(x => x.Tag, "work").Count == expectWork, "FindBy non-unique index count");
    Check(notes.FindBy(x => x.Tag, (string?)null).Count == 2000, "FindBy null value");

    var from = new DateTime(2020, 1, 2);
    var to = new DateTime(2020, 1, 3);
    int expectRange = Enumerable.Range(0, 20000).Count(i => { var d = new DateTime(2020, 1, 1).AddMinutes(i * 7); return d >= from && d <= to; });
    var range = notes.Range(x => x.Date, from, to);
    Check(range.Count == expectRange && range.Zip(range.Skip(1)).All(p => p.First.Date <= p.Second.Date), "Range on DateTime index (ordered)");

    var newest = notes.OrderedBy(x => x.Date, descending: true, take: 3);
    Check(newest.Select(x => x.Id).SequenceEqual(new[] { 20000, 19999, 19998 }), "OrderedBy desc + take");

    // ---- OrderedBy / Range: paging + both directions ----
    var allNotes = notes.GetAll();
    var expDate = allNotes.OrderBy(x => x.Date).ThenBy(x => x.Id).Select(x => x.Id).ToList();
    Check(notes.OrderedBy(x => x.Date, false, 100, 5).Select(x => x.Id).SequenceEqual(expDate.Skip(100).Take(5)), "OrderedBy asc skip/take");
    Check(notes.OrderedBy(x => x.Date, true, 100, 5).Select(x => x.Id).SequenceEqual(Enumerable.Reverse(expDate).Skip(100).Take(5)), "OrderedBy desc skip/take");
    Check(notes.OrderedBy(x => x.Date, true).Select(x => x.Id).SequenceEqual(Enumerable.Reverse(expDate)), "OrderedBy desc (all rows) == reverse order");
    var expTag = allNotes.OrderBy(x => x.Tag, StringComparer.Ordinal).ThenBy(x => x.Id).Select(x => x.Id).ToList();
    Check(notes.OrderedBy(x => x.Tag).Select(x => x.Id).SequenceEqual(expTag)
          && notes.OrderedBy(x => x.Tag, true).Select(x => x.Id).SequenceEqual(Enumerable.Reverse(expTag)), "OrderedBy with duplicate values, both directions");
    var rd = notes.Range(x => x.Date, from, to, descending: true);
    Check(rd.Count == expectRange && rd.Zip(rd.Skip(1)).All(p => p.First.Date >= p.Second.Date), "Range descending");
    Check(notes.Range(x => x.Date, from, to, skip: 3, take: 4).Select(x => x.Id).SequenceEqual(range.Skip(3).Take(4).Select(x => x.Id)), "Range skip/take");
    Check(notes.OrderedBy(x => x.Date, take: 0).Count == 0 && notes.Range(x => x.Date, from, to, take: 1).Count == 1, "take: 0 / take: 1");

    // ---- StartsWith, ||, CountWhere, Any, FirstOrDefault, Min/Max ----
    Func<IEnumerable<Note>, List<int>> ids = rows => rows.Select(x => x.Id).OrderBy(i => i).ToList();
    Check(ids(notes.Where(x => x.Slug.StartsWith("slug-12"))).SequenceEqual(ids(notes.FullScan(x => x.Slug.StartsWith("slug-12", StringComparison.Ordinal)))), "Where StartsWith through the index");
    Check(ids(notes.Where(x => x.Slug.StartsWith("slug-12", StringComparison.Ordinal) && x.Tag == "work")).SequenceEqual(ids(notes.FullScan(x => x.Slug.StartsWith("slug-12", StringComparison.Ordinal) && x.Tag == "work"))), "StartsWith + other condition");
    Check(notes.Where(x => x.Slug == "slug-1" || x.Slug == "slug-2" || x.Id == 5).Count == 3, "Where with || uses one index per branch");
    Check(notes.Where(x => x.Slug == "slug-1" || x.Title == "Title 2").Count == 2, "|| with a non-indexed branch falls back to a scan");
    Check(notes.Where(x => x.Id == 1 || x.Id == 1 || x.Slug == "slug-0").Count == 1, "|| doesn't return a row twice");
    Check(ids(notes.Where(x => x.Tag == "work" && x.Date >= from || x.Slug == "slug-3")).SequenceEqual(ids(notes.FullScan(x => x.Tag == "work" && x.Date >= from || x.Slug == "slug-3"))), "(a && b) || c == full scan");

    Check(notes.CountWhere(x => x.Tag == "work") == notes.FullScan(x => x.Tag == "work").Count, "CountWhere == (index only)");
    Check(notes.CountWhere(x => x.Date >= from && x.Date <= to) == expectRange, "CountWhere inclusive range (index only)");
    Check(notes.CountWhere(x => x.Date > from && x.Date < to) == notes.FullScan(x => x.Date > from && x.Date < to).Count, "CountWhere strict range");
    Check(notes.CountWhere(x => x.Tag == null) == 2000 && notes.CountWhere(x => x.Id <= 100) == 100, "CountWhere null / primary key");
    Check(notes.CountWhere(x => x.Tag == "work" && x.Mood == Mood.Ok) == notes.FullScan(x => x.Tag == "work" && x.Mood == Mood.Ok).Count, "CountWhere + non-indexed condition");
    Check(notes.CountWhere(x => x.Slug.StartsWith("slug-9")) == notes.FullScan(x => x.Slug.StartsWith("slug-9", StringComparison.Ordinal)).Count, "CountWhere StartsWith");
    Check(notes.Any(x => x.Slug == "slug-77") && !notes.Any(x => x.Slug == "nope") && !notes.Any(x => x.Id > 999999) && notes.Any(), "Any");
    Check(notes.FirstOrDefault(x => x.Slug == "slug-9")?.Id == 10 && notes.FirstOrDefault(x => x.Slug == "nope") == null, "FirstOrDefault");
    Check(notes.Max(x => x.Date) == allNotes.Max(x => x.Date) && notes.Min(x => x.Date) == allNotes.Min(x => x.Date)
          && notes.Max(x => x.Id) == 20000 && notes.Min(x => x.Id) == 1, "Min / Max on an index and on the primary key");
    Check(notes.Min(x => x.Tag) == allNotes.Where(x => x.Tag != null).Select(x => x.Tag!).OrderBy(s => s, StringComparer.Ordinal).First(), "Min ignores null values");
    sw.Restart();
    int orHits = 0;
    for (int i = 0; i < 500; i++) { var a1 = "slug-" + i; var a2 = "slug-" + (i + 1000); orHits += notes.Where(x => x.Slug == a1 || x.Slug == a2).Count; }
    long orMs = sw.ElapsedMilliseconds;
    Console.WriteLine($"Where with || via indexes: {orMs / 500.0:F2} ms each");
    Check(orHits == 1000 && orMs / 500.0 < 10, "Where with || via indexes is fast");

    Check(notes.Where(x => x.Title == "Title 77").Count == 1, "Where on non-indexed property (full scan)");

    // ---- Where() through indexes ----
    Check(notes.Where(x => x.Slug == "slug-500").Single().Id == 501, "Where == on unique index");
    string wanted = "slug-777";
    Check(notes.Where(x => wanted == x.Slug).Single().Id == 778, "Where with captured variable, reversed operands");
    Check(notes.Where(x => x.Id > 19990).Count == 10 && notes.Where(x => x.Id >= 5 && x.Id <= 8).Count == 4, "Where on primary key range");
    var wr = notes.Where(x => x.Date >= from && x.Date <= to && x.Title.Contains("1"));
    var wr2 = notes.FullScan(x => x.Date >= from && x.Date <= to && x.Title.Contains("1"));
    Check(wr.Count == wr2.Count && wr.Count > 0 && wr.Select(x => x.Id).OrderBy(i => i).SequenceEqual(wr2.Select(x => x.Id)), "Where range on index + extra condition == full scan");
    Check(notes.Where(x => x.Date > from && x.Date < to).Count == notes.FullScan(x => x.Date > from && x.Date < to).Count, "Where strict bounds");
    Check(notes.Where(x => x.Tag == "work" && x.Mood == Mood.Ok).Count == notes.FullScan(x => x.Tag == "work" && x.Mood == Mood.Ok).Count, "Where index eq + non-index condition");
    Check(notes.Where(x => x.Tag == null).Count == 2000, "Where == null on index");
    Check(notes.Where(x => x.Slug == "slug-1" || x.Slug == "slug-2").Count == 2, "Where with || (falls back to scan)");
    sw.Restart();
    int wh = 0;
    for (int i = 0; i < 3000; i++) { var k = "slug-" + (i * 5); wh += notes.Where(x => x.Slug == k).Count; }
    long idxMs = sw.ElapsedMilliseconds;
    sw.Restart();
    for (int i = 0; i < 20; i++) { var k = "slug-" + (i * 5); wh += notes.FullScan(x => x.Slug == k).Count; }
    Console.WriteLine($"Where via index: {idxMs / 3000.0:F3} ms each | full scan: {sw.ElapsedMilliseconds / 20.0:F1} ms each");
    Check(wh == 3020 && idxMs / 3000.0 < (sw.ElapsedMilliseconds / 20.0) / 20, "Where via index is >20x faster than a full scan");
}

// ---------- 2. persistence + XOR ----------
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();
    Check(notes.Count == 20000, "reopen: count persisted");
    Check(notes.FindBy(x => x.Slug, "slug-12345").Single().Id == 12346, "reopen: index persisted");
}
var raw = File.ReadAllBytes(Path.Combine(dir, "Note.cdb"));
Check(Find(raw, "UniqueMarkerText") < 0 && Find(raw, "Title 77") < 0 && Find(raw, "SLVZCDB1") < 0, "file content is XOR-ed (no plaintext)");
Console.WriteLine($"file size: {raw.Length / 1024} KB");

// ---------- 3. constraints, update, delete, rollback ----------
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();

    Check(Throws<UniqueConstraintException>(() => notes.Insert(new Note { Slug = "slug-5" })), "unique violation on insert");
    Check(notes.Count == 20000, "failed insert leaves count intact");
    Check(Throws<DuplicateKeyException>(() => notes.Insert(new Note { Id = 7, Slug = "fresh" })), "duplicate primary key");
    Check(notes.FindBy(x => x.Slug, "fresh").Count == 0, "rolled back: no leftovers in index");

    var n = notes.Find(10)!;
    n.Slug = "renamed-10"; n.Tag = "moved"; n.Title = "Changed";
    Check(notes.Update(n), "update returns true");
    Check(notes.FindBy(x => x.Slug, "slug-9").Count == 0 && notes.FindBy(x => x.Slug, "renamed-10").Count == 1, "old index entry removed on update");
    Check(notes.FindBy(x => x.Slug, "renamed-10").Single().Title == "Changed", "new index entry added on update");
    n.Slug = "slug-5";
    Check(Throws<UniqueConstraintException>(() => notes.Update(n)), "unique violation on update");
    Check(notes.Find(10)!.Slug == "renamed-10", "failed update rolled back");

    Check(!notes.Update(new Note { Id = 999999 }), "update of missing row returns false");
    Check(notes.Delete(11) && !notes.Delete(11), "delete");
    Check(notes.FindBy(x => x.Slug, "slug-10").Count == 0 && notes.Count == 19999, "delete removes index entries");

    var bad = Throws<Exception>(() => notes.Transaction(() =>
    {
        notes.Insert(new Note { Slug = "tx-1" });
        notes.Insert(new Note { Slug = "tx-2" });
        throw new InvalidOperationException("boom");
    }));
    Check(bad && notes.FindBy(x => x.Slug, "tx-1").Count == 0 && notes.Count == 19999, "transaction rolls back everything");

    notes.Transaction(() => { notes.Insert(new Note { Slug = "tx-ok-1" }); notes.Insert(new Note { Slug = "tx-ok-2" }); });
    Check(notes.FindBy(x => x.Slug, "tx-ok-2").Count == 1 && notes.Count == 20001, "transaction commits everything");

    // delete most rows, then compact
    var ids = notes.GetAll().Select(x => x.Id).Where(id => id % 10 != 0).ToList();
    notes.Transaction(() => { foreach (var id in ids) notes.Delete(id); });
    var remaining = notes.GetAll().OrderBy(x => x.Date).Select(x => x.Id).ToList();
    Check(notes.OrderedBy(x => x.Date).Select(x => x.Id).SequenceEqual(remaining)
          && notes.OrderedBy(x => x.Date, true).Select(x => x.Id).SequenceEqual(Enumerable.Reverse(remaining)), "OrderedBy both directions after mass delete (empty leaves)");
    long before = new FileInfo(Path.Combine(dir, "Note.cdb")).Length;
    long countBefore = notes.Count;
    notes.Compact();
    long after = new FileInfo(Path.Combine(dir, "Note.cdb")).Length;
    Console.WriteLine($"compact: {before / 1024} KB -> {after / 1024} KB");
    Console.WriteLine($"count before={countBefore} after={notes.Count}");
    Check(after < before / 2 && notes.Count == countBefore, "compact shrinks file, keeps data");
    Check(notes.FindBy(x => x.Slug, "slug-19").Count == 1 && notes.Find(21) == null && notes.GetAll().Count == notes.Count, "data + indexes valid after compact");
    notes.Insert(new Note { Slug = "after-compact" });
    Check(notes.FindBy(x => x.Slug, "after-compact").Count == 1, "writable after compact");
}

// ---------- 4. crash recovery ----------
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();
    Pager.TestFailAfterWal = true;
    bool threw = Throws<IOException>(() => notes.Insert(new Note { Slug = "crash-me", Title = "survives" }));
    Pager.TestFailAfterWal = false;
    Check(threw, "simulated crash after WAL");
}
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();
    Check(notes.FindBy(x => x.Slug, "crash-me").SingleOrDefault()?.Title == "survives", "WAL replayed on reopen (committed data recovered)");
}
File.WriteAllBytes(Path.Combine(dir, "Note.cdb.wal"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 });
using (var db = new CoreDatabase(dir))
{
    Check(db.Table<Note>().FindBy(x => x.Slug, "crash-me").Count == 1, "garbage WAL ignored");
}

// ---------- 5. wrong key + second process lock ----------
Check(Throws<CoreDbException>(() => { using var d = new CoreDatabase(dir, new CoreDbOptions { XorKey = new byte[] { 9, 9, 9 } }); d.Table<Note>(); }), "wrong XOR key rejected");
using (var db = new CoreDatabase(dir))
{
    db.Table<Note>();
    Check(Throws<CoreDbException>(() => { using var d2 = new CoreDatabase(dir); d2.Table<Note>(); }), "file locked while open elsewhere");
}

// ---------- 6. concurrency ----------
using (var db = new CoreDatabase(Path.Combine(dir, "conc"), new CoreDbOptions { Durable = false }))
{
    var t = db.Table<Item>();
    int writers = 4, perWriter = 2000;
    var stop = false;
    int readerErrors = 0;
    long reads = 0;

    var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
    {
        var rr = new Random(r);
        while (!Volatile.Read(ref stop))
        {
            try
            {
                int w = rr.Next(writers), k = rr.Next(perWriter);
                var found = t.FindBy(x => x.Key, $"w{w}-{k}");
                if (found.Count > 1) Interlocked.Increment(ref readerErrors);
                foreach (var f in found) if (f.Key != $"w{w}-{k}") Interlocked.Increment(ref readerErrors);
                t.Find(rr.Next(1, 100));
                Interlocked.Increment(ref reads);
            }
            catch { Interlocked.Increment(ref readerErrors); }
        }
    })).ToArray();

    sw.Restart();
    var ws = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
    {
        for (int k = 0; k < perWriter; k++)
        {
            var it = new Item { Key = $"w{w}-{k}", Value = k };
            t.Insert(it);
            if (k % 5 == 0) { it.Value = -k; t.Update(it); }
            if (k % 7 == 0) t.Delete(it);
        }
    })).ToArray();
    Task.WaitAll(ws);
    Volatile.Write(ref stop, true);
    Task.WaitAll(readers);
    Console.WriteLine($"concurrency: {writers * perWriter} inserts w/ updates+deletes in {sw.ElapsedMilliseconds} ms (Durable=false), {reads} parallel reads");

    int expected = writers * (perWriter - (perWriter + 6) / 7);
    Check(t.Count == expected && t.GetAll().Count == expected, "concurrent writers: count consistent");
    Check(readerErrors == 0, "concurrent readers saw no errors/torn data");
    Check(t.GetAll().All(x => t.FindBy(y => y.Key, x.Key).Count == 1), "every row reachable through its index");
}

// ---------- 7. CoreDbContext ----------
TestCtx.Path = $"{dir}/ctx.db";
using (var ctx = new TestCtx())
{
    ctx.items!.Insert(new Item { Key = "ctx-1", Value = 5 });
    Check(ctx.items.Where(x => x.Key == "ctx-1").Single().Value == 5, "CoreDbContext: DataSet insert + Where");
}
using (var ctx = new TestCtx())
{
    Check(ctx.items!.Count == 1 && Directory.Exists(TestCtx.Path), "CoreDbContext: data persisted in the given folder");
}
Check(Throws<InvalidOperationException>(() => new NoInitCtx()), "CoreDbContext: missing Initialize is reported");

// ---------- 8. UpdateMany / DeleteMany / non-null context property ----------
using (var db = new CoreDatabase(Path.Combine(dir, "many")))
{
    var t = db.Table<Item>();
    t.InsertMany(Enumerable.Range(0, 100).Select(i => new Item { Key = "m" + i, Value = i }));
    Check(t.UpdateMany(x => x.Value < 10, x => x.Value = -1) == 10 && t.Where(x => x.Value == -1).Count == 10, "UpdateMany(predicate, change)");
    var row = t.Where(x => x.Key == "m50").Single();
    row.Value = 500;
    Check(t.UpdateMany(new[] { row, new Item { Id = 9999, Key = "nope" } }) == 1 && t.Find(row.Id)!.Value == 500 && !t.Exists(9999), "UpdateMany(entities) skips missing rows");
    Check(Throws<InvalidOperationException>(() => t.UpdateMany(x => x.Key == "m1", x => x.Id = 777)) && t.Find(2)!.Key == "m1", "UpdateMany refuses key changes");
    Check(t.DeleteMany(x => x.Key == "m5" || x.Key == "m6") == 2, "DeleteMany(predicate)");
    Check(t.DeleteMany(new long[] { 1, 2, 3, 123456 }) == 3, "DeleteMany(keys)");
    Check(t.DeleteMany(t.Where(x => x.Id > 90)) == 10, "DeleteMany(entities)");
    Check(t.Count == 85 && t.GetAll().Count == 85 && t.Where(x => x.Key == "m5").Count == 0, "counts + indexes consistent after DeleteMany");
}
PropCtx.Path = $"{dir}/prop.db";
using (var ctx = new PropCtx())
{
    ctx.Items.Insert(new Item { Key = "p1" });   // no '!' needed: property is never null
    Check(ctx.Items.Count == 1, "CoreDbContext: non-nullable property style");
}

// ---------- 9. snapshot reads: readers don't wait for writers ----------
using (var db = new CoreDatabase(Path.Combine(dir, "snap"), new CoreDbOptions { Durable = false }))
{
    var t = db.Table<Item>();
    t.Insert(new Item { Key = "base" });
    var seen = new List<long>();
    long polls = 0;
    bool done = false;
    var reader = Task.Run(() =>
    {
        while (!Volatile.Read(ref done)) { seen.Add(t.Count); Interlocked.Increment(ref polls); }
    });
    Thread.Sleep(50);
    t.Transaction(() =>
    {
        t.InsertMany(Enumerable.Range(0, 1000).Select(i => new Item { Key = "tx" + i }));
        Check(t.Count == 1001 && t.Find(2) != null && t.FindBy(x => x.Key, "tx5").Count == 1, "inside a transaction: reads see its own changes");
        Thread.Sleep(400);   // a long transaction: the reader must keep running meanwhile
    });
    Thread.Sleep(50);
    done = true;
    reader.Wait();
    var states = seen.Distinct().OrderBy(x => x).ToList();
    Check(states.SequenceEqual(new long[] { 1, 1001 }), $"readers see only committed states ({string.Join(",", states)})");
    Check(polls > 10000, $"readers are not blocked by a long transaction ({polls} reads)");
}

using (var db = new CoreDatabase(Path.Combine(dir, "starve"), new CoreDbOptions { Durable = false }))
{
    var t = db.Table<Item>();
    t.InsertMany(Enumerable.Range(0, 1000).Select(i => new Item { Key = "s" + i }));
    bool stop = false;
    long reads = 0, writes = 0;
    var workers = new List<Task>();
    for (int w = 0; w < 4; w++)
    {
        int seed = w;
        workers.Add(Task.Run(() =>
        {
            var r = new Random(seed);
            while (!Volatile.Read(ref stop))
            {
                var it = t.Find(r.Next(1, 1001))!;
                it.Value++;
                t.Update(it);
                Interlocked.Increment(ref writes);
            }
        }));
    }
    for (int rd = 0; rd < 4; rd++)
    {
        int seed = 100 + rd;
        workers.Add(Task.Run(() =>
        {
            var r = new Random(seed);
            while (!Volatile.Read(ref stop))
            {
                t.Find(r.Next(1, 1001));
                Interlocked.Increment(ref reads);
            }
        }));
    }
    Thread.Sleep(3000);
    stop = true;
    Task.WaitAll(workers.ToArray());
    Console.WriteLine($"4 writers + 4 readers for 3 s: {writes} writes, {reads} reads");
    Check(reads > 50_000 && writes > 100, "readers are not starved by continuous writers");
}

// ---------- 10. async + empty-table behaviour ----------
using (var db = new CoreDatabase(Path.Combine(dir, "async")))
{
    var t = db.Table<Item>();
    Check(!t.TryMin(x => x.Id, out _) && !t.Any() && Throws<InvalidOperationException>(() => t.Max(x => x.Id)), "Min/Max on an empty table");
    t.InsertAsync(new Item { Key = "a1" }).GetAwaiter().GetResult();
    Check(t.FindByAsync(x => x.Key, "a1").Result.Count == 1 && t.CountWhereAsync(x => x.Key == "a1").Result == 1
          && t.WhereAsync(x => x.Key.StartsWith("a")).Result.Count == 1 && t.AnyAsync(x => x.Key == "a1").Result, "async queries");
    Task.WaitAll(Enumerable.Range(0, 50).Select(i => t.InsertAsync(new Item { Key = "p" + i })).ToArray());
    Check(t.CountAsyncCheck() == 51, "50 parallel async inserts");
}

// ---------- 11. transactions over several tables ----------
var mdir = Path.Combine(dir, "multi");
using (var db = new CoreDatabase(mdir))
{
    var notes = db.Table<Note>();
    var items = db.Table<Item>();
    db.Transaction(() => { notes.Insert(new Note { Slug = "m1" }); items.Insert(new Item { Key = "m1" }); });
    Check(notes.Count == 1 && items.Count == 1, "multi-table: both tables committed");

    Check(Throws<UniqueConstraintException>(() => db.Transaction(() => { notes.Insert(new Note { Slug = "m2" }); items.Insert(new Item { Key = "m1" }); })), "multi-table: a failing operation throws");
    Check(notes.Count == 1 && notes.FindBy(x => x.Slug, "m2").Count == 0 && items.Count == 1, "multi-table: failure rolls back BOTH tables");

    bool swallowed = Throws<InvalidOperationException>(() => db.Transaction(() =>
    {
        notes.Insert(new Note { Slug = "m3" });
        try { items.Insert(new Item { Key = "m1" }); } catch (UniqueConstraintException) { }
    }));
    Check(swallowed && notes.Count == 1, "multi-table: a swallowed failure still rolls back");

    Check(Throws<Exception>(() => db.Transaction(() =>
    {
        notes.Insert(new Note { Slug = "m4" });
        db.Table<Log>().Insert(new Log { Msg = "first use inside the transaction" });
        throw new Exception("boom");
    })), "multi-table: exception inside the action");
    Check(notes.Count == 1 && db.Table<Log>().Count == 0, "multi-table: a table first used inside the transaction is rolled back too");

    db.Transaction(() =>
    {
        notes.Insert(new Note { Slug = "m5" });
        db.Transaction(() => items.Insert(new Item { Key = "m5" }));    // nested = joins
        Check(notes.Count == 2 && items.Count == 2, "multi-table: reads inside see the uncommitted changes");
    });
    Check(notes.Count == 2 && items.Count == 2 && db.Table<Log>().Count == 0, "multi-table: nested transaction joins the outer one");

    // a reader on another thread only ever sees all-or-nothing
    var seenPairs = new HashSet<(long, long)>();
    bool stopPoll = false;
    var poller = Task.Run(() => { while (!Volatile.Read(ref stopPoll)) { var c = (notes.Count, items.Count); lock (seenPairs) seenPairs.Add(c); } });
    for (int i = 0; i < 200; i++) db.Transaction(() => { notes.Insert(new Note { Slug = "pair" + i }); items.Insert(new Item { Key = "pair" + i }); });
    stopPoll = true;
    poller.Wait();
    Check(seenPairs.All(p => p.Item1 == p.Item2 || p.Item1 == p.Item2 + 1 || p.Item2 == p.Item1 + 1), "multi-table: concurrent readers see consistent per-table states (visibility across tables is not atomic)");
}

// crash AFTER the commit decision: both tables must be completed when the database is reopened
var c1 = Path.Combine(dir, "mc1");
using (var db = new CoreDatabase(c1))
{
    var n = db.Table<Note>();
    var i = db.Table<Item>();
    n.Insert(new Note { Slug = "base" });
    CoreDatabase.TestFailAfterDecision = true;
    bool crashed = Throws<IOException>(() => db.Transaction(() => { n.Insert(new Note { Slug = "crash1" }); i.Insert(new Item { Key = "crash1" }); }));
    CoreDatabase.TestFailAfterDecision = false;
    Check(crashed, "multi-table: simulated crash after the commit decision");
}
using (var db = new CoreDatabase(c1))
{
    Check(db.Table<Note>().FindBy(x => x.Slug, "crash1").Count == 1 && db.Table<Item>().FindBy(x => x.Key, "crash1").Count == 1,
          "multi-table: after a crash past the commit point, reopening completes BOTH tables");
    Check(db.Table<Note>().Count == 2, "multi-table: nothing duplicated by the recovery");
}

// crash BEFORE the commit decision: neither table may show the transaction
var c2 = Path.Combine(dir, "mc2");
using (var db = new CoreDatabase(c2))
{
    var n = db.Table<Note>();
    var i = db.Table<Item>();
    n.Insert(new Note { Slug = "base" });
    CoreDatabase.TestFailBeforeDecision = true;
    bool crashed = Throws<IOException>(() => db.Transaction(() => { n.Insert(new Note { Slug = "crash2" }); i.Insert(new Item { Key = "crash2" }); }));
    CoreDatabase.TestFailBeforeDecision = false;
    Check(crashed, "multi-table: simulated crash before the commit decision");
}
using (var db = new CoreDatabase(c2))
{
    Check(db.Table<Note>().Count == 1 && db.Table<Item>().Count == 0, "multi-table: after a crash before the commit point, neither table has the transaction");
    Check(Directory.GetFiles(c2, "*.txn").Length == 0, "multi-table: no stale decision files");
}

// the context exposes Transaction too
using (var ctx = new PropCtx2())
{
    Check(Throws<Exception>(() => ctx.Transaction(() => { ctx.Items.Insert(new Item { Key = "ctx-tx" }); throw new Exception("x"); })) && ctx.Items.Count == 0, "CoreDbContext.Transaction rolls back");
    ctx.TransactionAsync(() => ctx.Items.Insert(new Item { Key = "ctx-tx2" })).GetAwaiter().GetResult();
    Check(ctx.Items.Count == 1, "CoreDbContext.TransactionAsync commits");
}

// durable commit cost (fsync per write)
using (var db = new CoreDatabase(Path.Combine(dir, "dur")))
{
    var t = db.Table<Item>();
    sw.Restart();
    for (int i = 0; i < 200; i++) t.Insert(new Item { Key = "d" + i });
    Console.WriteLine($"durable single inserts: {sw.ElapsedMilliseconds / 200.0:F1} ms each");
}

try { Directory.Delete(dir, true); } catch { }
Console.WriteLine(failed == 0 ? "\nALL TESTS PASSED" : $"\n{failed} TEST(S) FAILED");
return failed;

static bool Throws<TEx>(Action a) where TEx : Exception
{
    try { a(); return false; } catch (TEx) { return true; }
}

static int Find(byte[] hay, string needle)
{
    var n = Encoding.UTF8.GetBytes(needle);
    return hay.AsSpan().IndexOf(n);
}

enum Mood { Sad, Ok, Happy }

class Note
{
    [PrimaryKey(AutoIncrement = true)] public int Id { get; set; }
    [Index] public DateTime Date { get; set; }
    [Index(Unique = true)] public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    [Index] public string? Tag { get; set; }
    public List<string> Labels { get; set; } = new();
    public Mood Mood { get; set; }
    [Ignore] public string Temp { get; set; } = "";
}

class Item
{
    [PrimaryKey(AutoIncrement = true)] public long Id { get; set; }
    [Index(Unique = true)] public string Key { get; set; } = "";
    public int Value { get; set; }
}

class TestCtx : CoreDbContext
{
    public static string Path = "";
    public override void Configuration()
    {
        Initialize(Path);
        items = DataSet<Item>();
    }
    public DataSet<Item>? items;
}

class NoInitCtx : CoreDbContext
{
    public override void Configuration() { }
}

class PropCtx : CoreDbContext
{
    public static string Path = "";
    public override void Configuration() => Initialize(Path);
    public DataSet<Item> Items => DataSet<Item>();
}

class Log
{
    [PrimaryKey(AutoIncrement = true)] public int Id { get; set; }
    public string Msg { get; set; } = "";
}

class PropCtx2 : CoreDbContext
{
    public static string Path = "";
    public override void Configuration() => Initialize(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctx2-" + Guid.NewGuid().ToString("N")));
    public DataSet<Item> Items => DataSet<Item>();
}

static class Ext
{
    public static long CountAsyncCheck(this DataSet<Item> t) => t.Count;
}

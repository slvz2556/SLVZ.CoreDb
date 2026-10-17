using System.Diagnostics;
using System.Text;
using SLVZ.CoreDb;
using SLVZ.CoreDb.Storage;


using var db = new Ashkan();

var u = new User
{
    Email = "ashkan@outlook.com",
    Fullname = "Ashkan",
    ID = Guid.NewGuid(),
    Username = "SLVZ"
};

//db.Users.Insert(u);

var a = db.Users.Where(x => x.Username.StartsWith("SL"));


return 0;


class Ashkan : CoreDbContext
{
    public override void Configuration()
    {
        Initialize(Path.Combine(Environment.CurrentDirectory));
    }

    public DataSet<User> Users => DataSet<User>();
}


class User
{
    [PrimaryKey]
    public Guid ID { get; set; }

    [Index(Unique = true)]
    public string? Username { get; set; }


    [Index(Unique = true)]
    public string? Email { get; set; }

    public string? Fullname { get; set; }
}




/*

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
    Check(n500.Temp == "", "[Ignore] property not stored");
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
var raw = File.ReadAllBytes(Path.Combine(dir, "Note.slvz"));
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
    long before = new FileInfo(Path.Combine(dir, "Note.slvz")).Length;
    long countBefore = notes.Count;
    notes.Compact();
    long after = new FileInfo(Path.Combine(dir, "Note.slvz")).Length;
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
    //var notes = db.Table<Note>();
    //Pager.TestFailAfterWal = true;
    bool threw = Throws<IOException>(() => notes.Insert(new Note { Slug = "crash-me", Title = "survives" }));
    //Pager.TestFailAfterWal = false;
    Check(threw, "simulated crash after WAL");
}
using (var db = new CoreDatabase(dir))
{
    var notes = db.Table<Note>();
    Check(notes.FindBy(x => x.Slug, "crash-me").SingleOrDefault()?.Title == "survives", "WAL replayed on reopen (committed data recovered)");
}
File.WriteAllBytes(Path.Combine(dir, "Note.slvz.wal"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 });
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
*/
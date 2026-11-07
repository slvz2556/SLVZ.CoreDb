using System.Diagnostics;
using SLVZ.CoreDb;

int N = int.Parse(args[0]);
var dir = Path.Combine(Path.GetTempPath(), "bench-" + Guid.NewGuid().ToString("N"));
using var db = new CoreDatabase(dir, new CoreDbOptions { Durable = false });
var t = db.Table<Row>();
var sw = Stopwatch.StartNew();
for (int start = 0; start < N; start += 10000)
    t.InsertMany(Enumerable.Range(start, 10000).Select(i => new Row { Key = "key-" + i, Num = i % 100000, Title = "title " + i }));
Console.WriteLine($"insert {N}: {sw.Elapsed.TotalSeconds:F1}s = {N / sw.Elapsed.TotalSeconds:F0} rows/s");
Console.WriteLine($"file: {new FileInfo(Path.Combine(dir, "Row.cdb")).Length / 1048576} MB");
void Mem(string l) { var live = GC.GetTotalMemory(true) / 1048576; Console.WriteLine($"  [{l}] peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576} MB | live managed: {live} MB"); }
Mem("after insert");

var rnd = new Random(1);
sw.Restart();
for (int i = 0; i < 20000; i++) t.Find(rnd.Next(1, N));
Console.WriteLine($"Find by PK: {sw.Elapsed.TotalMilliseconds / 20000 * 1000:F0} us each");
sw.Restart();
for (int i = 0; i < 20000; i++) t.FindBy(x => x.Key, "key-" + rnd.Next(N));
Console.WriteLine($"FindBy unique string index: {sw.Elapsed.TotalMilliseconds / 20000 * 1000:F0} us each");
sw.Restart();
int hits = 0;
for (int i = 0; i < 200; i++) { int k = rnd.Next(100000); hits += t.FindBy(x => x.Num, k).Count; }
Console.WriteLine($"FindBy non-unique index ({hits / 200} rows each): {sw.Elapsed.TotalMilliseconds / 200:F2} ms each");
Mem("after lookups");

t.OrderedBy(x => x.Num, true, 0, 1); // warm-up JIT
sw.Restart(); var a = t.OrderedBy(x => x.Num, true, 0, 20);
Console.WriteLine($"OrderedBy desc take 20: {sw.Elapsed.TotalMilliseconds:F2} ms ({a.Count} rows)");
sw.Restart(); a = t.OrderedBy(x => x.Num, false, 0, 20);
Console.WriteLine($"OrderedBy asc take 20: {sw.Elapsed.TotalMilliseconds:F2} ms");
sw.Restart(); a = t.OrderedBy(x => x.Num, false, 500000, 20);
Console.WriteLine($"OrderedBy asc skip 500000 take 20: {sw.Elapsed.TotalMilliseconds:F0} ms (skips index entries, loads 20 rows)");
sw.Restart(); a = t.OrderedBy(x => x.Num, true, 500000, 20);
Console.WriteLine($"OrderedBy desc skip 500000 take 20: {sw.Elapsed.TotalMilliseconds:F0} ms");
Mem("after OrderedBy");

sw.Restart(); var r = t.Range(x => x.Num, 500, 520);
Console.WriteLine($"Range 21 values ({r.Count} rows), first call: {sw.Elapsed.TotalMilliseconds:F1} ms");
sw.Restart(); r = t.Range(x => x.Num, 700, 720);
Console.WriteLine($"Range 21 values ({r.Count} rows): {sw.Elapsed.TotalMilliseconds:F1} ms");
sw.Restart(); r = t.Range(x => x.Num, 0, 99999, descending: true, take: 20);
Console.WriteLine($"Range whole index desc take 20: {sw.Elapsed.TotalMilliseconds:F2} ms");
Mem("end");

class Row
{
    [PrimaryKey(AutoIncrement = true)] public int Id { get; set; }
    [Index(Unique = true)] public string Key { get; set; } = "";
    [Index] public int Num { get; set; }
    public string Title { get; set; } = "";
}

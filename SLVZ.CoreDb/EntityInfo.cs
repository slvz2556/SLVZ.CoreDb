using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace SLVZ.CoreDb;

/// <summary>Reads/writes one property value. Every value is prefixed with a "has value" flag.</summary>
internal sealed class FieldCodec
{
    public Action<BinaryWriter, object?> Write { get; }
    public Func<BinaryReader, object?> Read { get; }

    FieldCodec(Action<BinaryWriter, object?> w, Func<BinaryReader, object?> r)
    {
        Write = w;
        Read = r;
    }

    public static FieldCodec For(Type type)
    {
        var inner = Inner(Nullable.GetUnderlyingType(type) ?? type);
        return new FieldCodec(
            (w, v) =>
            {
                if (v is null) { w.Write(false); return; }
                w.Write(true);
                inner.Write(w, v);
            },
            r => r.ReadBoolean() ? inner.Read(r) : null);
    }

    static FieldCodec Make(Action<BinaryWriter, object> w, Func<BinaryReader, object> r) =>
        new((bw, v) => w(bw, v!), br => r(br));

    static FieldCodec Inner(Type t)
    {
        if (t.IsEnum) return Make((w, v) => w.Write(Convert.ToInt64(v)), r => Enum.ToObject(t, r.ReadInt64()));
        if (t == typeof(bool)) return Make((w, v) => w.Write((bool)v), r => r.ReadBoolean());
        if (t == typeof(byte)) return Make((w, v) => w.Write((byte)v), r => r.ReadByte());
        if (t == typeof(sbyte)) return Make((w, v) => w.Write((sbyte)v), r => r.ReadSByte());
        if (t == typeof(short)) return Make((w, v) => w.Write((short)v), r => r.ReadInt16());
        if (t == typeof(ushort)) return Make((w, v) => w.Write((ushort)v), r => r.ReadUInt16());
        if (t == typeof(int)) return Make((w, v) => w.Write((int)v), r => r.ReadInt32());
        if (t == typeof(uint)) return Make((w, v) => w.Write((uint)v), r => r.ReadUInt32());
        if (t == typeof(long)) return Make((w, v) => w.Write((long)v), r => r.ReadInt64());
        if (t == typeof(ulong)) return Make((w, v) => w.Write((ulong)v), r => r.ReadUInt64());
        if (t == typeof(float)) return Make((w, v) => w.Write((float)v), r => r.ReadSingle());
        if (t == typeof(double)) return Make((w, v) => w.Write((double)v), r => r.ReadDouble());
        if (t == typeof(decimal)) return Make((w, v) => w.Write((decimal)v), r => r.ReadDecimal());
        if (t == typeof(char)) return Make((w, v) => w.Write((ushort)(char)v), r => (char)r.ReadUInt16());
        if (t == typeof(string)) return Make((w, v) => w.Write((string)v), r => r.ReadString());
        if (t == typeof(DateTime))
            return Make(
                (w, v) => { var d = (DateTime)v; w.Write(d.Ticks); w.Write((byte)d.Kind); },
                r => { long ticks = r.ReadInt64(); var kind = (DateTimeKind)r.ReadByte(); return new DateTime(ticks, kind); });
        if (t == typeof(DateTimeOffset))
            return Make(
                (w, v) => { var d = (DateTimeOffset)v; w.Write(d.Ticks); w.Write((short)d.Offset.TotalMinutes); },
                r => { long ticks = r.ReadInt64(); short off = r.ReadInt16(); return new DateTimeOffset(ticks, TimeSpan.FromMinutes(off)); });
        if (t == typeof(TimeSpan)) return Make((w, v) => w.Write(((TimeSpan)v).Ticks), r => new TimeSpan(r.ReadInt64()));
        if (t == typeof(DateOnly)) return Make((w, v) => w.Write(((DateOnly)v).DayNumber), r => DateOnly.FromDayNumber(r.ReadInt32()));
        if (t == typeof(TimeOnly)) return Make((w, v) => w.Write(((TimeOnly)v).Ticks), r => new TimeOnly(r.ReadInt64()));
        if (t == typeof(Guid)) return Make((w, v) => w.Write(((Guid)v).ToByteArray()), r => new Guid(r.ReadBytes(16)));
        if (t == typeof(byte[]))
            return Make(
                (w, v) => { var b = (byte[])v; w.Write(b.Length); w.Write(b); },
                r => r.ReadBytes(r.ReadInt32()));

        // anything else (List<string>, nested classes, ...) is stored as JSON
        return Make(
            (w, v) => w.Write(JsonSerializer.Serialize(v, t)),
            r => JsonSerializer.Deserialize(r.ReadString(), t)!);
    }
}

internal sealed class PropMeta<T>
{
    public string Name = "";
    public Type Type = null!;
    public int Hash;
    public Func<T, object?> Get = null!;
    public Action<T, object?> Set = null!;
    public FieldCodec Codec = null!;
}

internal sealed class IndexMeta<T>
{
    public PropMeta<T> Prop = null!;
    public bool Unique;
}

/// <summary>Cached reflection metadata + record (de)serialization for an entity type.</summary>
internal sealed class EntityInfo<T> where T : class, new()
{
    public static readonly EntityInfo<T> Instance = new();

    public readonly PropMeta<T>[] Stored;
    public readonly PropMeta<T> Pk;
    public readonly bool AutoIncrement;
    public readonly IndexMeta<T>[] Indexes;
    readonly Dictionary<int, PropMeta<T>> _byHash;

    EntityInfo()
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttribute<IgnoreAttribute>() == null)
            .ToList();

        var stored = new List<PropMeta<T>>();
        var indexes = new List<IndexMeta<T>>();
        PropMeta<T>? pk = null;

        foreach (var p in props)
        {
            var m = new PropMeta<T>
            {
                Name = p.Name,
                Type = p.PropertyType,
                Hash = Fnv(p.Name),
                Get = MakeGetter(p),
                Set = MakeSetter(p),
                Codec = FieldCodec.For(p.PropertyType),
            };
            stored.Add(m);

            var pka = p.GetCustomAttribute<PrimaryKeyAttribute>();
            if (pka != null)
            {
                if (pk != null) throw new InvalidOperationException($"{typeof(T).Name} has more than one [PrimaryKey].");
                pk = m;
                AutoIncrement = pka.AutoIncrement;
            }
            else if (p.GetCustomAttribute<IndexAttribute>() is { } ia)
            {
                if (!KeyEncoder.IsSupported(p.PropertyType))
                    throw new InvalidOperationException($"{typeof(T).Name}.{p.Name}: type '{p.PropertyType.Name}' can't be indexed.");
                indexes.Add(new IndexMeta<T> { Prop = m, Unique = ia.Unique });
            }
        }

        pk ??= stored.FirstOrDefault(x => x.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (pk == null)
            throw new InvalidOperationException($"{typeof(T).Name} needs a [PrimaryKey] property (or a property named Id).");
        if (!KeyEncoder.IsSupported(pk.Type))
            throw new InvalidOperationException($"{typeof(T).Name}.{pk.Name}: type '{pk.Type.Name}' can't be a primary key.");
        if (AutoIncrement && pk.Type != typeof(int) && pk.Type != typeof(long))
            throw new InvalidOperationException("AutoIncrement works only with int or long keys.");

        Stored = stored.ToArray();
        Pk = pk;
        Indexes = indexes.ToArray();
        _byHash = Stored.ToDictionary(x => x.Hash);
    }

    public IndexMeta<T>? FindIndex(string propertyName) =>
        Indexes.FirstOrDefault(i => i.Prop.Name == propertyName);

    // Record = version byte, field count, then [name hash][length][value] per field.
    // Fields are matched by name, so adding/removing properties later is safe.
    public byte[] Serialize(T entity)
    {
        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms);
        using var tmp = new MemoryStream(64);
        using var tw = new BinaryWriter(tmp);

        w.Write((byte)1);
        w.Write7BitEncodedInt(Stored.Length);
        foreach (var p in Stored)
        {
            tmp.SetLength(0);
            p.Codec.Write(tw, p.Get(entity));
            tw.Flush();
            w.Write(p.Hash);
            w.Write7BitEncodedInt((int)tmp.Length);
            w.Write(tmp.GetBuffer(), 0, (int)tmp.Length);
        }
        w.Flush();
        return ms.ToArray();
    }

    public T Deserialize(byte[] data)
    {
        var entity = new T();
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms);

        r.ReadByte();
        int n = r.Read7BitEncodedInt();
        for (int i = 0; i < n; i++)
        {
            int hash = r.ReadInt32();
            int len = r.Read7BitEncodedInt();
            long end = ms.Position + len;
            if (_byHash.TryGetValue(hash, out var p))
            {
                try { p.Set(entity, p.Codec.Read(r)); }
                catch (Exception ex) when (ex is InvalidCastException or EndOfStreamException or FormatException or JsonException)
                {
                    // property type changed since the record was written: keep the default value
                }
            }
            ms.Position = end;
        }
        return entity;
    }

    static Func<T, object?> MakeGetter(PropertyInfo p)
    {
        var x = Expression.Parameter(typeof(T), "x");
        return Expression.Lambda<Func<T, object?>>(Expression.Convert(Expression.Property(x, p), typeof(object)), x).Compile();
    }

    static Action<T, object?> MakeSetter(PropertyInfo p)
    {
        var x = Expression.Parameter(typeof(T), "x");
        var v = Expression.Parameter(typeof(object), "v");
        var assign = Expression.Assign(Expression.Property(x, p), Expression.Convert(v, p.PropertyType));
        return Expression.Lambda<Action<T, object?>>(assign, x, v).Compile();
    }

    static int Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }
}

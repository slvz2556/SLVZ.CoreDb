using System.Globalization;
using System.Text;

namespace SLVZ.CoreDb;

/// <summary>Order-preserving binary encoding (memcmp order == value order) used for primary keys and indexes.</summary>
internal static class KeyEncoder
{
    public const int MaxPkBytes = 128;
    public const int MaxIndexValueBytes = 128;

    static readonly HashSet<Type> Supported = new()
    {
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(string),
        typeof(byte[]), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(DateOnly),
        typeof(TimeOnly), typeof(Guid),
    };

    public static bool IsSupported(Type type)
    {
        var u = Nullable.GetUnderlyingType(type) ?? type;
        return u.IsEnum || Supported.Contains(u);
    }

    public static byte[] EncodePk(object? value, Type type)
    {
        if (value is null) throw new ArgumentNullException(nameof(value), "Primary key can't be null.");
        var enc = Encode(value, type);
        if (enc.Length > MaxPkBytes)
            throw new CoreDbException($"Primary key is too long ({enc.Length} bytes, max {MaxPkBytes}).");
        return enc;
    }

    public static byte[] Trunc(byte[] enc) =>
        enc.Length <= MaxIndexValueBytes ? enc : enc[..MaxIndexValueBytes];

    public static byte[]? Successor(byte[] prefix)
    {
        int i = prefix.Length - 1;
        while (i >= 0 && prefix[i] == 0xFF) i--;
        if (i < 0) return null;
        var r = prefix[..(i + 1)];
        r[i]++;
        return r;
    }

    public static byte[] Encode(object? value, Type type)
    {
        var u = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null) return new byte[] { 0 };

        if (!u.IsEnum && u.IsPrimitive && value.GetType() != u)
            value = Convert.ChangeType(value, u, CultureInfo.InvariantCulture);

        var b = new List<byte>(16) { 1 };

        if (u.IsEnum) PutI64(b, Convert.ToInt64(value));
        else if (u == typeof(bool)) b.Add((bool)value ? (byte)1 : (byte)0);
        else if (u == typeof(byte)) b.Add((byte)value);
        else if (u == typeof(sbyte)) b.Add((byte)((byte)(sbyte)value ^ 0x80));
        else if (u == typeof(short)) PutU16(b, (ushort)((short)value ^ short.MinValue));
        else if (u == typeof(ushort)) PutU16(b, (ushort)value);
        else if (u == typeof(char)) PutU16(b, (char)value);
        else if (u == typeof(int)) PutU32(b, (uint)((int)value ^ int.MinValue));
        else if (u == typeof(uint)) PutU32(b, (uint)value);
        else if (u == typeof(long)) PutI64(b, (long)value);
        else if (u == typeof(ulong)) PutU64(b, (ulong)value);
        else if (u == typeof(float)) PutU64(b, Sortable((float)value));
        else if (u == typeof(double)) PutU64(b, Sortable((double)value));
        else if (u == typeof(decimal)) PutU64(b, Sortable((double)(decimal)value));
        else if (u == typeof(string)) PutEscaped(b, Encoding.UTF8.GetBytes((string)value));
        else if (u == typeof(byte[])) PutEscaped(b, (byte[])value);
        else if (u == typeof(DateTime)) PutI64(b, ((DateTime)value).Ticks);
        else if (u == typeof(DateTimeOffset)) PutI64(b, ((DateTimeOffset)value).UtcTicks);
        else if (u == typeof(TimeSpan)) PutI64(b, ((TimeSpan)value).Ticks);
        else if (u == typeof(DateOnly)) PutU32(b, (uint)(((DateOnly)value).DayNumber ^ int.MinValue));
        else if (u == typeof(TimeOnly)) PutI64(b, ((TimeOnly)value).Ticks);
        else if (u == typeof(Guid)) b.AddRange(((Guid)value).ToByteArray());
        else throw new NotSupportedException($"Type '{u.Name}' can't be used as a key or index.");

        return b.ToArray();
    }

    static ulong Sortable(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        return bits < 0 ? ~(ulong)bits : (ulong)bits | 0x8000000000000000UL;
    }

    static void PutU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

    static void PutU32(List<byte> b, uint v)
    {
        for (int s = 24; s >= 0; s -= 8) b.Add((byte)(v >> s));
    }

    static void PutU64(List<byte> b, ulong v)
    {
        for (int s = 56; s >= 0; s -= 8) b.Add((byte)(v >> s));
    }

    static void PutI64(List<byte> b, long v) => PutU64(b, (ulong)v ^ 0x8000000000000000UL);

    // 0x00 is escaped as 00 FF; the value ends with 00 00, so shorter strings sort first.
    static void PutEscaped(List<byte> b, byte[] data)
    {
        foreach (var x in data)
        {
            b.Add(x);
            if (x == 0) b.Add(0xFF);
        }
        b.Add(0);
        b.Add(0);
    }
}

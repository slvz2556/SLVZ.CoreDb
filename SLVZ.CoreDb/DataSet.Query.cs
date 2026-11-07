using System.Linq.Expressions;
using System.Reflection;
using SLVZ.CoreDb.Storage;

namespace SLVZ.CoreDb;

public sealed partial class DataSet<T>
{
    // ===================== public queries =====================

    /// <summary>
    /// Filters rows with a C# lambda, using indexes automatically:
    /// conditions joined by &amp;&amp; that compare an [Index] / [PrimaryKey] property with ==, &gt;, &gt;=, &lt;, &lt;= or
    /// <c>string.StartsWith</c> are answered through the B+Tree; branches joined by || each use their own index; the full lambda
    /// is then applied to the candidates, so any other condition still works. If some branch has no usable condition, all rows are scanned.
    /// Rows come back in index order when an index was used.
    /// <example><c>notes.Where(n =&gt; n.Slug.StartsWith("trip-") || n.Date &gt;= lastWeek)</c></example>
    /// </summary>
    public List<T> Where(Expression<Func<T, bool>> predicate) => RunRead(v =>
    {
        var list = new List<T>();
        Run(v, predicate, e => { list.Add(e); return true; });
        return list;
    });

    /// <summary>The first row matching <paramref name="predicate"/> (indexes are used like in <see cref="Where"/>), or null. Stops at the first match.</summary>
    public T? FirstOrDefault(Expression<Func<T, bool>> predicate) => RunRead(v =>
    {
        T? found = null;
        Run(v, predicate, e => { found = e; return false; });
        return found;
    });

    /// <summary>true if at least one row matches <paramref name="predicate"/>. Stops at the first match; when a single index condition is enough it doesn't even load rows.</summary>
    public bool Any(Expression<Func<T, bool>> predicate) => RunRead(v => CountOrAny(v, predicate, anyOnly: true) > 0);

    /// <summary>true if the table has at least one row.</summary>
    public bool Any() => Count > 0;

    /// <summary>Number of rows matching <paramref name="predicate"/>. When one index condition (==, &gt;=, &lt;=) is all there is, it counts index entries without loading any row.</summary>
    public long CountWhere(Expression<Func<T, bool>> predicate) => RunRead(v => CountOrAny(v, predicate, anyOnly: false));

    // ===================== execution =====================

    sealed record Step(BPlusTree? Tree, byte[]? Start, byte[]? End, bool Exact);   // Tree == null: the primary tree

    sealed class Bound
    {
        public bool HasEq, HasLo, HasHi, HasPrefix, Strict;
        public object? Eq, Lo, Hi;
        public string? Prefix;
        public int Matched;
    }

    sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? a, byte[]? b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] a)
        {
            var h = new HashCode();
            h.AddBytes(a);
            return h.ToHashCode();
        }
    }

    sealed class ParamFinder : ExpressionVisitor
    {
        readonly ParameterExpression _p;
        public bool Found;
        public ParamFinder(ParameterExpression p) => _p = p;
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == _p) Found = true;
            return node;
        }
    }

    /// <summary>Runs the predicate over the rows the plan selects (or all rows) and calls <paramref name="onMatch"/> for each match; return false from it to stop.</summary>
    void Run(TableView v, Expression<Func<T, bool>> predicate, Func<T, bool> onMatch)
    {
        var steps = Plan(predicate, v);
        // few candidates (index used): the interpreter starts instantly; full scan: real compiled IL is faster per row
        var test = predicate.Compile(preferInterpretation: steps != null);

        bool Visit(byte[] data)
        {
            var e = _e.Deserialize(data);
            return !test(e) || onMatch(e);
        }

        if (steps == null)
        {
            v.Primary.Scan(null, null, (_, val) => Visit(val));
            return;
        }
        if (steps.Count == 1)
        {
            ScanStep(v, steps[0], (_, data) => Visit(data));
            return;
        }

        var seen = new HashSet<byte[]>(ByteArrayComparer.Instance);   // a row can match several || branches
        foreach (var step in steps)
        {
            if (!ScanStep(v, step, (pk, data) => !seen.Add(pk) || Visit(data))) return;
        }
    }

    long CountOrAny(TableView v, Expression<Func<T, bool>> predicate, bool anyOnly)
    {
        var steps = Plan(predicate, v);
        if (steps is { Count: 1 } && steps[0].Exact)
        {
            long n = 0;
            var step = steps[0];
            (step.Tree ?? v.Primary).Scan(step.Start, step.End, (_, _) => { n++; return !anyOnly; });
            return n;
        }

        long c = 0;
        Run(v, predicate, _ => { c++; return !anyOnly; });
        return c;
    }

    /// <summary>Visits (primary key, record) for every row the step selects. Returns false if the visitor asked to stop.</summary>
    static bool ScanStep(TableView v, Step step, Func<byte[], byte[], bool> visit)
    {
        bool go = true;
        if (step.Tree == null)
        {
            v.Primary.Scan(step.Start, step.End, (k, val) => go = visit(k, val));
        }
        else
        {
            step.Tree.Scan(step.Start, step.End, (_, pk) =>
            {
                var data = v.Primary.Get(pk);
                if (data == null) return true;
                return go = visit(pk, data);
            });
        }
        return go;
    }

    // ===================== planning =====================

    /// <summary>One step per || branch, or null if any branch can't use an index (then everything is scanned).</summary>
    List<Step>? Plan(Expression<Func<T, bool>> expr, TableView v)
    {
        try
        {
            var param = expr.Parameters[0];
            var branches = new List<Expression>();
            SplitOr(expr.Body, branches);

            var steps = new List<Step>();
            foreach (var branch in branches)
            {
                var conjuncts = new List<Expression>();
                SplitAnd(branch, conjuncts);
                var step = PlanConjuncts(conjuncts, param, v);
                if (step == null) return null;
                steps.Add(step);
            }
            return steps;
        }
        catch (Exception)
        {
            return null;   // can't plan it: scan everything, the lambda decides (and reports its own errors)
        }
    }

    Step? PlanConjuncts(List<Expression> conjuncts, ParameterExpression param, TableView v)
    {
        var bounds = new Dictionary<string, Bound>();
        Bound BoundOf(string name)
        {
            if (!bounds.TryGetValue(name, out var b)) bounds[name] = b = new Bound();
            return b;
        }
        bool Usable(string name) => name == _e.Pk.Name || _e.FindIndex(name) != null;

        foreach (var c in conjuncts)
        {
            if (TryMatch(c, param, out var name, out var op, out var value))
            {
                if (!Usable(name)) continue;
                var b = BoundOf(name);
                switch (op)
                {
                    case ExpressionType.Equal:
                        b.HasEq = true; b.Eq = value; b.Matched++;
                        break;
                    case ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual when value != null:
                        b.HasLo = true; b.Lo = value; b.Matched++;
                        if (op == ExpressionType.GreaterThan) b.Strict = true;
                        break;
                    case ExpressionType.LessThan or ExpressionType.LessThanOrEqual when value != null:
                        b.HasHi = true; b.Hi = value; b.Matched++;
                        if (op == ExpressionType.LessThan) b.Strict = true;
                        break;
                }
            }
            else if (TryMatchStartsWith(c, param, out name, out var prefix))
            {
                if (!Usable(name)) continue;
                var b = BoundOf(name);
                b.HasPrefix = true; b.Prefix = prefix; b.Matched++;
            }
        }

        string? best = null;
        int bestScore = int.MaxValue;
        foreach (var (name, b) in bounds)
        {
            bool isPk = name == _e.Pk.Name;
            int score;
            if (b.HasEq) score = isPk ? 0 : _e.FindIndex(name)!.Unique ? 1 : 2;
            else if (b.HasPrefix) score = 3;
            else if (b.HasLo || b.HasHi) score = (isPk ? 4 : 6) + (b.HasLo && b.HasHi ? 0 : 1);
            else continue;
            if (score < bestScore) { bestScore = score; best = name; }
        }
        if (best == null) return null;

        var bd = bounds[best];
        bool pk = best == _e.Pk.Name;
        var type = pk ? _e.Pk.Type : _e.FindIndex(best)!.Prop.Type;
        bool truncated = false;
        byte[] Enc(object? val)
        {
            var raw = KeyEncoder.Encode(val, type);
            if (!pk && raw.Length > KeyEncoder.MaxIndexValueBytes) { truncated = true; return KeyEncoder.Trunc(raw); }
            return raw;
        }

        var tree = pk ? null : v.Indexes[best];
        // "exact" = the index entries ARE the answer (no other condition, inclusive bounds, keys compare like the values do)
        bool exactBase = KeyEncoder.IsExactType(type) && conjuncts.Count == bd.Matched && !bd.Strict && !bd.HasPrefix;

        if (bd.HasEq)
        {
            var p = Enc(bd.Eq);
            return new Step(tree, p, KeyEncoder.Successor(p), exactBase && !truncated && bd.Matched == 1);
        }
        if (bd.HasPrefix)
        {
            var p = KeyEncoder.EncodePrefix(bd.Prefix!);
            return new Step(tree, p, KeyEncoder.Successor(p), false);
        }

        var start = bd.HasLo ? Enc(bd.Lo) : new byte[] { 1 };   // 0x00 = null values
        var end = bd.HasHi ? KeyEncoder.Successor(Enc(bd.Hi)) : null;
        bool exactRange = exactBase && !truncated && bd.Matched == (bd.HasLo ? 1 : 0) + (bd.HasHi ? 1 : 0);
        return new Step(tree, start, end, exactRange);
    }

    static void SplitOr(Expression e, List<Expression> list)
    {
        if (e is BinaryExpression { NodeType: ExpressionType.OrElse } b)
        {
            SplitOr(b.Left, list);
            SplitOr(b.Right, list);
        }
        else list.Add(e);
    }

    static void SplitAnd(Expression e, List<Expression> list)
    {
        if (e is BinaryExpression { NodeType: ExpressionType.AndAlso } b)
        {
            SplitAnd(b.Left, list);
            SplitAnd(b.Right, list);
        }
        else list.Add(e);
    }

    static Expression Unwrap(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert } u ? Unwrap(u.Operand) : e;

    static bool TryMatch(Expression e, ParameterExpression param, out string name, out ExpressionType op, out object? value)
    {
        name = ""; op = default; value = null;
        if (e is not BinaryExpression b) return false;
        if (b.NodeType is not (ExpressionType.Equal or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual)) return false;

        var left = Unwrap(b.Left);
        var right = Unwrap(b.Right);
        Expression valueExpr;
        if (IsParamProperty(left, param, out name) && !Uses(right, param)) { valueExpr = right; op = b.NodeType; }
        else if (IsParamProperty(right, param, out name) && !Uses(left, param)) { valueExpr = left; op = Flip(b.NodeType); }
        else return false;

        value = Eval(valueExpr);
        return true;
    }

    /// <summary>Matches <c>x.Prop.StartsWith("abc")</c>, <c>StartsWith("abc", StringComparison.Ordinal)</c> and <c>StartsWith('a')</c>.
    /// The index is searched with an ordinal prefix; the full lambda is applied afterwards.</summary>
    static bool TryMatchStartsWith(Expression e, ParameterExpression param, out string name, out string prefix)
    {
        name = ""; prefix = "";
        if (e is not MethodCallExpression { Method.Name: "StartsWith", Object: { } target } call) return false;
        if (call.Method.DeclaringType != typeof(string)) return false;
        if (!IsParamProperty(Unwrap(target), param, out name)) return false;

        var args = call.Arguments;
        if (args.Count == 0 || args.Count > 2 || Uses(args[0], param)) return false;
        if (args.Count == 2)
        {
            if (args[1].Type != typeof(StringComparison) || Uses(args[1], param)) return false;
            if (Eval(args[1]) is not StringComparison.Ordinal) return false;   // only ordinal matches the index order exactly
        }

        var arg = Eval(args[0]);
        if (arg is char ch) prefix = ch.ToString();
        else if (arg is string s) prefix = s;
        else return false;
        return true;
    }

    // constants and captured variables (closure fields) are read by reflection; anything else is interpreted
    static object? Eval(Expression e)
    {
        switch (e)
        {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression { Expression: null } m:
                return m.Member is FieldInfo sf ? sf.GetValue(null) : ((PropertyInfo)m.Member).GetValue(null);
            case MemberExpression { Expression: { } inner } m when !HasCall(inner):
                var target = Eval(inner);
                return m.Member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)m.Member).GetValue(target);
            default:
                return Expression.Lambda<Func<object?>>(Expression.Convert(e, typeof(object))).Compile(preferInterpretation: true)();
        }
    }

    static bool HasCall(Expression e) => e is not (ConstantExpression or MemberExpression { Expression: null })
        && !(e is MemberExpression me && !HasCall(me.Expression!));

    static bool IsParamProperty(Expression e, ParameterExpression param, out string name)
    {
        if (e is MemberExpression { Member: PropertyInfo p, Expression: ParameterExpression pe } && pe == param)
        {
            name = p.Name;
            return true;
        }
        name = "";
        return false;
    }

    static bool Uses(Expression e, ParameterExpression param)
    {
        var f = new ParamFinder(param);
        f.Visit(e);
        return f.Found;
    }

    static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t,
    };
}

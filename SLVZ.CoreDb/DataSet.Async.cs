using System.Linq.Expressions;

namespace SLVZ.CoreDb;

// Async versions of the methods. They run the (blocking) work on a thread-pool thread, so the UI thread stays free.
// The CancellationToken is only checked before the work starts.
public sealed partial class DataSet<T>
{
    /// <summary>Async version of <see cref="Find"/>.</summary>
    public Task<T?> FindAsync(object key, CancellationToken ct = default) => Task.Run(() => Find(key), ct);
    /// <summary>Async version of <see cref="Exists"/>.</summary>
    public Task<bool> ExistsAsync(object key, CancellationToken ct = default) => Task.Run(() => Exists(key), ct);
    /// <summary>Async version of <see cref="GetAll"/>.</summary>
    public Task<List<T>> GetAllAsync(CancellationToken ct = default) => Task.Run(() => GetAll(), ct);
    /// <summary>Async version of <see cref="Where"/>.</summary>
    public Task<List<T>> WhereAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.Run(() => Where(predicate), ct);
    /// <summary>Async version of <see cref="FirstOrDefault"/>.</summary>
    public Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.Run(() => FirstOrDefault(predicate), ct);
    /// <summary>Async version of <see cref="Any(Expression{Func{T, bool}})"/>.</summary>
    public Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.Run(() => Any(predicate), ct);
    /// <summary>Async version of <see cref="CountWhere"/>.</summary>
    public Task<long> CountWhereAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.Run(() => CountWhere(predicate), ct);
    /// <summary>Async version of <see cref="FindBy{TValue}"/>.</summary>
    public Task<List<T>> FindByAsync<TValue>(Expression<Func<T, TValue>> property, TValue value, CancellationToken ct = default) => Task.Run(() => FindBy(property, value), ct);
    /// <summary>Async version of <see cref="Range{TValue}"/>.</summary>
    public Task<List<T>> RangeAsync<TValue>(Expression<Func<T, TValue>> property, TValue from, TValue to,
        bool descending = false, int skip = 0, int? take = null, CancellationToken ct = default) => Task.Run(() => Range(property, from, to, descending, skip, take), ct);
    /// <summary>Async version of <see cref="OrderedBy{TValue}"/>.</summary>
    public Task<List<T>> OrderedByAsync<TValue>(Expression<Func<T, TValue>> property, bool descending = false, int skip = 0, int? take = null,
        CancellationToken ct = default) => Task.Run(() => OrderedBy(property, descending, skip, take), ct);
    /// <summary>Async version of <see cref="Min{TValue}"/>.</summary>
    public Task<TValue> MinAsync<TValue>(Expression<Func<T, TValue>> property, CancellationToken ct = default) => Task.Run(() => Min(property), ct);
    /// <summary>Async version of <see cref="Max{TValue}"/>.</summary>
    public Task<TValue> MaxAsync<TValue>(Expression<Func<T, TValue>> property, CancellationToken ct = default) => Task.Run(() => Max(property), ct);

    /// <summary>Async version of <see cref="Insert"/>.</summary>
    public Task InsertAsync(T entity, CancellationToken ct = default) => Task.Run(() => Insert(entity), ct);
    /// <summary>Async version of <see cref="InsertMany"/>.</summary>
    public Task InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default) => Task.Run(() => InsertMany(entities), ct);
    /// <summary>Async version of <see cref="Update"/>.</summary>
    public Task<bool> UpdateAsync(T entity, CancellationToken ct = default) => Task.Run(() => Update(entity), ct);
    /// <summary>Async version of <see cref="Upsert"/>.</summary>
    public Task UpsertAsync(T entity, CancellationToken ct = default) => Task.Run(() => Upsert(entity), ct);
    /// <summary>Async version of <see cref="UpsertMany"/>.</summary>
    public Task UpsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default) => Task.Run(() => UpsertMany(entities), ct);
    /// <summary>Async version of <see cref="Delete(object)"/>.</summary>
    public Task<bool> DeleteAsync(object key, CancellationToken ct = default) => Task.Run(() => Delete(key), ct);
    /// <summary>Async version of <see cref="Delete(T)"/>.</summary>
    public Task<bool> DeleteAsync(T entity, CancellationToken ct = default) => Task.Run(() => Delete(entity), ct);
    /// <summary>Async version of <see cref="UpdateMany(IEnumerable{T})"/>.</summary>
    public Task<int> UpdateManyAsync(IEnumerable<T> entities, CancellationToken ct = default) => Task.Run(() => UpdateMany(entities), ct);
    /// <summary>Async version of <see cref="UpdateMany(Expression{Func{T, bool}}, Action{T})"/>.</summary>
    public Task<int> UpdateManyAsync(Expression<Func<T, bool>> predicate, Action<T> change, CancellationToken ct = default) => Task.Run(() => UpdateMany(predicate, change), ct);
    /// <summary>Async version of <see cref="DeleteMany(IEnumerable{T})"/>.</summary>
    public Task<int> DeleteManyAsync(IEnumerable<T> entities, CancellationToken ct = default) => Task.Run(() => DeleteMany(entities), ct);
    /// <summary>Async version of <see cref="DeleteMany{TKey}"/>.</summary>
    public Task<int> DeleteManyAsync<TKey>(IEnumerable<TKey> keys, CancellationToken ct = default) => Task.Run(() => DeleteMany(keys), ct);
    /// <summary>Async version of <see cref="DeleteMany(Expression{Func{T, bool}})"/>.</summary>
    public Task<int> DeleteManyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Task.Run(() => DeleteMany(predicate), ct);
    /// <summary>Async version of <see cref="Transaction"/>. The action itself is synchronous (a transaction belongs to one thread).</summary>
    public Task TransactionAsync(Action action, CancellationToken ct = default) => Task.Run(() => Transaction(action), ct);
    /// <summary>Async version of <see cref="Clear"/>.</summary>
    public Task ClearAsync(CancellationToken ct = default) => Task.Run(() => Clear(), ct);
    /// <summary>Async version of <see cref="Compact"/>.</summary>
    public Task CompactAsync(CancellationToken ct = default) => Task.Run(() => Compact(), ct);
}

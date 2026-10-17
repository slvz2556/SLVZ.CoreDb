namespace SLVZ.CoreDb;

/// <summary>Marks the primary key property. If no property has it, a property named "Id" is used.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class PrimaryKeyAttribute : Attribute
{
    /// <summary>Only for int / long keys: a key of 0 is replaced with the next number on insert.</summary>
    public bool AutoIncrement { get; set; }
}

/// <summary>Creates a B+Tree index on the property for very fast lookups (FindBy / Range / OrderedBy).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class IndexAttribute : Attribute
{
    /// <summary>No two rows may have the same (non-null) value.</summary>
    public bool Unique { get; set; }
}

/// <summary>The property is not stored.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class IgnoreAttribute : Attribute { }

namespace ZeroAlloc.Outbox.Orm;

/// <summary>
/// The persisted status column, stored as its integer value.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a separate declaration from the EF Core adapter's enum of the
/// same shape, rather than a shared one. Taking a dependency on
/// <c>ZeroAlloc.Outbox.EfCore</c> purely for three constants would drag EF Core
/// into every application using this store, which is the opposite of the point.
/// </para>
/// <para>
/// The values match that enum exactly, so the two adapters read and write the
/// same rows and can be swapped without a data migration. Changing a value here
/// silently reinterprets existing rows.
/// </para>
/// </remarks>
internal enum OrmOutboxMessageStatus
{
    Pending = 0,
    Succeeded = 1,
    DeadLetter = 2,
}

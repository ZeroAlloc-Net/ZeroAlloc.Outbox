using System.Data;
using System.Data.Async;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// Wraps an <see cref="IAsyncDbConnection"/> and records the <c>CommandText</c> of every command it
/// runs, passing everything else straight through. It makes the SQL a generated repository sends
/// observable, so a test can assert on the text rather than only on the rows it changes.
/// </summary>
internal sealed class RecordingAsyncConnection(IAsyncDbConnection inner) : IAsyncDbConnection
{
    private readonly List<string> _commands = [];

    /// <summary>The text of each command that ran, in order.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>The text of the most recent command that ran.</summary>
    public string LastCommand => _commands[^1];

#pragma warning disable CA2227 // Interface member; the setter is forwarded.
    public string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }
#pragma warning restore CA2227

    public int ConnectionTimeout => inner.ConnectionTimeout;

    public string Database => inner.Database;

    public ConnectionState State => inner.State;

    public bool CanCreateBatch => inner.CanCreateBatch;

    public IAsyncDbTransaction BeginTransaction() => inner.BeginTransaction();

    public IAsyncDbTransaction BeginTransaction(IsolationLevel isolationLevel)
        => inner.BeginTransaction(isolationLevel);

    public ValueTask<IAsyncDbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => inner.BeginTransactionAsync(cancellationToken);

    public ValueTask<IAsyncDbTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        => inner.BeginTransactionAsync(isolationLevel, cancellationToken);

    public void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    public ValueTask ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
        => inner.ChangeDatabaseAsync(databaseName, cancellationToken);

    public IAsyncDbCommand CreateCommand() => new RecordingAsyncCommand(inner.CreateCommand(), _commands);

    public IAsyncDbBatch CreateBatch() => inner.CreateBatch();

    public void Open() => inner.Open();

    public ValueTask OpenAsync(CancellationToken cancellationToken = default) => inner.OpenAsync(cancellationToken);

    public void Close() => inner.Close();

    public ValueTask CloseAsync() => inner.CloseAsync();

    public void Dispose() => inner.Dispose();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

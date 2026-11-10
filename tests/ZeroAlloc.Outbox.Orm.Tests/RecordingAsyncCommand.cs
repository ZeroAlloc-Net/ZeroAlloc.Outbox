using System.Data;
using System.Data.Async;

namespace ZeroAlloc.Outbox.Orm.Tests;

/// <summary>
/// The command a <see cref="RecordingAsyncConnection"/> hands out. It records its <c>CommandText</c>
/// when it executes and forwards everything to the wrapped command.
/// </summary>
internal sealed class RecordingAsyncCommand(IAsyncDbCommand inner, List<string> sink) : IAsyncDbCommand
{
    public string CommandText
    {
        get => inner.CommandText;
        set => inner.CommandText = value;
    }

    public int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public IAsyncDbConnection? Connection
    {
        get => inner.Connection;
        set => inner.Connection = value;
    }

    public IAsyncDbTransaction? Transaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value;
    }

    public IDataParameterCollection Parameters => inner.Parameters;

    public UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    public IAsyncDataReader ExecuteReader()
    {
        Record();
        return inner.ExecuteReader();
    }

    public IAsyncDataReader ExecuteReader(CommandBehavior behavior)
    {
        Record();
        return inner.ExecuteReader(behavior);
    }

    public int ExecuteNonQuery()
    {
        Record();
        return inner.ExecuteNonQuery();
    }

    public object? ExecuteScalar()
    {
        Record();
        return inner.ExecuteScalar();
    }

    public void Prepare() => inner.Prepare();

    public void Cancel() => inner.Cancel();

    public IDbDataParameter CreateParameter() => inner.CreateParameter();

    public ValueTask<IAsyncDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        Record();
        return inner.ExecuteReaderAsync(cancellationToken);
    }

    public ValueTask<IAsyncDataReader> ExecuteReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken = default)
    {
        Record();
        return inner.ExecuteReaderAsync(behavior, cancellationToken);
    }

    public ValueTask<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        Record();
        return inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        Record();
        return inner.ExecuteScalarAsync(cancellationToken);
    }

    public ValueTask PrepareAsync(CancellationToken cancellationToken = default)
        => inner.PrepareAsync(cancellationToken);

    public void Dispose() => inner.Dispose();

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private void Record() => sink.Add(inner.CommandText);
}

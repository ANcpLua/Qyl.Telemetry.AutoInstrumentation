using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners.Semantics;
using Qyl.Telemetry.AutoInstrumentation.Internal;

namespace Qyl.Telemetry.AutoInstrumentation.SqlClient;

internal static class SqlClientPayloadReader
{
    private const string CommandKey = "Command";
    private const string ExceptionKey = "Exception";
    private const string OperationIdKey = "OperationId";
    private const string TimestampKey = "Timestamp";

    public static bool TryRead(object? payload, bool isError, out SqlClientCommand command)
    {
        if (!TryGetPayload<SqlCommand>(payload, CommandKey, out var sqlCommand))
        {
            command = default;
            return false;
        }

        _ = TryGetPayload<Exception>(payload, ExceptionKey, out var exception);

        var (operation, summary) = QylDbQuerySummary.Describe(sqlCommand.CommandType, sqlCommand.CommandText);
        var endpoint = ParseDataSource(sqlCommand.Connection?.DataSource);

        command = new SqlClientCommand(
            Command: sqlCommand,
            Namespace: DatabaseSemantics.NullIfWhiteSpace(sqlCommand.Connection?.Database),
            Operation: operation,
            QuerySummary: summary,
            QueryText: sqlCommand.CommandText,
            ServerAddress: endpoint.Address,
            ServerPort: endpoint.Port,
            ErrorType: isError ? GetErrorType(exception) : null,
            Timestamp: TryGetPayload<long>(payload, TimestampKey, out var timestamp) ? timestamp : null);

        return true;
    }

    public static bool TryReadOperationStart(object? payload, out Guid operationId, out long timestamp)
    {
        timestamp = 0;
        return TryGetPayload(payload, OperationIdKey, out operationId) &&
               TryGetPayload(payload, TimestampKey, out timestamp);
    }

    public static bool TryReadOperationId(object? payload, out Guid operationId) =>
        TryGetPayload(payload, OperationIdKey, out operationId);

    private static bool TryGetPayload<T>(object? payload, string key, [MaybeNullWhen(false)] out T value)
    {
        if (payload is IEnumerable<KeyValuePair<string, object>> entries)
        {
            foreach (var entry in entries)
            {
                if (StringComparer.Ordinal.Equals(entry.Key, key) &&
                    entry.Value is T matched)
                {
                    value = matched;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetErrorType(Exception? exception)
        => exception switch
        {
            SqlException sqlException => sqlException.Number.ToString(CultureInfo.InvariantCulture),
            null => null,
            _ => exception.GetType().FullName,
        };

    private static SqlServerEndpoint ParseDataSource(string? dataSource)
    {
        if (DatabaseSemantics.NullIfWhiteSpace(dataSource) is not { } source)
            return default;

        source = StripProtocolPrefix(source);
        var port = ParsePort(ref source);

        var instanceSeparator = source.IndexOf('\\', StringComparison.Ordinal);
        if (instanceSeparator > 0)
            source = source[..instanceSeparator];

        return new SqlServerEndpoint(DatabaseSemantics.NullIfWhiteSpace(source), port);
    }

    private static string StripProtocolPrefix(string source)
    {
        var separator = source.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return source;

        var prefix = source[..separator];
        return StringComparer.OrdinalIgnoreCase.Equals(prefix, "tcp") ||
               StringComparer.OrdinalIgnoreCase.Equals(prefix, "np") ||
               StringComparer.OrdinalIgnoreCase.Equals(prefix, "lpc")
            ? source[(separator + 1)..]
            : source;
    }

    private static int? ParsePort(ref string source)
    {
        var separator = source.LastIndexOf(',');
        if (separator <= 0)
            return null;

        if (!int.TryParse(source.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return null;

        source = source[..separator];
        return port;
    }
}

internal readonly record struct SqlClientCommand(
    SqlCommand Command,
    string? Namespace,
    string? Operation,
    string? QuerySummary,
    string? QueryText,
    string? ServerAddress,
    int? ServerPort,
    string? ErrorType,
    long? Timestamp);

internal readonly record struct SqlServerEndpoint(string? Address, int? Port);

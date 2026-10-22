using ANcpLua.Roslyn.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Qyl.Telemetry.AutoInstrumentation.SourceGenerators;

/// <summary>
/// The named shape predicates: the genuinely-logic residue a declaration cannot express as data.
/// A declaration selects one by name; the predicate validates the overload against the library's
/// real signature and computes the structural facts the emitted interceptor needs.
/// </summary>
public sealed partial class QylAutoInstrumentationGenerator
{
    private readonly record struct ShapeMatch(
        string ReceiverTypeName,
        string ReturnType,
        EquatableArray<ParameterSpec> Parameters,
        bool IsAsync,
        string TypeParameterList = "",
        string ConstraintClauses = "",
        string ExtensionContainingType = "",
        string InstrumentationId = "",
        EquatableArray<string> MetricIds = default,
        string ShapeExpression = "");

    private static bool TryMatchShape(
        string shape,
        IntegrationDeclaration integration,
        IMethodSymbol symbol,
        ITypeSymbol? receiverType,
        out ShapeMatch match)
    {
        switch (shape)
        {
            case "DbCommand":
                return TryMatchDbCommand(symbol, receiverType, out match);
            case "WcfClient":
                return TryMatchWcfClient(symbol, out match);
            case "KafkaProduce":
                return TryMatchKafkaProduce(symbol, out match);
            case "KafkaConsume":
                return TryMatchKafkaConsume(symbol, out match);
            case "RedisCommand":
                return TryMatchRedisCommand(symbol, integration.HelperType, out match);
            default:
                throw new InvalidOperationException("Unknown interceptor shape: " + shape);
        }
    }

    private static bool TryMatchDbCommand(IMethodSymbol symbol, ITypeSymbol? receiverType, out ShapeMatch match)
    {
        match = default;
        var methodName = symbol.Name;
        var isAsync = methodName.EndsWithOrdinal("Async");
        if (!TryGetDbCommandParameters(symbol, methodName, out var parameters) ||
            !TryGetDbCommandReturn(symbol, methodName, isAsync, out var returnType))
        {
            return false;
        }

        // The provider is the receiver the call site names, so a SqlCommand typed as DbCommand still
        // reports under ADONET while a SqlCommand-typed receiver reports under SQLCLIENT.
        var effectiveReceiverType = receiverType is not null &&
                                    InheritsFromOrIs(receiverType, "global::System.Data.Common.DbCommand")
            ? receiverType
            : symbol.ContainingType;
        var instrumentationId = GetDbInstrumentationId(effectiveReceiverType);
        match = new ShapeMatch(
            EmittedTypeName(symbol.ContainingType),
            returnType,
            parameters,
            isAsync,
            InstrumentationId: instrumentationId,
            MetricIds: GetDbMetricIds(instrumentationId));
        return true;
    }

    private static bool TryGetDbCommandParameters(IMethodSymbol symbol, string methodName, out EquatableArray<ParameterSpec> parameters)
    {
        parameters = default;
        if (string.Equals(methodName, "ExecuteReader", StringComparison.Ordinal))
            return TryGetDbExecuteReaderParameters(symbol, allowCancellationToken: false, out parameters);

        if (string.Equals(methodName, "ExecuteReaderAsync", StringComparison.Ordinal))
            return TryGetDbExecuteReaderParameters(symbol, allowCancellationToken: true, out parameters);

        if (string.Equals(methodName, "ExecuteScalar", StringComparison.Ordinal) ||
            string.Equals(methodName, "ExecuteNonQuery", StringComparison.Ordinal))
        {
            return TryGetNoParameters(symbol, out parameters);
        }

        if (string.Equals(methodName, "ExecuteScalarAsync", StringComparison.Ordinal) ||
            string.Equals(methodName, "ExecuteNonQueryAsync", StringComparison.Ordinal))
        {
            return TryGetOptionalCancellationTokenParameters(symbol, out parameters);
        }

        return false;
    }

    private static bool TryGetDbCommandReturn(IMethodSymbol symbol, string methodName, bool isAsync, out string returnType)
    {
        returnType = EmittedTypeName(symbol.ReturnType, symbol);
        if (string.Equals(methodName, "ExecuteNonQuery", StringComparison.Ordinal))
            return symbol.ReturnType.SpecialType is SpecialType.System_Int32;

        if (string.Equals(methodName, "ExecuteScalar", StringComparison.Ordinal))
            return symbol.ReturnType.SpecialType is SpecialType.System_Object;

        if (string.Equals(methodName, "ExecuteReader", StringComparison.Ordinal))
            return InheritsFromOrIs(symbol.ReturnType, "global::System.Data.Common.DbDataReader");

        if (!isAsync || !TryGetTaskResult(symbol.ReturnType, out var taskResult))
            return false;

        if (string.Equals(methodName, "ExecuteNonQueryAsync", StringComparison.Ordinal))
            return taskResult.SpecialType is SpecialType.System_Int32;

        if (string.Equals(methodName, "ExecuteScalarAsync", StringComparison.Ordinal))
            return taskResult.SpecialType is SpecialType.System_Object;

        if (string.Equals(methodName, "ExecuteReaderAsync", StringComparison.Ordinal))
            return InheritsFromOrIs(taskResult, "global::System.Data.Common.DbDataReader");

        return false;
    }

    private static string GetDbInstrumentationId(ITypeSymbol type)
    {
        var display = CleanTypeName(type);
        if (display.StartsWithOrdinal("global::Microsoft.Data.SqlClient.") ||
            display.StartsWithOrdinal("global::System.Data.SqlClient."))
        {
            return "SQLCLIENT";
        }

        if (display.StartsWithOrdinal("global::Microsoft.Data.Sqlite."))
            return "SQLITE";

        if (display.StartsWithOrdinal("global::Npgsql."))
            return "NPGSQL";

        if (display.StartsWithOrdinal("global::MySqlConnector."))
            return "MYSQLCONNECTOR";

        if (display.StartsWithOrdinal("global::MySql.Data."))
            return "MYSQLDATA";

        if (display.StartsWithOrdinal("global::Oracle.ManagedDataAccess."))
            return "ORACLEMDA";

        return "ADONET";
    }

    private static EquatableArray<string> GetDbMetricIds(string instrumentationId)
        => instrumentationId switch
        {
            "NPGSQL" or "SQLCLIENT" => ImmutableArray.Create(instrumentationId).AsEquatableArray(),
            _ => default,
        };

    private static bool TryGetNoParameters(IMethodSymbol symbol, out EquatableArray<ParameterSpec> parameters)
    {
        parameters = default;
        return symbol.Parameters.Length is 0;
    }

    private static bool TryGetOptionalCancellationTokenParameters(IMethodSymbol symbol, out EquatableArray<ParameterSpec> parameters)
    {
        if (symbol.Parameters.Length is 0)
        {
            parameters = default;
            return true;
        }

        if (symbol.Parameters.Length is 1 && IsType(symbol.Parameters[0].Type, "global::System.Threading.CancellationToken"))
        {
            parameters = BuildParameters(symbol);
            return true;
        }

        parameters = default;
        return false;
    }

    private static bool TryGetDbExecuteReaderParameters(IMethodSymbol symbol, bool allowCancellationToken, out EquatableArray<ParameterSpec> parameters)
    {
        if (symbol.Parameters.Length is 0)
        {
            parameters = default;
            return true;
        }

        if (symbol.Parameters.Length is 1)
        {
            if (IsType(symbol.Parameters[0].Type, "global::System.Data.CommandBehavior") ||
                (allowCancellationToken && IsType(symbol.Parameters[0].Type, "global::System.Threading.CancellationToken")))
            {
                parameters = BuildParameters(symbol);
                return true;
            }
        }

        if (allowCancellationToken &&
            symbol.Parameters.Length is 2 &&
            IsType(symbol.Parameters[0].Type, "global::System.Data.CommandBehavior") &&
            IsType(symbol.Parameters[1].Type, "global::System.Threading.CancellationToken"))
        {
            parameters = BuildParameters(symbol);
            return true;
        }

        parameters = default;
        return false;
    }

    private static bool TryMatchWcfClient(IMethodSymbol symbol, out ShapeMatch match)
    {
        match = default;
        if (symbol.IsStatic ||
            symbol.MethodKind is not MethodKind.Ordinary ||
            symbol.IsGenericMethod ||
            !CanEmitByValueOrInParameters(symbol) ||
            IsSystemServiceModelType(symbol.ContainingType) ||
            GetWcfContractType(symbol.ContainingType) is not { } contractType ||
            FindWcfOperation(contractType, symbol) is not { } operation)
        {
            return false;
        }

        match = new ShapeMatch(
            EmittedTypeName(symbol.ContainingType),
            EmittedTypeName(symbol.ReturnType, symbol),
            BuildParameters(symbol),
            IsTask(symbol.ReturnType) || TryGetTaskResult(symbol.ReturnType, out _),
            ShapeExpression: QuoteLiteral(GetWcfMethodName(contractType, operation)));
        return true;
    }

    // Only a method that implements an operation of the contract sends a message; helpers and
    // object overrides on the client class do not.
    private static IMethodSymbol? FindWcfOperation(INamedTypeSymbol contractType, IMethodSymbol implementation)
    {
        foreach (var contract in contractType.AllInterfaces.Insert(0, contractType))
        {
            foreach (var member in contract.GetMembers(implementation.Name))
            {
                if (member is IMethodSymbol operation &&
                    SymbolEqualityComparer.Default.Equals(
                        implementation.ContainingType.FindImplementationForInterfaceMember(operation)?.OriginalDefinition,
                        implementation.OriginalDefinition))
                {
                    return operation;
                }
            }
        }

        return null;
    }

    private static INamedTypeSymbol? GetWcfContractType(INamedTypeSymbol clientType)
    {
        for (var current = clientType; current is not null; current = current.BaseType)
        {
            if (IsType(current.ConstructedFrom, "global::System.ServiceModel.ClientBase<TChannel>") &&
                current.TypeArguments is [INamedTypeSymbol contractType])
            {
                return contractType;
            }
        }

        return null;
    }

    // rpc.method is the contract's own naming: the ServiceContract/OperationContract Name overrides
    // when present, else the interface and method names.
    private static string GetWcfMethodName(INamedTypeSymbol contractType, IMethodSymbol operation)
    {
        var serviceName = GetAttributeName(
                              contractType,
                              "System.ServiceModel.ServiceContractAttribute") ??
                          contractType.Name;
        var operationName = GetAttributeName(
                                operation,
                                "System.ServiceModel.OperationContractAttribute") ??
                            operation.Name;
        return serviceName + "/" + operationName;
    }

    private static string? GetAttributeName(ISymbol symbol, string attributeType)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (!string.Equals(attribute.AttributeClass?.ToDisplayString(), attributeType, StringComparison.Ordinal))
                continue;

            foreach (var argument in attribute.NamedArguments)
            {
                if (string.Equals(argument.Key, "Name", StringComparison.Ordinal) &&
                    argument.Value.Value is string { Length: > 0 } name)
                {
                    return name;
                }
            }
        }

        return null;
    }

    private static bool IsSystemServiceModelType(ITypeSymbol? symbol)
        => symbol is INamedTypeSymbol named &&
           named.ContainingNamespace.ToDisplayString().StartsWithOrdinal("System.ServiceModel");

    private static bool TryMatchKafkaProduce(IMethodSymbol symbol, out ShapeMatch match)
    {
        match = default;
        var receiver = EmittedTypeName(symbol.ContainingType);
        if (string.Equals(symbol.Name, "ProduceAsync", StringComparison.Ordinal) &&
            TryGetTaskResult(symbol.ReturnType, out var resultType) &&
            IsConstructedGeneric(resultType, "Confluent.Kafka", "DeliveryResult`2") &&
            TryGetKafkaProduceParameters(symbol, isAsync: true, out var parameters))
        {
            match = new ShapeMatch(receiver, EmittedTypeName(symbol.ReturnType, symbol), parameters, true);
            return true;
        }

        if (string.Equals(symbol.Name, "Produce", StringComparison.Ordinal) &&
            symbol.ReturnsVoid &&
            TryGetKafkaProduceParameters(symbol, isAsync: false, out parameters))
        {
            match = new ShapeMatch(receiver, "void", parameters, false);
            return true;
        }

        return false;
    }

    private static bool TryGetKafkaProduceParameters(IMethodSymbol symbol, bool isAsync, out EquatableArray<ParameterSpec> parameters)
    {
        parameters = default;
        if (symbol.Parameters.Length is not (2 or 3))
            return false;

        var firstIsTopic = IsType(symbol.Parameters[0].Type, "global::System.String") ||
                           IsType(symbol.Parameters[0].Type, "global::Confluent.Kafka.TopicPartition");
        if (!firstIsTopic || !IsConstructedGeneric(symbol.Parameters[1].Type, "Confluent.Kafka", "Message`2"))
            return false;

        if (symbol.Parameters.Length is 3)
        {
            if (isAsync)
            {
                if (!IsType(symbol.Parameters[2].Type, "global::System.Threading.CancellationToken"))
                    return false;
            }
            else if (!IsKafkaDeliveryReportHandler(symbol.Parameters[2].Type))
            {
                return false;
            }
        }

        parameters = BuildParameters(symbol);
        return true;
    }

    private static bool IsKafkaDeliveryReportHandler(ITypeSymbol? symbol)
        => symbol is INamedTypeSymbol
        {
            ConstructedFrom.MetadataName: "Action`1",
            TypeArguments.Length: 1,
        } named &&
        string.Equals(named.ConstructedFrom.ContainingNamespace.ToDisplayString(), "System", StringComparison.Ordinal) &&
        IsConstructedGeneric(named.TypeArguments[0], "Confluent.Kafka", "DeliveryReport`2");

    private static bool TryMatchKafkaConsume(IMethodSymbol symbol, out ShapeMatch match)
    {
        match = default;
        if (!IsConstructedGeneric(symbol.ReturnType, "Confluent.Kafka", "ConsumeResult`2") ||
            !TryGetKafkaConsumeParameters(symbol, out var parameters))
        {
            return false;
        }

        match = new ShapeMatch(EmittedTypeName(symbol.ContainingType), EmittedTypeName(symbol.ReturnType, symbol), parameters, false);
        return true;
    }

    private static bool TryGetKafkaConsumeParameters(IMethodSymbol symbol, out EquatableArray<ParameterSpec> parameters)
    {
        parameters = default;
        if (symbol.Parameters.Length is 0)
            return true;

        if (symbol.Parameters.Length is not 1)
            return false;

        if (IsType(symbol.Parameters[0].Type, "global::System.Threading.CancellationToken") ||
            IsType(symbol.Parameters[0].Type, "global::System.TimeSpan") ||
            symbol.Parameters[0].Type.SpecialType is SpecialType.System_Int32)
        {
            parameters = BuildParameters(symbol);
            return true;
        }

        return false;
    }

    private static bool TryMatchRedisCommand(IMethodSymbol symbol, string helperType, out ShapeMatch match)
    {
        match = default;
        if (!(IsTask(symbol.ReturnType) || TryGetTaskResult(symbol.ReturnType, out _)) ||
            !TryGetRedisCommandParameters(symbol, out var parameters) ||
            !TryGetRedisOperation(symbol.Name, parameters, out var operation))
        {
            return false;
        }

        match = new ShapeMatch(
            EmittedTypeName(symbol.ContainingType),
            EmittedTypeName(symbol.ReturnType, symbol),
            parameters,
            true,
            ShapeExpression: RedisOperationExpression(in operation, helperType));
        return true;
    }

    private static bool TryGetRedisCommandParameters(IMethodSymbol symbol, out EquatableArray<ParameterSpec> parameters)
    {
        parameters = default;
        if (!CanEmitByValueOrInParameters(symbol))
            return false;

        if (string.Equals(symbol.Name, "ExecuteAsync", StringComparison.Ordinal))
        {
            if (symbol.Parameters.Length is 0 ||
                !IsType(symbol.Parameters[0].Type, "global::System.String"))
            {
                return false;
            }

            parameters = BuildParameters(symbol);
            return true;
        }

        if (symbol.Parameters.Length is 0)
            return false;

        if (!IsType(symbol.Parameters[0].Type, "global::StackExchange.Redis.RedisKey") &&
            !IsArrayOf(symbol.Parameters[0].Type, "global::StackExchange.Redis.RedisKey"))
        {
            return false;
        }

        parameters = BuildParameters(symbol);
        return true;
    }

    /// <summary>A call-site test that selects <paramref name="Command"/> when it holds.</summary>
    private readonly record struct RedisCommandBranch(string Condition, string Command);

    /// <summary>
    /// How a StackExchange.Redis call site names the command it puts on the wire.
    /// <paramref name="Branches"/> are C# expressions over the interceptor's parameters, evaluated
    /// in order, each selecting its own command; <paramref name="Command"/> is the command reached
    /// when no branch holds. <paramref name="CommandTextParameter"/> names the parameter carrying
    /// the command text for <c>IDatabaseAsync.ExecuteAsync</c>.
    /// </summary>
    private readonly record struct RedisOperationSpec(
        string Command,
        EquatableArray<RedisCommandBranch> Branches = default,
        string CommandTextParameter = "");

    private static string RedisOperationExpression(in RedisOperationSpec operation, string helperType)
    {
        var builder = new StringBuilder();
        if (operation.CommandTextParameter.Length > 0)
        {
            builder.Append(helperType);
            builder.Append(".NormalizeCommandText(");
            builder.Append(operation.CommandTextParameter);
            builder.Append(')');
            return builder.ToString();
        }

        foreach (var branch in operation.Branches)
        {
            builder.Append(branch.Condition);
            builder.Append(" ? ");
            AppendStringLiteral(builder, branch.Command);
            builder.Append(" : ");
        }

        AppendStringLiteral(builder, operation.Command);
        return builder.ToString();
    }

    private const string RedisKeyArrayType = "global::StackExchange.Redis.RedisKey[]";
    private const string RedisValueArrayType = "global::StackExchange.Redis.RedisValue[]";
    private const string RedisHashEntryArrayType = "global::StackExchange.Redis.HashEntry[]";
    private const string RedisWhenType = "global::StackExchange.Redis.When";
    private const string RedisOrderType = "global::StackExchange.Redis.Order";
    private const string RedisExpirationType = "global::StackExchange.Redis.Expiration";

    /// <summary>
    /// Resolves the Redis command an <c>IDatabaseAsync</c> overload puts on the wire. Support and
    /// naming are the same decision, so an unmapped overload is not instrumented rather than
    /// labelled with a guessed command. Overloads that only differ on the wire by an argument
    /// <em>value</em> carry a discriminator the interceptor evaluates at the call site.
    /// Every mapping is captured from a live server by <c>demos/Qyl.RealRedisDemo</c>, which
    /// compares this name against <c>IProfiledCommand.Command</c>.
    /// </summary>
    private static bool TryGetRedisOperation(
        string methodName,
        EquatableArray<ParameterSpec> parameters,
        out RedisOperationSpec operation)
    {
        operation = default;

        switch (methodName)
        {
            case "ExecuteAsync":
                operation = new RedisOperationSpec(string.Empty, CommandTextParameter: parameters[0].Name);
                return true;

            case "StringGetAsync":
                operation = new RedisOperationSpec(IsRedisParameter(parameters, 0, RedisKeyArrayType) ? "MGET" : "GET");
                return true;
            case "StringSetAsync":
                // ValueCondition.NotExists reaches SETNX, but ValueCondition carries neither an
                // equality operator nor IEquatable, so testing for it at the call site would box
                // on every intercepted SET, including the calls that have tracing switched off.
                // SET is reported for the whole method rather than charging every caller for the
                // one branch that differs.
                operation = new RedisOperationSpec("SET");
                return true;
            case "StringIncrementAsync":
                return TryGetRedisIncrementOperation(parameters, decrements: false, out operation);
            case "StringDecrementAsync":
                return TryGetRedisIncrementOperation(parameters, decrements: true, out operation);
            case "StringAppendAsync":
                operation = new RedisOperationSpec("APPEND");
                return true;
            case "StringLengthAsync":
                operation = new RedisOperationSpec("STRLEN");
                return true;
            case "StringGetRangeAsync":
                operation = new RedisOperationSpec("GETRANGE");
                return true;
            case "StringSetRangeAsync":
                operation = new RedisOperationSpec("SETRANGE");
                return true;
            case "StringGetSetAsync":
                operation = new RedisOperationSpec("GETSET");
                return true;
            case "StringGetDeleteAsync":
                operation = new RedisOperationSpec("GETDEL");
                return true;
            case "StringGetSetExpiryAsync":
                operation = new RedisOperationSpec("GETEX");
                return true;
            case "StringGetBitAsync":
                operation = new RedisOperationSpec("GETBIT");
                return true;
            case "StringSetBitAsync":
                operation = new RedisOperationSpec("SETBIT");
                return true;

            case "HashGetAsync":
                operation = new RedisOperationSpec(IsRedisParameter(parameters, 1, RedisValueArrayType) ? "HMGET" : "HGET");
                return true;
            case "HashSetAsync":
                operation = IsRedisParameter(parameters, 1, RedisHashEntryArrayType)
                    ? new RedisOperationSpec("HMSET")
                    : RedisWhenOperation(parameters, "HSET", "HSETNX", "NotExists");
                return true;
            case "HashDeleteAsync":
                operation = new RedisOperationSpec("HDEL");
                return true;
            case "HashExistsAsync":
                operation = new RedisOperationSpec("HEXISTS");
                return true;
            case "HashGetAllAsync":
                operation = new RedisOperationSpec("HGETALL");
                return true;
            case "HashKeysAsync":
                operation = new RedisOperationSpec("HKEYS");
                return true;
            case "HashValuesAsync":
                operation = new RedisOperationSpec("HVALS");
                return true;
            case "HashLengthAsync":
                operation = new RedisOperationSpec("HLEN");
                return true;
            case "HashStringLengthAsync":
                operation = new RedisOperationSpec("HSTRLEN");
                return true;
            case "HashRandomFieldAsync":
                operation = new RedisOperationSpec("HRANDFIELD");
                return true;
            case "HashIncrementAsync":
            case "HashDecrementAsync":
                operation = new RedisOperationSpec(IsRedisParameter(parameters, 2, "double") ? "HINCRBYFLOAT" : "HINCRBY");
                return true;

            case "KeyDeleteAsync":
                operation = new RedisOperationSpec("DEL");
                return true;
            case "KeyExistsAsync":
                operation = new RedisOperationSpec("EXISTS");
                return true;
            case "KeyExpireAsync":
                return TryGetRedisExpireOperation(parameters, out operation);
            case "KeyTimeToLiveAsync":
                operation = new RedisOperationSpec("PTTL");
                return true;
            case "KeyPersistAsync":
                operation = new RedisOperationSpec("PERSIST");
                return true;
            case "KeyTypeAsync":
                operation = new RedisOperationSpec("TYPE");
                return true;
            case "KeyRenameAsync":
                operation = RedisWhenOperation(parameters, "RENAME", "RENAMENX", "NotExists");
                return true;
            case "KeyTouchAsync":
                operation = new RedisOperationSpec("TOUCH");
                return true;
            case "KeyDumpAsync":
                operation = new RedisOperationSpec("DUMP");
                return true;
            case "KeyCopyAsync":
                operation = new RedisOperationSpec("COPY");
                return true;
            case "KeyMoveAsync":
                operation = new RedisOperationSpec("MOVE");
                return true;
            case "KeyIdleTimeAsync":
                operation = new RedisOperationSpec("OBJECT");
                return true;

            case "ListLeftPushAsync":
                operation = RedisWhenOperation(parameters, "LPUSH", "LPUSHX", "Exists");
                return true;
            case "ListRightPushAsync":
                operation = RedisWhenOperation(parameters, "RPUSH", "RPUSHX", "Exists");
                return true;
            case "ListLeftPopAsync":
                return TryGetRedisSingleKeyOperation(parameters, "LPOP", out operation);
            case "ListRightPopAsync":
                return TryGetRedisSingleKeyOperation(parameters, "RPOP", out operation);
            case "ListLengthAsync":
                operation = new RedisOperationSpec("LLEN");
                return true;
            case "ListRangeAsync":
                operation = new RedisOperationSpec("LRANGE");
                return true;
            case "ListGetByIndexAsync":
                operation = new RedisOperationSpec("LINDEX");
                return true;
            case "ListRemoveAsync":
                operation = new RedisOperationSpec("LREM");
                return true;
            case "ListSetByIndexAsync":
                operation = new RedisOperationSpec("LSET");
                return true;
            case "ListTrimAsync":
                operation = new RedisOperationSpec("LTRIM");
                return true;
            case "ListInsertBeforeAsync":
            case "ListInsertAfterAsync":
                operation = new RedisOperationSpec("LINSERT");
                return true;
            case "ListPositionAsync":
                operation = new RedisOperationSpec("LPOS");
                return true;
            case "ListMoveAsync":
                operation = new RedisOperationSpec("LMOVE");
                return true;
            case "ListRightPopLeftPushAsync":
                operation = new RedisOperationSpec("RPOPLPUSH");
                return true;

            case "SetAddAsync":
                operation = new RedisOperationSpec("SADD");
                return true;
            case "SetRemoveAsync":
                operation = new RedisOperationSpec("SREM");
                return true;
            case "SetContainsAsync":
                operation = new RedisOperationSpec(IsRedisParameter(parameters, 1, RedisValueArrayType) ? "SMISMEMBER" : "SISMEMBER");
                return true;
            case "SetMembersAsync":
                operation = new RedisOperationSpec("SMEMBERS");
                return true;
            case "SetLengthAsync":
                operation = new RedisOperationSpec("SCARD");
                return true;
            case "SetPopAsync":
                operation = new RedisOperationSpec("SPOP");
                return true;
            case "SetRandomMemberAsync":
            case "SetRandomMembersAsync":
                operation = new RedisOperationSpec("SRANDMEMBER");
                return true;
            case "SetMoveAsync":
                operation = new RedisOperationSpec("SMOVE");
                return true;

            case "SortedSetAddAsync":
                operation = new RedisOperationSpec("ZADD");
                return true;
            case "SortedSetRemoveAsync":
                operation = new RedisOperationSpec("ZREM");
                return true;
            case "SortedSetIncrementAsync":
            case "SortedSetDecrementAsync":
                operation = new RedisOperationSpec("ZINCRBY");
                return true;
            case "SortedSetLengthAsync":
                return TryGetRedisSortedSetLengthOperation(parameters, out operation);
            case "SortedSetLengthByValueAsync":
                operation = new RedisOperationSpec("ZLEXCOUNT");
                return true;
            case "SortedSetScoreAsync":
                operation = new RedisOperationSpec("ZSCORE");
                return true;
            case "SortedSetScoresAsync":
                operation = new RedisOperationSpec("ZMSCORE");
                return true;
            case "SortedSetRankAsync":
                operation = RedisOrderOperation(parameters, "ZRANK", "ZREVRANK");
                return true;
            case "SortedSetRangeByRankAsync":
            case "SortedSetRangeByRankWithScoresAsync":
                operation = RedisOrderOperation(parameters, "ZRANGE", "ZREVRANGE");
                return true;
            case "SortedSetRangeByScoreAsync":
            case "SortedSetRangeByScoreWithScoresAsync":
                operation = RedisOrderOperation(parameters, "ZRANGEBYSCORE", "ZREVRANGEBYSCORE");
                return true;
            case "SortedSetRangeByValueAsync":
                operation = RedisOrderOperation(parameters, "ZRANGEBYLEX", "ZREVRANGEBYLEX");
                return true;
            case "SortedSetRemoveRangeByRankAsync":
                operation = new RedisOperationSpec("ZREMRANGEBYRANK");
                return true;
            case "SortedSetRemoveRangeByScoreAsync":
                operation = new RedisOperationSpec("ZREMRANGEBYSCORE");
                return true;
            case "SortedSetRemoveRangeByValueAsync":
                operation = new RedisOperationSpec("ZREMRANGEBYLEX");
                return true;
            case "SortedSetPopAsync":
                if (IsRedisParameter(parameters, 0, RedisKeyArrayType))
                    return false;

                operation = RedisOrderOperation(parameters, "ZPOPMIN", "ZPOPMAX");
                return true;
            case "SortedSetRandomMemberAsync":
                operation = new RedisOperationSpec("ZRANDMEMBER");
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// StackExchange.Redis sends a decrement as the increment by the negated value, collapses an
    /// increment of exactly one or minus one onto INCR or DECR, sends DECRBY for every other
    /// negative increment and INCRBY otherwise, and routes the floating-point overload through
    /// <c>INCRBYFLOAT</c> in both directions.
    /// </summary>
    private static bool TryGetRedisIncrementOperation(
        EquatableArray<ParameterSpec> parameters,
        bool decrements,
        out RedisOperationSpec operation)
    {
        operation = default;

        // The bounded-increment overloads are marked experimental upstream and reach a wire
        // command this table has not pinned against a live server.
        if (IndexOfRedisParameter(parameters, RedisExpirationType) >= 0)
            return false;

        if (IsRedisParameter(parameters, 1, "double"))
        {
            operation = new RedisOperationSpec("INCRBYFLOAT");
            return true;
        }

        if (!IsRedisParameter(parameters, 1, "long"))
            return false;

        var value = parameters[1].Name;
        operation = new RedisOperationSpec(
            "INCRBY",
            RedisBranches(
                new RedisCommandBranch(value + " == 1L", decrements ? "DECR" : "INCR"),
                new RedisCommandBranch(value + " == -1L", decrements ? "INCR" : "DECR"),
                new RedisCommandBranch(value + (decrements ? " > 0L" : " < 0L"), "DECRBY")));
        return true;
    }

    /// <summary>StackExchange.Redis sends ZCARD for the unbounded range and ZCOUNT for any other.</summary>
    private static bool TryGetRedisSortedSetLengthOperation(
        EquatableArray<ParameterSpec> parameters,
        out RedisOperationSpec operation)
    {
        operation = default;
        if (!IsRedisParameter(parameters, 1, "double") || !IsRedisParameter(parameters, 2, "double"))
            return false;

        operation = new RedisOperationSpec(
            "ZCOUNT",
            RedisBranches(new RedisCommandBranch(
                "global::System.Double.IsNegativeInfinity(" + parameters[1].Name + ") && global::System.Double.IsPositiveInfinity(" + parameters[2].Name + ")",
                "ZCARD")));
        return true;
    }

    /// <summary>Excludes the multi-key overloads, which reach a different wire command.</summary>
    private static bool TryGetRedisSingleKeyOperation(
        EquatableArray<ParameterSpec> parameters,
        string command,
        out RedisOperationSpec operation)
    {
        operation = default;
        if (IsRedisParameter(parameters, 0, RedisKeyArrayType))
            return false;

        operation = new RedisOperationSpec(command);
        return true;
    }

    private static RedisOperationSpec RedisWhenOperation(
        EquatableArray<ParameterSpec> parameters,
        string command,
        string alternateCommand,
        string whenMember)
        => RedisDiscriminatedOperation(parameters, RedisWhenType, whenMember, command, alternateCommand);

    private static RedisOperationSpec RedisOrderOperation(
        EquatableArray<ParameterSpec> parameters,
        string ascendingCommand,
        string descendingCommand)
        => RedisDiscriminatedOperation(parameters, RedisOrderType, "Descending", ascendingCommand, descendingCommand);

    /// <summary>
    /// Builds the call-site test that selects <paramref name="alternateCommand"/>. The test is an
    /// enum comparison, which the call site evaluates without allocating. An overload without the
    /// discriminating parameter always reaches <paramref name="command"/>.
    /// </summary>
    private static RedisOperationSpec RedisDiscriminatedOperation(
        EquatableArray<ParameterSpec> parameters,
        string enumTypeName,
        string enumMember,
        string command,
        string alternateCommand)
    {
        var index = IndexOfRedisParameter(parameters, enumTypeName);
        return index < 0
            ? new RedisOperationSpec(command)
            : new RedisOperationSpec(
                command,
                RedisBranches(new RedisCommandBranch(
                    parameters[index].Name + " == " + enumTypeName + "." + enumMember,
                    alternateCommand)));
    }

    /// <summary>
    /// A null or MaxValue expiry reaches PERSIST, and StackExchange.Redis picks the second-precision command
    /// when the value carries no whole milliseconds — <c>TimeSpan.FromTicks(TimeSpan.TicksPerSecond + 1)</c>
    /// still reaches EXPIRE, so the test is the millisecond component rather than the tick
    /// remainder. Both tests read the argument the call site already holds.
    /// </summary>
    private static bool TryGetRedisExpireOperation(
        EquatableArray<ParameterSpec> parameters,
        out RedisOperationSpec operation)
    {
        operation = default;
        if (parameters.Length < 2)
            return false;

        var expiry = parameters[1].Name;
        switch (parameters[1].MatchTypeName)
        {
            case "global::System.TimeSpan?":
                operation = new RedisOperationSpec(
                    "PEXPIRE",
                    RedisBranches(
                        new RedisCommandBranch(expiry + " is null || " + expiry + ".Value == global::System.TimeSpan.MaxValue", "PERSIST"),
                        new RedisCommandBranch(expiry + ".Value.Milliseconds == 0", "EXPIRE")));
                return true;
            case "global::System.DateTime?":
                operation = new RedisOperationSpec(
                    "PEXPIREAT",
                    RedisBranches(
                        new RedisCommandBranch(expiry + " is null || " + expiry + ".Value == global::System.DateTime.MaxValue", "PERSIST"),
                        new RedisCommandBranch(expiry + ".Value.Millisecond == 0", "EXPIREAT")));
                return true;
            default:
                return false;
        }
    }

    private static EquatableArray<RedisCommandBranch> RedisBranches(params RedisCommandBranch[] branches)
        => ImmutableArray.Create(branches).AsEquatableArray();

    private static bool IsRedisParameter(EquatableArray<ParameterSpec> parameters, int index, string typeName)
        => index < parameters.Length &&
           string.Equals(parameters[index].MatchTypeName, typeName, StringComparison.Ordinal);

    private static int IndexOfRedisParameter(EquatableArray<ParameterSpec> parameters, string typeName)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (string.Equals(parameters[index].MatchTypeName, typeName, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static bool CanEmitByValueOrInParameters(IMethodSymbol symbol)
    {
        foreach (var parameter in symbol.Parameters)
        {
            if (parameter.RefKind is not RefKind.None and not RefKind.In)
                return false;
        }

        return true;
    }

    private static IMethodSymbol GetGenericMethodForEmission(IMethodSymbol symbol)
        => symbol.TypeParameters.Length > 0
            ? symbol
            : symbol.ReducedFrom is { TypeParameters.Length: > 0 } reducedFrom
                ? reducedFrom
                : symbol;

    private static EquatableArray<ParameterSpec> BuildParameters(IMethodSymbol symbol)
    {
        // C# binds optional defaults at the original call before redirecting it to the interceptor.
        var builder = ImmutableArray.CreateBuilder<ParameterSpec>(symbol.Parameters.Length);
        for (var i = 0; i < symbol.Parameters.Length; i++)
        {
            builder.Add(new ParameterSpec(
                EmittedTypeName(symbol.Parameters[i].Type, symbol),
                CleanTypeName(symbol.Parameters[i].Type),
                "p" + i.ToString(CultureInfo.InvariantCulture),
                symbol.Parameters[i].IsParams,
                symbol.Parameters[i].RefKind));
        }

        return builder.ToImmutable().AsEquatableArray();
    }

    private static bool IsConstructedGeneric(ITypeSymbol? symbol, string namespaceName, string metadataName)
        => symbol is INamedTypeSymbol named &&
           string.Equals(named.ConstructedFrom.MetadataName, metadataName, StringComparison.Ordinal) &&
           string.Equals(named.ConstructedFrom.ContainingNamespace.ToDisplayString(), namespaceName, StringComparison.Ordinal);

    private static bool IsArrayOf(ITypeSymbol? symbol, string elementFullyQualifiedName)
        => symbol is IArrayTypeSymbol array && IsType(array.ElementType, elementFullyQualifiedName);

    private static bool IsTask(ITypeSymbol? symbol)
        => IsType(symbol, "global::System.Threading.Tasks.Task");

    private static bool TryGetTaskResult(ITypeSymbol? symbol, out ITypeSymbol result)
    {
        result = null!;
        if (symbol is not INamedTypeSymbol named ||
            !IsType(named.ConstructedFrom, "global::System.Threading.Tasks.Task<TResult>") ||
            named.TypeArguments.Length is not 1)
        {
            return false;
        }

        result = named.TypeArguments[0];
        return true;
    }

    private static bool InheritsFromOrIs(ITypeSymbol? symbol, string fullyQualifiedName)
    {
        for (var current = symbol; current is not null; current = (current as INamedTypeSymbol)?.BaseType)
        {
            if (IsType(current, fullyQualifiedName))
                return true;
        }

        return false;
    }

    private static bool IsOrDerivesOrImplements(ITypeSymbol? symbol, string namespaceName, string metadataName)
    {
        if (symbol is null)
            return false;

        for (var current = symbol; current is not null; current = current.BaseType)
        {
            if (current is INamedTypeSymbol named && IsTypeByMetadata(named.OriginalDefinition, namespaceName, metadataName))
                return true;
        }

        foreach (var interfaceType in symbol.AllInterfaces)
        {
            if (IsTypeByMetadata(interfaceType.OriginalDefinition, namespaceName, metadataName))
                return true;
        }

        return false;
    }

    private static bool IsTypeByMetadata(INamedTypeSymbol symbol, string namespaceName, string metadataName)
        => string.Equals(symbol.MetadataName, metadataName, StringComparison.Ordinal) &&
           string.Equals(symbol.ContainingNamespace.ToDisplayString(), namespaceName, StringComparison.Ordinal);

    private static bool IsType(ITypeSymbol? symbol, string fullyQualifiedName)
    {
        if (symbol is null)
            return false;

        if (fullyQualifiedName is "global::System.String")
            return symbol.SpecialType is SpecialType.System_String;

        if (fullyQualifiedName is "global::System.Byte[]")
            return symbol is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte, Rank: 1 };

        if (fullyQualifiedName is "global::System.Object")
            return symbol.SpecialType is SpecialType.System_Object;

        if (fullyQualifiedName is "global::System.Int32")
            return symbol.SpecialType is SpecialType.System_Int32;

        var display = CleanTypeName(symbol);
        return string.Equals(display, fullyQualifiedName, StringComparison.Ordinal);
    }

    private static string CleanTypeName(ITypeSymbol symbol)
        => symbol.ToDisplayString(FullyQualifiedFormat);

    private static string EmittedTypeName(ITypeSymbol symbol)
        => symbol.ToDisplayString(EmittedTypeFormat);

    private static string EmittedTypeName(ITypeSymbol symbol, IMethodSymbol method)
    {
        var genericMethod = GetGenericMethodForEmission(method);
        return genericMethod.TypeParameters.Length is 0
            ? EmittedTypeName(symbol)
            : EmittedTypeName(symbol, genericMethod.TypeParameters, genericMethod.TypeArguments);
    }

    private static string EmittedTypeName(
        ITypeSymbol symbol,
        ImmutableArray<ITypeParameterSymbol> typeParameters,
        ImmutableArray<ITypeSymbol> typeArguments)
    {
        for (var i = 0; i < typeArguments.Length && i < typeParameters.Length; i++)
        {
            if (symbol.IsEqualTo(typeArguments[i]))
                return typeParameters[i].Name + NullableReferenceSuffix(symbol);
        }

        switch (symbol)
        {
            case ITypeParameterSymbol typeParameter:
                return typeParameter.Name + NullableReferenceSuffix(typeParameter);
            case IArrayTypeSymbol array:
                return EmittedTypeName(array.ElementType, typeParameters, typeArguments) +
                       "[" + new string(',', array.Rank - 1) + "]" + NullableReferenceSuffix(array);
            case INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: > 0 } named:
            {
                var constructedName = named.ConstructedFrom.ToDisplayString(FullyQualifiedFormat);
                var genericStart = constructedName.IndexOf('<');
                var typeName = genericStart < 0 ? constructedName : constructedName.Substring(0, genericStart);
                var arguments = named.TypeArguments
                    .Select(typeArgument => EmittedTypeName(typeArgument, typeParameters, typeArguments));
                return typeName + "<" + string.Join(", ", arguments) + ">" + NullableReferenceSuffix(named);
            }
            default:
                return EmittedTypeName(symbol);
        }
    }

    private static string NullableReferenceSuffix(ITypeSymbol symbol)
        => !symbol.IsValueType && symbol.NullableAnnotation is NullableAnnotation.Annotated ? "?" : string.Empty;

    private static string QuoteLiteral(string value)
        => SymbolDisplay.FormatLiteral(value, quote: true);

    private static void AppendStringLiteral(StringBuilder builder, string value)
        => builder.Append(QuoteLiteral(value));
}

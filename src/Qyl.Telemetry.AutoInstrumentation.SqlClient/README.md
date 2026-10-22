# Qyl.Telemetry.AutoInstrumentation.SqlClient

Zero-code, AOT-native instrumentation for `Microsoft.Data.SqlClient`, subscribed over its public
`DiagnosticSource` events. No IL rewriting, no profiler.

```bash
dotnet add package Qyl.Telemetry.AutoInstrumentation.SqlClient
```

With [`Qyl.Telemetry.Hosting`](https://www.nuget.org/packages/Qyl.Telemetry.Hosting) and its
`builder.AddQyl()` in the application, adding this package is the whole integration: the
`ModuleInitializer` bootstrap activates the listener when the assembly loads.

## Controls

| Variable | Effect |
| --- | --- |
| `OTEL_DOTNET_AUTO_TRACES_SQLCLIENT_INSTRUMENTATION_ENABLED` | `false` turns the instrumentation off (default on) |
| `OTEL_DOTNET_AUTO_SQLCLIENT_SET_DBSTATEMENT_FOR_TEXT` | `true` records the statement text on the span (default off) |

Every attribute is a generated constant from
[`Qyl.Telemetry.SemanticConventions`](https://www.nuget.org/packages/Qyl.Telemetry.SemanticConventions);
the instrumentation never renames or drops what SqlClient emitted.

## More

- The full control contract with evidence:
  [resolved contract](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/contracts/generated/qyl-aot-contract.resolved.yaml)
- Changes per version:
  [CHANGELOG](https://github.com/ANcpLua/Qyl.Telemetry.AutoInstrumentation/blob/main/CHANGELOG.md)

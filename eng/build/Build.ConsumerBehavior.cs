namespace Qyl.Build;

sealed partial class QylBuild
{
    Target ConsumerBehavior => definition => definition
        .Description("Consumer behavior: package consumers, generator compilation, AddQyl, native scopes, capture arrays, kill switches, middleware, parity and NativeAOT")
        .After(PackageLayout)
        .Executes(() =>
        {
            ProjectReferenceBehavior.Run();
            EnvironmentOptionsBehavior.Run();
            AddQylBehavior.Run();
            ApplicationScopeBehavior.Run();
            InstrumentationDisabledBehavior.Run();
            AspNetCoreMiddlewareDelegate.Run();
            SourceInterceptorConsumer.Run();
            ConsumerParity.Run();
            NativeAotConsumer.Run();
            Shell.Checked("dotnet",
                ["test", "--project", Repo.IntegrationTests, "--configuration", "Release", "--no-build", "--no-restore", "--",
                    "--filter", string.Join('|',
                        "FullyQualifiedName~CapturedHeaderTests",
                        "FullyQualifiedName~ClientHeaderCaptureTests",
                        "FullyQualifiedName~ClientMetadataCaptureTests",
                        "FullyQualifiedName~GeneratorContractTests",
                        "FullyQualifiedName~DbCommandInterceptorFailureTests",
                        "FullyQualifiedName~CollectorEndpointTests",
                        "FullyQualifiedName~RedisCommandNameTests",
                        "FullyQualifiedName~WcfClientInterceptionTests",
                        "FullyQualifiedName~DbQuerySummaryCommentTests",
                        "FullyQualifiedName~DbQuerySummaryTargetTests",
                        "FullyQualifiedName~HttpMethodSemanticsTests",
                        "FullyQualifiedName~KafkaPartitionTests",
                        "FullyQualifiedName~GenAiOptInDiagnosticTests",
                        "FullyQualifiedName~DiagnosticListenerSubscriberTests",
                        "FullyQualifiedName~CollectorDiscoveryTests",
                        "FullyQualifiedName~SessionBaggageTests",
                        "FullyQualifiedName~DatabaseDiagnosticListenerTests",
                        "FullyQualifiedName~DbCommandNativeSourceReceiverTests",
                        "FullyQualifiedName~DbCommandObservationTests",
                        "FullyQualifiedName~InstrumentationBootTests",
                        "FullyQualifiedName~HostConfigurationTests")],
                Repo.Root, echo: true);
            Log.Information("consumer-behavior-gate-ok");
        });
}

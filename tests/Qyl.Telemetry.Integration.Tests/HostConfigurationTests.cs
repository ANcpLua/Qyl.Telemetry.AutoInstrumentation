using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Qyl.Telemetry.AutoInstrumentation;
using SessionAttributes = Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Session.SessionAttributes;

namespace Qyl.Telemetry.Integration.Tests;

[TestClass]
public sealed class HostConfigurationTests(TestContext testContext)
{
    private const string SessionId = "checkout-session";

    [TestMethod]
    public void AddQylAspNetCoreInstrumentation_FirstCall_ReturnsTheSameCollection()
    {
        var services = new ServiceCollection();

        Assert.AreSame(services, services.AddQylAspNetCoreInstrumentation());
    }

    [TestMethod]
    public void AddQylAspNetCoreInstrumentation_FirstCall_RegistersOneStartupFilter()
    {
        var services = new ServiceCollection();

        services.AddQylAspNetCoreInstrumentation();

        Assert.ContainsSingle(services.Where(static service => service.ServiceType == typeof(IStartupFilter)));
    }

    [TestMethod]
    public void AddQylAspNetCoreInstrumentation_SecondCall_ReturnsTheSameCollection()
    {
        var services = new ServiceCollection();
        services.AddQylAspNetCoreInstrumentation();

        Assert.AreSame(services, services.AddQylAspNetCoreInstrumentation());
    }

    [TestMethod]
    public void AddQylAspNetCoreInstrumentation_SecondCall_KeepsTheFirstStartupFilter()
    {
        var services = new ServiceCollection();
        services.AddQylAspNetCoreInstrumentation();
        var first = services.Single(static service => service.ServiceType == typeof(IStartupFilter));

        services.AddQylAspNetCoreInstrumentation();

        Assert.AreSame(first, Assert.ContainsSingle(services.Where(static service => service.ServiceType == typeof(IStartupFilter))));
    }

    [TestMethod]
    [DataRow("service.namespace", "shop")]
    [DataRow("deployment.environment.name", "preview")]
    public async Task AddQyl_ExplicitResourceIdentity_SetsTheResourceAttribute(string attribute, string value)
    {
        using var host = await StartInMemoryHostAsync("Shop.Resource." + Guid.NewGuid().ToString("N"), [], ConfigureIdentity);

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        Assert.AreEqual(value, resource[attribute]);
        await host.StopAsync(testContext.CancellationToken);
    }

    [TestMethod]
    public async Task AddQyl_MetricExportDisabled_RegistersNoMeterProvider()
    {
        using var host = await StartInMemoryHostAsync("Shop.Resource." + Guid.NewGuid().ToString("N"), [], ConfigureIdentity);

        Assert.IsNull(host.Services.GetService<MeterProvider>());
        await host.StopAsync(testContext.CancellationToken);
    }

    [TestMethod]
    public async Task AddQyl_ExplicitResourceIdentity_ExportsTheAdditionalSourcesSpan()
    {
        var captured = new List<Activity>();
        var sourceName = "Shop.Resource." + Guid.NewGuid().ToString("N");
        using var host = await StartInMemoryHostAsync(sourceName, captured, ConfigureIdentity);

        using (var source = new ActivitySource(sourceName))
        using (source.CreateActivity("configured.operation", ActivityKind.Internal)?.Start())
        {
        }
        var flushed = host.Services.GetRequiredService<TracerProvider>().ForceFlush();

        Assert.IsTrue(flushed);
        Assert.AreEqual("configured.operation", Assert.ContainsSingle(captured).OperationName);
        await host.StopAsync(testContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SessionTagInheritance_EitherOption_ExportsParentAndChild(bool enabled)
    {
        var captured = await RunSessionParentAndChildAsync(enabled);

        Assert.AreSequenceEqual(["child", "parent"], captured.Select(static span => span.OperationName));
    }

    [TestMethod]
    [DataRow(true, SessionId)]
    [DataRow(false, null)]
    public async Task SessionTagInheritance_ExplicitOption_DecidesTheChildSession(bool enabled, string? childSession)
    {
        var captured = await RunSessionParentAndChildAsync(enabled);

        Assert.AreEqual(childSession, captured.Single(static span => span.OperationName == "child").GetTagItem(SessionAttributes.Id));
    }

    private static void ConfigureIdentity(QylTelemetryOptions options)
    {
        options.ServiceName = "checkout-config";
        options.ServiceNamespace = "shop";
        options.DeploymentEnvironment = "preview";
    }

    /// <summary>Starts a parent, a child beneath it, then tags only the parent with a session; returns the flushed export.</summary>
    private async Task<List<Activity>> RunSessionParentAndChildAsync(bool enabled)
    {
        var captured = new List<Activity>();
        var sourceName = "Shop.Session." + Guid.NewGuid().ToString("N");
        using var host = await StartInMemoryHostAsync(sourceName, captured, options => options.EnableSessionTagInheritance = enabled);
        using (var source = new ActivitySource(sourceName))
        {
            using var parent = source.CreateActivity("parent", ActivityKind.Internal)?.Start()
                               ?? throw new InvalidOperationException($"AddQyl did not subscribe to {sourceName}.");
            using var child = source.CreateActivity("child", ActivityKind.Internal)?.Start();
            parent.SetTag(SessionAttributes.Id, SessionId);
        }
        if (!host.Services.GetRequiredService<TracerProvider>().ForceFlush())
            throw new InvalidOperationException("The tracer provider did not flush.");
        await host.StopAsync(testContext.CancellationToken);
        return captured;
    }

    private async Task<IHost> StartInMemoryHostAsync(string sourceName, List<Activity> captured, Action<QylTelemetryOptions>? configure = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddQyl(options =>
        {
            options.EnableCollectorDiscovery = false;
            options.RequireConfiguredEndpoint = true;
            options.EnableLogExport = false;
            options.EnableMetricsExport = false;
            options.AdditionalSources.Add(sourceName);
            options.ConfigureTracing = tracing => tracing.AddInMemoryExporter(captured);
            configure?.Invoke(options);
        });
        var host = builder.Build();
        await host.StartAsync(testContext.CancellationToken);
        return host;
    }
}

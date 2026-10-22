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
    [TestMethod]
    public void AspNetCore_registration_is_fluent_and_idempotent()
    {
        var services = new ServiceCollection();
        Assert.AreSame(services, services.AddQylAspNetCoreInstrumentation());
        var first = Assert.ContainsSingle(services.Where(static service => service.ServiceType == typeof(IStartupFilter)));
        Assert.AreSame(services, services.AddQylAspNetCoreInstrumentation());
        Assert.AreSame(first, Assert.ContainsSingle(services.Where(static service => service.ServiceType == typeof(IStartupFilter))));
    }

    [TestMethod]
    public async Task Explicit_resource_identity_and_disabled_metric_export_are_honored()
    {
        var captured = new List<Activity>();
        var sourceName = "Shop.Resource." + Guid.NewGuid().ToString("N");
        using var host = await StartInMemoryHostAsync(sourceName, captured, static options =>
        {
            options.ServiceName = "checkout-config";
            options.ServiceNamespace = "shop";
            options.DeploymentEnvironment = "preview";
        });
        var provider = host.Services.GetRequiredService<TracerProvider>();
        var resource = provider.GetResource().Attributes.ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value);
        Assert.AreEqual("shop", resource["service.namespace"]);
        Assert.AreEqual("preview", resource["deployment.environment.name"]);
        Assert.IsNull(host.Services.GetService<MeterProvider>());
        using (var source = new ActivitySource(sourceName))
        {
            using var span = source.CreateActivity("configured.operation", ActivityKind.Internal)?.Start();
            Assert.IsNotNull(span);
        }
        Assert.IsTrue(provider.ForceFlush());
        Assert.AreEqual("configured.operation", Assert.ContainsSingle(captured).OperationName);
        await host.StopAsync(testContext.CancellationToken);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Session_inheritance_follows_the_explicit_option(bool enabled)
    {
        var captured = new List<Activity>();
        var sourceName = "Shop.Session." + Guid.NewGuid().ToString("N");
        using var host = await StartInMemoryHostAsync(sourceName, captured, options => options.EnableSessionTagInheritance = enabled);
        using (var source = new ActivitySource(sourceName))
        {
            using var parent = source.CreateActivity("parent", ActivityKind.Internal)?.Start();
            Assert.IsNotNull(parent);
            using var child = source.CreateActivity("child", ActivityKind.Internal)?.Start();
            Assert.IsNotNull(child);
            parent.SetTag(SessionAttributes.Id, "checkout-session");
        }
        Assert.IsTrue(host.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.HasCount(2, captured);
        var exportedChild = Assert.ContainsSingle(captured.Where(static span => span.OperationName == "child"));
        Assert.AreEqual(enabled ? "checkout-session" : null, exportedChild.GetTagItem(SessionAttributes.Id));
        await host.StopAsync(testContext.CancellationToken);
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

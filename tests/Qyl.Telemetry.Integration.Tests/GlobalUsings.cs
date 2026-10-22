global using Qyl.Build.AotPublishGate;
global using Qyl.Build.Demos;
global using Qyl.Build.Infrastructure;
global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using Serilog;

// The applications bind ports and the AOT publisher owns a shared build cache.
[assembly: DoNotParallelize]
// The demo lanes share internal run types, so their test classes are internal.
[assembly: DiscoverInternals]

using System.Diagnostics;

namespace Qyl.Telemetry.AutoInstrumentation.DiagnosticListeners;

/// <summary>
/// Base subscriber for built-in <see cref="DiagnosticListener"/> events. DiagnosticSource is a
/// managed BCL primitive, so this layer emits spans without IL rewriting or runtime code generation.
/// Completion-driven subscribers use the operation's reported start and end times so emitted
/// duration reflects the real operation.
/// </summary>
public abstract class QylDiagnosticListenerSubscriber : IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _eventSubscriptions = [];
    private IDisposable? _allListenersSubscription;
    private bool _disposed;

    /// <summary>The well-known <see cref="DiagnosticListener"/> name to subscribe to
    /// (e.g. <c>HttpHandlerDiagnosticListener</c>, <c>Microsoft.AspNetCore</c>).</summary>
    protected abstract string ListenerName { get; }

    /// <summary>The signal controlled by the OTEL_DOTNET_AUTO_* env var family.</summary>
    protected abstract QylAutoInstrumentationSignal Signal { get; }

    /// <summary>The instrumentation id used in OTEL_DOTNET_AUTO_{SIGNAL}_{ID}_INSTRUMENTATION_ENABLED.</summary>
    protected abstract string InstrumentationId { get; }

    /// <summary>Subscribe to <see cref="DiagnosticListener.AllListeners"/> and wait for the
    /// target listener to appear. Idempotent; safe under <c>[ModuleInitializer]</c>.</summary>
    public void Subscribe()
    {
        if (!QylAutoInstrumentationOptions.Current.IsInstrumentationEnabled(Signal, InstrumentationId))
            return;

        _allListenersSubscription ??= DiagnosticListener.AllListeners.Subscribe(new AllListenersObserver(this));
    }

    /// <summary>Implement to react to a single event from the subscribed listener.</summary>
    protected abstract void OnEvent(string name, object? payload);

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (QylAutoInstrumentationOptions.Current.IsInstrumentationEnabled(Signal, InstrumentationId))
            OnEvent(value.Key, value.Value);
    }

    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { /* invisible */ }

    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { /* invisible */ }

    /// <summary>Tear down the listener-discovery subscription and every listener subscription.
    /// Listener names are not unique (EF Core creates one listener per internal service provider),
    /// so there can be several.</summary>
    public void Dispose()
    {
        _allListenersSubscription?.Dispose();
        IDisposable[] eventSubscriptions;
        lock (_gate)
        {
            _disposed = true;
            eventSubscriptions = [.. _eventSubscriptions];
            _eventSubscriptions.Clear();
        }

        foreach (var subscription in eventSubscriptions)
            subscription.Dispose();

        GC.SuppressFinalize(this);
    }

    private void Track(IDisposable eventSubscription)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _eventSubscriptions.Add(eventSubscription);
                return;
            }
        }

        eventSubscription.Dispose();
    }

    private sealed class AllListenersObserver(QylDiagnosticListenerSubscriber owner) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == owner.ListenerName)
                owner.Track(value.Subscribe(owner));
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}

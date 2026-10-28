using Avalonia.Logging;

namespace WheelWizard.UI.Test;

/// <summary>
/// Collects the warnings Avalonia logs for bindings. A binding that cannot resolve only logs a warning and shows nothing,
/// so it goes unnoticed. Fields that are empty while data is still loading ("Value is null.") are expected and left out.
/// </summary>
internal sealed class BindingWarningSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous = Logger.Sink;
    private readonly List<string> _messages = [];

    public BindingWarningSink() => Logger.Sink = this;

    /// <summary>Bindings that could not reach their property (no such property, wrong data context, failed cast).</summary>
    public IReadOnlyList<string> Broken
    {
        get
        {
            lock (_messages)
                return _messages.Where(message => !message.EndsWith("Value is null.", StringComparison.Ordinal)).ToList();
        }
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Add(messageTemplate);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Add(messageTemplate + " " + string.Join(" | ", propertyValues));

    private void Add(string message)
    {
        lock (_messages)
            _messages.Add(message);
    }

    public void Dispose() => Logger.Sink = _previous;
}

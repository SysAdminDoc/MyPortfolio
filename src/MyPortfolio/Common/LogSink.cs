using System.Collections.ObjectModel;
using System.Windows;

namespace MyPortfolio.Common;

/// <summary>
/// Shared, thread-safe log sink. Every tab and service appends to this from
/// any thread; we marshal to the dispatcher and cap line count so the panel
/// can't grow unbounded.
/// </summary>
public sealed class LogSink
{
    private const int MaxLines = 600;
    public ObservableCollection<string> Lines { get; } = new();

    public void Append(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            DoAppend(stamped);
        else
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => DoAppend(stamped)));
    }

    public void Append(string prefix, string line) => Append($"[{prefix}] {line}");

    public IProgress<string> AsProgress(string prefix)
        => new Progress<string>(s => Append(prefix, s));

    private void DoAppend(string line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }
}

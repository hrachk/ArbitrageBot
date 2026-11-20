using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace ArbitrageBot.Services;

/// <summary>
/// Console output on its own thread. In a Windows console, selecting text with the mouse
/// (QuickEdit) pauses console writes; with a direct console sink every thread that logs then
/// blocks and the bot freezes. Here only this writer thread waits: the bot keeps trading, events
/// queue up (oldest dropped beyond the cap) and print when the selection ends. The file log is
/// written directly and is never affected.
/// </summary>
public sealed class NonBlockingConsoleSink : ILogEventSink, IDisposable
{
    private readonly Channel<LogEvent> _queue = Channel.CreateBounded<LogEvent>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Logger _console;
    private readonly Thread _writer;

    public NonBlockingConsoleSink(Logger console)
    {
        _console = console;
        _writer = new Thread(Run) { IsBackground = true, Name = "console-log" };
        _writer.Start();
    }

    public void Emit(LogEvent logEvent) => _queue.Writer.TryWrite(logEvent);

    private void Run()
    {
        var reader = _queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var e))
            {
                try { _console.Write(e); } catch { /* console gone: keep draining */ }
            }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Join(TimeSpan.FromSeconds(2));
        _console.Dispose();
    }
}

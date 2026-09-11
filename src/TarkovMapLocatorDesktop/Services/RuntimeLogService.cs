using System.IO;
using System.Text;
using System.Threading.Channels;

namespace TarkovMapLocatorDesktop.Services;

public enum RuntimeLogLevel
{
    Trace,
    Info,
    Warning,
    Error
}

public sealed record RuntimeLogEntry(
    DateTimeOffset Timestamp,
    RuntimeLogLevel Level,
    string Category,
    string Message,
    string? Detail)
{
    public string ToDisplayText() => Format("HH:mm:ss.fff");
    public string ToFileText() => Format("yyyy-MM-dd HH:mm:ss.fff zzz");

    private string Format(string timestampFormat)
    {
        var level = Level switch
        {
            RuntimeLogLevel.Trace => "TRACE",
            RuntimeLogLevel.Info => "INFO ",
            RuntimeLogLevel.Warning => "WARN ",
            RuntimeLogLevel.Error => "ERROR",
            _ => Level.ToString().ToUpperInvariant()
        };
        var header = $"[{Timestamp.LocalDateTime.ToString(timestampFormat)}] [{level}] [{Category}] {Message}";
        if (string.IsNullOrWhiteSpace(Detail)) return header;

        var normalizedDetail = Detail.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return header + Environment.NewLine + string.Join(
            Environment.NewLine,
            normalizedDetail.Split('\n').Select(line => $"    {line}"));
    }
}

public sealed record RuntimeLogHealth(
    int SessionEntryCount,
    long PendingFileEntryCount,
    long LostFileEntryCount,
    DateTimeOffset? LastSuccessfulWriteAt,
    string? LastWriteError);

/// <summary>
/// Process-wide runtime log. The UI keeps a bounded session view while a
/// single background writer persists the complete log without blocking WPF.
/// </summary>
public static class RuntimeLogService
{
    private const int MaxSessionEntries = 600;
    private const int MaxQueuedFileEntries = 2048;
    private static readonly object Sync = new();
    private static readonly Queue<RuntimeLogEntry> SessionEntries = new();
    private static readonly Channel<string> FileWriteQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedFileEntries)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        // Never block the UI thread. Reject the newest entry when the writer
        // cannot keep up so the loss is measurable instead of silently
        // evicting an unknown older diagnostic entry.
        FullMode = BoundedChannelFullMode.Wait
    });
    private static readonly Task FileWriterTask;
    private static int _shutdownStarted;
    private static long _pendingFileEntryCount;
    private static long _lostFileEntryCount;
    private static DateTimeOffset? _lastSuccessfulWriteAt;
    private static string? _lastWriteError;

    public static string LogDirectory { get; } = Path.Combine(
        ApplicationIdentity.ApplicationDataDirectory,
        "logs");

    public static string CurrentLogFilePath => Path.Combine(LogDirectory, $"runtime-{DateTime.Now:yyyy-MM-dd}.log");
    public static event Action? Changed;

    static RuntimeLogService()
    {
        TryPrepareDirectory();
        FileWriterTask = Task.Run(WriteLoopAsync);
    }

    public static int SessionEntryCount
    {
        get { lock (Sync) return SessionEntries.Count; }
    }

    public static void Trace(string category, string message, string? detail = null) =>
        Write(RuntimeLogLevel.Trace, category, message, detail);

    public static void Info(string category, string message, string? detail = null) =>
        Write(RuntimeLogLevel.Info, category, message, detail);

    public static void Warning(string category, string message, string? detail = null) =>
        Write(RuntimeLogLevel.Warning, category, message, detail);

    public static void Error(string category, string message, Exception exception, string? detail = null)
    {
        var exceptionDetail = exception.ToString();
        if (!string.IsNullOrWhiteSpace(detail)) exceptionDetail = detail + "\n" + exceptionDetail;
        Write(RuntimeLogLevel.Error, category, message, exceptionDetail);
    }

    public static async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0) FileWriteQueue.Writer.TryComplete();
        await FileWriterTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    public static string GetSessionText()
    {
        return FormatEntries(GetSessionEntries());
    }

    public static RuntimeLogEntry[] GetSessionEntries()
    {
        lock (Sync) return SessionEntries.ToArray();
    }

    public static RuntimeLogEntry[] FilterEntries(
        IEnumerable<RuntimeLogEntry> entries,
        RuntimeLogLevel? level,
        string? category,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var searchTerms = string.IsNullOrWhiteSpace(searchText)
            ? []
            : searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return entries.Where(entry =>
        {
            if (level is not null && entry.Level != level) return false;
            if (normalizedCategory is not null &&
                !string.Equals(entry.Category, normalizedCategory, StringComparison.OrdinalIgnoreCase)) return false;
            if (searchTerms.Length == 0) return true;

            var searchable = string.Join('\n', entry.Category, entry.Message, entry.Detail ?? string.Empty);
            return searchTerms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    public static string FormatEntries(IEnumerable<RuntimeLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(Environment.NewLine, entries.Select(entry => entry.ToDisplayText()));
    }

    public static RuntimeLogHealth GetHealth()
    {
        lock (Sync)
        {
            return new RuntimeLogHealth(
                SessionEntries.Count,
                Math.Max(0, Interlocked.Read(ref _pendingFileEntryCount)),
                Math.Max(0, Interlocked.Read(ref _lostFileEntryCount)),
                _lastSuccessfulWriteAt,
                _lastWriteError);
        }
    }

    public static void ClearSession()
    {
        lock (Sync) SessionEntries.Clear();
        NotifyChanged();
    }

    private static void Write(RuntimeLogLevel level, string category, string message, string? detail)
    {
        var entry = new RuntimeLogEntry(
            DateTimeOffset.Now,
            level,
            string.IsNullOrWhiteSpace(category) ? "应用" : category.Trim(),
            string.IsNullOrWhiteSpace(message) ? "无消息" : message.Trim(),
            string.IsNullOrWhiteSpace(detail) ? null : detail.Trim());

        lock (Sync)
        {
            SessionEntries.Enqueue(entry);
            while (SessionEntries.Count > MaxSessionEntries) SessionEntries.Dequeue();
        }

        Interlocked.Increment(ref _pendingFileEntryCount);
        if (!FileWriteQueue.Writer.TryWrite(entry.ToFileText() + Environment.NewLine))
        {
            Interlocked.Decrement(ref _pendingFileEntryCount);
            Interlocked.Increment(ref _lostFileEntryCount);
        }
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch
        {
            // A closing or unavailable UI must never break application logging.
        }
    }

    private static async Task WriteLoopAsync()
    {
        while (await FileWriteQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new StringBuilder(16 * 1024);
            var batchEntryCount = 0;
            while (batch.Length < 256 * 1024 && FileWriteQueue.Reader.TryRead(out var text))
            {
                batch.Append(text);
                batchEntryCount++;
                Interlocked.Decrement(ref _pendingFileEntryCount);
            }
            if (batch.Length == 0) continue;

            Exception? lastException = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    await File.AppendAllTextAsync(CurrentLogFilePath, batch.ToString(), Encoding.UTF8).ConfigureAwait(false);
                    lock (Sync)
                    {
                        _lastSuccessfulWriteAt = DateTimeOffset.Now;
                        _lastWriteError = null;
                    }
                    lastException = null;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    lastException = exception;
                    lock (Sync) _lastWriteError = exception.Message;
                    if (attempt < 3) await Task.Delay(150 * attempt).ConfigureAwait(false);
                }
            }

            if (lastException is not null)
            {
                Interlocked.Add(ref _lostFileEntryCount, batchEntryCount);
                NotifyChanged();
            }
        }
    }

    private static void TryPrepareDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "runtime-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Best-effort retention cleanup.
                    lock (Sync) _lastWriteError = exception.Message;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The in-memory log still works when the directory cannot be created.
            lock (Sync) _lastWriteError = exception.Message;
        }
    }
}

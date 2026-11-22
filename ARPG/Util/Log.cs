using System.Text;

namespace ARPG.Util;

/// <summary>
/// File logging. The game is a WinExe, so Console output goes nowhere on Windows —
/// <see cref="Init"/> tees Console.Out/Console.Error into <c>Logs/scrollbound-&lt;date&gt;.log</c>
/// next to the executable, so every existing Console.WriteLine lands in the file
/// unchanged. Thread-safe, flushed per line, and only the newest
/// <see cref="KeepFiles"/> log files are kept. The last <see cref="RecentCapacity"/>
/// lines also stay in memory for crash reports.
/// </summary>
public static class Log
{
    public const int KeepFiles = 5;
    public const int RecentCapacity = 200;

    private static readonly object Gate = new();
    private static StreamWriter _file;
    private static readonly Queue<string> Recent = new();
    private static TextWriter _originalOut, _originalError;

    /// <summary>The folder logs and crash files are written to (null until Init).</summary>
    public static string Directory { get; private set; }
    public static string CurrentFile { get; private set; }

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    /// <summary>Open today's log file (appending) and tee the console into it.
    /// Never throws: a read-only install folder just means no log file.</summary>
    public static void Init(string directory = null, bool teeConsole = true)
    {
        lock (Gate)
        {
            try
            {
                CloseLocked();
                Directory = directory ?? DefaultDirectory;
                System.IO.Directory.CreateDirectory(Directory);
                CurrentFile = Path.Combine(Directory, $"scrollbound-{DateTime.Now:yyyy-MM-dd}.log");
                _file = new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                Prune(Directory);
            }
            catch (Exception e)
            {
                _file = null;
                try { (_originalError ?? Console.Error).WriteLine($"[Log] No log file: {e.Message}"); } catch { }
            }
            if (teeConsole && _originalOut == null)
            {
                _originalOut = Console.Out;
                _originalError = Console.Error;
                Console.SetOut(new TeeWriter(_originalOut));
                Console.SetError(new TeeWriter(_originalError));
            }
        }
        Write($"==== Scrollbound log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
    }

    /// <summary>Stop logging and restore the console (tests; also safe at exit).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseLocked();
            if (_originalOut != null)
            {
                Console.SetOut(_originalOut);
                Console.SetError(_originalError);
                _originalOut = _originalError = null;
            }
        }
    }

    private static void CloseLocked()
    {
        try { _file?.Dispose(); } catch { }
        _file = null;
    }

    /// <summary>Delete all but the newest KeepFiles scrollbound-*.log files.</summary>
    private static void Prune(string directory)
    {
        var files = System.IO.Directory.GetFiles(directory, "scrollbound-*.log")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        foreach (var old in files.Skip(KeepFiles))
            try { File.Delete(old); } catch { }
    }

    /// <summary>One line to the log file (and the in-memory tail) — NOT to the console.
    /// Console.WriteLine reaches here through the tee.</summary>
    public static void Write(string line)
    {
        lock (Gate)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            Recent.Enqueue(stamped);
            while (Recent.Count > RecentCapacity) Recent.Dequeue();
            try
            {
                _file?.WriteLine(stamped);
                _file?.Flush();
            }
            catch { /* disk full / file gone: keep the game running */ }
        }
    }

    /// <summary>The newest lines (oldest first), for crash reports.</summary>
    public static List<string> RecentLines()
    {
        lock (Gate) return Recent.ToList();
    }

    /// <summary>Forwards everything to the real console and line-buffers a copy into the log.</summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _line = new();
        public TeeWriter(TextWriter inner) { _inner = inner; }
        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            try { _inner.Write(value); } catch { }
            lock (_line)
            {
                if (value == '\n') FlushLine();
                else if (value != '\r') _line.Append(value);
            }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            try { _inner.Write(value); } catch { }
            lock (_line)
            {
                foreach (char ch in value)
                {
                    if (ch == '\n') FlushLine();
                    else if (ch != '\r') _line.Append(ch);
                }
            }
        }

        public override void WriteLine(string value)
        {
            try { _inner.WriteLine(value); } catch { }
            lock (_line)
            {
                foreach (char ch in value ?? "")
                {
                    if (ch == '\n') FlushLine();
                    else if (ch != '\r') _line.Append(ch);
                }
                FlushLine();
            }
        }

        public override void Flush() { try { _inner.Flush(); } catch { } }

        private void FlushLine()
        {
            Log.Write(_line.ToString());
            _line.Clear();
        }
    }
}

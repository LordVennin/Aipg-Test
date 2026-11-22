using System.Text;

namespace ARPG.Util;

/// <summary>
/// Last-resort handlers: an unhandled exception on any thread (or an unobserved task
/// exception) writes <c>Logs/crash-&lt;timestamp&gt;.txt</c> — the exception, its stack and
/// the last 200 log lines — and attempts an emergency save of the local character.
/// Nothing in here may throw.
/// </summary>
public static class CrashReporter
{
    private static Action _emergencySave;
    private static int _handling;

    /// <summary>Hook the process-wide handlers. <paramref name="emergencySave"/> is
    /// called (guarded) after the crash file is written.</summary>
    public static void Install(Action emergencySave)
    {
        _emergencySave = emergencySave;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(e.ExceptionObject as Exception, e.IsTerminating ? "Unhandled exception (terminating)" : "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    private static void Handle(Exception ex, string what)
    {
        // One crash report at a time (a crash inside the emergency save must not recurse).
        if (Interlocked.Exchange(ref _handling, 1) == 1) return;
        try
        {
            string path = WriteCrashFile(ex, what, Log.Directory ?? Log.DefaultDirectory);
            try { Console.WriteLine($"[Crash] {what}: {ex?.GetType().Name}: {ex?.Message} — report: {path}"); } catch { }
            try
            {
                _emergencySave?.Invoke();
                try { Console.WriteLine("[Crash] Emergency save attempted."); } catch { }
            }
            catch (Exception saveEx)
            {
                try { Console.WriteLine($"[Crash] Emergency save failed: {saveEx.Message}"); } catch { }
            }
        }
        catch { /* never throw from a crash handler */ }
        finally { Interlocked.Exchange(ref _handling, 0); }
    }

    /// <summary>Write the crash report; returns its path (null if even that failed).</summary>
    public static string WriteCrashFile(Exception ex, string what, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Scrollbound crash report — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(what);
            sb.AppendLine($"Protocol {Core.GameNetConfig.ProtocolVersion}, sim {Core.GameNetConfig.SimVersion}, data {Data.ContentFingerprint.Short}");
            sb.AppendLine($"OS: {Environment.OSVersion}, .NET {Environment.Version}, thread '{Thread.CurrentThread.Name}'");
            sb.AppendLine();
            sb.AppendLine("---- exception ----");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();
            sb.AppendLine($"---- last {Log.RecentCapacity} log lines ----");
            foreach (var line in Log.RecentLines()) sb.AppendLine(line);
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch { return null; }
    }
}

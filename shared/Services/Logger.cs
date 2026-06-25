using System.Text;

namespace HyperVStatusTray.Services;

public static class Logger
{
    private static readonly object Sync = new();
    private static string _logPath = Path.Combine(Path.GetTempPath(), "HyperVStatusTray.log");

    public static string LogPath => _logPath;

    public static void Initialize(string dataDirectory, string fileName = "HyperVStatusTray.log")
    {
        Directory.CreateDirectory(dataDirectory);
        _logPath = Path.Combine(dataDirectory, fileName);

        try
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 2 * 1024 * 1024)
            {
                string backup = Path.Combine(dataDirectory, Path.GetFileNameWithoutExtension(fileName) + ".previous.log");
                File.Move(_logPath, backup, overwrite: true);
            }
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }

        Info(AppText.Get(AppText.DefaultLanguage, TextId.ProgramStartedLog));
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warning(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            StringBuilder line = new();
            line.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            line.Append(" [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                line.AppendLine();
                line.Append(exception);
            }

            lock (Sync)
            {
                File.AppendAllText(_logPath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Deliberately ignored.
        }
    }
}

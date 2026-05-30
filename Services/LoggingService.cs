using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BlackScholesApp.Services;

public interface ILoggingService
{
    string LogsDirectory { get; }
    Task<string> GetRecentLogsAsync(int maxLines = 500);
    void OpenLogsFolder();
}

public class LoggingService : ILoggingService
{
    public string LogsDirectory { get; }

    public LoggingService(string logsDirectory)
    {
        LogsDirectory = logsDirectory;
    }

    public async Task<string> GetRecentLogsAsync(int maxLines = 500)
    {
        try
        {
            if (!Directory.Exists(LogsDirectory))
                return "Папка с логами не найдена.";

            var files = Directory.GetFiles(LogsDirectory, "BlackScholesApp_*.log")
                .OrderByDescending(f => f)
                .Take(2)
                .ToArray();

            if (files.Length == 0)
                return "Лог-файлы не найдены.";

            var lines = new List<string>();
            foreach (var file in files)
            {
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    var content = await reader.ReadToEndAsync();
                    lines.AddRange(content.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch { /* файл заблокирован — пропускаем */ }
            }

            var recent = lines.TakeLast(maxLines).ToArray();
            return string.Join(Environment.NewLine, recent);
        }
        catch (Exception ex)
        {
            return $"Ошибка чтения логов: {ex.Message}";
        }
    }

    public void OpenLogsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", LogsDirectory);
        }
        catch { /* игнорируем */ }
    }
}

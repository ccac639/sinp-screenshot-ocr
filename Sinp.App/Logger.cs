using System;
using System.IO;
using System.Diagnostics;

namespace Sinp.App
{
    /// <summary>
    /// 日志工具（轻量、线程安全、按日期滚动）
    /// 日志存放：D:\开发\sinp\logs\YYYY-MM-DD.log
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static string _currentDate = "";

        private static string GetLogPath()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_currentDate != today)
            {
                _currentDate = today;
            }
            // 日志按架构要求放开发目录
            var logDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "logs"
            );
            Directory.CreateDirectory(logDir);
            return Path.Combine(logDir, $"{_currentDate}.log");
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
            Debug.WriteLine($"[Sinp INFO ] {message}");
        }

        public static void Warn(string message)
        {
            Write("WARN ", message);
            Debug.WriteLine($"[Sinp WARN ] {message}");
        }

        public static void Error(string message, Exception? ex = null)
        {
            var full = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
            Write("ERROR", full);
            Debug.WriteLine($"[Sinp ERROR] {full}");
        }

        private static void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\n";
            lock (_lock)
            {
                try { File.AppendAllText(GetLogPath(), line); }
                catch { /* 日志写入失败不崩溃 */ }
            }
        }
    }
}

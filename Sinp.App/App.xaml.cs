using System;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace Sinp.App
{
    /// <summary>
    /// App.xaml 交互逻辑 — 应用入口
    /// </summary>
    public partial class App : Application
    {
        public static string LogDir { get; private set; } = "";
        public static string AppDataDir { get; private set; } = "";

        protected override void OnStartup(StartupEventArgs e)
        {
            // 日志 & 数据目录（按架构要求：放开发目录）
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            LogDir     = Path.Combine(baseDir, "..", "..", "logs");
            AppDataDir = Path.Combine(baseDir, "..", "..", "data");
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(AppDataDir);

            Logger.Info("═══════════════════════════════════");
            Logger.Info($"  Sinp v1.0 启动  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Logger.Info("═══════════════════════════════════");

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("Sinp 退出");
            base.OnExit(e);
        }
    }
}

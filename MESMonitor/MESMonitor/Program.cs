using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace MESMonitor
{
    class Program
    {
        // 配置参数
        // 从App.config中读取配置项
        static readonly List<string> targetProcesses = ConfigurationManager.AppSettings["ProcessesToMonitor"].Split(',').ToList(); // 需要监控的进程名称列表
        static readonly Dictionary<string, string> processPaths = new Dictionary<string, string>(); // 进程启动路径配置字典
        static readonly int memoryThreshold = int.Parse(ConfigurationManager.AppSettings["MemoryThresholdMB"]); // 内存报警阈值(MB)
        static readonly string logPath = ConfigurationManager.AppSettings["LogPath"]; // 日志存储目录
        static readonly int checkInterval = int.Parse(ConfigurationManager.AppSettings["CheckIntervalMS"]); // 监控轮询间隔(毫秒)

        // 运行时状态
        static Dictionary<string, DateTime?> processDownTimes = new Dictionary<string, DateTime?>();
        private static readonly object syncLock = new object();
        static Timer monitorTimer;

        static Program()
        {
            // 解析进程路径配置
            // 示例配置格式："进程1:路径1;进程2:路径2"
            var pathConfig = ConfigurationManager.AppSettings["ProcessPaths"];
            foreach (var pair in pathConfig.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(new[] { ':' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    processPaths[parts[0].Trim()] = parts[1].Trim(); // 将进程名与路径关联存储
                }
            }
        }
        /// <summary>
        /// 主程序入口
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Directory.CreateDirectory(logPath);
            monitorTimer = new Timer(MonitorProcesses, null, 0, checkInterval);

            Console.WriteLine("监控已启动（按Q退出）...");
            while (Console.ReadKey().Key != ConsoleKey.Q) ;
            monitorTimer.Dispose(); // 释放定时器资源
        }
        /// <summary>
        /// 核心监控逻辑
        /// </summary>
        /// <param name="state">进程状态</param>
        static void MonitorProcesses(object state)
        {
            lock (syncLock)
            {
                try
                {
                    // 只监控有窗口的应用程序进程
                    var appProcesses = Process.GetProcesses()
                        .Where(p => HasApplicationWindow(p))
                        .ToList();

                    // 功能1：记录应用进程内存
                    foreach (var p in appProcesses)
                    {
                        using (p)
                        {
                            LogProcessInfo(p);
                            CheckMemoryUsage(p);
                        }
                    }

                    // 功能2：检查配置的进程是否存在
                    foreach (var processName in targetProcesses)
                    {
                        bool isRunning = Process.GetProcessesByName(processName)
                            .Any(p => !p.HasExited);

                        if (!isRunning)
                        {
                            HandleMissingProcess(processName);
                        }
                        else
                        {
                            ResetProcessStatus(processName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"监控异常: {ex.Message}", LogType.AppEvent);
                }
            }
        }
        
        // 新增日志类型枚举
        enum LogType
        {
            ProcessInfo,    // 进程内存日志
            MemoryAlert,    // 内存超标报警
            AppEvent        // 程序自启事件
        }
        /// <summary>
        /// 判断进程是否具有可见窗口，用来过滤非应用类型的进程。
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        static bool HasApplicationWindow(Process p)
        {
            try
            {
                // 通过MainWindowTitle和MainWindowHandle判断是否为应用程序窗口
                return !string.IsNullOrEmpty(p.MainWindowTitle) && p.MainWindowHandle != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// 处理进程离线逻辑
        /// </summary>
        /// <param name="processName">进程名称</param>
        static void HandleMissingProcess(string processName)
        {
            // 首次检测到离线
            if (!processDownTimes.ContainsKey(processName) || processDownTimes[processName] == null)
            {
                processDownTimes[processName] = DateTime.Now;
                WriteLog($"检测到 {processName} 进程离线", LogType.AppEvent);
            }
            // 离线超过1分钟则尝试重启
            else if ((DateTime.Now - processDownTimes[processName].Value).TotalMinutes >= 1)
            {
                StartProcess(processName);
                processDownTimes[processName] = null; // 重置状态
            }
        }
        /// <summary>
        /// 更新进程恢复状态
        /// </summary>
        /// <param name="processName">进程名称</param>
        static void ResetProcessStatus(string processName)
        {
            if (processDownTimes.ContainsKey(processName) && processDownTimes[processName] != null)
            {
                WriteLog($"进程 {processName} 已恢复", LogType.AppEvent);
                processDownTimes[processName] = null; // 清除离线记录
            }
        }
        /// <summary>
        /// 启动目标进程逻辑
        /// </summary>
        /// <param name="processName">进程名称</param>
        static void StartProcess(string processName)
        {
            if (processPaths.TryGetValue(processName, out string path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true // 使用Shell执行保证窗口显示
                    });
                    WriteLog($"已启动 {processName}", LogType.AppEvent);
                }
                catch (Exception ex)
                {
                    WriteLog($"启动失败: {processName} - {ex.Message}", LogType.AppEvent);
                }
            }
            else
            {
                WriteLog($"未配置 {processName} 的启动路径", LogType.AppEvent);
            }
        }
        /// <summary>
        /// 进程内存监控
        /// </summary>
        /// <param name="p"></param>
        static void CheckMemoryUsage(Process p)
        {
            try
            {
                var memoryMB = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 2);
                if (memoryMB > memoryThreshold)
                {
                    WriteLog($"内存超标: {p.ProcessName} ({memoryMB}MB)", LogType.MemoryAlert);
                }
            }
            catch { 
                /* 忽略已退出的进程 */ 
            }
        }
        /// <summary>
        /// 记录进程常规信息
        /// </summary>
        /// <param name="p"></param>
        static void LogProcessInfo(Process p)
        {
            try
            {
                double memoryMB = Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 2);
                WriteLog(
                    $"进程: {p.ProcessName.PadRight(20)} 内存: {memoryMB} MB", // PadRight保证对齐
                    LogType.ProcessInfo
                );
            }
            catch (Exception ex)
            {
                WriteLog($"记录进程信息失败: {ex.Message}", LogType.AppEvent);
            }
        }
        /// <summary>
        /// 写入日志
        /// </summary>
        /// <param name="message">日志信息</param>
        /// <param name="logType">日志类型</param>
        static void WriteLog(string message, LogType logType)
        {
            try
            {
                string datePart = DateTime.Now.ToString("yyyy-MM-dd");
                string fileName;
                // 根据日志类型生成文件名
                switch (logType)
                {
                    case LogType.ProcessInfo:
                        fileName = $"{datePart}_进程日志.txt";
                        break;
                    case LogType.MemoryAlert:
                        fileName = $"{datePart}_内存超标报警.txt";
                        break;
                    case LogType.AppEvent:
                        fileName = $"{datePart}_程序自启日志.txt";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(logType));
                }
                // 构造带时间戳的日志条目
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                // 写入日志文件
                File.AppendAllText(Path.Combine(logPath, fileName), logEntry + Environment.NewLine);
                // 程序事件类日志同时输出到控制台
                if (logType == LogType.AppEvent)
                {
                    Console.WriteLine(logEntry);
                }
            }
            catch (Exception ex)
            {
                // 日志写入失败时输出到控制台
                Console.WriteLine($"日志写入失败: {ex.Message}");
            }
        }
    }
}
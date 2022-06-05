﻿using System.Diagnostics;
using NewLife;
using NewLife.Log;
using NewLife.Threading;
using Stardust.Models;

namespace Stardust.Managers;

/// <summary>
/// 应用服务控制器
/// </summary>
internal class ServiceController : DisposeBase
{
    #region 属性
    /// <summary>服务名</summary>
    public String Name { get; set; }

    /// <summary>进程ID</summary>
    public Int32 ProcessId { get; set; }

    /// <summary>进程名</summary>
    public String ProcessName { get; set; }

    /// <summary>服务信息</summary>
    public ServiceInfo Info { get; set; }

    /// <summary>进程</summary>
    public Process Process { get; set; }

    /// <summary>监视文件改变的周期。默认5000ms</summary>
    public Int32 MonitorPeriod { get; set; } = 5000;

    /// <summary>延迟时间。重启进程或服务的延迟时间，默认3000ms</summary>
    public Int32 Delay { get; set; } = 3000;

    /// <summary>开始时间</summary>
    public DateTime StartTime { get; set; }

    private String _workdir;
    private TimerX _timer;
    #endregion

    #region 构造
    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        _timer.TryDispose();
    }
    #endregion

    #region 方法
    /// <summary>检查并启动应用</summary>
    /// <returns>本次是否成功启动，原来已启动返回false</returns>
    public Boolean Start()
    {
        if (Process != null) return false;

        var service = Info;

        // 修正路径
        var workDir = service.WorkingDirectory;
        var file = service.FileName?.Trim();
        if (file.IsNullOrEmpty()) return false;

        if (file.Contains("/") || file.Contains("\\"))
        {
            file = file.GetFullPath();
            if (workDir.IsNullOrEmpty()) workDir = Path.GetDirectoryName(file);
        }
        _workdir = workDir;

        var args = service.Arguments?.Trim();
        WriteLog("启动应用：{0} {1} {2}", file, args, workDir);

        var si = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workDir,

            // false时目前控制台合并到当前控制台，一起退出；
            // true时目标控制台独立窗口，不会一起退出；
            UseShellExecute = true,
        };

        using var span = Tracer?.NewSpan("StartService", service);
        try
        {
            var p = Process.Start(si);

            WriteLog("启动成功 PID={0}/{1}", p.Id, p.ProcessName);

            // 记录进程信息，避免宿主重启后无法继续管理
            SetProcess(p);

            StartTime = DateTime.Now;

            // 定时检查文件是否有改变
            if (service.ReloadOnChange) StartMonitor();

            return true;
        }
        catch (Exception ex)
        {
            span?.SetError(ex, null);
            Log?.Write(LogLevel.Error, "{0}", ex);
        }

        return false;
    }

    /// <summary>停止应用</summary>
    /// <param name="reason"></param>
    public void Stop(String reason)
    {
        var p = Process;
        if (p == null) return;

        WriteLog("停止应用 PID={0}/{0} 原因：{2}", p.Id, p.ProcessName, reason);

        using var span = Tracer?.NewSpan("StopService", Info);
        try
        {
            p.CloseMainWindow();
        }
        catch { }

        try
        {
            if (!p.HasExited) p.Kill();
        }
        catch (Exception ex)
        {
            span?.SetError(ex, null);
        }

        SetProcess(null);
    }

    /// <summary>检查已存在进程并接管，如果进程已退出则重启</summary>
    /// <returns>本次是否成功启动（或接管），原来已启动返回false</returns>
    public Boolean Check()
    {
        var p = Process;
        if (p != null)
        {
            if (!p.HasExited) return false;

            Process = null;
            WriteLog("应用[{0}/{1}]已退出！", p.ProcessName, p.Id);
        }
        else if (ProcessId > 0)
        {
            try
            {
                p = Process.GetProcessById(ProcessId);
                if (p != null && !p.HasExited && p.ProcessName == ProcessName)
                {
                    WriteLog("应用[{0}/{1}]已启动，直接接管", Name, p.Id);

                    SetProcess(p);
                    if (Info != null && Info.ReloadOnChange) StartMonitor();

                    if (StartTime.Year < 2000) StartTime = DateTime.Now;

                    return true;
                }
            }
            catch (Exception ex)
            {
                if (ex is not ArgumentException) XTrace.WriteException(ex);
            }
        }

        // 准备启动进程
        var rs = Start();

        return rs;
    }

    public void SetProcess(Process process)
    {
        Process = process;
        if (process != null)
        {
            ProcessId = process.Id;
            ProcessName = process.ProcessName;
        }
        else
        {
            ProcessId = 0;
            ProcessName = null;
        }
    }

    /// <summary>获取进程信息</summary>
    /// <returns></returns>
    public ProcessInfo ToModel()
    {
        return new ProcessInfo
        {
            Name = Name,
            ProcessId = ProcessId,
            ProcessName = ProcessName,
            CreateTime = StartTime,
            UpdateTime = DateTime.Now,
        };
    }

    private void StartMonitor()
    {
        // 定时检查文件是否有改变
        if (_timer == null) _timer = new TimerX(MonitorFileChange, null, 1_000, MonitorPeriod) { Async = true };
    }

    private readonly Dictionary<String, DateTime> _files = new();

    /// <summary>是否已准备。发生文件变化时，进入就绪状态，持续5秒没有改变后执行重启</summary>
    private Boolean _ready;
    private DateTime _readyTime;

    private void MonitorFileChange(Object state)
    {
        var first = _files.Count == 0;
        var changed = "";

        // 检查目标目录所有 *.dll 文件
        var di = !_workdir.IsNullOrEmpty() ? _workdir.AsDirectory() : Info?.WorkingDirectory?.AsDirectory();
        if (di == null || !di.Exists) return;

        if (first) WriteLog("监视文件改变：{0}", di.FullName);

        foreach (var fi in di.GetAllFiles("*.dll;*.exe"))
        {
            var time = fi.LastWriteTime.Trim();
            if (_files.TryGetValue(fi.FullName, out var dt))
            {
                if (dt < time)
                {
                    _files[fi.FullName] = time;
                    changed = fi.FullName;
                }
            }
            else
            {
                _files[fi.FullName] = time;
                changed = fi.FullName;
            }
        }

        if (!first && !changed.IsNullOrEmpty())
        {
            var msg = $"文件[{changed}]发生改变";
            XTrace.WriteLine(msg);

            // 进入就绪状态
            if (!_ready)
            {
                Stop(msg);

                _ready = true;

                // 快速再次检查
                _timer.SetNext(1000);
            }

            // 更新最后就绪时间，该时间之后5秒再启动
            _readyTime = DateTime.Now;
        }

        if (_ready && _readyTime.AddMilliseconds(Delay) < DateTime.Now)
        {
            Start();

            _ready = false;
        }
    }

    enum Status
    {
        None = 0,
        Ready = 1,
    }
    #endregion

    #region 日志
    /// <summary>性能追踪</summary>
    public ITracer Tracer { get; set; }

    /// <summary>日志</summary>
    public ILog Log { get; set; }

    /// <summary>写日志</summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public void WriteLog(String format, params Object[] args) => Log?.Info($"[{Name}]{format}", args);
    #endregion
}
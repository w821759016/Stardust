﻿using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NewLife;
using NewLife.Log;
using Stardust.Services;

namespace Stardust.Managers;

/// <summary>dotNet运行时</summary>
public class NetRuntime
{
    #region 属性
    /// <summary>基准路径</summary>
    public String BaseUrl { get; set; } = "http://x.newlifex.com/dotnet";

    /// <summary>静默安装</summary>
    public Boolean Silent { get; set; }

    /// <summary>缓存目录</summary>
    public String CachePath { get; set; }

    /// <summary>文件哈希。用于校验下载文件的完整性</summary>
    public IDictionary<String, String> Hashs { get; set; }

    /// <summary>事件客户端</summary>
    public IEventProvider EventProvider { get; set; }
    #endregion

    #region 构造
    /// <summary>实例化</summary>
    public NetRuntime()
    {
        var set = NewLife.Setting.Current;
        if (!set.PluginServer.IsNullOrEmpty())
        {
            BaseUrl = set.PluginServer.TrimEnd('/') + "/dotnet";
        }
    }
    #endregion

    #region 核心方法
    /// <summary>安装</summary>
    /// <param name="fileName"></param>
    /// <param name="baseUrl"></param>
    /// <param name="arg"></param>
    /// <returns></returns>
    public Boolean Install(String fileName, String baseUrl = null, String arg = null)
    {
        WriteLog("下载 {0}", fileName);

        var fullFile = fileName;
        if (!String.IsNullOrEmpty(CachePath)) fullFile = Path.Combine(CachePath, fileName);

        var hash = "";
        if (Hashs != null && !Hashs.TryGetValue(fileName, out hash)) hash = null;

        // 检查已存在文件的MD5哈希，不正确则重新下载
        var fi = new FileInfo(fullFile);
        if (fi.Exists && fi.Length < 1024 && !String.IsNullOrEmpty(hash) && GetMD5(fullFile) != hash)
        {
            fi.Delete();
            fi = null;
        }
        if (fi == null || !fi.Exists)
        {
            if (String.IsNullOrEmpty(baseUrl))
                baseUrl = BaseUrl?.TrimEnd('/');
            else
                baseUrl = BaseUrl?.TrimEnd('/') + '/' + baseUrl.TrimStart('/').TrimEnd('/');

            var url = $"{baseUrl}/{fileName}";
            WriteLog("正在下载：{0}", url);

            var dir = Path.GetDirectoryName(fullFile);
            if (!String.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 独立区域，下载完成后释放连接和文件句柄
            {
#if NET6_0_OR_GREATER
                using var http = new System.Net.Http.HttpClient();
                var hs = http.GetStreamAsync(url).Result;

                using var fs = new FileStream(fullFile, FileMode.CreateNew, FileAccess.Write);
                hs.CopyTo(fs);
#else
                using var http = new WebClient();
                http.DownloadFile(url, fullFile);
#endif
            }
            WriteLog("MD5: {0}", GetMD5(fullFile));

            //// 在windows系统上，下载完成以后，等待一会再安装，避免文件被占用（可能是安全扫描），提高安装成功率
            //if (Runtime.Windows) Thread.Sleep(15_000);
        }

        if (String.IsNullOrEmpty(arg)) arg = "/passive /promptrestart";
        if (!Silent) arg = null;

        WriteLog("正在安装：{0} {1}", fullFile, arg);

        if (IsWindows)
            return InstallOnWindows(fullFile, arg);
        else
            return InstallOnLinux(fullFile, arg);
    }

    Boolean InstallOnWindows(String fullFile, String arg)
    {
        var p = Process.Start(fullFile, arg);
        if (p.WaitForExit(600_000))
        {
            if (p.ExitCode == 0)
                WriteLog("安装完成！");
            else
                WriteLog("安装失败！ExitCode={0}", p.ExitCode);
            Environment.ExitCode = p.ExitCode;
            return p.ExitCode == 0;
        }
        else
        {
            WriteLog("安装超时！");
            Environment.ExitCode = 400;
            return false;
        }
    }

    Boolean InstallOnLinux(String fullFile, String arg)
    {
        // 建立目录
        var target = "/usr/share/dotnet";
        //target.EnsureDirectory(false);
        if (!Directory.Exists(target)) Directory.CreateDirectory(target);

        // 解压缩
        Process.Start(new ProcessStartInfo("tar", $"-xzf {fullFile} -C {target}") { UseShellExecute = true });

        // 建立链接
        Process.Start(new ProcessStartInfo("ln", $"{fullFile}/dotnet /usr/bin/dotnet -s") { UseShellExecute = true });

        WriteLog("安装完成！");

        return true;
    }

    static Version GetLast(IList<VerInfo> vers, String prefix = null, String suffix = null)
    {
        var ver = new Version();
        if (vers.Count > 0)
        {
            //WriteLog("已安装版本：");
            foreach (var item in vers)
            {
                if ((String.IsNullOrEmpty(prefix) || item.Name.StartsWith(prefix)) &&
                    (String.IsNullOrEmpty(suffix) || item.Name.EndsWith(suffix)))
                {
                    var str = item.Name.Trim('v');
                    var p = str.IndexOf('-');
                    if (p > 0) str = str.Substring(0, p);

                    var v = new Version(str);
                    if (v > ver) ver = v;
                }

                //WriteLog(item.Name);
            }
            //WriteLog("");
        }

        return ver;
    }

    /// <summary>安装.NET4.0</summary>
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public void InstallNet40()
    {
        var vers = new List<VerInfo>();
        vers.AddRange(Get1To45VersionFromRegistry());

        var ver = GetLast(vers, "v4.0");

        // 目标版本
        var target = new Version("4.0");
        if (ver >= target)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

        var rs = Install("dotNetFx40_Full_x86_x64.exe", null);
        if (!rs)
        {
            // 解决“一般信任关系失败”问题

            Process.Start("regsvr32", "/s Softpub.dll");
            Process.Start("regsvr32", "/s Wintrust.dll");
            Process.Start("regsvr32", "/s Initpki.dll");
            Process.Start("regsvr32", "/s Mssip32.dll");

#if NET45_OR_GREATER || NET6_0_OR_GREATER
            using var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"\Software\Microsoft\Windows\CurrentVersion\WinTrust\Trust Providers\Software Publishing", true);
            if (reg != null)
            {
                var v = (Int32)reg.GetValue("State");
                if (v != 0x23c00) reg.SetValue("State", 0x23c00);
            }
#endif

        }
    }

    /// <summary>安装.NET4.5</summary>
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public void InstallNet45()
    {
        var vers = new List<VerInfo>();
        vers.AddRange(Get1To45VersionFromRegistry());

        var ver = GetLast(vers, "v4.5");

        // 目标版本
        var target = new Version("4.5");
        if (ver >= target)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

        Install("NDP452-KB2901907-x86-x64-AllOS-ENU.exe");
        Install("NDP452-KB2901907-x86-x64-AllOS-CHS.exe");
    }

    /// <summary>安装.NET4.8</summary>
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public void InstallNet48()
    {
        var vers = new List<VerInfo>();
        vers.AddRange(Get1To45VersionFromRegistry());
        vers.AddRange(Get45PlusFromRegistry());

        var ver = GetLast(vers, null);

        // 目标版本。win10起支持4.8.1
        var osVer = Environment.OSVersion.Version;
        var target = osVer.Major >= 10 ? new Version("4.8.1") : new Version("4.8");
        if (ver >= target)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

#if NET20
        var is64 = IntPtr.Size == 8;
#else
        var is64 = Environment.Is64BitOperatingSystem;
#endif

        var isWin7 = osVer.Major == 6 && osVer.Minor == 1;
        if (isWin7)
        {
            //if (is64)
            //{
            //    Install("Windows6.1-KB3063858-x64.msu", "/win7", "/quiet /norestart");
            //}
            //else
            //{
            //    Install("Windows6.1-KB3063858-x86.msu", "/win7", "/quiet /norestart");
            //}
            InstallCert();
        }

        // win10/win11 中安装 .NET4.8.1
        if (osVer.Major >= 10)
        {
            Install("ndp481-x86-x64-allos-enu.exe", null, "/passive /promptrestart /showfinalerror");
            Install("ndp481-x86-x64-allos-chs.exe", null, "/passive /promptrestart /showfinalerror");
        }
        else
        {
            Install("ndp48-x86-x64-allos-enu.exe", null, "/passive /promptrestart /showfinalerror");
            Install("ndp48-x86-x64-allos-chs.exe", null, "/passive /promptrestart /showfinalerror");
        }
    }

    /// <summary>安装.NET6.0</summary>
    /// <param name="target">目标版本。包括子版本，如6.0.15</param>
    /// <param name="kind">安装类型。如aspnet/desktop/host</param>
    public void InstallNet6(String target, String kind = null)
    {
        var vers = GetNetCore();

        var suffix = "";
        if (!String.IsNullOrEmpty(kind)) suffix = "-" + kind;
        var ver = GetLast(vers, "v6.0", suffix);

        // 目标版本
        var targetVer = new Version(target);
        if (ver >= targetVer)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

#if NET20
        var is64 = IntPtr.Size == 8;
#else
        var is64 = Environment.Is64BitOperatingSystem;
#endif

        // win7需要vc2019运行时
        var osVer = Environment.OSVersion.Version;
        var isWin7 = osVer.Major == 6 && osVer.Minor == 1;
        if (isWin7 && ver.Major < 6)
        {
            if (is64)
            {
                Install("Windows6.1-KB3063858-x64.msu", "/win7", "/quiet /norestart");
                Install("VC_redist.x64.exe", "/vc2019", "/passive");
            }
            else
            {
                Install("Windows6.1-KB3063858-x86.msu", "/win7", "/quiet /norestart");
                Install("VC_redist.x86.exe", "/vc2019", "/passive");
            }
        }

        if (is64)
        {
            switch (kind)
            {
                case "aspnet":
                    Install($"dotnet-runtime-{target}-win-x64.exe");
                    Install($"aspnetcore-runtime-{target}-win-x64.exe");
                    break;
                case "desktop":
                    Install($"windowsdesktop-runtime-{target}-win-x64.exe");
                    break;
                case "host":
                    Install($"dotnet-hosting-{target}-win.exe");
                    break;
                default:
                    Install($"dotnet-runtime-{target}-win-x64.exe");
                    break;
            }
        }
        else
        {
            switch (kind)
            {
                case "aspnet":
                    Install($"dotnet-runtime-{target}-win-x86.exe");
                    Install($"aspnetcore-runtime-{target}-win-x86.exe");
                    break;
                case "desktop":
                    Install($"windowsdesktop-runtime-{target}-win-x86.exe");
                    break;
                case "host":
                    Install($"dotnet-hosting-{target}-win.exe");
                    break;
                default:
                    Install($"dotnet-runtime-{target}-win-x86.exe");
                    break;
            }
        }
    }

    /// <summary>安装.NET7.0</summary>
    /// <param name="target">目标版本。包括子版本，如6.0.15</param>
    /// <param name="kind">安装类型。如aspnet/desktop/host</param>
    public void InstallNet7(String target, String kind = null)
    {
        var vers = GetNetCore();

        var suffix = "";
        if (!String.IsNullOrEmpty(kind)) suffix = "-" + kind;
        var ver = GetLast(vers, "v7.0", suffix);

        // 目标版本
        var targetVer = new Version(target);
        if (ver >= targetVer)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

#if NET20
        var is64 = IntPtr.Size == 8;
#else
        var is64 = Environment.Is64BitOperatingSystem;
#endif

        // win7需要vc2019运行时
        var osVer = Environment.OSVersion.Version;
        var isWin7 = osVer.Major == 6 && osVer.Minor == 1;
        if (isWin7 && ver.Major < 6)
        {
            if (is64)
            {
                Install("Windows6.1-KB3063858-x64.msu", "/win7", "/quiet /norestart");
                Install("VC_redist.x64.exe", "/vc2019", "/passive");
            }
            else
            {
                Install("Windows6.1-KB3063858-x86.msu", "/win7", "/quiet /norestart");
                Install("VC_redist.x86.exe", "/vc2019", "/passive");
            }
        }

        if (is64)
        {
            switch (kind)
            {
                case "aspnet":
                    Install($"dotnet-runtime-{target}-win-x64.exe");
                    Install($"aspnetcore-runtime-{target}-win-x64.exe");
                    break;
                case "desktop":
                    Install($"windowsdesktop-runtime-{target}-win-x64.exe");
                    break;
                case "host":
                    Install($"dotnet-hosting-{target}-win.exe");
                    break;
                default:
                    Install($"dotnet-runtime-{target}-win-x64.exe");
                    break;
            }
        }
        else
        {
            switch (kind)
            {
                case "aspnet":
                    Install($"dotnet-runtime-{target}-win-x86.exe");
                    Install($"aspnetcore-runtime-{target}-win-x86.exe");
                    break;
                case "desktop":
                    Install($"windowsdesktop-runtime-{target}-win-x86.exe");
                    break;
                case "host":
                    Install($"dotnet-hosting-{target}-win.exe");
                    break;
                default:
                    Install($"dotnet-runtime-{target}-win-x86.exe");
                    break;
            }
        }
    }

    /// <summary>在Linux上安装.NET运行时</summary>
    /// <param name="target">目标版本。包括子版本，如6.0.15</param>
    /// <param name="kind">安装类型。如aspnet</param>
    public void InstallNetOnLinux(String target, String kind = null)
    {
        var vers = GetNetCore();

        var suffix = "";
        if (!String.IsNullOrEmpty(kind)) suffix = "-" + kind;
        var ver = GetLast(vers, "v" + target.Substring(0, 3), suffix);

        // 目标版本
        var targetVer = new Version(target);
        if (ver >= targetVer)
        {
            WriteLog("已安装最新版 v{0}", ver);
            return;
        }

#if NETSTANDARD ||NETCOREAPP
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLower();

        switch (kind)
        {
            case "aspnet":
                Install($"aspnetcore-runtime-{target}-linux-{arch}.tar.gz");
                break;
            default:
                Install($"dotnet-runtime-{target}-linux-{arch}.tar.gz");
                break;
        }
#endif
    }

    /// <summary>获取所有已安装版本</summary>
    /// <returns></returns>
    public IList<VerInfo> GetVers()
    {
        var vers = new List<VerInfo>();
#if NET5_0_OR_GREATER
        if (OperatingSystem.IsWindows())
        {
            vers.AddRange(Get1To45VersionFromRegistry());
            vers.AddRange(Get45PlusFromRegistry());
        }
#else
        vers.AddRange(Get1To45VersionFromRegistry());
        vers.AddRange(Get45PlusFromRegistry());
#endif
        vers.AddRange(GetNetCore());

        return vers;
    }

    /// <summary>获取Net45以下版本</summary>
    /// <returns></returns>
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public static IList<VerInfo> Get1To45VersionFromRegistry()
    {
        var list = new List<VerInfo>();
        if (!IsWindows) return list;

#if NET45_OR_GREATER || NET6_0_OR_GREATER
        // 注册表查找 .NET Framework
        using var ndpKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\");

        foreach (var versionKeyName in ndpKey.GetSubKeyNames())
        {
            // 跳过 .NET Framework 4.5
            if (versionKeyName == "v4") continue;
            if (!versionKeyName.StartsWith("v")) continue;

            var versionKey = ndpKey.OpenSubKey(versionKeyName);
            // 获取 .NET Framework 版本
            var ver = (String)versionKey.GetValue("Version", "");
            // 获取SP数字
            var sp = versionKey.GetValue("SP", "").ToString();

            if (!String.IsNullOrEmpty(ver))
            {
                // 获取 installation flag, or an empty string if there is none.
                var install = versionKey.GetValue("Install", "").ToString();
                if (String.IsNullOrEmpty(install)) // No install info; it must be in a child subkey.
                    list.Add(new VerInfo { Name = versionKeyName, Version = ver, Sp = sp });
                else if (!String.IsNullOrEmpty(sp) && install == "1")
                    list.Add(new VerInfo { Name = versionKeyName, Version = ver, Sp = sp });
            }
            else
            {
                foreach (var subKeyName in versionKey.GetSubKeyNames())
                {
                    var subKey = versionKey.OpenSubKey(subKeyName);
                    ver = (String)subKey.GetValue("Version", "");
                    if (!String.IsNullOrEmpty(ver))
                    {
                        var name = ver;
                        while (name.Length > 3 && name.Substring(name.Length - 2) == ".0")
                            name = name.Substring(0, name.Length - 2);
                        if (name[0] != 'v') name = 'v' + name;
                        sp = subKey.GetValue("SP", "").ToString();

                        var install = subKey.GetValue("Install", "").ToString();
                        if (String.IsNullOrEmpty(install)) //No install info; it must be later.
                            list.Add(new VerInfo { Name = name, Version = ver, Sp = sp });
                        else if (!String.IsNullOrEmpty(sp) && install == "1")
                            list.Add(new VerInfo { Name = name, Version = ver, Sp = sp });
                        else if (install == "1")
                            list.Add(new VerInfo { Name = name, Version = ver, Sp = sp });
                    }
                }
            }
        }
#endif

        return list;
    }

    /// <summary>获取Net45版本</summary>
    /// <returns></returns>
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public static IList<VerInfo> Get45PlusFromRegistry()
    {
        var list = new List<VerInfo>();
        if (!IsWindows) return list;

#if NET45_OR_GREATER || NET6_0_OR_GREATER
        const String subkey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\";

        using var ndpKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subkey);

        if (ndpKey == null) return list;

        //First check if there's an specific version indicated
        var name = "";
        var value = "";
        var ver = ndpKey.GetValue("Version");
        if (ver != null) name = ver.ToString();
        var release = ndpKey.GetValue("Release");
        if (release != null)
            value = CheckFor45PlusVersion((Int32)ndpKey.GetValue("Release"));

        if (String.IsNullOrEmpty(name)) name = value;
        if (String.IsNullOrEmpty(value)) value = name;
        if (!String.IsNullOrEmpty(name)) list.Add(new VerInfo { Name = "v" + value, Version = name });

        // Checking the version using >= enables forward compatibility.
        static String CheckFor45PlusVersion(Int32 releaseKey) => releaseKey switch
        {
            >= 533325 => "4.8.1",
            >= 528040 => "4.8",
            >= 461808 => "4.7.2",
            >= 461308 => "4.7.1",
            >= 460798 => "4.7",
            >= 394802 => "4.6.2",
            >= 394254 => "4.6.1",
            >= 393295 => "4.6",
            >= 379893 => "4.5.2",
            >= 378675 => "4.5.1",
            >= 378389 => "4.5",
            _ => ""
        };
#endif

        return list;
    }

    /// <summary>获取NetCore版本</summary>
    /// <returns></returns>
    public static IList<VerInfo> GetNetCore(Boolean exact = true)
    {
        var list = new List<VerInfo>();

        var dir = "";
        if (IsWindows)
        {
            dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (String.IsNullOrEmpty(dir)) return null;
            dir += "\\dotnet\\shared";
        }
        else
            dir = "/usr/share/dotnet/shared";

        var dic = new SortedDictionary<String, VerInfo>();
        var di = new DirectoryInfo(dir);
        if (di.Exists)
        {
            foreach (var item in di.GetDirectories())
            {
                foreach (var elm in item.GetDirectories())
                {
                    var name = "v" + elm.Name;
                    if (exact)
                    {
                        if (item.Name.Contains("AspNet"))
                            name += "-aspnet";
                        else if (item.Name.Contains("Desktop"))
                            name += "-desktop";
                    }
                    else if (name.Contains("-"))
                        continue;

                    if (!dic.ContainsKey(name))
                    {
                        dic.Add(name, new VerInfo { Name = name, Version = item.Name + " " + elm.Name });
                    }
                }
            }
        }

        foreach (var item in dic)
        {
            list.Add(item.Value);
        }

        // 通用处理
        if (list.Count == 0)
        {
            var infs = Execute("dotnet", "--list-runtimes")?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (infs != null)
            {
                foreach (var line in infs)
                {
                    var ss = line.Split(' ');
                    if (ss.Length >= 2)
                    {
                        var name = "v" + ss[1];
                        var ver = $"{ss[0]} {ss[1]}";
                        if (exact)
                        {
                            if (ver.Contains("AspNet"))
                                name += "-aspnet";
                            else if (ver.Contains("Desktop"))
                                name += "-desktop";
                        }
                        else if (name.Contains("-"))
                            continue;

                        VerInfo vi = null;
                        foreach (var item in list)
                        {
                            if (item.Name == name)
                            {
                                vi = item;
                                break;
                            }
                        }
                        if (vi == null)
                        {
                            vi = new VerInfo { Name = name, Version = ver };
                            list.Add(vi);
                        }

                        if (vi.Version.Length < ver.Length) vi.Version = ver;
                    }
                }
            }
        }

        return list;
    }

    private static String Execute(String cmd, String arguments = null)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, arguments)
            {
                // UseShellExecute 必须 false，以便于后续重定向输出流
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                //RedirectStandardError = true,
            };
            var process = Process.Start(psi);
            if (!process.WaitForExit(3_000))
            {
                process.Kill();
                return null;
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch { return null; }
    }
    #endregion

    #region 辅助
    /// <summary>是否Windows</summary>
    public static Boolean IsWindows => Environment.OSVersion.Platform <= PlatformID.WinCE;

    /// <summary>获取文件MD5</summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public static String GetMD5(String fileName)
    {
        var fi = new FileInfo(fileName);
        var md5 = MD5.Create();
        using var fs = fi.OpenRead();
        var buf = md5.ComputeHash(fs);
        var hex = BitConverter.ToString(buf).Replace("-", null);

        return hex;
    }

    /// <summary>加载内嵌的文件MD5信息</summary>
    /// <returns></returns>
    public static IDictionary<String, String> LoadMD5s()
    {
        var asm = Assembly.GetExecutingAssembly();
        var ms = asm.GetManifestResourceStream(typeof(NetRuntime).Namespace + ".res.md5.txt");

        var dic = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(ms);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine()?.Trim();
            if (String.IsNullOrEmpty(line)) continue;

            var ss = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (ss.Length >= 2)
            {
                dic[ss[0]] = ss[1];
            }
        }

        return dic;
    }

    /// <summary>安装微软根证书</summary>
    /// <returns></returns>
    public Boolean InstallCert()
    {
        WriteLog("准备安装微软根证书");

        // 释放文件
        var asm = Assembly.GetExecutingAssembly();
        var names = new[] { "CertMgr.Exe", "MicrosoftRootCertificateAuthority2011.cer" };
        foreach (var name in names)
        {
            var ms = asm.GetManifestResourceStream(typeof(NetRuntime).Namespace + ".res." + name);
            if (ms != null)
            {
                var buf = new Byte[ms.Length];
                ms.Read(buf, 0, buf.Length);

                File.WriteAllBytes(name, buf);
            }
        }

        var exe = names[0];
        var cert = names[1];
        if (!File.Exists(exe) || !File.Exists(cert)) return false;

        // 执行
        try
        {
            var p = Process.Start(exe, $"-add \"{cert}\" -s -r localMachine AuthRoot");

            return p.WaitForExit(30_000) && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            WriteLog(ex.Message);
            return false;
        }
        finally
        {
            if (File.Exists(cert)) File.Delete(cert);
            if (File.Exists(exe)) File.Delete(exe);
        }
    }
    #endregion

    #region 日志
    ///// <summary>性能追踪</summary>
    //public ITracer Tracer { get; set; }

    /// <summary>日志</summary>
    public ILog Log { get; set; }

    /// <summary>写日志</summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public void WriteLog(String format, params Object[] args)
    {
        Log?.Info($"[NetRuntime]{format}", args);

        var msg = (args == null || args.Length == 0) ? format : String.Format(format, args);
        DefaultSpan.Current?.AppendTag(msg);

        if (format.Contains("错误") || format.Contains("失败"))
            EventProvider?.WriteErrorEvent(nameof(ServiceController), msg);
        else
            EventProvider?.WriteInfoEvent(nameof(ServiceController), msg);
    }
    #endregion
}
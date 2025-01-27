﻿using System.ComponentModel;
using NewLife.Configuration;
using Stardust.Models;

namespace StarAgent;

/// <summary>配置</summary>
[Config("StarAgent")]
public class StarAgentSetting : Config<StarAgentSetting>
{
    #region 属性
    /// <summary>调试开关。默认true</summary>
    [Description("调试开关。默认true")]
    public Boolean Debug { get; set; } = true;

    /// <summary>证书</summary>
    [Description("证书")]
    public String Code { get; set; }

    /// <summary>密钥</summary>
    [Description("密钥")]
    public String Secret { get; set; }

    ///// <summary>本地服务。默认udp://127.0.0.1:5500</summary>
    //[Description("本地服务。默认udp://127.0.0.1:5500")]
    //public String LocalServer { get; set; } = "udp://127.0.0.1:5500";

    /// <summary>本地端口。默认5500</summary>
    [Description("本地端口。默认5500")]
    public Int32 LocalPort { get; set; } = 5500;

    /// <summary>更新通道。默认Release</summary>
    [Description("更新通道。默认Release")]
    public String Channel { get; set; } = "Release";

    /// <summary>延迟时间。重启进程或服务的延迟时间，默认3000ms</summary>
    [Description("延迟时间。重启进程或服务的延迟时间，默认3000ms")]
    public Int32 Delay { get; set; } = 3000;

    /// <summary>应用服务集合</summary>
    [Description("应用服务集合")]
    public ServiceInfo[] Services { get; set; }
    #endregion

    #region 构造
    #endregion

    #region 方法
    protected override void OnLoaded()
    {
        if (Services == null || Services.Length == 0)
        {
            var si = new ServiceInfo
            {
                Name = "test",
                FileName = "ping",
                Arguments = "newlifex.com",

                Enable = false,
            };
            var si2 = new ServiceInfo
            {
                Name = "test2",
                FileName = "cube.zip",
                Arguments = "urls=http://*:1080",
                WorkingDirectory = "../sso/web/",

                Enable = false,
            };

            Services = new[] { si, si2 };
        }

        base.OnLoaded();
    }
    #endregion
}
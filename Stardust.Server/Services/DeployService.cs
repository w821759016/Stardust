﻿using NewLife;
using System.Xml.Linq;
using Stardust.Data;
using Stardust.Data.Deployment;
using Stardust.Data.Nodes;
using Stardust.Models;

namespace Stardust.Server.Services;

public class DeployService
{
    private readonly RegistryService _registryService;

    public DeployService(RegistryService registryService)
    {
        _registryService = registryService;
    }

    /// <summary>更新应用部署的节点信息</summary>
    /// <param name="online"></param>
    public void UpdateDeployNode(AppOnline online)
    {
        if (online == null || online.AppId == 0 || online.NodeId == 0) return;

        // 提出StarAgent
        if (online.AppName == "StarAgent") return;

        // 找应用部署。此时只有应用标识和节点标识，可能对应多个部署集
        var list = AppDeploy.FindAllByAppId(online.AppId);
        if (list.Count == 0)
        {
            // 根据应用名查找
            var deploy = AppDeploy.FindByName(online.AppName);
            if (deploy != null)
            {
                // 部署名绑定到别的应用，退出
                if (deploy.AppId != 0 && deploy.AppId != online.AppId) return;

                // 当顶当前应用
                deploy.AppId = online.AppId;
                deploy.Update();
            }
            else
            {
                // 新增部署集，禁用状态，信息不完整
                deploy = new AppDeploy
                {
                    AppId = online.AppId,
                    Name = online.AppName,
                    Category = online.App?.Category
                };
                deploy.Insert();
            }
            list.Add(deploy);
        }

        // 查找节点。借助缓存找到启用的那一个部署节点，去更新它的信息。如果有多个无法识别，则都更新一遍
        //var nodes = AppDeployNode.Search(list.Select(e => e.Id).ToArray(), online.NodeId, null, null);
        var nodes = list.SelectMany(e => e.DeployNodes).Where(e => e.NodeId == online.NodeId).ToList();
        var node = nodes.FirstOrDefault(e => e.Enable);

        // 自动创建部署节点，更新信息
        if (node != null)
        {
            node.Fill(online);
            node.Update();
        }
        else
        {
            // 由于无法确定发布集，所以创建所有发布集的节点。此时不能启用，否则下一次应用启动时，将会拉取到该部署信息，而此时部署信息还不完整
            foreach (var deploy in list)
            {
                node = nodes.FirstOrDefault(e => e.AppId == deploy.Id);
                node ??= new AppDeployNode { AppId = deploy.Id, NodeId = online.NodeId, Enable = false };
                node.Fill(online);
                node.Save();
            }
        }
        {
            // 定时更新部署信息
            foreach (var deploy in list)
            {
                if (deploy.UpdateTime.AddHours(1) < DateTime.Now) deploy.Fix();
            }
        }
    }

    public void WriteHistory(Int32 appId, Int32 nodeId, String action, Boolean success, String remark, String ip)
    {
        var hi = AppDeployHistory.Create(appId, nodeId, action, success, remark, ip);
        hi.SaveAsync();
    }

    public Int32 Ping(Node node, AppInfo inf, String ip)
    {
        var name = !inf.AppName.IsNullOrEmpty() ? inf.AppName : inf.Name;
        if (name.IsNullOrEmpty()) return -1;

        // 应用
        var ap = App.FindByName(name);
        if (ap == null)
        {
            ap = new App { Name = name };
            ap.Insert();
        }
        {
            var clientId = $"{inf.IP?.Split(',').FirstOrDefault()}@{inf.Id}";
            _registryService.Ping(ap, inf, ip, clientId, null);
            AppMeter.WriteData(ap, inf, "Deploy", clientId, ip);
        }

        // 部署集
        var app = AppDeploy.FindByName(name);
        app ??= new AppDeploy { Name = name };
        app.AppId = ap.Id;
        if (!ap.Category.IsNullOrEmpty()) app.Category = ap.Category;
        app.Save();

        // 本节点所有发布
        var list = AppDeployNode.FindAllByNodeId(node.ID);
        var dn = list.FirstOrDefault(e => e.AppId == app.Id);
        dn ??= new AppDeployNode { AppId = app.Id, NodeId = node.ID };

        dn.Fill(inf);
        dn.LastActive = DateTime.Now;

        return dn.Update();
    }
}
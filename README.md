# TarkovMapLocator 使用说明

TarkovMapLocator 是一个用于《逃离塔科夫》的 WinUI 桌面工具。当前版本主界面为原生 Windows 界面，内置 Python 本地 API 后端，不再提供旧网页浏览器界面。

## 安装与启动

1. 下载并运行发布包中的 `TarkovMapLocator.exe`。
2. 首次启动后，工具会打开原生 WinUI 主窗口。
3. 程序会自动启动内置 Python API 后端，用于跳蚤市场价格、任务物品清单和本地缓存。

用户无需单独安装 Python、.NET、Windows App SDK 或 WebView2。

## 项目结构

- `src\TarkovMapLocator.App`：WinUI 3 桌面主程序。
- `src\TarkovMapLocator.Core`：地图、日志等可复用 C# 核心逻辑。
- `src\TarkovMapLocator.Updater`：覆盖式更新器。
- `backend\python`：内置 Python 本地 API 后端源码。
- `runtime\python`：随程序分发的 Python 运行时。
- `assets`：地图图片、地图缓存和点位图标资源。
- `data`：地图元数据 JSON。
- `installer`：安装包和更新包构建脚本。

## 地图

- 支持自动选择地图和手动选择地图。
- 支持读取截图文件名中的坐标并显示玩家位置。
- 支持读取游戏 Log 判断战局地图和战局状态。
- 支持撤离点、转移点、标签和其他点位显示。
- 支持画中画小地图、队友位置同步和屏幕滤镜。

截图目录和 Log 目录会自动检测，也可以在界面里手动选择。目录选择会写入本机用户配置，下次启动自动记住。

## 跳蚤市场价格

- 支持 PVP / PVE 价格模式。
- 支持中文、英文、简称和物品 ID 搜索。
- 数据由内置 Python API 后端从 tarkov.dev 获取并缓存。
- 刷新价格需要联网；已有缓存时可以继续显示缓存结果。

## 任务物品清单

- 支持 PVP / PVE 模式。
- 支持任务、藏身处筛选和排序。
- 支持记录已有数量、标记具体来源完成状态。
- 数据和完成状态保存在本机用户配置目录中。

## 队友同步

队友同步使用 TCP 连接，默认本机端口为：

```text
39247
```

房主开启房间后，可以用 Sakura Frp 或其他内网穿透工具转发该端口，再把外网地址发给队友。队友在“我要加入队友”里输入房主给出的地址即可。

## 更新

如果使用更新器分发新版，用户先关闭 TarkovMapLocator，再运行更新器即可覆盖安装目录里的程序文件。用户配置保存在 AppData，不会被覆盖。

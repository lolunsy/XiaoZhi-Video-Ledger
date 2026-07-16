# 小智剪辑分类账

一款面向剪辑工作的 Windows 视频素材整理工具，用卡片和文件夹视图快速浏览素材，并记录素材的使用状态。

## 当前版本

`v0.1.0 正式版`

请前往本仓库的 [Releases](https://github.com/lolunsy/XiaoZhi-Video-Ledger/releases) 页面下载 Windows x64 发布包。解压后运行 `小智剪辑分类账.exe`，无需安装。

当前正式版兼容 Windows 10 与 Windows 11，界面图标不依赖 Win11 字体；主窗口使用克制圆角、柔和双层投影和轻边框，最大化时会自动贴合当前显示器工作区，不会覆盖任务栏。

公开版使用本项目独立设计的“播放＋素材轨迹”标识，不包含公司内部使用的 Logo 或图标。

## 主要能力

- 扫描视频素材目录并生成代表帧
- 按文件夹、状态、修改时间筛选素材
- 记录未使用、已使用、备选和不考虑状态
- 拖拽素材到剪映等剪辑软件，同时记录使用次数
- 悬停浏览视频画面，快速预览和定位原文件
- 多项目管理、自动监控与数据迁移备份

## 发布说明

本仓库同时提供完整 C# / WPF 源代码、测试代码、构建脚本以及正式版下载，不包含任何个人账本、素材文件、缓存或本地配置。

## 源码结构

- `src/XiaoZhiLedger.App`：WPF 桌面应用与界面
- `src/XiaoZhiLedger.Core`：扫描、存储、FFmpeg 和核心业务逻辑
- `tests/XiaoZhiLedger.Core.Tests`：数据兼容性与核心功能检查
- `publish.ps1`：Windows x64 正式包构建脚本

## 构建

需要 .NET 8 SDK：

```powershell
dotnet build XiaoZhiLedger.sln -c Release
dotnet run --project tests/XiaoZhiLedger.Core.Tests -c Release
```

源码公开用于查看和学习，不代表授予复制、修改、二次分发或商业使用权，具体以仓库中的 `LICENSE` 为准。

作者：[lolunsy](https://github.com/lolunsy)

Copyright © 2026 lolunsy. All rights reserved. 未经作者许可，请勿二次打包、转售或以其他作者名义重新发布。

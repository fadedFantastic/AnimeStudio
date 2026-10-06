# Fork 维护方式

## 已配置的结构

- `origin`：`https://github.com/fadedFantastic/AnimeStudio.git`
- `upstream`：`https://github.com/Escartem/AnimeStudio.git`
- `master`：保持上游原版，本次已快进到 `db860e1`。
- `codex/asset-explorer`：自用功能分支，保留 Asset Explorer、分离网格导出、CLI 索引选项和打包修复，并整合上述上游版本。

本次只在本地保存提交和合并，没有推送。核对时 GitHub 上的 `origin/master` 仍是 `a4220d4`，比本地 master 落后 56 个上游提交。网页 Sync Fork 或之后推送本地 master 都可以更新它；远端状态变化后应重新 fetch 核对。

原始修改先按 CLI/资源分析工具、Asset Explorer、打包修复三个提交保存，再整合上游，便于后续追踪和移植。

## 整合时保留的边界

- 新窗口、索引、路径恢复、依赖加载和模型导出集中在 `AnimeStudio.AssetExplorer`，通过 `MainForm.AssetExplorer.cs` 连接 Studio。
- 批量加载复用上游 `LoadFiles(files, mergeSplitAssets: false)`，已去掉重复的 `LoadFilesPreprocessed` 实现。
- 显式 bundle 偏移仅扩展到 ZZZ，原有 Endfield 保持支持，其他游戏保持上游行为。
- `AssetsHelper` 保留上游逐 bundle 回调、流式输出和 HSR 内存释放修复。可选 hash/预读配置及辅助类放在 `AssetsHelper.IndexingOptions.cs`，预读默认关闭。
- CLI 的 `--no_hash` 和 `--read_ahead` 已移植到上游新的 System.CommandLine API。
- `build.ps1` 保留上游原生 DLL 打包规则，只修改发布副本的 apphost。编译目录里的 GUI/CLI 仍可直接启动。可用 `-OutputRoot` 指定独立的打包目录。
- 真实动画测试发现上游默认旧 ZZZ 解码路径遗漏标量曲线，因此启用了上游已有的 V2 解码器，修正 V2 Dispose 的 DLL 绑定与输入缓冲区处理，并验证解码样本数量。其他游戏的解码分支未切换。
- 原生解码器改为对应上游的新 DLL 布局，测试项目通过项目引用获取原生依赖，不再复制已删除的 x86/x64 目录。

这些是后续合并需要重点保留的少量接入点。不要用整文件的 ours/theirs 覆盖核心加载器、动画解码器或打包脚本。

## 日常同步

先确认工作区修改已经保存在功能分支，工作区干净，再执行：

```powershell
git fetch upstream
git switch master
git merge --ff-only upstream/master
git switch codex/asset-explorer
git merge master
```

有冲突时手工整合，完成构建和测试后提交合并。`master` 不放自用功能，便于持续使用 GitHub 的 Sync Fork；功能都在 `codex/asset-explorer` 上维护。

准备上传时再执行以下命令，本次没有执行：

```powershell
git push origin master
git push -u origin codex/asset-explorer
```

不需要强制推送，也不需要重新 clone。GitHub 默认展示 master，查看自用功能时切换到 `codex/asset-explorer`。

## 构建和验证

```powershell
dotnet build AnimeStudio.GUI -c Release -f net10.0-windows
dotnet build AnimeStudio.CLI -c Release -f net10.0-windows
dotnet run --project tests/AnimeStudio.AssetExplorer.Tests -c Release -- --test asset-explorer-test-output
```

保留 .NET 9 发布时同时构建该目标。打包可运行 `build.ps1`；验证包建议通过 `-OutputRoot` 指定临时输出位置，避免覆盖已有发布包中的 Maps 数据。

本次用正常 NuGet 恢复完成 .NET 9/10 构建及打包；在 .NET 10 下运行回归测试、验证 CLI hash 开关，并实际导出 Remielle 的 27 个网格和额外选择的 Gal_Idle 动画。动画解码输出为 242 帧 × 3918 条曲线，未丢弃标量曲线。.NET 9 未做运行验证。

新增 `.github/workflows/asset-explorer.yml`，在功能分支推送、PR 或手动触发时检查 GUI/CLI 的 .NET 9/10 构建及 .NET 10 回归。上游原来的 Build 工作流保持不变，发布功能分支时可以手动选择该分支触发。

## 上传范围

只上传源码、测试、脚本和文档。`dist/`、`bin/obj`、`zzz-map/`、默认测试输出和本机配置均已忽略；游戏清单、下载字典、FBX/贴图和编译产物不应进入 Git。

# Fork 上传与同步审查

## 当前仓库来源

本地 reflog 的最初记录是 `clone: from https://github.com/Escartem/AnimeStudio.git`。
当前唯一配置的远端 `origin` 也指向 Escartem，上游历史已获取，但本轮没有修改远端地址、切换分支、合并、提交或推送。

本次核对的提交位置：

| 位置 | master 提交 | 关系 |
| --- | --- | --- |
| 本地工作区基线 | `7cfb26b` | 比 fadedFantastic/master 落后 6 个提交 |
| fadedFantastic/AnimeStudio | `a4220d4` | 比 Escartem/master 落后 56 个提交，无独有提交 |
| Escartem/AnimeStudio | `db860e1` | 比本地基线领先 62 个提交 |

这些数字对应上述提交快照，后续远端更新后需重新核对。

## 本轮清理与上传范围

- 已清理本次开发的六个模型验证输出目录，以及已使用完毕的一次性清理脚本。
- 正式程序仍在 `AnimeStudio.GUI/bin/Release/net10.0-windows`；现有 `dist/net10.0-windows` 发布包及其 Maps 目录保留。
- `dist/`、编译产生的 `bin/obj` 原本已忽略；新增 `/zzz-map/`、`/asset-explorer-test-output/` 和 `/.claude/settings.local.json` 忽略规则。
- `zzz-map/Maps/Z3-AssetIndex-Eleiyas.json` 是约 68 MB 的下载缓存，保留本地使用，不上传。
- 上传内容应为新模块、GUI 适配层、回归测试、源码修改、工具脚本和文档；不包含游戏资源、清单缓存、导出的 FBX/贴图和可执行文件。

## 同步冲突预演

使用本地基线、当前工作区文件和远端文件进行 `git merge-file -p` 三方预演，没有执行真实合并。

| 本地改动文件 | 合并 fadedFantastic/master | 合并 Escartem/master |
| --- | --- | --- |
| `AnimeStudio/AssetsHelper.cs` | 4 处冲突 | 4 处冲突 |
| `build.ps1` | 3 处冲突 | 3 处冲突 |
| `AnimeStudio.CLI/Components/CommandLine.cs` | 文本合并干净 | 3 处冲突 |
| `AnimeStudio.GUI/MainForm.cs` | 文本合并干净 | 4 处冲突 |
| `AnimeStudio.slnx` | 文本合并干净 | 1 处冲突 |

其余已修改的受跟踪文件文本合并干净；新增文件未发现同路径冲突。
这只是文本合并检查，不等于合并后编译或运行通过。

## 关键改动与维护建议

### 核心加载器

`AssetsManager.cs` 增加了 `LoadFilesPreprocessed`，并把显式 bundle 偏移应用范围从 Endfield 扩大到了所有游戏。后者是运行行为变化，不能作为纯 UI 改动看待；目前主要验证了 ZZZ。整合时建议限制为 ZZZ 与原有 Endfield，或改成新工具显式启用的选项。

上游现在已有 `LoadFiles(string[] files, bool mergeSplitAssets)`。整合后可以改用 `mergeSplitAssets: false`，减少维护自定义 `LoadFilesPreprocessed` 接口的必要性。

### 核心索引与旧 CLI 优化

`AssetsHelper.cs` 的跳过 hash、预读和批量预处理来自此前的 CLI 索引优化，新 Asset Explorer 的扫描器并不依赖这些开关。上游已把旧索引循环改成逐 bundle 回调和流式写出，并加入 HSR 大文件的内存释放修复。

不能在冲突时整文件选择本地版本，否则可能丢掉上游的 OOM 修复。建议把旧 CLI 优化作为独立提交组；保留时应移植到上游新流程中。`ReadAhead` 默认为 0，可独立考虑是否继续维护。

### GUI 接入

大部分新逻辑在 `AnimeStudio.AssetExplorer` 和 `MainForm.AssetExplorer.cs` 中；原有 `MainForm.cs` 只增加初始化入口，并把资产结构构建改成可等待的 Task。继续保持这种边界，不要把新功能大面积塞回上游窗体文件。

最新上游已经迁移了 GUI 图像处理依赖，并调整了原生 DLL 的布局。即便项目文件自动合并成功，也需要重新构建和验证 FBX、动画、贴图及音频加载。测试项目中的原生 DLL 复制路径也要随上游调整。

### 打包脚本

本地修复把 apphost 路径补丁限定到 dist 副本，使编译目录中的 GUI/CLI 仍可直接运行。你的 fork 中已经有另一套防止 `bin\\bin\\` 叠加的修复，最新上游还调整了 DLL 拷贝与目录整理。

合并时保留新的原生库打包要求，同时保留“只修改发布副本”的约束；不要用本地旧脚本覆盖整个上游脚本。

## 推荐维护方式

1. 在准备提交时先将本地功能保存到独立功能分支，按 CLI 优化、Asset Explorer、打包修复分组，便于逐组移植。本次审查没有创建提交。
2. 把远端整理为自己的 `origin` 和上游 `upstream`。当前配置下对应命令如下，尚未执行：

   ```powershell
   git remote rename origin upstream
   git remote add origin https://github.com/fadedFantastic/AnimeStudio.git
   git fetch origin
   ```

3. 尽量让 fork 的 `master` 保持与上游同步，自用功能维护在独立分支。当前 fork 没有独有提交，可先同步其 master；然后将功能分支与最新上游合并，手工处理上表冲突。
4. 未保存当前修改前，不要直接覆盖或重置工作区。也不需要重新 clone，更不应靠强制推送解决历史不同的问题。
5. 合并后重新执行 .NET 10 构建和回归测试；保留 .NET 9 发布目标时也要验证它。另用真实 ZZZ 模型验证分离网格、所选动画与贴图输出，以及编译目录/发布目录两个启动入口。

   ```powershell
   dotnet build AnimeStudio.GUI -c Release -f net10.0-windows
   dotnet run --project tests/AnimeStudio.AssetExplorer.Tests -- --test asset-explorer-test-output
   ```

6. 当前 GitHub Actions 只在 master 推送时自动构建。功能分支可以手动触发 workflow_dispatch；若需要功能分支自动验证，再单独增加相应触发条件。

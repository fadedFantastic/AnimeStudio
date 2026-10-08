# Asset Explorer

Studio 内的新入口：**Maps → Open Asset Explorer (Fast)**。原来的 Asset Browser 保留用于上游兼容和版本差异比较。

也可以运行 `tools/Start-AssetExplorer.ps1`，直接打开与 Studio 联动的窗口。

## 使用

1. 打开已有 `.json`、`.tsv`、`.map` 或新格式 `.aex` 清单。首次运行自动读取旧 ZzzMap 的清单位置、游戏目录和路径字典设置。
2. 搜索资源名、路径或 blk，按类型筛选。普通搜索忽略大小写；正则有超时保护。查询在后台运行，输入新条件会取消旧查询。
3. 选中 Animator、GameObject 或有模型层级的 Mesh。点 **Load Selected** 后返回 Studio，Asset List 和 Scene Hierarchy 都已填充，可继续使用原来的操作。
4. 点 **一键导出 FBX + 动画 + 贴图**，选择输出目录即可。无需手动展开 blk 和 CAB，也无需在树中逐个勾选。

需要额外动画时，先将模型加入“导出列表”，再搜索 AnimationClip 并加入。导出列表跨搜索保留；列表非空时，Load 和两个导出按钮都使用该列表，否则使用当前表格选中行。双击资源也可加入列表。

一键 FBX 导出会收集模型层级、同 CAB 动画、AnimatorController/OverrideController 引用，以及额外选中的 AnimationClip。它强制启用 FBX 动画和蒙皮，其他 FBX 参数沿用 Studio 的 Export Options。

ZZZ 的部分原始模型（例如 `Avatar_Female_Size02_Remielle_Origin_Model.fbx`）只保存骨架，Renderer 的 Mesh 指针被清空。新工具会从当前清单查找 `DiscreteMeshAssets/<模型版本>/<部件>.mesh`，验证资源路径或 container hash，加载并绑定分离网格；不会拿同名的其他皮肤或 LOD 替代。请使用完整清单。模型源文件本身没有动画或控制器引用时，需把需要的 AnimationClip 加入导出列表。

每个模型输出到独立文件夹，包含 FBX、材质 JSON、PNG 贴图和 `export-report.json`。ZZZ 有些材质/贴图在运行时绑定，因此额外保存加载到的 CAB 依赖材质和贴图；不会猜测这些动态绑定的着色器连接。依赖集中可能包括共享效果贴图。缺失依赖、找不到所选对象、指定动画没有有效轨道或贴图转换失败会明确报错。失败/取消后的文件夹保留 `export-incomplete.txt`，不可视为完整导出。

“导出选中资源”保留普通资源转换用途，例如单独导出纹理、音频、文本和 Mesh；AnimationClip 配合模型嵌入 FBX 请使用一键 FBX 按钮。

## 按资源目录批量导出

点击 **目录批量导出…**，或右键一条资源选择 **收集/导出所在资源目录…**。
这里填写清单中的 `Assets/...` 路径，例如 `Assets/OriginalResRepos/ART/Char/Avatar/Female/Female_Size02/Remielle/UIAni`，不是磁盘上的 Blocks 文件夹。

1. 保留“包含子目录”可递归收集，取消则仅收集该目录直接包含的资源。
2. 点击“收集目录”预览主资源文件和对象数；收集不受主窗口当前搜索、类型筛选或导出列表影响。
3. 点击“一键导出目录…”选择保存位置。也可以直接点击导出，由工具先自动收集。

同一主资源路径下的所有子资源归入一个任务。FBX 的子 AnimationClip 会写进其自己的 FBX，不会另存为散落的 `.anim`，也不会混入其他 FBX。没有网格的动画 FBX 使用其自身骨架导出。依赖材质、贴图会放在同一主资源的输出目录中。

输出保留所选目录下的相对层级；每个主资源使用独立的 `.export-<标识>` 文件夹，避免同名贴图互相覆盖。FBX 先在短临时路径生成再迁移，支持较长的目标路径。普通单对象资源沿用现有格式转换；没有通用原格式写入器的 Unity 复合资源（如 prefab/asset）会将主对象、子对象、原始对象数据及可取得的外部资源数据归档到一个 JSON 中，这些 JSON 不作为 Unity 原始工程文件直接导入。

“并发数”默认 2，可设置 1～8，关闭窗口后保存。每个独立工作进程逐个加载主资源并释放内存，复用自己的 CAB 依赖索引；FBX 和所属子动画始终由同一进程整体导出。工作进程按需查询主窗口的分离网格索引，不重复加载整份资源清单。机械硬盘或内存紧张时建议设为 1；增加并发不保证线性提速。取消会停止全部工作进程并保留完成的文件，失败资源会记录后继续处理；查看输出根目录的 `export-manifest.json` 和 `results.jsonl`。未完成的资源目录保留 `export-incomplete.txt`。

只有真实的已还原路径参与收集，`~` 推测目录和仍是数字 hash 的路径不会被当作已知目录。同一分离网格路径若对应多个不同对象，会报告歧义，不任意选取；本机 Ramiel Body_2 数据存在这种情况，需要更新/核对清单。

验收：递归导出 Remielle/UIAni 下 44 个主 FBX，逐个验证子动画已嵌入、没有独立 `.anim` 文件；Materials 下 40 个主资源导出成功。另通过目录边界、大小写/分隔符、非递归、去重、文件名冲突、取消和部分失败报告检查。

并发验收（2026-10-07）：Remielle/UIAni 的 44 个 FBX，单进程约 170 秒、双进程约 110 秒，耗时减少约 35%。两次运行的主资源路径、子动画清单、网格数和输出文件清单一致，均检查 FBX 中实际存在 AnimationStack。此为本机数据，受磁盘缓存、资源大小和其他负载影响；双进程测试使用 .NET 9 构建在 .NET 10 运行时上运行，单进程使用 .NET 10 构建。回归覆盖并发上限、日志一致性、失败继续、取消全部活动任务；实际进程测试验证取消和工作进程异常退出后重建。

## Unity 动画根节点兼容

动画 FBX 的外层场景容器现在保留为 Null 节点，子骨骼继续导出为骨骼节点。
先前无网格动画会把外层容器一并转成骨骼，Unity 保留它后，曲线路径多出 `Avatar_…_Ani_…/`，无法匹配模型的 `Bone_Root/…`。
本次通过可选的 `preserveRootNodeAsNull` 修复 Asset Explorer 的导出；上游普通导出的默认行为不变，无需修改原生 FBX DLL。
旧动画文件需要重新导出。动画文件本身没有网格时，Unity 预览需要指定对应的模型。

验收：Unity 2022.3.61f1 中主攻击动画的 389 条节点路径全部匹配模型，采样时 350 个节点发生运动，四段子动画均通过绑定检查。回归检查验证 FBX 根节点类型和动画保留。`tests/UnityFbxValidation/Editor/FbxPlaybackValidation.cs` 可放入临时 Unity 工程复验实际导入与播放。

## 索引与路径

- 首次导入大清单会生成压缩二进制缓存；之后按源文件路径、大小和修改时间验证缓存。表格采用 ListView VirtualMode，不为数百万条结果创建 UI 行对象。
- **建立 / 更新索引**扫描所选目录下的 `.blk`。元数据和 CAB 依赖在一次读取中提取，不计算逐对象内容 hash，不预读抢占机械硬盘。
- 每个 blk 有独立缓存，比较大小、修改时间、游戏类型和完整索引选项。未变化的文件直接复用；变化的文件重新扫描；删除的文件不会进入新索引。
- 取消后保留已完成的 blk 缓存；完整索引与 CABMap 写完后才发布。扫描失败的文件列在索引旁的 `scan-errors.txt`，再次更新会重试它们。
- 新数据默认位于 `%LOCALAPPDATA%\AnimeStudio\AssetExplorer`。旧的 `%LOCALAPPDATA%\AnimeStudio\ZzzMap` 清单和字典仅用于迁移读取，保留原文件。
- 路径字典目录支持 `Z3-AssetIndex-Eleiyas.json` 和 `Z3-AssetIndex-Recovered.json`。点击“补全路径”复用原 ZzzMap 的 hash 验证与目录推导；`~` 表示推测目录，推测值不参与资源定位。
- 自动查找清单同目录或相邻 `Maps` 目录的同名 `.bin`；也可手动指定。游戏移动后，将“资源根目录”改为新的 Blocks 路径，按 CABMap 中的相对目录定位。
- 同一 CAB 的多个 blk 副本仍保留各自的资源位置。若旧 CABMap 只记录了另一份副本，会读取所选 bundle 的头部补齐依赖。一次操作中不要同时选择同一 CAB 的不同副本。

## 与上游的边界

功能代码在独立项目 `AnimeStudio.AssetExplorer`。GUI 中只有项目引用、初始化入口、可等待的资产树构建和独立的 `MainForm.AssetExplorer.cs` 适配层。扫描和目录导出运行在子进程中，避免上游 Logger、TypeFlags、CABMap 等静态状态干扰 Studio。

核心加载器另外修复了一处问题：手动 bundle 偏移原先只对 Endfield 生效，ZZZ 会读取整个 blk。现在仅将支持范围扩展至 ZZZ，其他游戏保持上游行为。批量加载复用上游的 `LoadFiles(files, mergeSplitAssets: false)` 接口。资源身份使用源文件、bundle 偏移、CAB 和 PathID，名称不作为身份。

合并最新上游后，启用了其 ZZZ V2 动画解码器，并修正 V2 缓冲区释放函数的 DLL 绑定。解码后会验证帧数、曲线数和样本数一致，避免遗漏标量曲线后在 FBX 转换中越界。

## 本机验收（2026-10-06）

- 真实旧清单：8,956,455 条，约 3.3 GB JSON；首次导入并缓存约 37 秒，缓存约 99 MB，再次载入约 10 秒。
- 缓存载入后，普通全类型搜索 `Anby` 约 97 ms；限定 Animator 约 10 ms。时间随硬件和缓存状态变化。
- 真实 blk：首次扫描成功，第二次复用缓存；修改测试副本时间后重新扫描；取消不发布不完整清单。
- 真实模型测试：基础依赖加载 158 个 CAB 约 1 秒；补齐 Palicus 的 9 个分离网格后，导出 18 个网格、109 段动画、38 张贴图，并检查 FBX 中的 AnimationStack。
- Remielle 原始模型测试：补齐 27 个分离网格，成功导出全部 27 个部件；额外选择 `Avatar_Female_Size02_Remielle_Origin_Ani_Gal_Idle` 后，动作成功嵌入同一个 FBX。
- 回归检查覆盖紧凑/缩进 JSON、二进制身份保留、大小写/类型/blk/正则筛选、取消、跨 blk 循环依赖、同 blk 多偏移、路径迁移、缺失依赖、路径 hash 补全。
- 调用 Studio 实际的资产结构构建流程后，Scene Hierarchy 中包含模型，Asset List 中包含动画。
- 真实重复 CAB 副本通过依赖解析和加载验证，所加载对象来自用户选中的物理 blk。

标准构建：`dotnet build AnimeStudio.GUI -c Release -f net10.0-windows`。

回归检查：`dotnet run --project tests/AnimeStudio.AssetExplorer.Tests -- --test <输出目录>`。

同步到上游 `db860e1` 后，已通过正常 NuGet 恢复重新构建 GUI、CLI 和发布包的 .NET 9/10 版本，并在 .NET 10 下运行回归及真实资源导出测试。.NET 9 进行了编译验证，未进行运行验证。

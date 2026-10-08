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
输入清单中的资源根目录，如 `Assets/OriginalResRepos/ART/Char/Avatar/Female/Female_Size02/Remielle`，点击 **列出目录**。

- 左侧目录树支持多选。勾选或取消父目录会同步子目录，之后可以单独排除某个子目录。根目录中的文件也由根节点的勾选状态控制。
- 文件类型按主资源的原扩展名列出，如 `.fbx`、`.png`、`.json`，支持多选；目录和类型共同筛选右侧列表。全部取消表示不导出，不会回退到导出全部。
- 每个 FBX 及其子 AnimationClip 始终作为一个任务。筛选 FBX 不会拆散子资源；动画只写入自己的 FBX，不混入其他文件。
- 点击 **导出勾选资源…**，选择与输入根目录对应的输出位置。例如根目录为 Remielle，结果直接是 `输出目录/Ani/Avatar_Female_Size02_Remielle_Origin_Ani_Idle_AFK.fbx`。不再生成带时间戳的批次目录或 `.export-…` 包装目录。
- 输出目录不生成 `primary-resource.json`、批次报告或材质 JSON 副本。FBX 保留原生材质及所引用的贴图；不再额外转储整个 CAB 的依赖。贴图等普通单对象资源沿用可用的转换格式，文本保留原始扩展名和内容。
- 暂不能还原成可用文件的 prefab/asset/material 等复合资源会**跳过并记录原因**，不生成替代 JSON 或伪装成原格式的原始数据。原本就是 JSON 的文本资源仍可按 `.json` 导出。
- 已存在的主文件会跳过，不覆盖、不添加重复编号。同批次的输出重名或依赖内容冲突会明确报错；相同内容的共享贴图可以复用。失败项不影响其他资源。

每个主资源先在临时目录导出并验证，再放入原资源目录。取消会停止全部工作进程；失败或取消不会把临时文件作为完成结果留在输出目录中。结果窗口显示成功、跳过、失败和未完成数量。
**查看导出日志**打开应用工作目录下 `ExportLogs/export-<时间>-<标识>.json`；对应 `.jsonl` 逐项保存结果。默认位置为 `%LOCALAPPDATA%/AnimeStudio/AssetExplorer/ExportLogs`，日志不混入导出的资源目录。

**并发数**默认 2，支持 1～8。工作进程各自复用 CAB 依赖索引，按需查询主窗口的分离网格索引，不重复加载整份资源清单。机械硬盘或内存紧张时可设为 1，增加并发不保证线性提速。

只有真实还原的路径参与收集，`~` 推测路径不会决定资源归属。目录条件独立于主窗口的搜索、类型筛选和导出列表。相同分离网格路径若有多个不同对象会报告歧义，不任意选取；本机 Ramiel Body_2 数据存在这种情况。

## Unity 动画根节点兼容

动画 FBX 的外层场景容器现在保留为 Null 节点，子骨骼继续导出为骨骼节点。
先前无网格动画会把外层容器一并转成骨骼，Unity 保留它后，曲线路径多出 `Avatar_…_Ani_…/`，无法匹配模型的 `Bone_Root/…`。
本次通过可选的 `preserveRootNodeAsNull` 修复 Asset Explorer 的导出；上游普通导出的默认行为不变，无需修改原生 FBX DLL。
旧动画文件需要重新导出。动画文件本身没有网格时，Unity 预览需要指定对应的模型。

回归检查覆盖真实二进制 FBX 根节点及动画、目录/扩展名多选、相对目录布局、并发共享贴图、文件冲突、已有文件跳过、取消和失败清理。
`tests/UnityFbxValidation/Editor/FbxPlaybackValidation.cs` 可放入临时 Unity 工程验证实际导入：使用 Generic 和 Copy From Other Avatar，检查所有动画路径匹配模型，并采样动作确认骨骼运动。命令参数见该脚本注释。

2026-10-08 本机验收：在 Unity 2022.3.61f1 中重新导入 Remielle Origin 攻击动画及模型，主动作的 389 条节点路径全部匹配，采样时 350 个节点发生运动，4 段子动画均通过绑定检查。应用路径字典后，UIAni 目录的 116 个 FBX 并行导出全部成功；Materials 的 65 个不支持原格式还原的资源全部明确跳过，未生成替代 JSON。

## 动画 FBX 无损体积优化

Asset Explorer 的单模型和目录 FBX 导出自动启用无损整理，保留普通、`Default`、`End` 等全部动画，不合并也不删除这些动作。

- 对本导出器的标准自动三次曲线，仅当所有键值逐位一致、切线斜率为零且时间严格递增时，删除中间重复关键帧；保留首尾时间与曲线绑定。恒定零位移、单位缩放等有意义的姿态约束不会被整条删除。
- 有变化的曲线不会抽帧、量化或近似简化，哪怕变化只有一个浮点 ULP。非零切线、未知插值标志或其他未知曲线字段均保留。
- 只剔除没有任何引用的空曲线；有引用的空曲线保留。FBX 数组使用格式自带的 zlib 无损压缩，解码后的数值不变。
- 先写入临时文件，成功且确实更小时才替换；优化可重复执行。ASCII、旧版本或未识别的尾部布局保留原文件，不套用二进制优化。

2026-10-09 验收：Remielle Origin Attack Normal 01 从 18,386,672 字节减至 10,947,248 字节（约 40.5%），保留四段动作，精简 4,006 条恒定曲线中的 688,982 个重复关键帧。独立 FBX 解码比较确认其他字段及曲线数据一致；Unity 2022.3.61f1 关闭导入端有损压缩后，对 1,396 个帧/半帧时间点检查曲线值、局部姿态及 BlendShape 权重，并在首、中、末帧比较蒙皮顶点，差异均为零。

测试入口：`tests/compare_fbx_lossless.py` 比较压缩前后的 FBX 解码数据；`tests/UnityFbxValidation/Editor/FbxLosslessValidation.cs` 在临时 Unity 工程中进行零容差动画比较。常规回归另外覆盖细微动作、非零切线、空曲线引用、FBX 7300/7500、幂等性及失败不覆盖原文件。

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

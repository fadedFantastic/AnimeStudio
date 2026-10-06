# ZZZ 资源工具

图形界面现已整合到 Studio：**Maps → Open Asset Explorer (Fast)**。

```powershell
.\tools\Start-AssetExplorer.ps1
```

支持快速搜索、类型/blk/正则筛选、路径补全、增量索引、Load Selected，以及包含动画和贴图的一键 FBX 导出。
模型和额外的 AnimationClip 可以加入跨搜索保留的导出列表。详细使用和验收结果见 [Asset Explorer](../docs/asset-explorer.md)。

统一使用 `Start-AssetExplorer.ps1`。旧 ZzzMap 独立项目和兼容启动脚本已清理；旧清单、路径字典和配置继续保留，新工具首次运行会迁移读取。
下面的命令行脚本仍可使用。

## Build-ZzzAssetMap.ps1

一条命令跑完整个链路：定位游戏目录 → 编译 CLI → 下载路径字典 → 扫描全部 blk → 生成索引。
重复运行是幂等的，已经编译好的 CLI 和下载好的字典都会跳过。

### 产物

默认落在仓库下的 `zzz-map\`（建议加进 `.gitignore`）：

| 文件 | 说明 |
|---|---|
| `out\zzz_assets.json` | 资源清单原文，`Name` / `Container` / `Source` / `Offset` / `PathID` / `Type` / `Hash` |
| `out\zzz_assets.tsv` | 同上的制表符索引，`Find-ZzzAsset.ps1` 查的就是它 |
| `Maps\zzz_assets.bin` | CABMap（CAB 名 → blk 路径 + 偏移 + 依赖） |
| `Maps\Z3-AssetIndex-Eleiyas.json` | 路径字典 |
| `build.log` | CLI 完整日志，出问题先看这个 |

`Source` 是 blk 的完整路径，`Offset` 是这个资源所在的 bundle 在 blk 里的字节偏移 —— 一个 blk
通常打包了多个 bundle，所以两个字段合起来才是精确位置。

### 常用参数

```powershell
# 指定游戏目录（默认会自动探测常见安装位置）
.\tools\Build-ZzzAssetMap.ps1 -GameDir 'E:\Games\miHoYo Launcher\games\ZenlessZoneZero Game\ZenlessZoneZero_Data\StreamingAssets'

# 顺便产出 .map，可以用 GUI 的 Maps → Open Asset Browser 打开
.\tools\Build-ZzzAssetMap.ps1 -MapType 'JSON,MessagePack'

# 完整清单：记录 bundle 里每一个对象（含 Transform 等），文件大很多、也更慢
.\tools\Build-ZzzAssetMap.ps1 -Full

# 不联网，Container 保持数字 hash（Name 字段仍然可读）
.\tools\Build-ZzzAssetMap.ps1 -SkipPathDict

# 字典自己下好了
.\tools\Build-ZzzAssetMap.ps1 -PathDictFile D:\dl\Z3-AssetIndex.json

# 强制重编 CLI
.\tools\Build-ZzzAssetMap.ps1 -Rebuild
```

## 速度

全量约 80 GB / 16000 个 blk。在机械硬盘上实测：

| 配置 | 吞吐 | 全量耗时 |
|---|---|---|
| 改动前（默认算 hash） | 64.4 MB/s | ~21 分钟 |
| 现在（默认 `--no_hash` + 去掉重复目录枚举） | **76.9 MB/s** | **~18 分钟** |

两处改动：

- **跳过逐对象 hash**（`AnimeStudio\AssetsHelper.cs` 的 `ComputeHash`）。原本每个对象都要
  `GetRawData()` 把完整字节读出来算一遍 xxHash，只有做版本间内容比对才用得上。实测 +12%。
  产出的清单逐条比对过，除 `Hash` 字段外与开启时完全一致。要那一列就加 `-WithHash`。
- **不再重复枚举目录**（`AssetsManager.LoadFilesPreprocessed`）。`LoadFiles` 每次都会
  `MergeSplitAssets` 扫一遍整个目录，逐文件循环调用时这笔开销乘以文件数 ——
  16000 个文件的目录上是 4.4 ms × 16107 ≈ 71 秒。调用方已经做过 split 预处理，直接跳过。

**再往下就是磁盘的物理极限了。** 同一批数据在文件缓存命中时能跑到 137 MB/s，
说明 CPU 还有 1.8 倍余量，冷读的 77 MB/s 就是这块 HDD 的实际吞吐
（顺序读上限 92 MB/s，16000 个文件分散在盘上有寻道损失）。

试过后台线程预读，**在机械硬盘上是负优化**：预读线程和主线程抢磁头，
深度 8 时比不预读慢了将近一半（1520 vs 2830 文件 / 150 秒）。所以 `-ReadAhead` 默认 0。
资源要是放在 SSD 上，瓶颈会变成 CPU 的 137 MB/s（全量约 10 分钟），那时候预读才可能有意义。

### Minimal vs Full默认 **Minimal**，只收录「可导出」类型：`Texture2D` `Mesh` `Sprite` `AudioClip` `VideoClip`
`AnimationClip` `TextAsset` `Material` `Shader` `Font` `MonoBehaviour` `MiHoYoBinData` `Animator`。
日常找模型、贴图、音频、文本足够了，清单也小得多。

`-Full` 会把 `Transform` `MeshFilter` `GameObject` 之类全部记下来，清单可能上千万行。
只有需要遍历完整对象图时才用。

具体哪些类型算「可导出」，由 `AnimeStudio.CLI\App.config` 里 `types` 这一项的第二个布尔值
（`Item2`）决定，可以自己改。

### 路径字典

ZZZ 的 `container` 在 bundle 里存的是 ulong hash，不是路径。脚本会从
[Eleiyas/Z3-Asset-Map](https://github.com/Eleiyas/Z3-Asset-Map) 拉一份 hash → path 的字典，
CLI 读到之后就能把 `Container` 还原成 `assets\...\xxx.prefab` 这种可读形式
（对应 `AnimeStudio\AssetsHelper.cs:389`）。

字典必须叫 `Z3-AssetIndex-Eleiyas.json` 且放在 CLI 工作目录的 `Maps\` 下 —— 这个文件名是
`AnimeStudio.CLI\Program.cs:23` 里写死的，脚本已经处理好了。

字典覆盖不到的资源，`Container` 会留空或保持数字，但 `Name` 一般仍然可读，照样能搜到。

---

## Find-ZzzAsset.ps1

对 TSV 做流式匹配，几百万条也是秒级。所有条件都是正则、大小写不敏感，多个条件之间是「与」。

```powershell
# 模糊搜（同时匹配名字、路径、类型、blk 名）
.\tools\Find-ZzzAsset.ps1 Anby

# 按列精确过滤
.\tools\Find-ZzzAsset.ps1 -Name '^Avatar_Female' -Type Mesh
.\tools\Find-ZzzAsset.ps1 -Container 'ui/atlas' -Type Sprite

# 反查：某个 blk 里都装了什么
.\tools\Find-ZzzAsset.ps1 -Blk '^2429662787\.blk$' -All

# 导出结果
.\tools\Find-ZzzAsset.ps1 -Type AudioClip -All -Csv .\audio.csv
```

输出默认显示 `Name` / `Type` / `Blk` / `Offset` / `Container`，完整字段（含 `PathID`、blk 全路径
`Source`）仍在对象上，可以直接接管道：

```powershell
.\tools\Find-ZzzAsset.ps1 Anby -Type Texture2D -All | Select-Object -Expand Source -Unique
```

---

## 查到之后怎么导出

拿到 blk 路径后，直接对单个文件跑 CLI：

```powershell
$cli = '.\AnimeStudio.CLI\bin\Release\net10.0-windows\AnimeStudio.CLI.exe'
& $cli --game ZZZ --types Texture2D --names '^Anby' `
    'E:\...\Blocks\2429662787.blk' .\out
```

**注意**：CLI 目前没法同时「加载 CABMap 解析依赖」和「导出资源」——
`AnimeStudio.CLI\Program.cs:147-158` 里 `--map_op CABMap` 会走加载分支，但那之后
`Program.cs:174` 的导出条件就不成立了。所以导模型这类需要跨 blk 找贴图/材质的情况，
用 GUI 更省事：先 `Maps → CABMap → Load` 选上生成好的 `zzz_assets.bin`，再拖 blk 进去。

---

## 常见问题

**扫描很慢 / 想中断**
Ctrl+C 即可，已写入的日志保留。但清单是最后一次性写出的，中断就得重来。可以先拿一小部分
试水：把几十个 blk 复制到临时目录，然后 `-GameDir <临时目录> -WholeStreamingAssets`。

**日志里一堆 `Error while reading` / `no assets found`**
正常。`StreamingAssets` 下混着 `.pck`（Wwise 音频）等非 Unity 文件，CLI 认不出来就跳过。
脚本默认只扫 `Blocks` 子目录，已经过滤掉大部分。

**提示找不到 .NET SDK**
装 [.NET 10 SDK](https://dotnet.microsoft.com/download)。已有 9 也行，脚本会自动选。

**游戏更新后**
重跑 `Build-ZzzAssetMap.ps1` 即可。想留旧版做对比就换个 `-MapName`，
再用 GUI 的 Asset Browser 同时载入两份 `.map` 比较差异。

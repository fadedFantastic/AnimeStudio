# Unity 运行时加载 blk 的工作机制推测

> 基于 AnimeStudio 源码逆向分析，结合 Unity AssetBundle 加载原理，推测原神 CBT2/CBT3 运行时如何发现、定位、加载 blk 中的资源。

---

## 1. 整体架构

blk 是米哈游在原神 CBT2/CBT3 时期使用的资源容器格式。它不是简单的"文件→Bundle"映射，而是一套**内容寻址的分层索引系统**。

```mermaid
flowchart TD
    subgraph "索引层（元数据）"
        AI["AssetIndex\n(JSON 清单文件)"]
        CAB["CABMap\n(Content-Addressable Bundle Map)"]
    end

    subgraph "容器层（物理存储）"
        BLK1["block_000.blk"]
        BLK2["block_001.blk"]
        BLK3["block_xxx.blk"]
    end

    subgraph "Bundle 层（Unity 格式）"
        B1["UnityFS Bundle"]
        B2["UnityRaw Bundle"]
        B3["UnityFS Bundle"]
    end

    subgraph "资源层（游戏资产）"
        R1["Texture2D"]
        R2["GameObject/Prefab"]
        R3["Mesh"]
        R4["Material"]
        R5["AnimationClip"]
        R6["MiHoYoBinData"]
    end

    AI -->|"Bundle ID → offset"| CAB
    CAB -->|"hash → 文件路径 + 偏移"| BLK1
    CAB --> BLK2
    BLK1 -->|"解密 + 拆分"| B1
    BLK1 --> B2
    BLK2 --> B3
    B1 --> R1
    B1 --> R2
    B2 --> R3
    B3 --> R4
    B3 --> R5
    B3 --> R6
```

---

## 2. 索引系统

### 2.1 AssetIndex（资源索引）

`AssetIndex` 是一个 JSON 文件，记录了游戏中所有资源的元数据。其结构如下：

```csharp
record AssetIndex {
    Dictionary<string, string> Types;           // 类型名 → 程序集名映射
    Dictionary<int, List<SubAssetInfo>> SubAssets;  // 资产ID → 子资产列表
    Dictionary<int, List<int>> Dependencies;    // 资产ID → 依赖资产ID列表
    List<uint> PreloadBlocks;                   // 预加载的 block 列表
    List<uint> PreloadShaderBlocks;             // 预加载的 shader block 列表
    Dictionary<int, BlockInfo> Assets;          // 资产ID → 所在 block 信息
    List<uint> SortList;                        // 排序列表
}
```

**关键结构 `BlockInfo`**：每个资产记录它所属的 Bundle 编号和在该 Bundle 内的偏移。

```csharp
record BlockInfo {
    byte Language;    // 语言版本
    uint Id;          // Bundle 编号（对应 CABMap 中的 block hash）
    uint Offset;      // 资产在 Bundle 内的偏移
}
```

### 2.2 CABMap（内容寻址 Bundle 映射）

CABMap 是一个二进制文件（`.bin`），通过 `BuildCABMap` 扫描所有 `.blk` 文件生成。它建立了**内容哈希 → 文件路径 + 偏移**的映射：

```csharp
// CABMap 结构
Dictionary<string, Entry> CABMap;

record Entry {
    string Path;              // blk 文件相对于 BaseFolder 的路径
    long Offset;              // Bundle 在该 blk 文件中的偏移
    List<string> Dependencies; // 该 Bundle 依赖的其他 Bundle 的哈希
}
```

**CABMap 的构建过程**：
1. 遍历所有 `.blk` 文件
2. 对每个 blk 解密，拆分为多个 Bundle
3. 对每个 Bundle，解析其内部的 `AssetBundle` 对象
4. 提取内容哈希（`fileName`）和依赖关系
5. 记录哈希对应的文件路径和偏移

```mermaid
sequenceDiagram
    participant Build as CABMap 构建器
    participant BLK as .blk 文件
    participant Bundle as Unity Bundle
    participant AB as AssetBundle 对象

    Build->>BLK: 读取并解密
    BLK-->>Build: XORStream（解密后数据）
    Build->>Build: 扫描 Bundle 边界（UnityFS/UnityRaw signature）
    
    loop 每个 Bundle
        Build->>Bundle: 解析 Bundle 头部
        Bundle-->>Build: Header + Blocks + Directory
        Build->>Bundle: 读取内部文件列表
        loop 每个内部文件
            Build->>AB: 如果是 AssetBundle 类型
            AB-->>Build: m_Name，m_Container，m_PreloadTable
            Build->>Build: CABMap[fileName] = { Path, Offset, Dependencies }
        end
    end
```

### 2.3 运行时索引查找流程

```mermaid
flowchart TD
    A["游戏请求加载资源\n(如: texture_001)"] --> B["在 AssetIndex 中查找"]
    B --> C{"找到对应的资产ID?"}
    C -->|"是"| D["获取 BlockInfo:\nBundle ID + Offset"]
    C -->|"否"| E["加载失败"]
    D --> F["在 CABMap 中查找\nBundle ID 对应的位置"]
    F --> G{"找到?"}
    G -->|"是"| H["获取 blk 文件路径\n+ Bundle 在文件内的偏移"]
    G -->|"否"| E
    H --> I["打开并解密 blk 文件"]
    I --> J["Seek 到 Bundle 偏移位置"]
    J --> K["加载 Unity Bundle"]
    K --> L["在 Bundle 中定位资产\n(通过 PathID)"]
    L --> M["返回资产对象"]
```

---

## 3. 运行时加载流程

### 3.1 启动阶段

```mermaid
flowchart TD
    A["游戏启动"] --> B["加载 AssetIndex JSON"]
    B --> C["初始化 ResourceIndex"]
    C --> D["构建 BundleMap\n(Bundle ID → 路径映射)"]
    D --> E["加载 CABMap.bin"]
    E --> F["CABMap 常驻内存\n(供后续查找使用)"]
    F --> G["预加载 PreloadBlocks\n(启动必需的资源)"]
    G --> H["游戏进入可交互状态"]
```

### 3.2 单个资源加载流程

```mermaid
sequenceDiagram
    participant Game as 游戏逻辑
    participant RI as ResourceIndex
    participant CAB as CABMap
    participant Cache as 解密缓存
    participant BLK as .blk 文件
    participant Bundle as Bundle 加载器
    participant Asset as 资产实例

    Game->>RI: 请求加载 "avatar_001"
    RI->>RI: 查找 AssetIndex.Assets[id]
    RI-->>Game: BlockInfo { Id: 0x12345678, Offset: 0x4000 }

    Game->>CAB: 查找 Bundle 0x12345678
    CAB-->>Game: { Path: "StreamingAssets/block_003.blk", Offset: 0x2A000 }

    Game->>Cache: 检查 block_003.blk 是否已解密
    Cache-->>Game: 未缓存

    Game->>BLK: 打开 block_003.blk
    Game->>Game: 读取 header → AES.Decrypt → 推导 seed
    Game->>Game: MT19937_64(seed) → xorpad[0x1000]
    Game->>Game: 解密全部数据到内存
    Game->>Cache: 缓存解密后的流

    Game->>Bundle: 从解密流 offset=0x2A000 处加载 Bundle
    Bundle->>Bundle: 解析 UnityFS header
    Bundle->>Bundle: 解压 blocks
    Bundle->>Bundle: 读取 Directory 和文件列表
    Bundle->>Asset: 定位 PathID → 反序列化资产
    Asset-->>Game: 返回 GameObject/Texture/Mesh
```

### 3.3 解密缓存策略

由于一个 blk 包含多个 Bundle，最合理的策略是：

```mermaid
flowchart TD
    A["请求 Bundle X"] --> B{"blk 文件已解密?"}
    B -->|"否"| C["打开 blk 文件"]
    C --> D["解密整个 blk\n(AES → MT19937_64 → XOR)"]
    D --> E["缓存解密后的数据\n(内存 MemoryStream)"]
    B -->|"是"| F["使用缓存的解密流"]
    E --> F
    F --> G["Seek 到 Bundle 偏移"]
    G --> H["加载 Bundle"]
```

**关键点**：只需解密一次，后续对同一 blk 文件的访问直接复用解密后的数据。解密后的数据可保留在内存中，或使用 `MemoryMappedFile` 映射到磁盘临时文件。

### 3.4 依赖解析

当一个 Bundle 被加载时，它可能依赖其他 Bundle：

```mermaid
flowchart LR
    subgraph "Bundle A 的依赖"
        A["Bundle A\n(prefab)"] --> B["Bundle B\n(shared texture)"]
        A --> C["Bundle C\n(shared material)"]
        B --> D["Bundle D\n(shared shader)"]
    end
```

依赖解析通过 CABMap 中的 `Dependencies` 字段实现：

```csharp
// CABMap entry for Bundle A
Entry {
    Path = "StreamingAssets/block_003.blk",
    Offset = 0x2A000,
    Dependencies = ["hash_of_Bundle_B", "hash_of_Bundle_C"]
}
```

依赖的 Bundle 可能在同一 blk 或不同 blk 中，CABMap 通过哈希查找统一处理。

---

## 4. 资源类型体系

### 4.1 AssetBundle 容器

`AssetBundle` 是 Unity 的容器类型，存储了路径到资产的映射：

```csharp
class AssetBundle {
    List<PPtr<Object>> m_PreloadTable;         // 预加载资产指针表
    List<KeyValuePair<string, AssetInfo>> m_Container;  // 路径 → 资产信息
}

class AssetInfo {
    int preloadIndex;      // 在 PreloadTable 中的起始索引
    int preloadSize;       // 连续的资产数量
    PPtr<Object> asset;    // 主资产指针
}
```

**加载时**：遍历 `m_Container`，根据 `preloadIndex` 和 `preloadSize` 从 `m_PreloadTable` 中取出对应的资产指针，建立路径到资产的映射。

### 4.2 资源类型枚举

```mermaid
classDiagram
    class Object {
        +PathID
        +Type
    }
    class NamedObject {
        +Name
    }
    class AssetBundle {
        +m_PreloadTable
        +m_Container
    }
    class ResourceManager {
        +m_Container
    }
    class IndexObject {
        +AssetMap
    }
    class NapAssetBundleIndexAsset {
        +m_AssetArray
        +m_BundleArray
        +m_BlockArray
    }
    class MiHoYoBinData {
        +RawData
        +Type: JSON/Bytes
    }
    class GameObject {
        +m_Name
        +m_Components
    }
    class Texture2D {
        +image data
    }
    class Mesh {
        +vertex data
    }
    class Material {
        +shader
        +properties
    }
    class AnimationClip {
        +curves
    }

    Object <|-- NamedObject
    NamedObject <|-- AssetBundle
    NamedObject <|-- IndexObject
    NamedObject <|-- NapAssetBundleIndexAsset
    Object <|-- MiHoYoBinData
    Object <|-- ResourceManager
    NamedObject <|-- GameObject
    NamedObject <|-- Texture2D
    NamedObject <|-- Mesh
    NamedObject <|-- Material
    NamedObject <|-- AnimationClip
```

### 4.3 MiHoYoBinData（米哈游自定义数据）

米哈游在 Unity 中扩展了自定义资产类型 `MiHoYoBinData`，用于存储游戏配置数据：

```csharp
class MiHoYoBinData {
    byte[] RawData;    // 二进制数据
    // 运行时可能附加 XOR 解密
    // 内容可能是 JSON 或二进制
}
```

这些数据通常通过 `IndexObject` 进行索引：

```csharp
class IndexObject {
    List<KeyValuePair<string, Index>> AssetMap;
    // Key: 资源名称（如 "Config/Avatar/10000001"）
    // Value: Index { PPtr<Object>, Size }
}
```

---

## 5. 完整运行时数据流

```mermaid
flowchart TD
    subgraph "启动初始化"
        S1["读取 AssetIndex JSON"] --> S2["构建 BundleMap"]
        S2 --> S3["加载 CABMap.bin"]
        S3 --> S4["预加载 PreloadBlocks"]
    end

    subgraph "资源请求"
        R1["GameObject.Instantiate\n('avatar_001')"] --> R2["ResourceManager\n查找容器路径"]
        R2 --> R3["AssetIndex 查找\n资产ID → BlockInfo"]
        R3 --> R4["CABMap 查找\nBlock ID → 文件路径 + 偏移"]
    end

    subgraph "文件加载"
        R4 --> F1{"blk 已解密?"}
        F1 -->|"否"| F2["解密 blk\n(AES + MT19937_64 + XOR)"]
        F1 -->|"是"| F3["复用解密缓存"]
        F2 --> F3
        F3 --> F4["Seek → 加载 Bundle"]
        F4 --> F5["解压 blocks\n(LZ4/LZMA/Zstd)"]
        F5 --> F6["读取 Directory\n提取文件列表"]
    end

    subgraph "资产解析"
        F6 --> A1["定位 AssetBundle 对象"]
        A1 --> A2["遍历 m_Container\n建立路径映射"]
        A2 --> A3["通过 PathID\n定位目标资产"]
        A3 --> A4["反序列化资产\n(GameObject/Texture/Mesh)"]
        A4 --> A5["解析依赖\n递归加载"]
    end

    subgraph "资源就绪"
        A4 --> T1["返回给调用方"]
        A5 --> T1
    end
```

---

## 6. 关键推测

### 6.1 为什么用 blk 容器格式

| 问题 | 推测 |
|---|---|
| 为什么多个 Bundle 打成一个 blk？ | 减少文件数量，降低文件系统开销。移动端尤其重要，减少 inode 使用和文件打开次数。 |
| 为什么只对 CBT 使用？ | Live 版本改用 `Mhy` 格式（额外的 `MhyShiftRow/Key/Mul` 层），安全性更高。blk 可能是早期方案。 |
| 为什么用 XOR 而不用 AES 全加密？ | 性能考虑。XOR 流解密极快，MT19937_64 生成密钥流只需要整数运算。对移动端 CPU 友好。 |
| 为什么 AES 只加密 key？ | 密钥材料（16 字节）用 AES 保护，数据体用 XOR。平衡了安全性和性能。 |

### 6.2 文件组织推测

基于 CBT 构建的常见模式，文件结构如下：

```
StreamingAssets/
├── AssetIndex.json          # 资源索引清单
├── block_000.blk            # 核心资源（启动必需）
├── block_001.blk            # 角色资源
├── block_002.blk            # 场景资源
├── block_003.blk            # UI 资源
├── ...
└── block_xxx.blk            # 其他资源
```

每个 blk 文件按功能或场景分组，配合 AssetIndex 中 `PreloadBlocks` 列表实现按需加载。

### 6.3 内存管理策略

```mermaid
flowchart TD
    subgraph "内存分区"
        M1["常驻内存\n- AssetIndex\n- CABMap\n- ResourceIndex"] 
        M2["按需加载\n- 解密后的 blk 数据\n- 解压后的 Bundle 数据"]
        M3["LRU 缓存\n- 最近使用的 Bundle\n- 最近使用的资产"]
    end

    M1 -->|"永久"| M1
    M2 -->|"使用完毕释放"| M2
    M2 -->|"频繁访问提升"| M3
    M3 -->|"长期未用淘汰"| M2
```

### 6.4 与 Live 版本（Mhy 格式）的演进

```mermaid
flowchart LR
    subgraph "CBT2/CBT3 (blk)"
        B1["AES 解密 key"]
        B2["XOR 流解密数据"]
        B3["无 SBox 层"]
    end

    subgraph "Live (Mhy)"
        M1["AES 解密 key"]
        M2["SBox 变换"]
        M3["MhyShiftRow/Key/Mul\n自定义变换"]
        M4["XOR 流解密数据"]
    end

    B1 --> B2
    M1 --> M2 --> M3 --> M4
```

Live 版本在 AES 解密和 XOR 解密之间增加了多层自定义变换，安全性显著提升，但核心思路（MT19937_64 + XOR 流）保持不变。

---

## 7. 总结

blk 的运行时加载体系可以概括为：

1. **索引层**：`AssetIndex`（JSON）提供资源 ID 到 Bundle ID 的映射，`CABMap`（二进制）提供 Bundle ID 到文件路径 + 偏移的映射
2. **容器层**：`.blk` 文件 = 多个 Unity Bundle 的拼接 + 整体 XOR 加密
3. **解密层**：AES-128 解密密钥材料 → MT19937_64 生成 XOR 密钥流 → 解密整个容器
4. **Bundle 层**：标准 Unity Bundle 格式（UnityFS/UnityRaw），支持 LZ4/LZMA/Zstd 压缩
5. **资源层**：标准 Unity 资产（GameObject、Texture、Mesh 等）+ 米哈游自定义类型（MiHoYoBinData、IndexObject 等）
6. **缓存策略**：解密结果缓存复用，避免重复解密同一 blk
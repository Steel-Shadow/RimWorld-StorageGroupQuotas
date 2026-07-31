# Storage Group Quotas / 存储组配额

<p align="center">
  <img src="About/Preview.png" alt="Four independent ammunition stacks kept inside one storage-group quota while excess is hauled outside" width="987">
</p>

[English](#english) | [简体中文](#简体中文)

<a id="english"></a>

## English

Set a clear per-item limit for an entire linked storage group. For ammo depots, you can also keep several real stacks so different pawns have separate pickup targets instead of waiting on one reserved pile.

RimWorld 1.6 · Requires Harmony · Package ID: `steelshadow.storagegroupquotas`

### Which mode should I use?

| What you want | Mode | Result |
| --- | --- | --- |
| Keep an exact total of medicine, meals, shells, ammo, or another item | **Entire storage count** | Each item type may occupy no more than X units across the whole linked storage group. |
| Keep several physical stacks that pawns can reserve separately | **Similar stacks ×N** | Each stack holds at most X units, with at most N stacks. Total capacity is X × N. |

Quotas are calculated separately for every item type. Setting a quota of 100 does **not** mean 100 mixed items across the warehouse; it means up to 100 of each item that uses that value.

#### Why keep several stacks?

With some hauling or reservation optimization setups, one physical stack may be reserved by only one pawn or job at a time. Multiple physical stacks provide separate reservation targets and can reduce queues when several weapons reload or are resupplied together.

### Quick start

1. Select a shelf, storage building, or stockpile.
2. Open the vanilla **Storage** tab and click **Quotas** in the upper-right corner.
3. Choose **Entire storage count** or **Similar stacks ×N**.
4. Set a default value for all currently allowed items.
5. Use an item's **Default** button to give that item its own override.

The window shows the current quantity and physical stack count for every listed item, plus any overflow or stack layout waiting for hauling work.

### Example: a 105 mm ammunition depot

Set **25 per stack** and **Similar stacks ×4**:

- Total capacity is 100 rounds.
- One stack of 100 is not treated as 75 excess. Haulers progressively split it into up to four stacks of no more than 25.
- If the group contains 125 rounds, exactly 25 are moved outside the group; the retained 100 are then arranged into separate stacks when valid cells are available.

### What happens in game?

- All linked shelves in the same vanilla `StorageGroup` share one quota.
- Each item definition is counted independently.
- New hauling jobs are capped by the group's remaining capacity.
- Existing excess is moved by ordinary hauling jobs; it is never deleted or teleported.
- Haulers first look for valid storage outside the source group. If none exists, they look for a reachable non-storage floor cell outside the group.
- Similar-stacks mode first tries to split or consolidate stacks inside the group. If the selected layout cannot be completed inside the group, the unresolved part may be moved outside.
- The mod does not change `ThingDef.stackLimit` globally.

Because cleanup uses normal work, it needs an available hauler, a valid reservation, a path, and enough usable cells. Changes are therefore not necessarily immediate.

### What does 0 mean?

- **Entire storage count:** `0` means unlimited.
- **Similar stacks ×N:** `0` uses the item's current stack limit as the per-stack value. Total capacity is then `current stack limit × N`.

A configured per-stack value never raises a stack above its current `ThingDef.stackLimit`.

### Compatibility

- RimWorld 1.6
- **Harmony is required.**
- Combat Extended is not required, but CE ammunition works because quotas apply to ordinary storable item definitions.
- Pick Up And Haul receives optional destination-capacity support. This is a focused compatibility patch, not a guarantee for every custom hauling path.
- **Stack Gap (`Andromeda.StackGap`) is incompatible.** Disable it before enabling this mod. Its settings are not migrated.

### Installation outside Steam Workshop

1. Make sure Harmony loads before this mod.
2. Copy the repository/mod folder into RimWorld's `Mods` directory.
3. Enable **Storage Group Quotas** and keep Stack Gap disabled.

Steam Workshop users only need to subscribe and enable the mod and its required Harmony dependency.

### Player FAQ

**Does each shelf get its own quota?**

No. Linked shelves share the quota of their vanilla storage group. An unlinked shelf or stockpile uses its own local slot group.

**Does Similar stacks ×N always create exactly N stacks?**

No. N is a maximum. The actual number depends on the stored quantity, valid cells, compatible stacks, and available haulers.

**Are quality, material, or hit-point variants separate quotas?**

No. The quota and the N-stack budget are per `ThingDef`. Variants that cannot stack with one another can still share that same quota budget.

**Can the group briefly exceed its quota?**

Yes. Capacity is based on currently spawned contents; several already-created jobs, a custom hauling mod that bypasses the patched methods, or directly spawned items may temporarily overshoot. Normal cleanup work handles the result.

**Where is the GitHub project?**

[Source code and issue tracker](https://github.com/Steel-Shadow/RimWorld-StorageGroupQuotas)

### Developer guide

#### Project layout

| Path | Responsibility |
| --- | --- |
| `Source/StorageQuotaData.cs` | Saved mode, default value, stack count, and per-`ThingDef` overrides. |
| `Source/QuotaDataStore.cs` | Runtime attachment of quota data to vanilla `StorageSettings`. |
| `Source/QuotaUtility.cs` | Scope resolution, counting, effective capacities, overflow discovery, and scan caching. |
| `Source/HarmonyPatches.cs` | Vanilla storage/UI patches and optional Pick Up And Haul integration. |
| `Source/WorkGiver_MoveQuotaOverflow.cs` | Ordinary hauling jobs for exact overflow removal and similar-stack rebalancing. |
| `Source/Window_StorageQuotas.cs` | Quota configuration window in the vanilla Storage tab. |
| `Defs/WorkGiverDefs/WorkGivers.xml` | Registration of the custom hauling `WorkGiverDef`. |
| `Languages/` | English and Simplified Chinese keyed translations. |

The mod deliberately has no custom `JobDriver`, `GameComponent`, `MapComponent`, or global `ModSettings`. It uses vanilla `JobDefOf.HaulToCell` jobs and stores data with the relevant vanilla `StorageSettings`.

#### Scope resolution and persistence

`QuotaUtility.ScopeAt`, `ScopeForSettings`, and `ScopeForThing` resolve a local `SlotGroup` to `SlotGroup.StorageGroup` when one exists; otherwise they use the local slot group. This is the code path that makes linked shelves share a quota.

At runtime, `QuotaDataStore` uses a `ConditionalWeakTable<StorageSettings, Holder>`. `Patch_StorageSettings_ExposeData` deep-scribes the attached `StorageQuotaData` under the `storageGroupQuotas` node. Inactive data in TotalCount mode is omitted from saves. `Patch_StorageSettings_CopyFrom` clones quota data when vanilla storage settings are copied.

#### Capacity formulas

Let `v` be the per-item override, or the default when no override exists; let `L = max(1, ThingDef.stackLimit)`; and let `N` be `SimilarStackCount`.

```text
TotalCount:
  v = 0  -> total limit = unlimited
  v > 0  -> total limit = v

SimilarStacks:
  per-stack limit = min(L, v = 0 ? L : v)
  total limit     = per-stack limit × N
```

The multiplication uses `long` and saturates at `int.MaxValue`. The UI accepts quota values from 0 to 1,000,000,000 and N from 1 to 1,000.

Both quantity and stack budgets are per exact `ThingDef`, not per `CanStackWith` equivalence class. Actual merges still require `Thing.CanStackWith()`.

#### Harmony patch points

| Patch | Purpose |
| --- | --- |
| `StorageSettings.ExposeData` postfix | Save and load quota data with vanilla storage settings. |
| `StorageSettings.CopyFrom` postfix | Clone quota state when settings are copied. |
| `StoreUtility.NoStorageBlockersIn` postfix | Reject a destination cell when no quota capacity remains. |
| `HaulAIUtility.HaulToCellStorageJob` postfix | Cap `Job.count` to remaining capacity and disable opportunistic duplicates. |
| `ITab_Storage.FillTab` postfix | Add the **Quotas** button to the vanilla Storage tab. |
| `PickUpAndHaul.WorkGiver_HaulToInventory.CapacityAt` postfix | Optionally cap Pick Up And Haul's destination capacity through reflection. |

The Pick Up And Haul patch is skipped if its type or exact `CapacityAt(Thing, IntVec3, Map)` signature is unavailable.

#### Overflow and rebalancing lifecycle

`QuotaUtility.BuildQuotaWorkThings()` scans each storage-group scope once, groups spawned contents by `ThingDef`, then orders stacks by descending count and `thingIDNumber`. Larger stacks are kept first when deciding which exact units lie beyond total capacity.

Candidate lists are cached per map for 30 game ticks. `NotifySettingsChanged()` increments a version counter so UI changes invalidate the cache immediately.

`WorkGiver_MoveQuotaOverflow` then:

1. Recomputes the exact overflow count for the selected stack.
2. Searches storage groups outside the source scope in vanilla priority order, respecting their filters and quotas.
3. Falls back to a reachable, reservable, standable non-storage floor cell within radius 40, avoiding fire, blockers, forbidden cells, and growing zones when applicable.
4. For layout-only work, first tries to merge into the fullest compatible retained stack or create a new stack on a valid group cell while fewer than N stacks exist.
5. If the layout still cannot be resolved internally, routes the unresolved part through the outside-storage/floor fallback.

The registered work giver belongs to `Hauling`, has `priorityInType` 20, requires Manipulation, and assigns a priority of `1000 + exact overflow`. Forbidden, burning, unreachable, unreservable, or non-haulable items are skipped.

#### Known limitations

- Only RimWorld 1.6 is declared and tested.
- In-flight hauling jobs do not reserve quota capacity, so temporary overshoot is possible.
- Fully custom storage or hauling code that bypasses the patched vanilla methods may ignore incoming limits; the cleanup work giver can still handle spawned excess later.
- Similar-stack budgets are per `ThingDef`; quality/material/hit-point variants do not each receive N stacks.
- Internal rebalancing needs compatible stacks or free valid cells. Otherwise layout-only excess may be moved outside.
- The floor fallback searches only within radius 40 and does not guarantee roofing, temperature control, or weather protection.
- The quota window builds its item list when opened from the current storage filter; changing that filter while the window remains open does not rebuild the list.
- Stack Gap data is not migrated, and both mods must not run together.
- There is currently no automated test project.

#### Build

The SDK-style project targets .NET Framework 4.7.2 and references RimWorld, Unity, and Harmony assemblies with `Private=false`.

From the repository root:

```powershell
dotnet build .\Source\StorageGroupQuotas.csproj -c Release
```

The default paths target the author's Steam installation. Override them elsewhere:

```powershell
dotnet build .\Source\StorageGroupQuotas.csproj -c Release `
  -p:RimWorldManagedDir="X:\RimWorld\RimWorldWin64_Data\Managed" `
  -p:HarmonyDir="X:\Harmony\Assemblies"
```

Output is written to `1.6/Assemblies`. A clean Workshop package should contain only runtime content such as `About`, `Defs`, `Languages`, and the release DLL; preserve `About/PublishedFileId.txt` after the first upload so future uploads update the same item.

<a id="简体中文"></a>

## 简体中文

为整个链接存储组设置清楚的单项数量上限。对于弹药库，还可以保留多个真实堆栈，让不同 Pawn 各自找到可预留的取用目标，不必都等待同一堆物资。

RimWorld 1.6 · 需要 Harmony · 包 ID：`steelshadow.storagegroupquotas`

### 两种模式怎么选？

| 你的需求 | 模式 | 实际结果 |
| --- | --- | --- |
| 精确控制药品、食物、炮弹、弹药等某种物品的总数 | **整个仓库数量** | 整个链接存储组内，每种物品最多保留 X 件。 |
| 保留多个可由不同 Pawn 分别预留的实体堆栈 | **类似堆栈 ×N** | 每堆最多 X 件，最多保留 N 个真实堆栈，总容量为 X × N。 |

配额会对每种物品分别计算。设置 100 并不是“整个仓库里各种物品合计 100 件”，而是每个使用该值的物品各自最多 100 件。

#### 为什么要保留多堆？

在部分搬运或预留优化环境下，一堆实体物品同时可能只能被一个 Pawn 或任务预留。多个真实堆栈能提供彼此独立的预留目标，减少多件武器同时换弹或补给时的排队。

### 快速使用

1. 选择货架、仓储建筑或仓储区。
2. 打开原版“存储”标签，点击右上角“存储配额”。
3. 选择“整个仓库数量”或“类似堆栈 ×N”。
4. 为当前允许存放的物品设置默认值。
5. 点击某个物品的“使用默认值”按钮，可为它设置单独的覆盖值。

窗口会显示每种物品的现有数量和实际堆数，并提示仍在等待搬运处理的超量物品或堆栈布局。

### 示例：105mm 弹药库

设置为**每堆 25、类似堆栈 ×4**：

- 总容量是 100 发。
- 如果组内是一堆 100 发，它不会被当成 75 发超量物品；搬运工会逐步把它拆成最多四堆、每堆不超过 25 发。
- 如果组内有 125 发，会准确搬出 25 发；在存在合法格位时，再把保留的 100 发整理为多个独立堆栈。

### 游戏里的实际行为

- 同一个原版 `StorageGroup` 中的链接货架共享一份配额。
- 每个物品定义分别统计数量。
- 新的入库搬运不会超过该组当前的剩余容量。
- 已有超量物品通过正常搬运工作移走，不会被删除或瞬移。
- 搬运工优先寻找来源组之外的合法仓储；找不到时，再寻找组外可到达的非仓储地面。
- “类似堆栈 ×N”会优先在组内拆分或归并；如果所选布局无法在组内完成，未解决部分可能被搬到组外。
- 本模组不会全局修改 `ThingDef.stackLimit`。

由于整理依赖正常工作，必须存在可用搬运工、合法预留、可达路径和足够格位，因此设置变化不一定立即完成。

### 0 表示什么？

- **整个仓库数量：**`0` 表示不限量。
- **类似堆栈 ×N：**`0` 表示使用该物品当前的堆叠上限作为单堆值，总容量为“当前堆叠上限 × N”。

自定义的单堆值不会让实际堆栈超过当前 `ThingDef.stackLimit`。

### 兼容性

- RimWorld 1.6
- **必须加载 Harmony。**
- 不强制依赖 Combat Extended，但 CE 弹药会作为普通可存储物品受到配额控制。
- 为 Pick Up And Haul 提供可选的目标格容量适配；这是针对性补丁，不代表覆盖所有自定义搬运路径。
- **与 Stack Gap（`Andromeda.StackGap`）不兼容。**启用本模组前请将其禁用；旧设置不会迁移。

### 非 Steam 创意工坊安装

1. 确保 Harmony 在本模组之前加载。
2. 将仓库／模组文件夹复制到 RimWorld 的 `Mods` 目录。
3. 启用 **Storage Group Quotas**，并保持 Stack Gap 禁用。

Steam 创意工坊用户只需订阅并启用本模组及其必需的 Harmony 依赖。

### 玩家常见问题

**每个货架会单独计算吗？**

不会。链接货架共享原版存储组的配额；没有链接的货架或仓储区使用自己的局部存储范围。

**“类似堆栈 ×N”一定会凑齐 N 堆吗？**

不会。N 是上限，实际堆数取决于现有数量、合法格位、可合并堆和可用搬运工。

**不同品质、材质或耐久度会分别计算配额吗？**

不会。配额和 N 个堆位按 `ThingDef` 统计；即使某些变体彼此不能合并，也会共享同一份配额预算。

**存储组会不会短暂超额？**

可能。容量按当前已经生成在地图上的内容计算；多个已经创建的任务、绕过补丁的自定义搬运模组或直接生成的物品都可能导致短暂超额，之后再由正常整理工作处理。

**GitHub 项目在哪里？**

[源代码与问题反馈](https://github.com/Steel-Shadow/RimWorld-StorageGroupQuotas)

### 开发者说明

#### 项目结构

| 路径 | 职责 |
| --- | --- |
| `Source/StorageQuotaData.cs` | 保存模式、默认值、堆数和按 `ThingDef` 的单项覆盖值。 |
| `Source/QuotaDataStore.cs` | 在运行时把配额数据附着到原版 `StorageSettings`。 |
| `Source/QuotaUtility.cs` | 范围解析、计数、有效容量、超量发现和扫描缓存。 |
| `Source/HarmonyPatches.cs` | 原版仓储／界面补丁及 Pick Up And Haul 可选适配。 |
| `Source/WorkGiver_MoveQuotaOverflow.cs` | 用正常搬运工作准确移走超量物品并整理类似堆栈。 |
| `Source/Window_StorageQuotas.cs` | 原版“存储”标签中的配额设置窗口。 |
| `Defs/WorkGiverDefs/WorkGivers.xml` | 注册自定义搬运 `WorkGiverDef`。 |
| `Languages/` | 英文和简体中文 Keyed 翻译。 |

本模组刻意不引入自定义 `JobDriver`、`GameComponent`、`MapComponent` 或全局 `ModSettings`。所有搬运都使用原版 `JobDefOf.HaulToCell`，数据则跟随对应的原版 `StorageSettings`。

#### 范围解析与存档

`QuotaUtility.ScopeAt`、`ScopeForSettings` 和 `ScopeForThing` 会在存在 `SlotGroup.StorageGroup` 时将局部 `SlotGroup` 解析为该存储组，否则使用局部范围。这就是链接货架共享配额的代码路径。

运行时，`QuotaDataStore` 使用 `ConditionalWeakTable<StorageSettings, Holder>`。`Patch_StorageSettings_ExposeData` 通过 `Scribe_Deep` 把 `StorageQuotaData` 写入 `storageGroupQuotas` 节点；总数量模式下完全未启用的数据不会进入存档。`Patch_StorageSettings_CopyFrom` 会在复制原版存储设置时克隆配额数据。

#### 容量公式

设 `v` 为单项覆盖值（没有覆盖时使用默认值），`L = max(1, ThingDef.stackLimit)`，`N` 为 `SimilarStackCount`。

```text
整个仓库数量：
  v = 0  -> 总容量不限
  v > 0  -> 总容量 = v

类似堆栈 ×N：
  单堆上限 = min(L, v = 0 ? L : v)
  总容量   = 单堆上限 × N
```

乘法使用 `long`，超过 `int.MaxValue` 时饱和为 `int.MaxValue`。界面允许配额值 0～1,000,000,000，N 为 1～1,000。

数量和堆位预算都按精确的 `ThingDef` 统计，而不是按 `CanStackWith` 等价类统计；真正合并时仍要求 `Thing.CanStackWith()`。

#### Harmony 补丁点

| 补丁 | 作用 |
| --- | --- |
| `StorageSettings.ExposeData` postfix | 随原版存储设置读写配额数据。 |
| `StorageSettings.CopyFrom` postfix | 复制设置时克隆配额状态。 |
| `StoreUtility.NoStorageBlockersIn` postfix | 没有剩余配额时拒绝目标格。 |
| `HaulAIUtility.HaulToCellStorageJob` postfix | 把 `Job.count` 限制到剩余容量并关闭机会性重复搬运。 |
| `ITab_Storage.FillTab` postfix | 在原版“存储”标签加入“存储配额”按钮。 |
| `PickUpAndHaul.WorkGiver_HaulToInventory.CapacityAt` postfix | 通过反射可选限制 Pick Up And Haul 的目标容量。 |

如果找不到 Pick Up And Haul 的类型或精确 `CapacityAt(Thing, IntVec3, Map)` 签名，兼容补丁会跳过。

#### 超量与堆栈整理流程

`QuotaUtility.BuildQuotaWorkThings()` 对每个存储组范围只扫描一次，把已生成内容按 `ThingDef` 分组，再按堆数降序和 `thingIDNumber` 排序。判断总量超限时优先保留较大的堆，从而确定真正落在配额之外的准确数量。

候选列表按地图缓存 30 游戏 tick；`NotifySettingsChanged()` 会递增版本号，使界面修改立即让缓存失效。

`WorkGiver_MoveQuotaOverflow` 随后会：

1. 为选中的堆重新计算准确超量数量。
2. 按原版优先级寻找来源范围之外的仓储组，并遵守目标过滤器和目标配额。
3. 找不到仓储时，在半径 40 内寻找可到达、可预留、可站立的非仓储地面，并避开火灾、阻挡、禁用格，以及不适合相应物品的种植区。
4. 对仅有布局问题的物品，优先合并到仍保留且最满的兼容堆；当前堆数小于 N 时，也可以在组内合法格位建立新堆。
5. 组内仍无法解决时，把未解决部分转入组外仓储／地面后备流程。

注册的 WorkGiver 属于 `Hauling`，`priorityInType` 为 20，需要 Manipulation，并使用 `1000 + 准确超量数` 作为工作优先值。禁用、燃烧、不可达、无法预留或不可搬运的物品会被跳过。

#### 已知限制

- 仅声明并测试 RimWorld 1.6。
- 正在路上的搬运任务不会预留配额容量，因此可能短暂超额。
- 完全绕过这些原版方法的自定义仓储／搬运代码可能不遵守入库限制；物品生成后仍可由整理 WorkGiver 处理。
- 类似堆栈预算按 `ThingDef` 计算；不同品质、材质或耐久变体不会各自获得 N 个堆位。
- 组内整理需要兼容堆或合法空格，否则仅布局超额的部分也可能被搬出。
- 地面后备只搜索半径 40，不保证屋顶、温控或天气保护。
- 配额窗口在打开时根据当前存储过滤器构建物品列表；保持窗口开启并修改过滤器不会动态重建列表。
- Stack Gap 数据不会迁移，两个模组不能同时运行。
- 当前没有自动化测试项目。

#### 构建

SDK 风格项目以 .NET Framework 4.7.2 为目标，引用 RimWorld、Unity 和 Harmony 程序集，并将这些引用设为 `Private=false`。

在仓库根目录运行：

```powershell
dotnet build .\Source\StorageGroupQuotas.csproj -c Release
```

默认路径指向作者本机 Steam 安装；其他开发环境可以覆盖：

```powershell
dotnet build .\Source\StorageGroupQuotas.csproj -c Release `
  -p:RimWorldManagedDir="X:\RimWorld\RimWorldWin64_Data\Managed" `
  -p:HarmonyDir="X:\Harmony\Assemblies"
```

输出位于 `1.6/Assemblies`。干净的 Workshop 包只应包含 `About`、`Defs`、`Languages` 和 Release DLL 等运行文件；首次上传后必须保留 `About/PublishedFileId.txt`，以后才能更新同一创意工坊项目。

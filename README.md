# Storage Group Quotas / 存储组配额

[English](#english) | [简体中文](#简体中文)

<a id="english"></a>

## English

Storage Group Quotas is a RimWorld 1.6 mod that attaches item quotas to the vanilla `StorageGroup`. It provides two explicit modes: **Entire storage count** and **Similar stacks ×N**.

### Rules

- A quota covers the entire vanilla `StorageGroup`. Twelve linked shelves are one quota scope, not twelve separately calculated shelves.
- **Entire storage count:** the entered value is the total number of a `ThingDef`—including Combat Extended ammunition—that may remain in the group. `0` means unlimited.
- **Similar stacks ×N:** the entered value is the per-stack cap, and total capacity is `per-stack cap × N`. `0` uses the item's current `ThingDef.stackLimit`.
- Similar-stacks mode uses normal hauling jobs to split oversized stacks and merge fragments beyond N stacks. This keeps several independent `Thing` stacks that different pawns can reserve at the same time—useful when several weapons need ammunition during combat.
- Incoming hauling is limited to the storage group's remaining quota.
- When existing contents exceed the quota, haulers first move the exact excess amount to valid storage outside the group.
- If no other valid storage exists, haulers place the excess on a reachable, valid floor cell outside the group.
- The mod does not modify `ThingDef.stackLimit`; it enforces per-stack limits through destination capacity and in-group rebalancing jobs.

For example, configure 105 mm ammunition as **25 per stack, Similar stacks ×4**:

- The group's total capacity is 100 rounds.
- If the group contains one stack of 100, haulers reorganize it into at most four stacks of no more than 25. The 100 rounds are not treated as excess.
- If the group contains 125 rounds, haulers first move exactly 25 outside the group, then reorganize the retained 100 into independent stacks.

### Installation and use

1. Disable Stack Gap (`Andromeda.StackGap`) and remove it from the active mod list. The two mods are incompatible.
2. Load Harmony before Storage Group Quotas.
3. For a GitHub install, download `StorageGroupQuotas.zip` from [Releases](https://github.com/Steel-Shadow/RimWorld-StorageGroupQuotas/releases), extract it into RimWorld's `Mods` directory, and enable it. Source archives do not contain compiled assemblies.
4. Select a shelf or stockpile, open the vanilla **Storage** tab, and click **Quotas** in the upper-right corner.
5. Select a mode and configure its default. You can also set per-item overrides, such as a separate value for 105 mm ammunition.

Pick Up And Haul is optionally supported: its destination-cell capacity calculation also respects the target storage group's remaining quota.

Existing Stack Gap settings are not migrated. Configure the new quotas after enabling this mod.

### Difference from Stack Gap terminology

Both modes use the vanilla `StorageGroup` as their scope, rather than only the currently selected shelf:

- **Entire storage count:** total capacity equals the configured value.
- **Similar stacks ×N:** total capacity equals the configured per-stack value multiplied by N, while preserving up to N independent stacks.

Items above total capacity are not left in place or instantly ejected. The mod creates normal hauling jobs to move them outside the group. If only the stack layout is incorrect, items are reorganized inside the group instead.

### Build

Run from the `Source` directory:

```powershell
dotnet build .\StorageGroupQuotas.csproj -c Release
```

The project restores public compile-time references for RimWorld 1.6.4871, Harmony 2.4.1, and .NET Framework 4.7.2 from NuGet, so a local RimWorld installation is not required to build it. Output is written to `1.6/Assemblies`, which is intentionally ignored by Git.

Every push to `main` runs the GitHub Actions workflow in `.github/workflows/build-release.yml`. A successful build replaces the installable `StorageGroupQuotas.zip` asset in the rolling `continuous` prerelease.

<a id="简体中文"></a>

## 简体中文

Storage Group Quotas（存储组配额）是一个面向 RimWorld 1.6 的独立模组。它把物品配额正确绑定到原版 `StorageGroup`（存储组），并提供**整个仓库数量**和**类似堆栈 ×N**两种明确模式。

### 规则

- 配额作用于整个原版 `StorageGroup`。12 个链接货架属于同一个配额范围，而不是 12 个分别计算的货架。
- **整个仓库数量：**输入值是组内某个 `ThingDef`（包括 Combat Extended 弹药）允许保留的总件数；`0` 表示不限量。
- **类似堆栈 ×N：**输入值是单堆上限，总容量为“单堆上限 × N”；`0` 表示采用该物品当前的 `ThingDef.stackLimit`。
- 类似堆栈模式会通过正常搬运工作拆分过大的单堆、合并超过 N 堆的零散堆，使组内保留多个可由不同 Pawn 同时预留的独立 `Thing` 堆栈。这在战斗中多件武器同时等待换弹时尤其有用。
- 入库搬运量不会超过该存储组的剩余配额。
- 现有数量超出配额时，搬运工优先把准确的超出数量送到该组之外的合法仓储。
- 如果没有其他合法仓储，搬运工会把超量部分放到该组之外、可到达且可放置的地面。
- 本模组不修改 `ThingDef.stackLimit`；单堆限制通过目标容量和组内整理工作实现。

例如，把 105mm 弹药设置为**每堆 25、类似堆栈 ×4**：

- 该组的总容量是 100 发。
- 如果组内当前是一堆 100 发，搬运工会把它整理成最多四堆、每堆不超过 25 发；不会把这 100 发当成超量搬走。
- 如果组内当前有 125 发，搬运工会先把准确的 25 发搬出该组，再把保留的 100 发整理为多个独立堆栈。

### 安装与使用

1. 禁用 Stack Gap（`Andromeda.StackGap`）并将其移出启用列表；两个模组不能同时运行。
2. 确保 Harmony 在本模组之前加载。
3. 如果从 GitHub 安装，请在 [Releases](https://github.com/Steel-Shadow/RimWorld-StorageGroupQuotas/releases) 下载 `StorageGroupQuotas.zip`，解压到 RimWorld 的 `Mods` 目录并启用。源码压缩包不包含已经编译的程序集。
4. 选择货架或仓储区，打开原版“存储”标签，点击右上角“存储配额”。
5. 选择模式并设置默认值；也可以为 105mm 弹药等单项设置覆盖值。

本模组对 Pick Up And Haul 提供可选兼容：它计算目标格容量时，同样会受到目标存储组剩余配额的限制。

Stack Gap 的旧设置不会自动迁移；启用本模组后，请在新的存储组配额窗口中重新设置。

### 与 Stack Gap 文案的区别

两种模式都以原版 `StorageGroup` 为统计范围，而不是只统计当前选中的单个货架：

- **整个仓库数量：**总容量就是设置值。
- **类似堆栈 ×N：**总容量是每堆设置值乘以 N，同时保留最多 N 个独立堆栈。

超出总容量的物品不会留在原处，也不会瞬间弹出；本模组会生成正常搬运工作，将超量部分移出该组。如果只是堆栈布局不符合要求，物品只会在组内重新整理。

### 构建

在 `Source` 目录运行：

```powershell
dotnet build .\StorageGroupQuotas.csproj -c Release
```

项目从 NuGet 恢复 RimWorld 1.6.4871、Harmony 2.4.1 与 .NET Framework 4.7.2 的公开编译引用，因此构建时不需要在本机安装 RimWorld。输出位于 `1.6/Assemblies`，该目录会被 Git 忽略。

每次向 `main` 推送都会运行 `.github/workflows/build-release.yml`。构建成功后，工作流会替换滚动预发布版 `continuous` 中可直接安装的 `StorageGroupQuotas.zip`。

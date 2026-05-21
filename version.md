# Jellyfin 12 兼容修复记录（2026-05-21）

本文档记录本次针对 `jellyfin-plugin-mergeversions` 的完整修复过程，方便后续复盘和继续维护。

## 1. 问题背景

在 Jellyfin 12 容器环境中，插件出现以下典型错误：

- `MissingMethodException: Video.SetPrimaryVersionId(string)` / `Video.get_PrimaryVersionId()`
- 说明插件与 Jellyfin 12 的实体 API 签名不完全一致（不同版本中 `PrimaryVersionId` 的类型/访问方式存在变化）
- 现象上表现为：
  - 合并任务执行报错或部分执行
  - 剧集列表仍显示 2 条（如同一集 `.mp4` + `.mkv`），虽然详情页能看到版本关联

## 2. 本次核心改动

### 2.1 目标框架与构建配置

- 将插件目标框架调整为 `net10.0`（匹配实际容器 runtime：`tfm=net10.0`）
- 更新构建元信息，避免 SDK/Runtime 不匹配导致构建失败

涉及文件：

- `Jellyfin.Plugin.MergeVersions/Jellyfin.Plugin.MergeVersions.csproj`
- `build.yaml`

### 2.2 增加跨版本兼容层（反射方式）

由于不同 Jellyfin 版本对 `Video` 的 API 可能是 `string` / `Guid` / `Guid?` 或方法签名不同，新增兼容读写逻辑：

- `PrimaryVersionId` 读取兼容：
  - 统一通过反射读取属性值并尝试解析
- `PrimaryVersionId` 写入兼容：
  - 优先调用 `SetPrimaryVersionId(...)`（自动匹配 `string/Guid/Guid?`）
  - 若无可用方法，回退到属性写入
- `GetLinkedAlternateVersions` 兼容：
  - 优先实体方法
  - 再尝试 `ILibraryManager` 方法
  - 最后回退到 `LinkedAlternateVersions` 的 `ItemId` 回查

涉及文件：

- `Jellyfin.Plugin.MergeVersions/MergeVersionsManager.cs`

### 2.3 修复 nullable ID 等编译问题

- 修复 `LinkedChild.ItemId` 为 nullable 时的编译错误（先 `HasValue` 再读取）
- 解决多轮编译中出现的签名不兼容问题

涉及文件：

- `Jellyfin.Plugin.MergeVersions/MergeVersionsManager.cs`

### 2.4 剧集合并策略修正（解决“仍显示 2 条”）

针对实际数据库现象（同一集两条记录）：

- `PrimaryVersionId` 为空
- `PresentationUniqueKey` 仍不同

本次做了两个关键修正：

- 合并分组去掉 `ProductionYear` 条件（避免同集因年份差异无法进同组）
- 合并时同步尝试对齐副版本的 `PresentationUniqueKey` 到主版本

并进一步修复“被误跳过写入”的情况：

- 原逻辑会跳过已存在于 `alternateVersionsOfPrimary` 的条目
- 新逻辑改为：所有非主版本都强制回写主版本关系与关键字段（幂等处理，不重复添加 link）

涉及文件：

- `Jellyfin.Plugin.MergeVersions/MergeVersionsManager.cs`

### 2.5 增强调试日志（用于定位落库失败）

新增 merge 关键路径日志，记录：

- 合并组信息（group count / primary id / primary path / primary key）
- 每个副版本写入前后值与持久化后值：
  - `PrimaryVersionId`：before / after / persisted
  - `PresentationUniqueKey`：before / after / persisted
- 兼容写入命中路径：
  - `method:*` 或 `property:*` 或 `no-writable-target`

这样可以快速区分：

- DLL 未更新
- 反射命中失败
- `UpdateToRepositoryAsync` 后未持久化

涉及文件：

- `Jellyfin.Plugin.MergeVersions/MergeVersionsManager.cs`

## 3. 主要提交记录（按时间顺序）

- `50d63a9` Fix Jellyfin 12 alternate-version API compatibility
- `df98ada` Retarget plugin build to net8.0 for SDK/runtime compatibility（中间过渡）
- `2920576` Target net10.0 to match Jellyfin 12 runtime（最终方向）
- `cc6dd6a` Fix compile errors with net10 target and current Jellyfin API
- `971a8a2` Replace SetPrimaryVersionId calls with direct PrimaryVersionId assignment
- `2dcaffd` Add compatibility layer for PrimaryVersionId and linked versions APIs
- `53bc492` Handle nullable linked item ids in compatibility fallback
- `7b583c0` try multi eposide（包含 episode 分组与 key 对齐）
- `6ce3fba` Add detailed merge diagnostics for primary and presentation keys
- `2eee7ea` Always rewrite non-primary episode metadata during merge（关键修复）

## 4. 数据库验证结论（本次排查中确认）

在 `BaseItems` 表中：

- `Type` 使用全类名（如 `MediaBrowser.Controller.Entities.TV.Episode`），不是简写 `Episode`
- 同一集重复条目若 `PresentationUniqueKey` 不同且 `PrimaryVersionId` 为空，列表通常仍会显示为 2 条

## 5. 当前状态与后续建议

当前代码已经包含：

- Jellyfin 12 的 API 兼容处理
- 剧集合并字段强制回写
- 完整调试日志

建议后续操作：

- 继续观察 `Merge write result` 日志与数据库字段是否同步变化
- 若后续稳定，可保留兼容层并适当精简日志级别


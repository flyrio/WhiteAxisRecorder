# WhiteAxisRecorder(白轴记录器)

## 简介
WhiteAxisRecorder 是 AEAssist 的 IAEPlugin 插件，用于记录战斗触发条件并导出为 AEAssist 触发树兼容的 JSON。
输出可直接作为 AEAssist 触发树使用（如 M10S_白轴MT.json），结构保持兼容。

## 功能
- 自动开始/结束记录，战斗结束可自动导出（可在 UI 关闭）
- 记录敌人读条（包含读条时间）
- 可选记录敌人技能效果（AbilityEffect，默认关闭，400ms 去重）
- 可选记录敌人平A
- 可选记录队伍减益(自己/队友，debuff)，并记录持续时间
- 可自定义各类条目的颜色
- 可选记录Boss增益(Boss身上，buff)，并记录持续时间（默认开启）
- 战斗结束保留多把记录，可编辑保留节点后导出
- 状态类条目按目标+状态去重（窗口内合并，默认 300ms），排除"濒死/衰弱/受伤加重/伤害降低"
- 不记录友方单位的技能与读条

## 记录来源
- 敌人读条(EnemyCastSpellCondParams)
- AbilityEffect 触发(ReceviceAbilityEffectCondParams，排除友方来源，默认关闭)
- 队伍减益出现(AddStatusCondParams，仅记录自己/队友，导出为目标符合条件+目标选择器过滤)
- Boss增益出现(AddStatusCondParams，仅记录Boss目标，导出为目标符合条件+目标选择器过滤)

## 导出
- 导出目录可在 UI 手动填写，留空时使用默认目录
- 默认输出目录:`D:\Triggerlines`
- 文件命名:`Territory_{TerritoryId}_Job_{JobId}_YYYYMMDD_HHMMSS.json`
- 备注:`技能名(来源:施法者/无来源 目标:职能1/职能2/无目标) +Xs`，附带读条/持续时间
- 结构:`TreeRoot -> TreeParallel -> TreeSequence -> TreeCondNode`

## 使用流程
1. 编译插件(参考 `docs/BUILD.md`)
2. 进入副本并自动记录
3. 在导出目录查看导出文件(默认 `D:\Triggerlines`)

## 文档
- 使用说明:`docs/USAGE.md`
- 导出格式:`docs/EXPORT_FORMAT.md`
- 构建说明:`docs/BUILD.md`
- 排查说明:`docs/TROUBLESHOOTING.md`

## 说明
- 仅记录触发条件，不新增行为节点
- 导出 JSON 与 AEAssist 触发树兼容
- 需安装 AEAssist 本体

# 导出格式

导出 JSON 与 AEAssist 触发树兼容，结构示例:

结构:`TreeRoot -> TreeParallel -> TreeSequence -> TreeCondNode`

每条记录对应一个序列节点，挂在并行节点下。

```json
{
  "GUID": "...",
  "ConfigVersion": 6,
  "TargetJob": 21,
  "Author": "Player",
  "Name": "副本名(1323)_职业名",
  "TreeRoot": {
    "DisplayName": "Start",
    "Childs": [
      {
        "$type": "AEAssist.CombatRoutine.Trigger.Node.TreeParallel, AEAssist",
        "DisplayName": "并行",
        "AnyReturn": false,
        "StopWhenDead": false,
        "Childs": [
          {
            "$type": "AEAssist.CombatRoutine.Trigger.Node.TreeSequence, AEAssist",
            "DisplayName": "序列",
            "IgnoreNodeResult": false,
            "StopWhenDead": false,
            "Childs": [
              {
                "$type": "AEAssist.CombatRoutine.Trigger.Node.TreeCondNode, AEAssist",
                "CondLogicType": 0,
                "CheckOnce": false,
                "ReverseResult": false,
                "TriggerConds": [
                  {
                    "$type": "AEAssist.CombatRoutine.Trigger.TriggerCond.TriggerCondEnemyCastSpell, AEAssist",
                    "DisplayName": "通用/敌人读条",
                    "RegexNameOrId": "技能ID"
                  }
                ],
                "DisplayName": "等待条件",
                "Important": false,
                "Color": {
                  "X": 0.2,
                  "Y": 0.8,
                  "Z": 0.2,
                  "W": 1.0
                },
                "Id": 3,
                "Enable": true,
                "Remark": "",
                "Tag": ""
              }
            ],
            "Important": false,
            "Color": {
              "X": 0.2,
              "Y": 0.8,
              "Z": 0.2,
              "W": 1.0
            },
            "Id": 2,
            "Enable": true,
            "Remark": "",
            "Tag": ""
          }
        ],
        "Important": false,
        "Color": {
          "X": 0.2,
          "Y": 0.8,
          "Z": 0.2,
          "W": 1.0
        },
        "Id": 1,
        "Enable": true,
        "Remark": "",
        "Tag": ""
      }
    ],
    "Important": false,
    "Color": {
      "X": 1.0,
      "Y": 1.0,
      "Z": 0.4,
      "W": 1.0
    },
    "Id": 0,
    "Enable": true,
    "Remark": "",
    "Tag": ""
  }
}
```



## 触发条件映射
- 敌人读条: `EnemyCastSpellCondParams` -> `TriggerCondEnemyCastSpell`
  - `RegexNameOrId`: 技能 ID（若无法解析则回退技能名）
  - `NeedTargetable`: 是否需要可选中
- 技能效果: `ReceviceAbilityEffectCondParams` -> `TriggerCondReceviceAbilityEffect`（需勾选 `记录效果触发`）
  - `ActionId`: 技能 ID
  - `CheckIsMe`: 固定为 `false`
  - `LimitType`: 固定为 `0`
- 队伍减益出现: `AddStatusCondParams` -> `TriggerCondWaitTarget`（目标符合条件）
  - `TargetSelector.Enable`: 固定为 `true`
  - `TargetSelector.Target`: 从周围队友中过滤
  - `TargetSelector.NeedTargetable`: 按目标是否可选中
  - `TargetSelector.FilterDatas[0].Filter`: `4`（Buff过滤）
  - `TargetSelector.FilterDatas[0].UintParam1`: 状态 ID
  - `TargetSelector.FilterDatas[0].LeftTime`: 持续时间（毫秒，取整）
  - `TargetSelector.FilterDatas[0].CompareType`: `5`
  - `TargetSelector.FilterDatas[0].BuffCompareType`: `3`
  - `TargetSelector.FilterDatas[0].Remark`: `获得buff {状态ID} {持续时间}ms`
- Boss增益出现: `AddStatusCondParams` -> `TriggerCondWaitTarget`（默认开启，仅记录Boss目标）
  - `TargetSelector.Target`: 从周围敌人中选择
  - 其余字段同上
## 备注字段
- `TreeCondNode.Remark` 格式:`名称(来源:施法者/无来源 目标:职能1/职能2/无目标) +Xs`，附加 `读条 Ys` 和/或 `持续 Zms`
- 目标来源于触发时的目标集合，多个目标用 `/` 分隔，无目标则写 `无目标`，优先使用职能(职业)名，无法解析时回退为目标名
- `X` 为触发相对记录开始时间

## 节点颜色
- 条件节点默认勾选高亮显示（`Important = true`）
- TreeRoot 使用 `1.0, 1.0, 0.4, 1.0`，并行/序列节点使用 `0.2, 0.8, 0.2, 1.0`
- 触发节点颜色按条目颜色配置

## 去重策略
- AbilityEffect 默认 400ms 去重
- 状态类（队伍减益/Boss增益）仅在持续时间变长时追加记录
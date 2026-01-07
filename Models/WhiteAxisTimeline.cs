using System.Text.Json.Serialization;

namespace WhiteAxisRecorder.Models
{
    public sealed class TimelineConfig
    {
        public string GUID { get; set; } = string.Empty;
        public int ConfigVersion { get; set; } = 6;
        public int TargetJob { get; set; }
        public string Author { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TreeRoot TreeRoot { get; set; } = new();
        public List<object> ExposedVars { get; set; } = new();
        public string ExposedVarDesc { get; set; } = string.Empty;
        public string OpenerScript { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public int TerritoryTypeId { get; set; }
        public int TerritoryWeatherId { get; set; }
        public string TargetAcrAuthor { get; set; } = string.Empty;
        public bool ClearCustomed { get; set; }
        public string LogsAddress { get; set; } = string.Empty;
    }

    public sealed class TreeRoot
    {
        public string DisplayName { get; set; } = "Start";
        public List<object> Childs { get; set; } = new();
        public bool Important { get; set; }
        public Color Color { get; set; } = new();
        public int Id { get; set; }
        public bool Enable { get; set; } = true;
        public string Remark { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    public abstract class TreeNodeBase
    {
        public string DisplayName { get; set; } = string.Empty;
        public bool Important { get; set; }
        public Color Color { get; set; } = new();
        public int Id { get; set; }
        public bool Enable { get; set; } = true;
        public string Remark { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    public sealed class TreeSequence : TreeNodeBase
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.Node.TreeSequence, AEAssist";

        public bool IgnoreNodeResult { get; set; }
        public bool StopWhenDead { get; set; }
        public List<object> Childs { get; set; } = new();
    }

    public sealed class TreeParallel : TreeNodeBase
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.Node.TreeParallel, AEAssist";

        public bool AnyReturn { get; set; }
        public bool StopWhenDead { get; set; }
        public List<object> Childs { get; set; } = new();
    }

    public sealed class TreeCondNode : TreeNodeBase
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.Node.TreeCondNode, AEAssist";

        public int CondLogicType { get; set; }
        public bool CheckOnce { get; set; }
        public bool ReverseResult { get; set; }
        public List<object> TriggerConds { get; set; } = new();
    }

    public sealed class TriggerCondEnemyCastSpell
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.TriggerCond.TriggerCondEnemyCastSpell, AEAssist";

        public string Remark { get; set; } = string.Empty;
        public string RegexNameOrId { get; set; } = string.Empty;
        public bool NeedTargetable { get; set; }
        public string DisplayName { get; set; } = "通用/敌人读条";
    }

    public sealed class TriggerCondReceviceAbilityEffect
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.TriggerCond.TriggerCondReceviceAbilityEffect, AEAssist";

        public string Remark { get; set; } = string.Empty;
        public string DisplayName { get; set; } = "副本/等待技能效果";
        public int ActionId { get; set; }
        public bool CheckIsMe { get; set; }
        public int LimitType { get; set; }
    }

    public sealed class TriggerCondGameLog
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.TriggerCond.TriggerCondGameLog, AEAssist";

        public string Remark { get; set; } = string.Empty;
        public string DisplayName { get; set; } = "通用/游戏日志";
        public string RegexValue { get; set; } = string.Empty;
        public bool LimitMsgType { get; set; }
        public int MsgType { get; set; }
    }

    public sealed class TriggerCondWaitTarget
    {
        [JsonPropertyName("$type")]
        public string Type { get; set; } = "AEAssist.CombatRoutine.Trigger.TriggerCond.TriggerCondWaitTarget, AEAssist";

        public TargetSelector TargetSelector { get; set; } = new();
        public string? Remark { get; set; }
        public string DisplayName { get; set; } = "General/目标符合条件";
    }

    public sealed class TargetSelector
    {
        public bool Enable { get; set; }
        public int Target { get; set; }
        public List<TargetSelectorFilterData> FilterDatas { get; set; } = new();
        public bool NeedTargetable { get; set; }
        public int SndFilter { get; set; }
        public int PMIndex { get; set; }
    }

    public sealed class TargetSelectorFilterData
    {
        public int Filter { get; set; }
        public string Remark { get; set; } = string.Empty;
        public string StrParam1 { get; set; } = string.Empty;
        public int UintParam1 { get; set; }
        public float FloatParam1 { get; set; }
        public int LeftTime { get; set; }
        public int JobsCategory { get; set; }
        public int Jobs { get; set; }
        public int CompareType { get; set; }
        public int BuffCompareType { get; set; }
        public int Marker { get; set; }
        public bool Nearest { get; set; }
    }

    public sealed class Color
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }
    }
}

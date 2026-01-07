using AEAssist;
using AEAssist.CombatRoutine.Module;
using AEAssist.CombatRoutine.Trigger;
using AEAssist.Helper;
using AEAssist.MemoryApi;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhiteAxisRecorder.Models;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace WhiteAxisRecorder.Recorder
{
    public sealed class WhiteAxisRecorderService
    {
        private const int AbilityEffectDebounceMs = 400;
        private const int OutputPathMaxLength = 512;
        private const int TargetSelectorPartyMember = 3;
        private const int TargetSelectorEnemy = 4;
        private const int TargetSelectorFilterBuff = 4;
        private const int TargetSelectorCompareType = 5;
        private const int TargetSelectorBuffCompareType = 3;
        private const int DefaultStatusDedupeMs = 300;
        private const int EnemyCastSameSkillDebounceMs = 300;
        private const float StatusDurationIncreaseThresholdMs = 500f;

        private readonly object _sync = new();
        private readonly List<RecordedTrigger> _records = new();
        private readonly Dictionary<string, DateTime> _recentKeys = new();
        private readonly Dictionary<string, StatusSeenInfo> _statusSeen = new();
        private readonly List<RecordedSession> _sessions = new();
        private static readonly object _statusLookupLock = new();
        private static Dictionary<string, uint>? _statusNameLookup;
        private int _nextSessionId = 1;
        private int _selectedSessionId = -1;

        private bool _recordingEnabled = true;
        private bool _recordDebuffs;
        private bool _recordBossBuffs = true;
        private bool _recordAbilityEffects;
        private bool _recordAutoAttacks;
        private bool _autoExportOnEnd = true;
        private int _statusDedupeMs = DefaultStatusDedupeMs;
        private string _outputDirectoryInput = string.Empty;
        private string _customOutputDirectory = string.Empty;
        private string _configPath = string.Empty;
        private readonly Color _colorEnemyCastTargetable = DefaultGreen();
        private readonly Color _colorEnemyCastUntargetable = DefaultGreen();
        private readonly Color _colorAbilityEffect = DefaultGreen();
        private readonly Color _colorAutoAttack = DefaultGreen();
        private readonly Color _colorDebuff = DefaultGreen();
        private readonly Color _colorBossBuff = DefaultGreen();
        private bool _sessionActive;
        private DateTime _sessionStartUtc;
        private int _territoryId;
        private int _nextNodeId;
        private string _status = "空闲";
        private string _lastExportPath = string.Empty;

        public void OnLoad()
        {
            LoadConfig();
            TriggerlineData.OnCondParamsCreate += OnCondParamsCreate;
            Svc.DutyState.DutyCompleted += OnDutyCompleted;
            Svc.DutyState.DutyWiped += OnDutyWiped;
        }

        public void Dispose()
        {
            TriggerlineData.OnCondParamsCreate -= OnCondParamsCreate;
            Svc.DutyState.DutyCompleted -= OnDutyCompleted;
            Svc.DutyState.DutyWiped -= OnDutyWiped;
        }

        public void Update()
        {
            try
            {
                bool inMission = Core.Resolve<MemApiDuty>().InMission;
                if (!_recordingEnabled)
                {
                    if (_sessionActive)
                        ClearSession("记录已关闭");
                    return;
                }

                if (inMission && !_sessionActive)
                {
                    StartSession();
                }
                else if (!inMission && _sessionActive)
                {
                    EndSession("离开副本", export: _autoExportOnEnd);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 更新错误: {ex.Message}");
            }
        }

        public void Draw()
        {
            bool optionChanged = false;
            if (ImGui.Checkbox("启用记录", ref _recordingEnabled))
            {
                if (!_recordingEnabled && _sessionActive)
                    ClearSession("记录已关闭");
                optionChanged = true;
            }

            ImGui.SameLine();
            if (ImGui.Checkbox("自动导出", ref _autoExportOnEnd))
                optionChanged = true;

            ImGui.Spacing();
            ImGui.Text("状态记录:");
            ImGui.SameLine();
            if (ImGui.Checkbox("记录队伍减益(自己/队友)", ref _recordDebuffs))
                optionChanged = true;
            ImGui.SameLine();
            if (ImGui.Checkbox("记录Boss增益(Boss身上)", ref _recordBossBuffs))
                optionChanged = true;
            int statusDedupeMs = _statusDedupeMs;
            if (ImGui.InputInt("状态去重间隔(ms)", ref statusDedupeMs))
            {
                _statusDedupeMs = Math.Clamp(statusDedupeMs, 0, 600000);
                optionChanged = true;
            }
            ImGui.Spacing();
            ImGui.Text("战斗事件:");
            ImGui.SameLine();
            if (ImGui.Checkbox("记录效果触发", ref _recordAbilityEffects))
                optionChanged = true;
            ImGui.SameLine();
            if (ImGui.Checkbox("记录平A", ref _recordAutoAttacks))
                optionChanged = true;

            if (optionChanged)
                SaveConfig();

            ImGui.Separator();
            ImGui.Text($"状态: {_status}");
            ImGui.Text($"记录中: {(_sessionActive ? "是" : "否")}");
            ImGui.Text($"触发数量: {_records.Count}");
            if (!string.IsNullOrWhiteSpace(_lastExportPath))
            {
                ImGui.Text($"最近导出: {_lastExportPath}");
            }

            if (ImGui.Button("立即导出"))
            {
                ExportSession();
            }

            ImGui.SameLine();

            if (ImGui.Button("清空"))
            {
                ClearSession("手动清空");
            }

            ImGui.Separator();
            ImGui.Text("条目颜色");
            bool colorChanged = false;
            colorChanged |= DrawColorPicker("读条(可选中)", _colorEnemyCastTargetable);
            colorChanged |= DrawColorPicker("读条(不可选中)", _colorEnemyCastUntargetable);
            colorChanged |= DrawColorPicker("技能效果", _colorAbilityEffect);
            colorChanged |= DrawColorPicker("平A效果", _colorAutoAttack);
            colorChanged |= DrawColorPicker("减益", _colorDebuff);
            colorChanged |= DrawColorPicker("Boss增益", _colorBossBuff);
            if (colorChanged)
                SaveConfig();

            ImGui.Separator();
            ImGui.Text($"战斗记录: {_sessions.Count}");
            List<RecordedSession> sessionsSnapshot;
            lock (_sync)
            {
                sessionsSnapshot = new List<RecordedSession>(_sessions);
            }

            if (sessionsSnapshot.Count == 0)
            {
                ImGui.Text("暂无记录");
            }
            else
            {
                ImGui.BeginChild("##SessionList", new Vector2(0, 120), true);
                foreach (var session in sessionsSnapshot)
                {
                    bool selected = session.Id == _selectedSessionId;
                    if (ImGui.Selectable(BuildSessionTitle(session), selected))
                        _selectedSessionId = session.Id;
                }
                ImGui.EndChild();

                RecordedSession? selectedSession;
                lock (_sync)
                {
                    selectedSession = _sessions.FirstOrDefault(session => session.Id == _selectedSessionId);
                }

                if (selectedSession != null)
                {
                    ImGui.Text($"记录数: {selectedSession.Entries.Count}");
                    ImGui.Text($"开始: {selectedSession.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    ImGui.Text($"结束: {selectedSession.EndUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    string territoryDisplay = BuildTerritoryDisplay(selectedSession.TerritoryId);
                    string jobDisplay = BuildJobDisplay(selectedSession.JobId);
                    ImGui.Text($"来源: {territoryDisplay} / {jobDisplay} / {selectedSession.Reason}");

                    if (ImGui.Button("全选"))
                    {
                        lock (_sync)
                        {
                            foreach (var entry in selectedSession.Entries)
                                entry.Selected = true;
                        }
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("全不选"))
                    {
                        lock (_sync)
                        {
                            foreach (var entry in selectedSession.Entries)
                                entry.Selected = false;
                        }
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("删除未选"))
                    {
                        lock (_sync)
                        {
                            selectedSession.Entries.RemoveAll(entry => !entry.Selected);
                        }
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("导出选中"))
                    {
                        ExportSession(selectedSession, selectedOnly: true);
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("删除该记录"))
                    {
                        lock (_sync)
                        {
                            _sessions.Remove(selectedSession);
                            if (_selectedSessionId == selectedSession.Id)
                                _selectedSessionId = _sessions.Count > 0 ? _sessions[0].Id : -1;
                        }
                    }

                    ImGui.BeginChild("##SessionEntries", new Vector2(0, 200), true);
                    for (int i = 0; i < selectedSession.Entries.Count; i++)
                    {
                        var entry = selectedSession.Entries[i];
                        bool selected = entry.Selected;
                        ImGui.PushID(i);
                        if (ImGui.Checkbox("##keep", ref selected))
                        {
                            lock (_sync)
                            {
                                entry.Selected = selected;
                            }
                        }
                        ImGui.SameLine();
                        ImGui.TextUnformatted(BuildRemark(entry.Trigger, selectedSession.StartUtc));
                        ImGui.PopID();
                    }
                    ImGui.EndChild();
                }
            }

            ImGui.Separator();
            ImGui.Text("导出目录");
            ImGui.InputText("自定义路径", ref _outputDirectoryInput, OutputPathMaxLength);
            ImGui.SameLine();
            if (ImGui.Button("保存"))
            {
                _customOutputDirectory = (_outputDirectoryInput ?? string.Empty).Trim();
                _outputDirectoryInput = _customOutputDirectory;
                SaveConfig();
            }

            ImGui.SameLine();
            if (ImGui.Button("打开目录"))
            {
                OpenOutputDirectory();
            }

            string defaultOutputDirectory = GetDefaultOutputDirectory();
            ImGui.Text("默认路径(可复制)");
            ImGui.InputText("##DefaultOutputDir", ref defaultOutputDirectory, OutputPathMaxLength, ImGuiInputTextFlags.ReadOnly);

            string effectiveOutputDirectory = ResolveOutputDirectory();
            ImGui.Text("当前生效(可复制)");
            ImGui.InputText("##EffectiveOutputDir", ref effectiveOutputDirectory, OutputPathMaxLength, ImGuiInputTextFlags.ReadOnly);
        }

        private void OnDutyCompleted(object? sender, ushort e)
        {
            if (!_sessionActive)
                return;

            EndSession("副本完成", export: _autoExportOnEnd);
        }

        private void OnDutyWiped(object? sender, ushort e)
        {
            if (!_sessionActive)
                return;

            EndSession("副本团灭", export: _autoExportOnEnd);
        }

        private void StartSession()
        {
            lock (_sync)
            {
                _sessionActive = true;
                _sessionStartUtc = DateTime.UtcNow;
                _territoryId = (int)Core.Resolve<MemApiZoneInfo>().GetCurrTerrId();
                _records.Clear();
                _recentKeys.Clear();
                _statusSeen.Clear();
                _nextNodeId = 1;
                _status = "记录中";
            }

            LogHelper.Print("WhiteAxisRecorder 已开始记录。");
        }

        private void ClearSession(string reason)
        {
            lock (_sync)
            {
                _sessionActive = false;
                _records.Clear();
                _recentKeys.Clear();
                _statusSeen.Clear();
                _nextNodeId = 1;
                _status = reason;
            }

            LogHelper.Print($"WhiteAxisRecorder 已清空记录: {reason}。");
        }

        private void EndSession(string reason, bool export)
        {
            RecordedSession? session = SaveSessionSnapshot(reason);
            if (export && session != null)
                ExportSession(session, selectedOnly: false);
            ClearSession(reason);
        }

        private RecordedSession? SaveSessionSnapshot(string reason)
        {
            List<RecordedTrigger> snapshot;
            DateTime startUtc;
            int territoryId;
            int jobId;
            string author;

            lock (_sync)
            {
                if (_records.Count == 0)
                {
                    _status = reason;
                    return null;
                }

                snapshot = new List<RecordedTrigger>(_records);
                startUtc = _sessionStartUtc;
                territoryId = _territoryId;
                jobId = Core.Me != null ? GetClassJobId(Core.Me.ClassJob) : 0;
                author = Core.Me?.Name.ToString() ?? "Unknown";
            }

            var session = new RecordedSession
            {
                Id = _nextSessionId++,
                StartUtc = startUtc,
                EndUtc = DateTime.UtcNow,
                TerritoryId = territoryId,
                JobId = jobId,
                Author = author,
                Reason = reason,
                Entries = snapshot.ConvertAll(record => new RecordedEntry(record))
            };

            lock (_sync)
            {
                _sessions.Insert(0, session);
                if (_selectedSessionId < 0)
                    _selectedSessionId = session.Id;
            }

            return session;
        }

        private void OnCondParamsCreate(ITriggerCondParams condParams)
        {
            try
            {
                if (!Core.Resolve<MemApiDuty>().InMission)
                    return;

                if (!_recordingEnabled)
                    return;

                if (!_sessionActive)
                    StartSession();

                var typeName = condParams.GetType().Name;
                if (typeName == "EnemyCastSpellCondParams")
                {
                    RecordEnemyCast(condParams);
                    return;
                }

                if (typeName == "ReceviceAbilityEffectCondParams")
                {
                    if (_recordAbilityEffects)
                        RecordAbilityEffect(condParams);
                    return;
                }

                if (typeName == "AddStatusCondParams")
                {
                    RecordDebuff(condParams);
                    RecordBossBuff(condParams);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 记录错误: {ex.Message}");
            }
        }

        private void RecordEnemyCast(ITriggerCondParams condParams)
        {
            IBattleChara? caster = GetObjectProperty<IBattleChara>(condParams, "Object", "Caster", "Source");
            if (caster != null)
            {
                uint? casterId = GetEntityId(caster);
                if (casterId.HasValue && IsPartyMember(casterId.Value))
                    return;
            }

            bool? casterTargetable = caster != null
                ? GetBoolProperty(caster, "IsTargetable", "Targetable", "CanTarget")
                : null;
            string? name = GetStringProperty(condParams, "RegexNameOrId", "ActionName", "SpellName", "Name");
            int? actionId = GetIntProperty(condParams, "ActionId", "SpellId", "Id", "SkillId", "AbilityId");
            bool needTargetable = casterTargetable
                ?? (GetBoolProperty(condParams, "NeedTargetable", "IsTargetable") ?? false);
            float? castTimeSeconds = GetFloatProperty(condParams, "TotalCastTimeInSec", "CastTimeInSec", "CastTime", "TotalCastTime");
            if (castTimeSeconds.HasValue && castTimeSeconds.Value <= 0)
                castTimeSeconds = null;

            string? nameOrId = NormalizeNameOrId(name, actionId);
            if (string.IsNullOrWhiteSpace(nameOrId))
                return;

            string? sourceName = ResolveTargetLabel(caster);
            if (string.IsNullOrWhiteSpace(sourceName))
                sourceName = GetSourceNameFromCondParams(condParams);
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                uint? sourceId = GetUintProperty(condParams, "ObjectId", "ObjectID");
                if (sourceId.HasValue)
                    sourceName = ResolveTargetLabelFromId(sourceId.Value);
            }

            string targetSummary = BuildTargetSummary(GetTargetNamesFromCondParams(condParams));
            var record = new RecordedTrigger(
                RecordedTriggerType.EnemyCast,
                nameOrId,
                actionId,
                needTargetable,
                DateTime.UtcNow,
                castTimeSeconds,
                null,
                sourceName,
                targetSummary,
                false);
            string dedupeKey = actionId.HasValue ? $"EnemyCast:{actionId.Value}" : $"EnemyCast:{nameOrId}";
            AddRecord(record, dedupeKey, EnemyCastSameSkillDebounceMs);
        }

        private void RecordAbilityEffect(ITriggerCondParams condParams)
        {
            int? actionId = GetIntProperty(condParams, "ActionId", "SpellId", "Id", "SkillId", "AbilityId");
            string? name = GetStringProperty(condParams, "ActionName", "SpellName", "Name");
            string? nameOrId = NormalizeNameOrId(name, actionId);
            if (string.IsNullOrWhiteSpace(nameOrId) || actionId == null)
                return;

            bool isAutoAttack = IsAutoAttack(actionId.Value);
            if (!_recordAutoAttacks && isAutoAttack)
                return;

            uint? sourceId = null;
            IGameObject? source = GetObjectProperty<IGameObject>(condParams, "Source", "SourceObject", "SourceEntity");
            if (source != null)
                sourceId = GetEntityId(source);
            else
                sourceId = GetUintProperty(condParams, "SourceId", "SourceEntityId", "CasterId", "SourceObjectId", "SourceObjectID");

            if (sourceId.HasValue && IsPartyMember(sourceId.Value))
                return;

            string? sourceName = ResolveTargetLabel(source);
            if (string.IsNullOrWhiteSpace(sourceName))
                sourceName = GetSourceNameFromCondParams(condParams);

            string targetSummary = BuildTargetSummary(GetTargetNamesFromCondParams(condParams));
            var record = new RecordedTrigger(
                RecordedTriggerType.AbilityEffect,
                nameOrId,
                actionId,
                false,
                DateTime.UtcNow,
                null,
                null,
                sourceName,
                targetSummary,
                isAutoAttack);
            AddRecord(record, $"Ability:{actionId}", AbilityEffectDebounceMs);
        }

        private void RecordDebuff(ITriggerCondParams condParams)
        {
            if (!_recordDebuffs)
                return;

            if (!TryResolveStatusInfo(condParams, out int statusId, out string statusName))
                return;

            if (IsExcludedStatusName(statusName))
                return;

            IBattleChara? target = GetObjectProperty<IBattleChara>(condParams, "Target");
            if (target == null)
                return;

            uint? targetId = GetEntityId(target);
            if (!targetId.HasValue || !IsPartyMember(targetId.Value))
                return;

            if (!IsDebuff((uint)statusId))
                return;

            float? durationMilliseconds = GetStatusDurationMilliseconds(target, (uint)statusId);
            string? targetName = ResolveTargetLabel(target);
            bool needTargetable = GetBoolProperty(target, "IsTargetable", "Targetable", "CanTarget") ?? false;
            int? stackCount = GetStatusStackCount(condParams);

            string? sourceName = GetSourceNameFromCondParams(condParams);
            string targetSummary = BuildTargetSummary(string.IsNullOrWhiteSpace(targetName)
                ? Array.Empty<string>()
                : new[] { targetName });
            string dedupeKey = $"Debuff:Id:{statusId}:{targetId.Value}";
            string dedupeNameKey = $"Debuff:Name:{statusName}:{targetId.Value}";
            string dedupeLabelKey = !string.IsNullOrWhiteSpace(targetSummary)
                ? $"Debuff:Label:{statusName}:{targetSummary}"
                : string.Empty;
            if (!ShouldRecordStatus(durationMilliseconds, stackCount, dedupeKey, dedupeNameKey, dedupeLabelKey))
                return;

            var record = new RecordedTrigger(
                RecordedTriggerType.DebuffApplied,
                statusName,
                statusId,
                needTargetable,
                DateTime.UtcNow,
                null,
                durationMilliseconds,
                sourceName,
                targetSummary,
                false);

            AddRecord(record, dedupeKey, 0);
        }

        private void RecordBossBuff(ITriggerCondParams condParams)
        {
            if (!_recordBossBuffs)
                return;

            if (!TryResolveStatusInfo(condParams, out int statusId, out string statusName))
                return;

            if (IsExcludedStatusName(statusName))
                return;

            IBattleChara? target = GetObjectProperty<IBattleChara>(condParams, "Target");
            if (target == null)
                return;

            uint? targetId = GetEntityId(target);
            if (!targetId.HasValue)
                return;

            if (IsPartyMember(targetId.Value))
                return;

            if (!IsBossTarget(target))
                return;

            if (!IsBuff((uint)statusId))
                return;

            float? durationMilliseconds = GetStatusDurationMilliseconds(target, (uint)statusId);
            string? targetName = ResolveTargetLabel(target);
            bool needTargetable = GetBoolProperty(target, "IsTargetable", "Targetable", "CanTarget") ?? false;
            int? stackCount = GetStatusStackCount(condParams);

            string? sourceName = GetSourceNameFromCondParams(condParams);
            string targetSummary = BuildTargetSummary(string.IsNullOrWhiteSpace(targetName)
                ? Array.Empty<string>()
                : new[] { targetName });
            string dedupeKey = $"BossBuff:Id:{statusId}:{targetId.Value}";
            string dedupeNameKey = $"BossBuff:Name:{statusName}:{targetId.Value}";
            string dedupeLabelKey = !string.IsNullOrWhiteSpace(targetSummary)
                ? $"BossBuff:Label:{statusName}:{targetSummary}"
                : string.Empty;
            if (!ShouldRecordStatus(durationMilliseconds, stackCount, dedupeKey, dedupeNameKey, dedupeLabelKey))
                return;

            var record = new RecordedTrigger(
                RecordedTriggerType.BossBuffApplied,
                statusName,
                statusId,
                needTargetable,
                DateTime.UtcNow,
                null,
                durationMilliseconds,
                sourceName,
                targetSummary,
                false);

            AddRecord(record, dedupeKey, 0);
        }

        private void AddRecord(RecordedTrigger record, string dedupeKey, int debounceMs)
        {
            lock (_sync)
            {
                if (!_sessionActive)
                    return;

                if (debounceMs > 0)
                {
                    if (_recentKeys.TryGetValue(dedupeKey, out var lastTime))
                    {
                        if ((record.TimestampUtc - lastTime).TotalMilliseconds < debounceMs)
                            return;
                    }
                    _recentKeys[dedupeKey] = record.TimestampUtc;
                }

                _records.Add(record);
            }
        }

        private void ExportSession()
        {
            try
            {
                List<RecordedTrigger> snapshot;
                int territoryId;
                int jobId;
                string author;
                DateTime sessionStartUtc;

                lock (_sync)
                {
                    if (_records.Count == 0)
                    {
                        LogHelper.Print("WhiteAxisRecorder: 没有可导出的记录。");
                        return;
                    }

                    snapshot = new List<RecordedTrigger>(_records);
                    territoryId = _territoryId;
                    sessionStartUtc = _sessionStartUtc;
                    author = Core.Me?.Name.ToString() ?? "Unknown";
                    jobId = Core.Me != null ? GetClassJobId(Core.Me.ClassJob) : 0;
                }

                ExportSessionInternal(snapshot, territoryId, jobId, author, sessionStartUtc);
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 导出错误: {ex.Message}");
            }
        }

        private void ExportSession(RecordedSession session, bool selectedOnly)
        {
            try
            {
                List<RecordedTrigger> records;
                lock (_sync)
                {
                    records = selectedOnly
                        ? session.Entries.Where(entry => entry.Selected).Select(entry => entry.Trigger).ToList()
                        : session.Entries.Select(entry => entry.Trigger).ToList();
                }

                ExportSessionInternal(records, session.TerritoryId, session.JobId, session.Author, session.StartUtc);
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 导出错误: {ex.Message}");
            }
        }

        private void ExportSessionInternal(List<RecordedTrigger> records, int territoryId, int jobId, string author, DateTime sessionStartUtc)
        {
            if (records.Count == 0)
            {
                LogHelper.Print("WhiteAxisRecorder: 没有可导出的记录。");
                return;
            }

            _nextNodeId = 1;
            TimelineConfig config = BuildTimelineConfig(records, territoryId, jobId, author, sessionStartUtc);

            string outputDirectory = ResolveOutputDirectory();
            Directory.CreateDirectory(outputDirectory);

            string fileName = BuildFileName(BuildExportBaseName(territoryId, jobId));
            string outputPath = Path.Combine(outputDirectory, fileName);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(outputPath, json, new System.Text.UTF8Encoding(false));

            _lastExportPath = outputPath;
            LogHelper.Print($"WhiteAxisRecorder 已导出: {outputPath}");
        }

        private TimelineConfig BuildTimelineConfig(List<RecordedTrigger> records, int territoryId, int jobId, string author, DateTime sessionStartUtc)
        {
            string displayName = BuildExportDisplayName(territoryId, jobId);

            var config = new TimelineConfig
            {
                GUID = Guid.NewGuid().ToString("N"),
                ConfigVersion = 6,
                TargetJob = jobId,
                Author = author,
                Name = displayName,
                TreeRoot = new TreeRoot
                {
                    DisplayName = "Start",
                    Important = false,
                    Color = DefaultRootColor(),
                    Id = 0,
                    Enable = true,
                    Remark = string.Empty,
                    Tag = string.Empty
                },
                ExposedVars = new List<object>(),
                ExposedVarDesc = string.Empty,
                OpenerScript = string.Empty,
                Note = "白轴自动记录。",
                TerritoryTypeId = territoryId,
                TerritoryWeatherId = 0,
                TargetAcrAuthor = author,
                ClearCustomed = false,
                LogsAddress = string.Empty,
            };

            var parallel = new TreeParallel
            {
                Id = NextNodeId(),
                DisplayName = "并行",
                AnyReturn = false,
                StopWhenDead = false,
                Important = false,
                Enable = true,
                Remark = string.Empty,
                Tag = string.Empty,
                Color = DefaultGreen()
            };

            foreach (var record in records)
            {
                TreeCondNode? node = BuildCondNode(record, sessionStartUtc);
                if (node == null)
                    continue;

                var sequence = new TreeSequence
                {
                    Id = NextNodeId(),
                    DisplayName = "序列",
                    IgnoreNodeResult = false,
                    StopWhenDead = false,
                    Important = false,
                    Enable = true,
                    Remark = string.Empty,
                    Tag = string.Empty,
                    Color = DefaultGreen()
                };

                sequence.Childs.Add(node);
                parallel.Childs.Add(sequence);
            }

            config.TreeRoot.Childs.Add(parallel);
            return config;
        }

        private TreeCondNode? BuildCondNode(RecordedTrigger record, DateTime sessionStartUtc)
        {
            object? cond = record.Type switch
            {
                RecordedTriggerType.EnemyCast => BuildEnemyCastCond(record),
                RecordedTriggerType.AbilityEffect => BuildAbilityEffectCond(record),
                RecordedTriggerType.DebuffApplied => BuildStatusTargetCond(record),
                RecordedTriggerType.BossBuffApplied => BuildStatusTargetCond(record),
                _ => null
            };

            if (cond == null)
                return null;

            string remark = BuildRemark(record, sessionStartUtc);

            return new TreeCondNode
            {
                Id = NextNodeId(),
                DisplayName = "等待条件",
                CondLogicType = 0,
                CheckOnce = false,
                ReverseResult = false,
                TriggerConds = new List<object> { cond },
                Important = true,
                Enable = true,
                Remark = remark,
                Tag = string.Empty,
                Color = ResolveRecordColor(record),
            };
        }

        private TriggerCondEnemyCastSpell BuildEnemyCastCond(RecordedTrigger record)
        {
            return new TriggerCondEnemyCastSpell
            {
                Remark = record.NameOrId,
                RegexNameOrId = record.ActionId?.ToString() ?? record.NameOrId,
                NeedTargetable = record.NeedTargetable,
                DisplayName = "通用/敌人读条"
            };
        }

        private TriggerCondReceviceAbilityEffect? BuildAbilityEffectCond(RecordedTrigger record)
        {
            if (record.ActionId == null)
                return null;

            return new TriggerCondReceviceAbilityEffect
            {
                Remark = record.NameOrId,
                DisplayName = "副本/等待技能效果",
                ActionId = record.ActionId.Value,
                CheckIsMe = false,
                LimitType = 0
            };
        }

        private TriggerCondWaitTarget? BuildStatusTargetCond(RecordedTrigger record)
        {
            if (record.ActionId == null)
                return null;

            int target = record.Type == RecordedTriggerType.DebuffApplied
                ? TargetSelectorPartyMember
                : TargetSelectorEnemy;

            int leftTime = record.DurationMilliseconds.HasValue
                ? Math.Max(0, (int)MathF.Round(record.DurationMilliseconds.Value))
                : 0;

            var filter = new TargetSelectorFilterData
            {
                Filter = TargetSelectorFilterBuff,
                Remark = BuildBuffFilterRemark(record.ActionId.Value, record.DurationMilliseconds),
                StrParam1 = string.Empty,
                UintParam1 = record.ActionId.Value,
                FloatParam1 = 0f,
                LeftTime = leftTime,
                JobsCategory = 0,
                Jobs = 0,
                CompareType = TargetSelectorCompareType,
                BuffCompareType = TargetSelectorBuffCompareType,
                Marker = 0,
                Nearest = false
            };

            return new TriggerCondWaitTarget
            {
                DisplayName = "General/目标符合条件",
                TargetSelector = new TargetSelector
                {
                    Enable = true,
                    Target = target,
                    FilterDatas = new List<TargetSelectorFilterData> { filter },
                    NeedTargetable = record.NeedTargetable,
                    SndFilter = 0,
                    PMIndex = 0
                }
            };
        }

        private string BuildRemark(RecordedTrigger record)
        {
            return BuildRemark(record, _sessionStartUtc);
        }

        private string BuildRemark(RecordedTrigger record, DateTime sessionStartUtc)
        {
            string sourceSummary = string.IsNullOrWhiteSpace(record.SourceName)
                ? "无来源"
                : record.SourceName;
            string targetSummary = string.IsNullOrWhiteSpace(record.TargetName)
                ? "无目标"
                : record.TargetName;
            string name = $"{record.NameOrId}(来源:{sourceSummary} 目标:{targetSummary})";
            double seconds = (record.TimestampUtc - sessionStartUtc).TotalSeconds;
            if (seconds < 0)
                return name;

            string remark = $"{name} +{seconds:F1}s";
            if (record.CastTimeSeconds.HasValue && record.CastTimeSeconds.Value > 0)
                remark += $" 读条 {record.CastTimeSeconds.Value:F1}s";
            if (record.DurationMilliseconds.HasValue && record.DurationMilliseconds.Value > 0)
                remark += $" 持续 {record.DurationMilliseconds.Value:F0}ms";
            return remark;
        }

        private static string BuildSessionTitle(RecordedSession session)
        {
            string start = session.StartUtc.ToLocalTime().ToString("HH:mm:ss");
            string territoryDisplay = BuildTerritoryDisplay(session.TerritoryId);
            string jobDisplay = BuildJobDisplay(session.JobId);
            return $"#{session.Id} {start} {territoryDisplay} / {jobDisplay} / {session.Reason}";
        }

        private static string BuildExportBaseName(int territoryId, int jobId)
        {
            return $"Territory_{territoryId}_Job_{jobId}";
        }

        private static string BuildExportDisplayName(int territoryId, int jobId)
        {
            string territoryDisplay = BuildTerritoryDisplay(territoryId);
            string jobDisplay = BuildJobDisplay(jobId);
            return $"{territoryDisplay}_{jobDisplay}";
        }

        private static string BuildJobDisplay(int jobId)
        {
            string? jobName = GetJobNameById(jobId);
            return string.IsNullOrWhiteSpace(jobName) ? $"未知职业({jobId})" : jobName;
        }

        private static string BuildTerritoryDisplay(int territoryId)
        {
            string? territoryName = GetTerritoryNameById(territoryId);
            if (string.IsNullOrWhiteSpace(territoryName))
                return $"未知副本({territoryId})";

            return $"{territoryName}({territoryId})";
        }

        private static string? GetTerritoryNameById(int territoryId)
        {
            if (territoryId <= 0)
                return null;

            var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
            if (sheet == null)
                return null;

            var row = sheet.GetRow((uint)territoryId);
            string? name = null;
            if (row.ContentFinderCondition.IsValid)
                name = row.ContentFinderCondition.Value.Name.ToString();

            if (string.IsNullOrWhiteSpace(name))
                name = row.PlaceName.Value.Name.ToString();

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private Color ResolveRecordColor(RecordedTrigger record)
        {
            return record.Type switch
            {
                RecordedTriggerType.EnemyCast => CloneColor(record.NeedTargetable ? _colorEnemyCastTargetable : _colorEnemyCastUntargetable),
                RecordedTriggerType.AbilityEffect => CloneColor(record.IsAutoAttack ? _colorAutoAttack : _colorAbilityEffect),
                RecordedTriggerType.DebuffApplied => CloneColor(_colorDebuff),
                RecordedTriggerType.BossBuffApplied => CloneColor(_colorBossBuff),
                _ => DefaultGreen()
            };
        }

        private static Color CloneColor(Color source)
        {
            return new Color
            {
                X = source.X,
                Y = source.Y,
                Z = source.Z,
                W = source.W
            };
        }

        private int NextNodeId()
        {
            return _nextNodeId++;
        }

        private static Color DefaultGreen()
        {
            return new Color { X = 0.2f, Y = 0.8f, Z = 0.2f, W = 1.0f };
        }

        private static Color DefaultRootColor()
        {
            return new Color { X = 1.0f, Y = 1.0f, Z = 0.4f, W = 1.0f };
        }

        private static string BuildBuffFilterRemark(int statusId, float? durationMilliseconds)
        {
            string duration = durationMilliseconds.HasValue ? $"{durationMilliseconds.Value:F0}ms" : "0ms";
            return $"获得buff {statusId} {duration}";
        }

        private static string BuildFileName(string baseName)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safeName = SanitizeFileName(baseName);
            return $"{safeName}_{timestamp}.json";
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static bool IsPartyMember(uint entityId)
        {
            if (entityId == 0)
                return false;

            if (Core.Me != null)
            {
                uint? selfId = GetEntityId(Core.Me);
                if (selfId.HasValue && selfId.Value == entityId)
                    return true;
            }

            foreach (var member in Svc.Party)
            {
                if (member.EntityId == entityId)
                    return true;
            }

            return false;
        }

        private static bool IsBossTarget(IBattleChara target)
        {
            if (target.ObjectKind != ObjectKind.BattleNpc)
                return false;

            bool? isEnemy = GetBoolProperty(target, "IsEnemy", "IsHostile");
            if (isEnemy.HasValue && !isEnemy.Value)
                return false;

            int? rank = ConvertToInt(GetPropertyValue(target, "BattleNpcRank", "BNpcRank", "NpcRank", "Rank"));
            if (rank.HasValue)
                return rank.Value > 0;

            return true;
        }

        private static bool IsExcludedStatusName(string statusName)
        {
            return statusName.IndexOf("濒死", StringComparison.Ordinal) >= 0
                || statusName.IndexOf("衰弱", StringComparison.Ordinal) >= 0
                || statusName.IndexOf("受伤加重", StringComparison.Ordinal) >= 0
                || statusName.IndexOf("伤害降低", StringComparison.Ordinal) >= 0;
        }

        private static int? GetStatusStackCount(ITriggerCondParams condParams)
        {
            int? stack = GetIntProperty(condParams, "StackCount", "Stacks", "Stack", "StackNum", "StackLevel", "StackValue");
            if (!stack.HasValue)
                return null;

            return stack.Value < 0 ? null : stack.Value;
        }

        private static bool TryResolveStatusInfo(ITriggerCondParams condParams, out int statusId, out string statusName)
        {
            statusId = 0;
            statusName = string.Empty;

            string? rawName = GetStringProperty(condParams, "StatusName", "Name");
            int? id = ResolveStatusId(condParams, rawName);
            if (!id.HasValue)
                return false;

            if (string.IsNullOrWhiteSpace(rawName))
                rawName = GetStatusNameById(id.Value);

            string? normalized = NormalizeNameOrId(rawName?.Trim(), id.Value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            statusId = id.Value;
            statusName = normalized;
            return true;
        }

        private static int? ResolveStatusId(ITriggerCondParams condParams, string? statusName)
        {
            int? id = ValidateStatusId(GetIntProperty(condParams, "StatusId", "StatusID", "StatusRowId", "StatusRowID"), statusName);
            if (!id.HasValue)
                id = ValidateStatusId(GetIntProperty(condParams, "Id", "ID"), statusName);

            if (!id.HasValue)
            {
                object? statusObj = GetPropertyValue(condParams, "Status", "StatusData", "StatusRow", "StatusInfo");
                if (statusObj != null)
                {
                    id = ValidateStatusId(GetIntProperty(statusObj, "RowId", "RowID", "Id", "ID"), statusName);
                    if (!id.HasValue)
                    {
                        object? value = GetPropertyValue(statusObj, "Value");
                        if (value != null)
                            id = ValidateStatusId(GetIntProperty(value, "RowId", "RowID", "Id", "ID"), statusName);
                    }
                }
            }

            if (!id.HasValue && !string.IsNullOrWhiteSpace(statusName))
            {
                uint? nameId = GetStatusIdByName(statusName);
                if (nameId.HasValue)
                    id = (int)nameId.Value;
            }

            return id;
        }

        private static int? ValidateStatusId(int? statusId, string? statusName)
        {
            if (!statusId.HasValue)
                return null;

            if (!IsValidStatusId(statusId.Value))
                return null;

            if (!string.IsNullOrWhiteSpace(statusName))
            {
                string? nameFromId = GetStatusNameById(statusId.Value);
                if (!string.IsNullOrWhiteSpace(nameFromId)
                    && !string.Equals(nameFromId.Trim(), statusName.Trim(), StringComparison.Ordinal))
                    return null;
            }

            return statusId.Value;
        }

        private static bool IsValidStatusId(int statusId)
        {
            if (statusId <= 0)
                return false;

            var sheet = Svc.Data.GetExcelSheet<Status>();
            if (sheet == null)
                return false;

            return sheet.GetRowOrDefault((uint)statusId).HasValue;
        }

        private static uint? GetStatusIdByName(string statusName)
        {
            if (string.IsNullOrWhiteSpace(statusName))
                return null;

            string key = statusName.Trim();
            if (key.Length == 0)
                return null;

            if (_statusNameLookup == null)
            {
                lock (_statusLookupLock)
                {
                    if (_statusNameLookup == null)
                        _statusNameLookup = BuildStatusNameLookup();
                }
            }

            return _statusNameLookup != null && _statusNameLookup.TryGetValue(key, out uint id) ? id : null;
        }

        private static Dictionary<string, uint>? BuildStatusNameLookup()
        {
            var sheet = Svc.Data.GetExcelSheet<Status>();
            if (sheet == null)
                return null;

            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var row in sheet)
            {
                string? name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!map.ContainsKey(name))
                    map.Add(name, row.RowId);
            }

            return map;
        }

        private static string? GetStatusNameById(int statusId)
        {
            if (statusId <= 0)
                return null;

            var sheet = Svc.Data.GetExcelSheet<Status>();
            if (sheet == null)
                return null;

            Status? status = sheet.GetRowOrDefault((uint)statusId);
            if (!status.HasValue)
                return null;

            string? name = status.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static bool IsDebuff(uint statusId)
        {
            var sheet = Svc.Data.GetExcelSheet<Status>();
            if (sheet == null)
                return false;

            Status? status = sheet.GetRowOrDefault(statusId);
            if (!status.HasValue)
                return false;

            return status.Value.StatusCategory == 2;
        }

        private static bool IsBuff(uint statusId)
        {
            var sheet = Svc.Data.GetExcelSheet<Status>();
            if (sheet == null)
                return false;

            Status? status = sheet.GetRowOrDefault(statusId);
            if (!status.HasValue)
                return false;

            return status.Value.StatusCategory == 1;
        }

        private bool ShouldRecordStatus(float? durationMilliseconds, int? stackCount, params string[] keys)
        {
            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;
                StatusSeenInfo? info = null;

                foreach (var key in keys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (_statusSeen.TryGetValue(key, out var existing))
                    {
                        info = existing;
                        break;
                    }
                }

                if (info == null)
                {
                    info = new StatusSeenInfo
                    {
                        LastSeenUtc = now,
                        LastDurationMs = durationMilliseconds,
                        LastStack = stackCount
                    };

                    foreach (var key in keys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                            _statusSeen[key] = info;
                    }

                    return true;
                }

                bool stackChanged = stackCount.HasValue
                    && info.LastStack.HasValue
                    && stackCount.Value != info.LastStack.Value;
                bool durationIncreased = durationMilliseconds.HasValue
                    && info.LastDurationMs.HasValue
                    && durationMilliseconds.Value > info.LastDurationMs.Value + StatusDurationIncreaseThresholdMs;
                bool timePassed = _statusDedupeMs <= 0
                    || (now - info.LastSeenUtc).TotalMilliseconds >= _statusDedupeMs;

                if (!stackChanged && !durationIncreased && !timePassed)
                {
                    if (durationMilliseconds.HasValue
                        && (!info.LastDurationMs.HasValue || durationMilliseconds.Value > info.LastDurationMs.Value))
                        info.LastDurationMs = durationMilliseconds.Value;

                    if (stackCount.HasValue)
                        info.LastStack = stackCount.Value;

                    return false;
                }

                info.LastSeenUtc = now;
                if (durationMilliseconds.HasValue)
                    info.LastDurationMs = durationMilliseconds.Value;
                if (stackCount.HasValue)
                    info.LastStack = stackCount.Value;

                foreach (var key in keys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        _statusSeen[key] = info;
                }

                return true;
            }
        }

        private static bool IsAutoAttack(int actionId)
        {
            var sheet = Svc.Data.GetExcelSheet<LuminaAction>();
            if (sheet != null)
            {
                var row = sheet.GetRowOrDefault((uint)actionId);
                if (row.HasValue)
                {
                    object? actionCategory = GetPropertyValue(row.Value, "ActionCategory");
                    if (actionCategory != null)
                    {
                        int? categoryId = ConvertToInt(GetPropertyValue(actionCategory, "RowId", "RowID", "Id", "ID"));
                        if (categoryId.HasValue)
                            return categoryId.Value == 1;
                    }
                }
            }

            return actionId == 7 || actionId == 8;
        }

        private static string GetAeAssistBaseDirectory()
        {
            string? aeassistDir = GetAssemblyDirectory(FindAeAssistAssembly());
            string? root = FindAeAssistRoot(aeassistDir);
            if (!string.IsNullOrWhiteSpace(root))
                return root;

            string? pluginDir = GetAssemblyDirectory(typeof(WhiteAxisRecorderService).Assembly);
            root = FindAeAssistRoot(pluginDir);
            if (!string.IsNullOrWhiteSpace(root))
                return root;

            string? coreDir = GetAssemblyDirectory(typeof(Core).Assembly);
            if (!string.IsNullOrWhiteSpace(coreDir))
                return coreDir;

            return AppContext.BaseDirectory;
        }

        private static string GetDefaultOutputDirectory()
        {
            return Path.Combine("D:\\", "Triggerlines");
        }

        private string ResolveOutputDirectory()
        {
            if (string.IsNullOrWhiteSpace(_customOutputDirectory))
                return GetDefaultOutputDirectory();

            if (Path.IsPathRooted(_customOutputDirectory))
                return _customOutputDirectory;

            return Path.Combine(GetAeAssistBaseDirectory(), _customOutputDirectory);
        }

        private string GetConfigPath()
        {
            if (!string.IsNullOrWhiteSpace(_configPath))
                return _configPath;

            string settingsDir = Path.Combine(GetAeAssistBaseDirectory(), "Settings");
            _configPath = Path.Combine(settingsDir, "WhiteAxisRecorder.json");
            return _configPath;
        }

        private void LoadConfig()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path))
                    return;

                string json = File.ReadAllText(path);
                RecorderConfig? config = JsonSerializer.Deserialize<RecorderConfig>(json);
                _customOutputDirectory = (config?.OutputDirectory ?? string.Empty).Trim();
                _outputDirectoryInput = _customOutputDirectory;
                ApplyColorConfig(config?.Colors);
                ApplyOptionConfig(config);
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 配置读取失败: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                string path = GetConfigPath();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                var config = new RecorderConfig
                {
                    OutputDirectory = _customOutputDirectory,
                    Colors = new RecorderColorConfig
                    {
                        EnemyCastTargetable = CloneColor(_colorEnemyCastTargetable),
                        EnemyCastUntargetable = CloneColor(_colorEnemyCastUntargetable),
                        AbilityEffect = CloneColor(_colorAbilityEffect),
                        AutoAttack = CloneColor(_colorAutoAttack),
                        Debuff = CloneColor(_colorDebuff),
                        BossBuff = CloneColor(_colorBossBuff)
                    },
                    RecordingEnabled = _recordingEnabled,
                    AutoExportOnEnd = _autoExportOnEnd,
                    RecordDebuffs = _recordDebuffs,
                    RecordBossBuffs = _recordBossBuffs,
                    RecordAbilityEffects = _recordAbilityEffects,
                    RecordAutoAttacks = _recordAutoAttacks,
                    StatusDedupeMs = _statusDedupeMs
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
                LogHelper.Print($"WhiteAxisRecorder 配置已保存: {path}");
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 配置保存失败: {ex.Message}");
            }
        }

        private void ApplyColorConfig(RecorderColorConfig? colors)
        {
            if (colors == null)
                return;

            CopyColor(_colorEnemyCastTargetable, colors.EnemyCastTargetable);
            CopyColor(_colorEnemyCastUntargetable, colors.EnemyCastUntargetable);
            CopyColor(_colorAbilityEffect, colors.AbilityEffect);
            CopyColor(_colorAutoAttack, colors.AutoAttack);
            CopyColor(_colorDebuff, colors.Debuff);
            CopyColor(_colorBossBuff, colors.BossBuff);
        }

        private void ApplyOptionConfig(RecorderConfig? config)
        {
            if (config == null)
                return;

            if (config.RecordingEnabled.HasValue)
                _recordingEnabled = config.RecordingEnabled.Value;
            if (config.AutoExportOnEnd.HasValue)
                _autoExportOnEnd = config.AutoExportOnEnd.Value;
            if (config.RecordDebuffs.HasValue)
                _recordDebuffs = config.RecordDebuffs.Value;
            if (config.RecordBossBuffs.HasValue)
                _recordBossBuffs = config.RecordBossBuffs.Value;
            if (config.RecordAbilityEffects.HasValue)
                _recordAbilityEffects = config.RecordAbilityEffects.Value;
            if (config.RecordAutoAttacks.HasValue)
                _recordAutoAttacks = config.RecordAutoAttacks.Value;
            if (config.StatusDedupeMs.HasValue)
                _statusDedupeMs = Math.Clamp(config.StatusDedupeMs.Value, 0, 600000);
        }

        private static void CopyColor(Color target, Color? source)
        {
            if (source == null)
                return;

            target.X = source.X;
            target.Y = source.Y;
            target.Z = source.Z;
            target.W = source.W;
        }

        private void OpenOutputDirectory()
        {
            try
            {
                string outputDirectory = ResolveOutputDirectory();
                Directory.CreateDirectory(outputDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", outputDirectory)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogHelper.Print($"WhiteAxisRecorder 打开目录失败: {ex.Message}");
            }
        }

        private static bool DrawColorPicker(string label, Color color)
        {
            Vector4 value = new Vector4(color.X, color.Y, color.Z, color.W);
            if (ImGui.ColorEdit4(label, ref value))
            {
                ApplyColor(color, value);
                return true;
            }

            return false;
        }

        private static void ApplyColor(Color target, Vector4 value)
        {
            target.X = value.X;
            target.Y = value.Y;
            target.Z = value.Z;
            target.W = value.W;
        }

        private static string? GetAssemblyDirectory(Assembly? assembly)
        {
            if (assembly == null)
                return null;

            string? location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
                return null;

            return Path.GetDirectoryName(location);
        }

        private static Assembly? FindAeAssistAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, "AEAssist", StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }

            return null;
        }

        private static string? FindAeAssistRoot(string? startDir)
        {
            if (string.IsNullOrWhiteSpace(startDir))
                return null;

            DirectoryInfo? current = new DirectoryInfo(startDir);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "AEAssistCNVersion")))
                    return current.FullName;

                if (string.Equals(current.Name, "AEAssist", StringComparison.OrdinalIgnoreCase)
                    && current.Parent != null
                    && string.Equals(current.Parent.Name, "AEAssistCNVersion", StringComparison.OrdinalIgnoreCase)
                    && current.Parent.Parent != null)
                    return current.Parent.Parent.FullName;

                if (string.Equals(current.Name, "Plugins", StringComparison.OrdinalIgnoreCase)
                    && current.Parent != null)
                    return current.Parent.FullName;

                if (Directory.Exists(Path.Combine(current.FullName, "Plugins"))
                    && Directory.Exists(Path.Combine(current.FullName, "Triggerlines")))
                    return current.FullName;

                current = current.Parent;
            }

            return null;
        }

        private static int GetClassJobId(object? classJobRef)
        {
            if (classJobRef == null)
                return 0;

            int? direct = ConvertToInt(classJobRef);
            if (direct.HasValue)
                return direct.Value;

            int? rowId = ConvertToInt(GetPropertyValue(classJobRef, "RowId", "RowID", "Id", "ID"));
            if (rowId.HasValue)
                return rowId.Value;

            object? value = GetPropertyValue(classJobRef, "Value");
            if (value == null)
                return 0;

            rowId = ConvertToInt(GetPropertyValue(value, "RowId", "RowID", "Id", "ID"));
            return rowId ?? 0;
        }

        private static uint? GetEntityId(object? obj)
        {
            if (obj is IGameObject gameObject)
                return gameObject.EntityId;

            return null;
        }

        private static List<string> GetTargetNamesFromCondParams(object condParams)
        {
            var names = new List<string>();

            string? directName = GetStringProperty(condParams, "TargetName");
            AddTargetName(directName, names);

            AddTargetName(GetObjectProperty<IGameObject>(condParams, "Target", "TargetObject", "TargetEntity", "TargetChara", "TargetActor"), names);
            AddTargetNamesFromEnumerable(GetPropertyValue(condParams, "Targets", "TargetObjects", "TargetEntities", "TargetActors", "TargetList"), names);

            uint? targetId = GetUintProperty(condParams, "TargetId", "TargetID", "TargetEntityId", "TargetEntityID", "TargetObjectId", "TargetObjectID");
            if (targetId.HasValue)
                AddTargetNameById(targetId.Value, names);

            AddTargetNamesFromEnumerable(GetPropertyValue(condParams, "TargetIds", "TargetIDs", "TargetEntityIds", "TargetEntityIDs", "TargetObjectIds", "TargetObjectIDs"), names);

            return names;
        }

        private static string? GetSourceNameFromCondParams(object condParams)
        {
            string? directName = GetStringProperty(condParams, "SourceName", "CasterName");
            string? resolved = ResolveTargetLabel(directName);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;

            IGameObject? source = GetObjectProperty<IGameObject>(condParams, "Source", "SourceObject", "SourceEntity", "Caster", "SourceChara", "SourceActor");
            if (source != null)
                return ResolveTargetLabel(source);

            uint? sourceId = GetUintProperty(condParams, "SourceId", "SourceEntityId", "CasterId", "SourceObjectId", "SourceObjectID");
            if (sourceId.HasValue)
                return ResolveTargetLabelFromId(sourceId.Value);

            return null;
        }

        private static string BuildTargetSummary(IEnumerable<string> targets)
        {
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var target in targets)
            {
                if (string.IsNullOrWhiteSpace(target))
                    continue;

                string trimmed = target.Trim();
                if (trimmed.Length == 0)
                    continue;

                if (seen.Add(trimmed))
                    unique.Add(trimmed);
            }

            return unique.Count == 0 ? "无目标" : string.Join("/", unique);
        }

        private static void AddTargetNamesFromEnumerable(object? value, List<string> names)
        {
            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                    AddTargetName(item, names);
            }
        }

        private static void AddTargetName(object? value, List<string> names)
        {
            string? label = ResolveTargetLabel(value);
            AddTargetName(label, names);
        }

        private static void AddTargetName(string? name, List<string> names)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            names.Add(name);
        }

        private static void AddTargetNameById(uint id, List<string> names)
        {
            AddTargetName(ResolveTargetLabelFromId(id), names);
        }

        private static string? ResolveTargetLabel(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case IGameObject gameObject:
                    return ResolveTargetLabelFromGameObject(gameObject);
                case string name:
                    return ResolveTargetLabelFromName(name);
                case uint id:
                    return ResolveTargetLabelFromId(id);
                case int id when id > 0:
                    return ResolveTargetLabelFromId((uint)id);
                case ulong id when id <= uint.MaxValue:
                    return ResolveTargetLabelFromId((uint)id);
                case long id when id > 0 && id <= uint.MaxValue:
                    return ResolveTargetLabelFromId((uint)id);
            }

            string? jobName = ResolveJobNameFromObject(value);
            if (!string.IsNullOrWhiteSpace(jobName))
                return jobName;

            string? fallbackName = GetStringProperty(value, "Name");
            return ResolveTargetLabelFromName(fallbackName);
        }

        private static string? ResolveTargetLabelFromGameObject(IGameObject gameObject)
        {
            string? jobName = ResolveJobNameFromObject(gameObject);
            if (!string.IsNullOrWhiteSpace(jobName))
                return jobName;

            return ResolveTargetLabelFromName(gameObject.Name?.ToString());
        }

        private static string? ResolveTargetLabelFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string? jobName = ResolvePartyJobNameByName(name);
            return string.IsNullOrWhiteSpace(jobName) ? name : jobName;
        }

        private static string? ResolveTargetLabelFromId(uint id)
        {
            if (id == 0)
                return null;

            foreach (var obj in Svc.Objects)
            {
                if (obj.EntityId == id)
                    return ResolveTargetLabelFromGameObject(obj);
            }

            return ResolvePartyJobNameById(id);
        }

        private static string? ResolvePartyJobNameByName(string name)
        {
            foreach (var member in Svc.Party)
            {
                string? memberName = member.Name?.ToString();
                if (string.Equals(memberName, name, StringComparison.Ordinal))
                    return ResolveJobNameFromObject(member);
            }

            if (Core.Me != null)
            {
                string? selfName = Core.Me.Name?.ToString();
                if (string.Equals(selfName, name, StringComparison.Ordinal))
                    return ResolveJobNameFromObject(Core.Me);
            }

            return null;
        }

        private static string? ResolvePartyJobNameById(uint id)
        {
            foreach (var member in Svc.Party)
            {
                if (member.EntityId == id)
                    return ResolveJobNameFromObject(member);
            }

            if (Core.Me != null)
            {
                uint? selfId = GetEntityId(Core.Me);
                if (selfId.HasValue && selfId.Value == id)
                    return ResolveJobNameFromObject(Core.Me);
            }

            return null;
        }

        private static string? ResolveJobNameFromObject(object? value)
        {
            if (value == null)
                return null;

            object? classJobRef = GetPropertyValue(value, "ClassJob", "ClassJobId", "ClassJobID", "JobId", "JobID", "Job");
            int? jobId = GetClassJobId(classJobRef);
            if (!jobId.HasValue || jobId.Value <= 0)
                return null;

            return GetJobNameById(jobId.Value);
        }

        private static string? GetJobNameById(int jobId)
        {
            if (jobId <= 0)
                return null;

            var sheet = Svc.Data.GetExcelSheet<ClassJob>();
            if (sheet == null)
                return null;

            var row = sheet.GetRow((uint)jobId);
            string? name = row.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private static float? GetStatusDurationMilliseconds(IBattleChara target, uint statusId)
        {
            var buffApi = Core.Resolve<MemApiBuff>();
            if (buffApi == null)
                return null;

            if (!buffApi.GetTimeSpanLeft(target, statusId, out var timeSpan))
                return null;

            if (timeSpan.TotalMilliseconds <= 0)
                return null;

            return (float)timeSpan.TotalMilliseconds;
        }

        private static string? NormalizeNameOrId(string? name, int? actionId)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            if (actionId.HasValue)
                return actionId.Value.ToString();
            return null;
        }

        private static string? GetStringProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            return value?.ToString();
        }

        private static int? GetIntProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            if (value == null)
                return null;

            return value switch
            {
                int i => i,
                uint ui => unchecked((int)ui),
                short s => s,
                ushort us => us,
                byte b => b,
                long l => (int)l,
                ulong ul => (int)ul,
                string str when int.TryParse(str, out int parsed) => parsed,
                _ => null
            };
        }

        private static uint? GetUintProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            if (value == null)
                return null;

            return value switch
            {
                uint ui => ui,
                int i when i >= 0 => (uint)i,
                ushort us => us,
                short s when s >= 0 => (uint)s,
                byte b => b,
                long l when l >= 0 && l <= uint.MaxValue => (uint)l,
                ulong ul when ul <= uint.MaxValue => (uint)ul,
                string str when uint.TryParse(str, out uint parsed) => parsed,
                _ => null
            };
        }

        private static float? GetFloatProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            if (value == null)
                return null;

            return value switch
            {
                float f => f,
                double d => (float)d,
                decimal m => (float)m,
                int i => i,
                uint ui => ui,
                long l => l,
                ulong ul => ul,
                string str when float.TryParse(str, out float parsed) => parsed,
                _ => null
            };
        }

        private static int? ConvertToInt(object? value)
        {
            if (value == null)
                return null;

            return value switch
            {
                int i => i,
                uint ui => unchecked((int)ui),
                short s => s,
                ushort us => us,
                byte b => b,
                long l => (int)l,
                ulong ul => (int)ul,
                string str when int.TryParse(str, out int parsed) => parsed,
                _ => null
            };
        }

        private static ulong? GetUlongProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            if (value == null)
                return null;

            return value switch
            {
                ulong ul => ul,
                long l when l >= 0 => (ulong)l,
                uint ui => ui,
                int i when i >= 0 => (ulong)i,
                ushort us => us,
                short s when s >= 0 => (ulong)s,
                byte b => b,
                string str when ulong.TryParse(str, out var parsed) => parsed,
                _ => null
            };
        }

        private static T? GetObjectProperty<T>(object instance, params string[] names) where T : class
        {
            object? value = GetPropertyValue(instance, names);
            return value as T;
        }

        private static bool? GetBoolProperty(object instance, params string[] names)
        {
            object? value = GetPropertyValue(instance, names);
            if (value == null)
                return null;

            return value switch
            {
                bool b => b,
                string str when bool.TryParse(str, out bool parsed) => parsed,
                _ => null
            };
        }

        private static object? GetPropertyValue(object instance, params string[] names)
        {
            var type = instance.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            foreach (string name in names)
            {
                PropertyInfo? prop = type.GetProperty(name, flags);
                if (prop != null)
                    return prop.GetValue(instance);

                FieldInfo? field = type.GetField(name, flags);
                if (field != null)
                    return field.GetValue(instance);
            }

            return null;
        }

        private sealed class RecordedSession
        {
            public int Id { get; set; }
            public DateTime StartUtc { get; set; }
            public DateTime EndUtc { get; set; }
            public int TerritoryId { get; set; }
            public int JobId { get; set; }
            public string Author { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public List<RecordedEntry> Entries { get; set; } = new();
        }

        private sealed class RecordedEntry
        {
            public RecordedEntry(RecordedTrigger trigger)
            {
                Trigger = trigger;
            }

            public RecordedTrigger Trigger { get; }
            public bool Selected { get; set; } = true;
        }

        private sealed class RecorderConfig
        {
            public string? OutputDirectory { get; set; }
            public RecorderColorConfig? Colors { get; set; }
            public bool? RecordingEnabled { get; set; }
            public bool? AutoExportOnEnd { get; set; }
            public bool? RecordDebuffs { get; set; }
            public bool? RecordBossBuffs { get; set; }
            public bool? RecordAbilityEffects { get; set; }
            public bool? RecordAutoAttacks { get; set; }
            public int? StatusDedupeMs { get; set; }
        }

        private sealed class RecorderColorConfig
        {
            public Color? EnemyCastTargetable { get; set; }
            public Color? EnemyCastUntargetable { get; set; }
            public Color? AbilityEffect { get; set; }
            public Color? AutoAttack { get; set; }
            public Color? Debuff { get; set; }
            public Color? BossBuff { get; set; }
        }

        private sealed record RecordedTrigger(
            RecordedTriggerType Type,
            string NameOrId,
            int? ActionId,
            bool NeedTargetable,
            DateTime TimestampUtc,
            float? CastTimeSeconds,
            float? DurationMilliseconds,
            string? SourceName,
            string? TargetName,
            bool IsAutoAttack);

        private sealed class StatusSeenInfo
        {
            public DateTime LastSeenUtc { get; set; }
            public float? LastDurationMs { get; set; }
            public int? LastStack { get; set; }
        }

        private enum RecordedTriggerType
        {
            EnemyCast,
            AbilityEffect,
            DebuffApplied,
            BossBuffApplied
        }
    }
}

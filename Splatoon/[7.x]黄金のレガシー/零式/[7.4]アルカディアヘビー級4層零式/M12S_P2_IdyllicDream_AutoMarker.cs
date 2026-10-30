using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using Splatoon.SplatoonScripting;

namespace SplatoonScriptsOfficial.Duties.Dawntrail;

/// <summary>
/// ヘビー級零式4層 後半「アルカディアンドリーム」（game8 / ぬけまる式）
///  1. 模倣細胞が最初に出る形（十字 / X字）をパーティチャットに出す
///  2. 模倣細胞から自分についた線の方角 → 入れ替え(1↔B, 4↔C)後の担当 → 自分にマーカーを付ける
///  3. 空間裂断で島に移動した分身から、安置（東島/西島 × タテ/横）をパーティチャットに出す
///
/// リプレイ(2026-10-24, 10-27)で検証したイベント
///  - アルカディアンドリーム 46345 詠唱開始から約12.5秒: 模倣細胞(DataID 19210)が 4体ずつ出現演出(ActorControl 0x197, 4562)
///    先に出た4体が斜め=X字 / 東西南北=十字
///  - 約16.5秒: 模倣細胞(中心から14mの8方向)から全員に1本ずつ線(線ID 373)
///  - 約162秒: 残った扇分身と円分身が東西の島へ移動し、横扇 46351 / 縦扇 46352 / 円 48303 を詠唱
///    円が移動した島は立ち入り禁止。扇が移動した島が安置で、横扇ならタテ側・縦扇なら横側が安置
///  - 空間裂断で北/南の扇の一方がブラックホールに吸い込まれる(46365) → 最後の扇の種類が確定
///  - 東西の島の扇が発動(46355 横 / 46356 縦) → このタイミングで最後の扇の タテ/横 安置を通知（約17秒前）
///  - 最後のアルカディアンドリームの約5秒前: ブラックホールから扇分身が出て 46362 / 46363 を詠唱（保険の通知タイミング）
/// </summary>
public class M12S_P2_IdyllicDream_AutoMarker : SplatoonScript
{
    public override Metadata Metadata => new(4, "suzu");
    public override HashSet<uint>? ValidTerritories => [1327];

    private const uint CastIdyllicDream = 46345;
    private const uint CellDataId = 19210;          // 模倣細胞
    private const uint TetherCell = 373;
    private const uint TimelineCellAppear = 4562;   // 模倣細胞の出現演出
    private const uint ActorControlTimeline = 0x197;
    private const uint CastFanHorizontal = 46351;   // パワーガッシャー（横扇）
    private const uint CastFanVertical = 46352;     // パワーガッシャー（縦扇）
    private const uint CastCircleSecond = 48303;    // スネークキック（円）2回目
    private const uint CastFanHorizontalSecond = 46362; // ブラックホールから出た扇（横）
    private const uint CastFanVerticalSecond = 46363;   // ブラックホールから出た扇（縦）
    private const uint ActionTearEnter = 46365;         // 分身がブラックホールに吸い込まれる
    private const uint ActionFanHorizontalFire = 46355; // 扇の発動（横）
    private const uint ActionFanVerticalFire = 46356;   // 扇の発動（縦）

    // フィールドマーカー配置（ユーザー提供の図）: 方角 → マーカー名
    private static readonly string[] DirNames = ["北", "北東", "東", "南東", "南", "南西", "西", "北西"];
    private static readonly string[] WaymarkByDir = ["A", "2", "B", "3", "C", "4", "D", "1"];

    private enum Stage { Idle, WaitPattern, WaitTether, WaitIsland, WaitFinalFan, Done }
    private Stage _stage = Stage.Idle;
    private long _idStartMs;
    private string _pattern = "";       // "十字" / "X字"
    private string _myTetherDir = "";
    private string _myFinalSpot = "";
    private string _myMarker = "";
    private string _islandResult = "";
    private string _finalFanResult = "";
    private bool _finalFanSent;
    private readonly Dictionary<uint, bool> _fanIsHorizontal = []; // 分身ごとの扇の種類
    private bool? _portaledIsHorizontal;                          // 吸い込まれた扇の種類
    private bool _markerPlaced;
    private readonly List<(long DueMs, string Command)> _queue = [];

    private Config C => Controller.GetConfig<Config>();

    public override void OnReset() => ResetAll(false);

    public override void OnDirectorUpdate(DirectorUpdateCategory category)
    {
        if (category.EqualsAny(DirectorUpdateCategory.Commence, DirectorUpdateCategory.Recommence, DirectorUpdateCategory.Wipe))
            ResetAll(C.ClearMarkerOnWipe);
    }

    private void ResetAll(bool clearMarker)
    {
        if (clearMarker && _markerPlaced) Run("/mk off <me>");
        _stage = Stage.Idle;
        _pattern = _myTetherDir = _myFinalSpot = _myMarker = _islandResult = _finalFanResult = "";
        _finalFanSent = false;
        _fanIsHorizontal.Clear();
        _portaledIsHorizontal = null;
        _markerPlaced = false;
        _queue.Clear();
    }

    public override void OnStartingCast(uint source, uint castId)
    {
        if (castId == CastIdyllicDream)
        {
            // 1回目の処理の途中（線待ち・島待ち）なら無視。最後のアルカディアンドリームでは状態を戻すだけ
            if (_stage is Stage.Idle or Stage.Done or Stage.WaitFinalFan)
            {
                ResetAll(false);
                _stage = Stage.WaitPattern;
                _idStartMs = Environment.TickCount64;
            }
            return;
        }

        // 扇分身の種類を記録（どの扇がブラックホールに吸い込まれたかを後で判定するため）
        if (_stage != Stage.Idle && castId is CastFanHorizontal or CastFanVertical)
            _fanIsHorizontal[source] = castId == CastFanHorizontal;

        if (_stage == Stage.WaitIsland && castId is CastFanHorizontal or CastFanVertical or CastCircleSecond)
        {
            var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == source);
            if (obj == null) return;
            var dx = obj.Position.X - 100f;
            if (MathF.Abs(dx) < 10f) return; // 島（東西 約14m）にいる分身だけ
            var side = dx > 0 ? "東" : "西";
            var other = dx > 0 ? "西" : "東";

            string island, orient;
            if (castId == CastCircleSecond)
            {
                // 円が来た島は禁止 → 反対側。向きは扇の詠唱で確定させる
                island = other;
                orient = "";
                if (_islandResult == "") _islandResult = $"{island}|";
                return;
            }
            island = side;
            orient = castId == CastFanHorizontal ? C.TextForHorizontalFan : C.TextForVerticalFan;
            _islandResult = $"{island}|{orient}";
            if (C.EnableIslandChat)
                Enqueue(C.IslandChatDelay, C.IslandChatFormat.Replace("{島}", island).Replace("{向き}", orient));
            _stage = Stage.WaitFinalFan;
            return;
        }

        // 保険: 島の扇の発動で通知できなかった場合は、ブラックホールから出た扇の詠唱開始で通知
        if (_stage == Stage.WaitFinalFan && castId is CastFanHorizontalSecond or CastFanVerticalSecond)
        {
            if (!_finalFanSent) SendFinalFan(castId == CastFanHorizontalSecond);
            _stage = Stage.Done;
        }
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        var id = set.Action?.RowId ?? 0;
        if (id == 0 || set.Source == null) return;

        // 北/南の扇がブラックホールに吸い込まれた → 最後の扇の種類が確定
        if (id == ActionTearEnter && _fanIsHorizontal.TryGetValue(set.Source.EntityId, out var h))
            _portaledIsHorizontal = h;

        // 東西の島の扇が発動した瞬間に、最後の扇の安置を通知
        if (_stage == Stage.WaitFinalFan && !_finalFanSent && C.FinalFanTimingIsland
            && id is ActionFanHorizontalFire or ActionFanVerticalFire
            && MathF.Abs(set.Source.Position.X - 100f) > 10f
            && _portaledIsHorizontal != null)
        {
            SendFinalFan(_portaledIsHorizontal.Value);
        }
    }

    private void SendFinalFan(bool horizontal)
    {
        var orient = horizontal ? C.TextForHorizontalFan : C.TextForVerticalFan;
        _finalFanResult = orient;
        _finalFanSent = true;
        if (C.EnableFinalFanChat)
            Enqueue(C.FinalFanChatDelay, C.FinalFanChatFormat.Replace("{向き}", orient));
    }

    public override void OnActorControl(uint sourceId, uint command, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8, ulong targetId, byte replaying)
    {
        if (_stage != Stage.WaitPattern) return;
        if (command != ActorControlTimeline || p1 != TimelineCellAppear) return;
        if (Environment.TickCount64 - _idStartMs > 25000) return;
        var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == sourceId);
        if (obj == null || obj.DataId != CellDataId) return;

        var dir = GetDir8(obj.Position);
        _pattern = dir % 2 == 1 ? "X字" : "十字";
        if (C.EnablePatternChat)
            Enqueue(C.PatternChatDelay, _pattern == "十字" ? C.CrossChat : C.XChat);
        _stage = Stage.WaitTether;
    }

    public override void OnTetherCreate(uint source, uint target, uint data2, uint data3, uint data5)
    {
        if (_stage is not (Stage.WaitPattern or Stage.WaitTether)) return;
        if (data3 != TetherCell) return;
        if (BasePlayer == null || target != BasePlayer.EntityId) return;
        var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == source);
        if (obj == null || obj.DataId != CellDataId) return;

        var dir = GetDir8(obj.Position);
        _myTetherDir = DirNames[dir];
        var spot = WaymarkByDir[dir];
        if (C.UseSwap)
        {
            spot = spot switch { "1" => "B", "B" => "1", "4" => "C", "C" => "4", _ => spot };
        }
        _myFinalSpot = spot;
        _myMarker = C.MarkerBySpot.TryGetValue(spot, out var mk) ? mk : "";
        if (C.EnableMarker && _myMarker != "")
        {
            Enqueue(C.MarkerDelay, $"/mk {_myMarker} <me>");
            _markerPlaced = true;
            if (C.ClearMarkerAfter > 0) Enqueue(C.MarkerDelay + C.ClearMarkerAfter, "/mk off <me>");
        }
        _stage = Stage.WaitIsland;
    }

    public override void OnUpdate()
    {
        if (_queue.Count == 0) return;
        var now = Environment.TickCount64;
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            if (_queue[i].DueMs > now) continue;
            Run(_queue[i].Command);
            _queue.RemoveAt(i);
        }
    }

    private void Enqueue(float delaySeconds, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        _queue.Add((Environment.TickCount64 + (long)(Math.Max(0f, delaySeconds) * 1000), command.Trim()));
    }

    // リプレイ再生中は実行せずログに出すだけ
    private static void Run(string command)
    {
        if (Svc.Condition[ConditionFlag.DutyRecorderPlayback])
            DuoLog.Information($"[IdyllicDream] {command}");
        else
            Chat.Instance.ExecuteCommand(command);
    }

    // 0=北, 1=北東, 2=東, ... 7=北西（中心 100,100）
    private static int GetDir8(Vector3 p)
    {
        var deg = MathF.Atan2(p.X - 100f, -(p.Z - 100f)) * 180f / MathF.PI;
        if (deg < 0) deg += 360f;
        return (int)MathF.Floor((deg + 22.5f) / 45f) % 8;
    }

    public override void OnSettingsDraw()
    {
        ImGuiEx.Text("■ マーカー自動付与");
        ImGui.Checkbox("自分にマーカーを付ける", ref C.EnableMarker);
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderFloat("線がついてから付けるまで（秒）", ref C.MarkerDelay, 0f, 10f, "%.1f");
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderFloat("付けてから外すまで（秒, 0=外さない）", ref C.ClearMarkerAfter, 0f, 120f, "%.0f");
        ImGui.Checkbox("全滅・やり直し時に外す", ref C.ClearMarkerOnWipe);
        ImGui.Checkbox("入れ替えを適用（1↔B, 4↔C）", ref C.UseSwap);
        ImGuiEx.Text("入れ替え後の位置 → マーカー（/mk の名前）");
        foreach (var spot in new[] { "A", "2", "B", "3", "C", "4", "D", "1" })
        {
            var v = C.MarkerBySpot.TryGetValue(spot, out var s) ? s : "";
            ImGui.SetNextItemWidth(100f);
            if (ImGui.InputText($"{spot}##mk{spot}", ref v, 20)) C.MarkerBySpot[spot] = v;
            if (spot is not ("3" or "1")) ImGui.SameLine();
        }

        ImGui.Separator();
        ImGuiEx.Text("■ 十字 / X字 のチャット");
        ImGui.Checkbox("チャットに出す##pattern", ref C.EnablePatternChat);
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderFloat("判明してから出すまで（秒）##pattern", ref C.PatternChatDelay, 0f, 10f, "%.1f");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("十字のとき", ref C.CrossChat, 100);
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("X字のとき", ref C.XChat, 100);

        ImGui.Separator();
        ImGuiEx.Text("■ 島の安置のチャット");
        ImGui.Checkbox("チャットに出す##island", ref C.EnableIslandChat);
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderFloat("判明してから出すまで（秒）##island", ref C.IslandChatDelay, 0f, 10f, "%.1f");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("書式（{島} {向き}）", ref C.IslandChatFormat, 100);
        ImGui.SetNextItemWidth(100f);
        ImGui.InputText("横扇のときの向き", ref C.TextForHorizontalFan, 20);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputText("縦扇のときの向き", ref C.TextForVerticalFan, 20);
        ImGuiEx.HelpMarker("横扇は東西に扇を出すのでタテ側、縦扇は南北に扇を出すので横側が安置、という設定が初期値です。下の「最後の扇」でも同じ文字を使います。");

        ImGui.Separator();
        ImGuiEx.Text("■ 最後の扇（最後のアルカディアンドリーム直前）のチャット");
        ImGui.Checkbox("チャットに出す##final", ref C.EnableFinalFanChat);
        ImGuiEx.TextV("通知するタイミング:");
        ImGui.SameLine();
        ImGuiEx.RadioButtonBool("島の扇の発動時", "最後の扇の詠唱開始時", ref C.FinalFanTimingIsland, sameLine: true);
        ImGuiEx.HelpMarker("島の扇の発動時: 最後の扇の発動の約17秒前。\n最後の扇の詠唱開始時: 発動の約4秒前。\n島の扇の時点で判定できなかった場合は、自動的に詠唱開始時に通知します。");
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderFloat("上のタイミングから出すまで（秒）##final", ref C.FinalFanChatDelay, 0f, 10f, "%.1f");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("書式（{向き}）##final", ref C.FinalFanChatFormat, 100);

        if (ImGuiEx.CollapsingHeader("Debug"))
        {
            ImGui.Text($"Stage: {_stage}");
            ImGui.Text($"Pattern: {_pattern}");
            ImGui.Text($"My tether from: {_myTetherDir} → spot {_myFinalSpot} → {_myMarker}");
            ImGui.Text($"Island: {_islandResult}");
            ImGui.Text($"Final fan: {_finalFanResult} (portaled horizontal: {_portaledIsHorizontal?.ToString() ?? "?"})");
            ImGui.Text($"Queue: {string.Join(" / ", _queue.Select(x => x.Command))}");
            ImGuiEx.Text("リプレイ再生中はコマンドを送らず、チャットログ（Echo）に表示します。");
        }
    }

    public class Config : IEzConfig
    {
        public bool EnableMarker = true;
        public float MarkerDelay = 1.5f;
        public float ClearMarkerAfter = 0f;
        public bool ClearMarkerOnWipe = true;
        public bool UseSwap = true;
        public Dictionary<string, string> MarkerBySpot = new()
        {
            ["A"] = "attack4",
            ["2"] = "attack1",
            ["B"] = "attack3",
            ["3"] = "attack2",
            ["C"] = "bind1",
            ["4"] = "stop1",
            ["D"] = "bind2",
            ["1"] = "stop2",
        };

        public bool EnablePatternChat = true;
        public float PatternChatDelay = 1.5f;
        public string CrossChat = "/p A-D頭割りから";
        public string XChat = "/p 1-4頭割りから";

        public bool EnableIslandChat = true;
        public float IslandChatDelay = 1.5f;
        public string IslandChatFormat = "/p {島}島の{向き}が安置";
        public string TextForHorizontalFan = "タテ";
        public string TextForVerticalFan = "横";

        public bool EnableFinalFanChat = true;
        public bool FinalFanTimingIsland = true;
        public float FinalFanChatDelay = 0f;
        public string FinalFanChatFormat = "/p 最後は{向き}安置";
    }
}

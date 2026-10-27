using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.Hooks.ActionEffectTypes;
using ECommons.ImGuiMethods;
using Splatoon;
using Splatoon.Memory;
using Splatoon.SplatoonScripting;

namespace SplatoonScriptsOfficial.Duties.Dawntrail;

/// <summary>
/// ミストウェイク 2ボス アムドゥシアス「ギャロッピングサンダー」
/// ・予兆(45348)が出た順に突進範囲(幅5の直線)を描画し、突進(45347)が来るたびに先頭から消す
/// ・各直線に当たる毒気塊(DataID 19064)を予兆の時点で割り当て、爆発(45349, 半径9)を順番に描画する
///   （線の中心から 2.5 + 毒気塊の当たり判定 以内 = 当たる。リプレイで検証済み）
/// ・どの直線にも当たらなかった毒気塊は、爆発の詠唱が始まった時点で描画する
/// </summary>
public class D122_Amdusias_GallopingThunder : SplatoonScript
{
    public override Metadata Metadata => new(2, "suzu");
    public override HashSet<uint>? ValidTerritories => [1314]; // ミストウェイク

    private const uint CastStart = 45346;     // ギャロッピングサンダー（本体の詠唱）
    private const uint ChargePreview = 45348; // 予兆（補助オブジェクト: 位置=始点, 詠唱地点=終点）
    private const uint Charge = 45347;        // 実際の突進
    private const uint Burst = 45349;         // 毒気塊の爆発
    private const uint PoisonCloudDataId = 19064;
    private const int MaxLines = 8;
    private const int MaxClouds = 24;
    private const float HalfWidth = 2.5f;     // 突進の幅5
    private const float BurstRadius = 9f;

    private readonly List<(Vector3 From, Vector3 To)> _lines = [];
    private readonly List<List<(uint Id, Vector3 Pos)>> _groups = []; // _lines と同じ順番
    private readonly HashSet<uint> _assigned = [];
    private readonly HashSet<uint> _exploded = [];
    private int _done;          // 着弾済みの突進の本数
    private long _lastAddedAt;  // 保険のタイムアウト用

    private Config C => Controller.GetConfig<Config>();

    public override void OnSetup()
    {
        for (var i = 0; i < MaxLines; i++)
        {
            Controller.RegisterElement($"Line{i}", new Element(2)
            {
                Enabled = false,
                radius = HalfWidth,
                thicc = 2f,
                Filled = true,
            });
            Controller.RegisterElement($"Num{i}", new Element(0)
            {
                Enabled = false,
                radius = 0f,
                overlayFScale = 2f,
                overlayBGColor = 0xFF000000,
                overlayTextColor = 0xFFFFFFFF,
            });
        }
        for (var i = 0; i < MaxClouds; i++)
        {
            Controller.RegisterElement($"Cloud{i}", new Element(0)
            {
                Enabled = false,
                radius = BurstRadius,
                thicc = 2f,
                Filled = true,
            });
        }
    }

    public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        if (packet->ActionID == CastStart)
        {
            ResetAll();
            return;
        }
        if (packet->ActionID != ChargePreview) return;
        if (_lines.Count >= MaxLines) return;

        var to = packet->Position;
        // 始点は補助オブジェクトの位置。取れなければ直前の線の終点で代用
        var from = Svc.Objects.FirstOrDefault(x => x.EntityId == sourceId)?.Position
                   ?? (_lines.Count > 0 ? _lines[^1].To : to);
        _lines.Add((from, to));

        // この直線に当たる毒気塊（まだ他の線に割り当てられていないもの）を確保
        var group = new List<(uint, Vector3)>();
        foreach (var obj in Svc.Objects)
        {
            if (obj.DataId != PoisonCloudDataId) continue;
            if (_assigned.Contains(obj.EntityId)) continue;
            if (obj is IBattleChara b && b.IsDead) continue;
            if (!InRect(obj.Position, from, to, HalfWidth + obj.HitboxRadius)) continue;
            group.Add((obj.EntityId, obj.Position));
            _assigned.Add(obj.EntityId);
        }
        _groups.Add(group);
        _lastAddedAt = Environment.TickCount64;
    }

    private static bool InRect(Vector3 p, Vector3 from, Vector3 to, float halfWidth)
    {
        var d = new Vector2(to.X - from.X, to.Z - from.Z);
        var len = d.Length();
        if (len < 0.01f) return false;
        var u = d / len;
        var rel = new Vector2(p.X - from.X, p.Z - from.Z);
        var along = Vector2.Dot(rel, u);
        var side = MathF.Abs(rel.X * u.Y - rel.Y * u.X);
        return along >= 0 && along <= len && side <= halfWidth;
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        var id = set.Action?.RowId;
        if (id == Charge && _done < _lines.Count) _done++;
        if (id == Burst && set.Source != null) _exploded.Add(set.Source.EntityId);
    }

    public override void OnUpdate()
    {
        // 予兆が最後に出てから30秒経ったら消す（取りこぼし対策）
        if (_lines.Count > 0 && Environment.TickCount64 - _lastAddedAt > 30000) ResetAll();

        DrawLines();
        DrawClouds();
    }

    private void DrawLines()
    {
        for (var i = 0; i < MaxLines; i++)
        {
            var line = Controller.GetElementByName($"Line{i}");
            var num = Controller.GetElementByName($"Num{i}");
            if (line == null || num == null) continue;
            var visible = i >= _done && i < _lines.Count;
            line.Enabled = visible;
            num.Enabled = visible && C.ShowNumbers;
            if (!visible) continue;

            var (from, to) = _lines[i];
            var isNext = i == _done;
            line.SetRefPosition(from);
            line.SetOffPosition(to);
            line.color = (isNext ? C.NextColor : C.LaterColor).ToUint();
            line.fillIntensity = isNext ? 0.45f : 0.2f;

            num.SetRefPosition((from + to) / 2f);
            num.overlayText = (i - _done + 1).ToString();
        }
    }

    private void DrawClouds()
    {
        // 描画対象: (位置, 次に爆発するか)
        var draw = new List<(Vector3 Pos, bool Next)>();

        if (C.ShowClouds)
        {
            // 直線に当たった毒気塊: 未爆発のグループを順番に、設定した数だけ
            var shownGroups = 0;
            foreach (var group in _groups)
            {
                var remaining = group.Where(c => !_exploded.Contains(c.Id)).ToList();
                if (remaining.Count == 0) continue;
                foreach (var c in remaining) draw.Add((c.Pos, shownGroups == 0));
                if (++shownGroups >= C.CloudGroupsShown) break;
            }

            // 爆発の詠唱中の毒気塊（どの線にも当たらなかったもの等）は常に表示
            foreach (var obj in Svc.Objects)
            {
                if (obj.DataId != PoisonCloudDataId) continue;
                if (obj is not IBattleChara b || !b.IsCasting || b.CastActionId != Burst) continue;
                if (_exploded.Contains(obj.EntityId)) continue;
                if (draw.Any(x => Vector3.Distance(x.Pos, obj.Position) < 0.5f)) continue;
                draw.Add((obj.Position, true));
            }
        }

        for (var i = 0; i < MaxClouds; i++)
        {
            var e = Controller.GetElementByName($"Cloud{i}");
            if (e == null) continue;
            e.Enabled = i < draw.Count;
            if (!e.Enabled) continue;
            e.SetRefPosition(draw[i].Pos);
            e.color = (draw[i].Next ? C.CloudNextColor : C.CloudLaterColor).ToUint();
            e.fillIntensity = draw[i].Next ? 0.35f : 0.12f;
        }
    }

    public override void OnReset() => ResetAll();
    public override void OnCombatEnd() => ResetAll();

    private void ResetAll()
    {
        _lines.Clear();
        _groups.Clear();
        _assigned.Clear();
        _exploded.Clear();
        _done = 0;
    }

    public override void OnSettingsDraw()
    {
        ImGui.Text("突進 / Charges");
        ImGui.Checkbox("順番の数字を表示 / Show order numbers", ref C.ShowNumbers);
        ImGui.ColorEdit4("次の突進 / Next charge", ref C.NextColor, ImGuiColorEditFlags.NoInputs);
        ImGui.ColorEdit4("それ以降 / Later charges", ref C.LaterColor, ImGuiColorEditFlags.NoInputs);
        ImGui.Separator();
        ImGui.Text("毒気塊の爆発 / Poison cloud bursts");
        ImGui.Checkbox("爆発範囲を表示 / Show bursts", ref C.ShowClouds);
        ImGui.SetNextItemWidth(120f);
        ImGui.SliderInt("先読みするグループ数 / Groups shown", ref C.CloudGroupsShown, 1, 5);
        ImGui.ColorEdit4("次の爆発 / Next burst", ref C.CloudNextColor, ImGuiColorEditFlags.NoInputs);
        ImGui.ColorEdit4("それ以降 / Later bursts", ref C.CloudLaterColor, ImGuiColorEditFlags.NoInputs);
        if (ImGuiEx.CollapsingHeader("Debug"))
        {
            ImGui.Text($"Lines: {_lines.Count}  Done: {_done}  Exploded: {_exploded.Count}");
            for (var i = 0; i < _lines.Count; i++)
                ImGui.Text($"{i + 1}: {_lines[i].From} -> {_lines[i].To}  clouds={_groups[i].Count}");
        }
    }

    public class Config : IEzConfig
    {
        public bool ShowNumbers = true;
        public Vector4 NextColor = 0xFF0000FF.ToVector4();        // 赤（ABGR）
        public Vector4 LaterColor = 0xFF00A0FF.ToVector4();       // 橙
        public bool ShowClouds = true;
        public int CloudGroupsShown = 2;
        public Vector4 CloudNextColor = 0xFFFF00C8.ToVector4();   // 紫
        public Vector4 CloudLaterColor = 0xFFFF80E0.ToVector4();  // 薄紫
    }
}

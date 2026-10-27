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
/// ヘビー級零式4層 後半 レプリケーション1回目（game8 位置固定式）
/// 2回目の分身移動で「最外周（斜め）に来た闇分身」を北とみなし、
/// 近接DPS / タンク の闇誘導位置と炎誘導位置を同時に表示する。
/// 自分の担当（闇耐性低下IIあり=炎、なし=闇）の位置にはガイド線を引く。
///
/// 判定の流れ（リプレイで検証済み）
///  1. 1回目: 外周4体のうち 46303(闇) を詠唱した分身 = 親の闇分身
///  2. 約10秒後: 各分身から 5m 離れた位置に子分身が2体ずつ出現 → 親の闇分身の子 = 2回目の闇分身
///  3. 子の闇分身が移動: 1体は最外周の斜め(中心から約14m)、もう1体は反対側の内側(約7m)
///  4. 最外周の闇分身の方角(NE/SE/SW/NW)で散開位置が決まる。闇耐性低下(3323)がない人が闇担当
///  5. 闇の着弾(46304)でガイドを消す
/// </summary>
public class M12S_P2_Replication1_DarkBait : SplatoonScript
{
    public override Metadata Metadata => new(2, "suzu");
    public override HashSet<uint>? ValidTerritories => [1327];

    private const uint CastReplication = 46296;  // レプリケーション（ボス）
    private const uint CastDark = 46303;         // マイティマジック（闇分身の詠唱）
    private const uint ActionDarkHit = 46304;    // 闇の着弾（プレイヤー散開）
    private const uint CloneDataId = 19204;      // 人型分体
    private const uint DarkResistanceDown = 3323;

    private static readonly Vector2 Center = new(100f, 100f);

    // 最外周の闇分身の位置（中心から約14m の斜め）
    private static readonly Dictionary<Dir, Vector2> OuterPositions = new()
    {
        [Dir.NE] = new(109.88f, 90.07f),
        [Dir.SE] = new(109.88f, 109.88f),
        [Dir.SW] = new(90.07f, 109.88f),
        [Dir.NW] = new(90.07f, 90.07f),
    };

    // 最外周の闇分身が北東のときの散開位置（中心からの相対座標, X=東+, Y=南+）
    // ひし形模様の頂点に一致。他の方角はこれを90°ずつ回転させる
    private static readonly Vector2 TankOffsetNE = new(1.365f, 6.825f);    // 闇 タンク (101.365, 106.825)
    private static readonly Vector2 MeleeOffsetNE = new(-6.825f, -1.365f); // 闇 近接   ( 93.175,  98.635)
    private static readonly Vector2 FireOffsetNE = new(1.365f, -1.365f);   // 炎 近接・タンク共通（頭割り） (101.365, 98.635)

    private static readonly HashSet<uint> TankJobs = [1, 3, 19, 21, 32, 37];
    private static readonly HashSet<uint> MeleeJobs = [2, 4, 20, 22, 29, 30, 34, 39, 41];

    private enum Dir { None, NE, SE, SW, NW }
    private enum State { Idle, WaitMaster, WaitChildren, WaitOuter, Show, Done }
    public enum RoleSetting { Auto, Tank, Melee }

    private State _state = State.Idle;
    private uint _masterId;
    private Vector3 _masterPos;
    private readonly HashSet<uint> _darkChildren = [];
    private Dir _outer = Dir.None;

    private Config C => Controller.GetConfig<Config>();

    public override void OnSetup()
    {
        foreach (var name in new[] { "Dark", "Fire" })
        {
            Controller.RegisterElement(name, new Element(0)
            {
                Enabled = false,
                radius = 0.6f,
                thicc = 5f,
                overlayFScale = 2f,
                overlayVOffset = 2f,
                overlayBGColor = 0xFF000000,
                overlayTextColor = 0xFFFFFFFF,
            });
        }
        Controller.RegisterElement("Other", new Element(0)
        {
            Enabled = false,
            radius = 0.6f,
            thicc = 2f,
            overlayFScale = 1.4f,
            overlayVOffset = 1.5f,
            overlayBGColor = 0xC0000000,
            overlayTextColor = 0xFFC0C0C0,
        });
    }

    public override void OnReset() => ResetAll();

    private void ResetAll()
    {
        _state = State.Idle;
        _masterId = 0;
        _masterPos = default;
        _darkChildren.Clear();
        _outer = Dir.None;
    }

    public override unsafe void OnStartingCast(uint sourceId, PacketActorCast* packet)
    {
        if (packet->ActionID == CastReplication)
        {
            ResetAll();
            _state = State.WaitMaster;
            return;
        }

        if (packet->ActionID == CastDark && _state == State.WaitMaster)
        {
            var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == sourceId);
            if (obj == null) return;
            _masterId = sourceId;
            _masterPos = obj.Position;
            _state = State.WaitChildren;
        }
        else if (packet->ActionID == CastDark && _state is State.WaitChildren or State.WaitOuter)
        {
            // 保険: 子分身の追跡に失敗しても、2回目の闇の詠唱位置から方角を決める
            var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == sourceId);
            if (obj != null && TryGetOuterDir(obj.Position, out var dir))
            {
                _outer = dir;
                _state = State.Show;
            }
        }
    }

    public override void OnActionEffectEvent(ActionEffectSet set)
    {
        if (_state == State.Show && set.Action?.RowId == ActionDarkHit) _state = State.Done;
    }

    public override void OnUpdate()
    {
        var dark = Controller.GetElementByName("Dark");
        var fire = Controller.GetElementByName("Fire");
        var other = Controller.GetElementByName("Other");
        if (dark == null || fire == null || other == null) return;
        dark.Enabled = false;
        fire.Enabled = false;
        other.Enabled = false;

        TrackClones();
        if (_state != State.Show || _outer == Dir.None) return;
        if (BasePlayer == null) return;

        // 闇耐性低下IIがある人は炎担当、ない人は闇担当
        var takesDark = !(BasePlayer.StatusList?.Any(x => x.StatusId == DarkResistanceDown) ?? false);

        var role = C.Role;
        if (role == RoleSetting.Auto)
        {
            var job = BasePlayer.ClassJob.RowId;
            if (TankJobs.Contains(job)) role = RoleSetting.Tank;
            else if (MeleeJobs.Contains(job)) role = RoleSetting.Melee;
            else return; // ヒーラー・遠隔は対象外
        }

        var mine = role == RoleSetting.Tank ? TankOffsetNE : MeleeOffsetNE;
        var theirs = role == RoleSetting.Tank ? MeleeOffsetNE : TankOffsetNE;
        var highlight = GradientColor.Get(C.ColorA, C.ColorB, 333).ToUint();

        // 闇誘導位置
        dark.SetRefPosition(ToWorld(Rotate(mine, _outer)));
        dark.overlayText = role == RoleSetting.Tank ? "闇 タンク" : "闇 近接";
        dark.tether = C.TetherToAssigned && takesDark;
        dark.color = takesDark ? highlight : C.DarkColor.ToUint();
        dark.thicc = takesDark ? 5f : 3f;
        dark.Enabled = true;

        // 炎誘導位置（近接・タンク共通の頭割り）
        if (C.ShowFire)
        {
            fire.SetRefPosition(ToWorld(Rotate(FireOffsetNE, _outer)));
            fire.overlayText = "炎 頭割り";
            fire.tether = C.TetherToAssigned && !takesDark;
            fire.color = !takesDark ? highlight : C.FireColor.ToUint();
            fire.thicc = !takesDark ? 5f : 3f;
            fire.Enabled = true;
        }

        if (C.ShowPartnerSpot)
        {
            other.SetRefPosition(ToWorld(Rotate(theirs, _outer)));
            other.color = C.PartnerColor.ToUint();
            other.overlayText = role == RoleSetting.Tank ? "近接" : "タンク";
            other.Enabled = true;
        }
    }

    private void TrackClones()
    {
        if (_state == State.WaitChildren)
        {
            // 親の闇分身から 5m の位置に出現した子分身 2体
            foreach (var obj in Svc.Objects)
            {
                if (obj.DataId != CloneDataId || obj.EntityId == _masterId) continue;
                var dist = Vector2.Distance(XZ(obj.Position), XZ(_masterPos));
                if (MathF.Abs(dist - 5f) < 0.3f) _darkChildren.Add(obj.EntityId);
            }
            if (_darkChildren.Count >= 2) _state = State.WaitOuter;
        }

        if (_state == State.WaitOuter)
        {
            foreach (var id in _darkChildren)
            {
                var obj = Svc.Objects.FirstOrDefault(x => x.EntityId == id);
                if (obj != null && TryGetOuterDir(obj.Position, out var dir))
                {
                    _outer = dir;
                    _state = State.Show;
                    break;
                }
            }
        }
    }

    private static bool TryGetOuterDir(Vector3 pos, out Dir dir)
    {
        foreach (var (d, p) in OuterPositions)
        {
            if (Vector2.Distance(p, XZ(pos)) < 0.5f)
            {
                dir = d;
                return true;
            }
        }
        dir = Dir.None;
        return false;
    }

    // NE を基準に時計回りへ 90° ずつ回転（地図上: 北→東→南→西）
    private static Vector2 Rotate(Vector2 v, Dir d)
    {
        var steps = d switch { Dir.SE => 1, Dir.SW => 2, Dir.NW => 3, _ => 0 };
        for (var i = 0; i < steps; i++) v = new Vector2(-v.Y, v.X);
        return v;
    }

    private static Vector3 ToWorld(Vector2 offset) => new(Center.X + offset.X, 0f, Center.Y + offset.Y);
    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);

    public override void OnSettingsDraw()
    {
        ImGuiEx.Text("ロール / Role");
        ImGuiEx.EnumCombo("##role", ref C.Role);
        ImGuiEx.HelpMarker("Auto: ジョブから自動判定（タンク / 近接DPS）。ヒーラー・遠隔は表示しません。");
        ImGui.Checkbox("炎誘導位置も表示 / Show fire spot", ref C.ShowFire);
        ImGui.Checkbox("自分の担当の位置にガイド線を引く / Tether to assigned spot", ref C.TetherToAssigned);
        ImGuiEx.HelpMarker("闇耐性低下IIがある=炎担当、ない=闇担当。担当の位置は点滅色＋ガイド線、もう一方は固定色で表示します。");
        ImGui.Checkbox("闇の相方の位置も薄く表示 / Show partner dark spot", ref C.ShowPartnerSpot);
        ImGui.ColorEdit4("担当の色A", ref C.ColorA, ImGuiColorEditFlags.NoInputs);
        ImGui.SameLine();
        ImGui.ColorEdit4("担当の色B", ref C.ColorB, ImGuiColorEditFlags.NoInputs);
        ImGui.ColorEdit4("闇位置の色（担当外）", ref C.DarkColor, ImGuiColorEditFlags.NoInputs);
        ImGui.ColorEdit4("炎位置の色（担当外）", ref C.FireColor, ImGuiColorEditFlags.NoInputs);
        ImGui.ColorEdit4("相方の位置の色", ref C.PartnerColor, ImGuiColorEditFlags.NoInputs);

        if (ImGuiEx.CollapsingHeader("Debug"))
        {
            ImGui.Text($"State: {_state}");
            ImGui.Text($"Master dark clone: {_masterId:X} at {_masterPos}");
            ImGui.Text($"Dark children: {string.Join(", ", _darkChildren.Select(x => x.ToString("X")))}");
            ImGui.Text($"Outer dark clone: {_outer}");
        }
    }

    public class Config : IEzConfig
    {
        public RoleSetting Role = RoleSetting.Auto;
        public bool ShowPartnerSpot = false;
        public bool ShowFire = true;
        public bool TetherToAssigned = true;
        public Vector4 ColorA = 0xFFFF00FF.ToVector4();
        public Vector4 ColorB = 0xFFFFFF00.ToVector4();
        public Vector4 DarkColor = 0xFFFF40C0.ToVector4(); // 紫（ABGR）
        public Vector4 FireColor = 0xFF0080FF.ToVector4(); // 橙
        public Vector4 PartnerColor = 0x80C0C0C0.ToVector4();
    }
}

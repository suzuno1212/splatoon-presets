# [7.4] 遺忘行路 ミストウェイク

| 項目 | 内容 |
|---|---|
| 対応 | 日本語クライアント / Splatoon 3.9.x |
| 最終更新 | 2026-10-27 |

取り込み方は [取り込み方ガイド](../../../../docs/取り込み方.md) を見てください。
全レイアウトをまとめて取り込む場合は [layouts.txt](layouts.txt) を開いて「Copy raw file」。

## 変更履歴
<details>
<summary>開く</summary>

- 2026-10-15: ペトリブレス、ギャロッピングサンダー（v2）を公開

</details>

## レイアウト

### D12_トレノ・カトブレパス_ペトリブレス
1ボス トレノ・カトブレパスのペトリブレス（前方120°扇）の範囲を表示します。

```
~Lv2~{"Name":"D12_トレノ・カトブレパス_ペトリブレス","Group":"[7.x]ミストウェイク","ZoneLockH":[1314],"ElementsL":[{"Name":"扇120°","type":4,"radius":30.0,"coneAngleMin":-60,"coneAngleMax":60,"color":4278190335,"fillIntensity":0.3,"thicc":3.0,"refActorNPCNameID":14270,"refActorRequireCast":true,"refActorCastId":[43335],"refActorComparisonType":6,"includeHitbox":true,"includeRotation":true,"FillStep":4.0}]}
```

## スクリプト

### 2ボス アムドゥシアス ギャロッピングサンダー
- ファイル: [D122_Amdusias_GallopingThunder.cs](D122_Amdusias_GallopingThunder.cs) → 開いて右上の「Copy raw file」
- 何をするか: 突進の予兆が出た順に、突進範囲（幅5の直線）を番号付きで表示します。次の突進は赤、それ以降は橙。突進するたびに先頭から消えます。各直線に当たる毒気塊の爆発範囲（半径9）も、爆発する順に表示します。
- 設定項目: 番号表示、色、毒気塊の爆発範囲の表示と先読みするグループ数

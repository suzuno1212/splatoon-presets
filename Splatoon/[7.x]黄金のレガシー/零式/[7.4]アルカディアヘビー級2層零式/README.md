# [7.4] アルカディアヘビー級2層零式

| 項目 | 内容 |
|---|---|
| 対応 | 日本語クライアント / Splatoon 3.9.x |
| 最終更新 | 2026-10-27 |

取り込み方は [取り込み方ガイド](../../../../docs/取り込み方.md) を見てください。
全レイアウトをまとめて取り込む場合は [layouts.txt](layouts.txt) を開いて「Copy raw file」。

## 変更履歴
<details>
<summary>開く</summary>

- 2026-10-17: ディープインパクト（タンク用）を公開

</details>

## レイアウト

### Hvy2S_ディープブルー_ディープインパクト_タンク
**タンクのときだけ**表示。ディープブルーがディープインパクトを詠唱している間、自分の周りに半径6の範囲（薄い青）と「アムレン使う！」を表示します。
※ディープインパクトは「ディープブルーから一番遠いプレイヤー」に飛ぶため、担当でない回にも表示されます。

```
~Lv2~{"Name":"Hvy2S_ディープブルー_ディープインパクト_タンク","Group":"[7.4]アルカディアヘビー級2層零式","ZoneLockH":[1323],"JobLockH":[1,3,19,21,32,37],"ElementsL":[{"Name":"トリガー：ディープインパクト詠唱","type":1,"refActorNPCNameID":14369,"refActorRequireCast":true,"refActorCastId":[46519],"refActorComparisonType":6,"Conditional":true,"Nodraw":true},{"Name":"自分中心 半径6","type":1,"radius":6.0,"color":4294947920,"fillIntensity":0.25,"thicc":3.0,"overlayText":"アムレン使う！","overlayFScale":3.0,"overlayVOffset":1.0,"overlayBGColor":4278190080,"overlayTextColor":4278255615,"refActorType":1}]}
```

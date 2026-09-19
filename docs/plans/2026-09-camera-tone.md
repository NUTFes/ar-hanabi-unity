# 明るさ補正（白飛び対策）

## Context

会場の照明が明るいと、カメラ映像が白飛び（画素が255に張り付く）し、
MediaPipe PoseLandmarker が人を検出できなくなる場合がある。

このプロジェクトのカメラ入力パイプラインには、これまで映像に手を入れる箇所が
1つも無かった（`PoseLandmarkDetector.DetectFromCamera` は
`WebCamTexture.GetPixels32()` → `Texture2D.SetPixelData()` を生のまま繋ぐだけ）。
Unity の `WebCamTexture` には露出・ゲイン・ホワイトバランスを操作する API が無く、
ネイティブプラグインを書かない限りカメラ側を絞ることはできない
（`CameraBackgroundController.cs` の解像度・fps 周りのコメント参照）。

そこで **検出に渡す画像だけを白飛びの手前まで戻すトーン補正（レベル補正＋ガンマ）**
を1段挟む。自動で合わせつつ、会場ごとの違いに対応できるよう管理画面から手で
調整できるようにする。表示側（背景Quad・来場者が見る映像）には掛けない。

---

## 調査で分かった現状（設計の起点）

| 事実 | 影響 |
|---|---|
| `WebCamTexture` に露出・ゲイン・WB の API が無い | カメラ側を絞る選択肢がそもそも無い。ソフト側で戻すしかない |
| `PoseLandmarkDetector.DetectFromCamera` が生画素を MediaPipe にそのまま渡す | 白飛びが検出の入力にそのまま乗る |
| `SelfieSegmentationController.RunSegmentation` も同じ生画素から人物マスクを推論 | 白飛びは検出だけでなく人物切り抜きも劣化させる |
| `DetectFrozenFrame`（`PoseLandmarkDetector.cs`）が生画素のハッシュでフレーム停止を検知 | 補正を先に適用するとハッシュが動き続け、停止を検知できなくなる |
| `develop` の `CameraBackgroundController.OpenDevice` で `ReleaseWebCamTexture()` が成功経路に混入していた（`8c03a77` の修正時の混入） | この状態では映像が一切出ず、補正の動作確認自体ができない。**先に直す必要がある既存バグ** |

---

## 設計の原則（`DopamineModeController` から借りるもの）

1. **「上書きして戻す」を一切やらない。「実効値を毎回引く」に統一する。**
   自動レベル（`autoLevels`）が ON の間も、手動の保存値（`blackLevel`/`whiteLevel`）
   には一切書き込まない。自動が計算した値は `_autoBlack`/`_autoWhite` という
   別のフィールド（実行時のみ・`SettingsStore` には保存しない）にしか入らない。
   自動を OFF にした瞬間、読む先が保存値へ戻るだけで済む
   （戻す処理が存在しなければ、戻し忘れも、戻す前に落ちる事故も、原理的に起きない）。
2. **既定では挙動が変わらない。** 補正 OFF、またはほぼ恒等（自動が「普通の露出」と
   判断した場合を含む）のときは画素に一切触れない。呼び出し側はループへ入らない
   （`HasCorrection` が `false` になる）。
3. **黙って効かない状態を作らない。** 白飛び率・平均輝度を管理画面のヘルプ行に常時出す。
   完全に255へ張り付いた画素はどんな式を通しても情報が戻らないので、
   高いときは「打つ手はカメラ側」であることを画面から読めるようにする。

---

## アーキテクチャ

### `CameraToneController`（新規・`Scripts/Core/CameraToneController.cs`）

`SpaceModeController` / `ExperienceDirector` / `DopamineModeController` と同じ形
（Singleton `Instance` ／ `GetOrCreate()` ＋ `RuntimeInitializeOnLoadMethod` ／
`SettingsStore` 永続化 ／ `event Action OnChanged`）。シーンに置く必要は無い。

ドーパミンモードの「マスターは毎起動OFFから始まる」は踏襲しない。
カメラ位置・鏡像設定と同じ「その会場の条件」を扱うコンポーネントなので、
前回の状態を素直に引き継ぐ。

**保存する値**

| フィールド | 既定 | 範囲 |
|---|---|---|
| `toneEnabled` | `true` | — |
| `autoLevels`  | `true` | — |
| `blackLevel`  | `0.0`  | 0〜0.60 |
| `whiteLevel`  | `1.0`  | 0.40〜1.0 |
| `gamma`       | `1.0`  | 0.40〜2.5 |

### 自動レベルは画面を縦6分割して独立に計算する（1回目の実装からの変更点）

白飛びは画面のどこにでも起こりうる（照明が上から当たれば上部、反射なら下部、
それ以外の高さも当然ありうる）。フレーム全体を1本のヒストグラムで見ると、
飛んでいる範囲が画面の一部にとどまる場合に統計へほとんど効かず、自動補正が
利かない。逆に1本の値を画面全体に強く掛けると、飛んでいない範囲まで
不必要に暗くなる。

そこで縦方向に `AutoBandCount`（内部定数、既定6）個のバンドへ分け、
バンドごとに個別の黒レベル・白レベルを自動計算する。横方向は分割しない
（会場の照明は天井・窓など高さに依存することが多い前提。横方向のむらが
問題になった場合は同じ考え方を横方向にも拡張できる）。

手動モード（自動OFF）は今も分割しない。画面全体に同じ1組の値を掛ける
単純な役割のまま、GUI（黒レベル・白レベル・ガンマの3スライダー）も変えていない。
自動が「画面のどこが飛んでいるか」を勝手に見つけて直すのに対し、手動は
「操作した人が画面全体に対して決めた値」という単純な受け皿にとどめてある。

**実効値**（バンドごと。自動のときだけ差し替わる。ガンマは自動の対象外でバンド共通）

```csharp
private float EffBlackForBand(int band) => autoLevels ? _autoBlackBand[band] : blackLevel;
private float EffWhiteForBand(int band) => autoLevels ? _autoWhiteBand[band] : whiteLevel;
private float EffGamma => gamma;
```

**掛ける式**（`x` は 0..1。バンドごとのLUTに焼き込む）

```
y = clamp01((x - EffBlack) / max(eps, EffWhite - EffBlack))
y = pow(y, EffGamma)
```

バンドごとに256エントリの `byte[]` LUT を持ち、実効値が変わったバンドだけ焼き直す
（`RainbowTint` が色相 LUT でやっているのと同じ考え方）。
適用時は「その行がどのバンドの近くか」で隣り合う2バンドの間を線形補間するので、
バンドの境目が段差として見えることはない（行ごとに256要素だけの補間LUTを
1回焼くだけなので、幅ぶんの画素数より遥かに軽い）。
`HasCorrection` は補正 OFF、または全バンドがほぼ恒等
（`black<eps && white>1-eps` が全バンドで成立 かつ `gamma≈1`）なら `false` になり、
呼び出し側はループに入らずコストがゼロになる。

**自動レベルの計測**（`Measure(Color32[] raw, int width, int height, float now)`）

- 0.25秒に1回だけ実際に計測する（呼び出し自体は毎フレームでよい。内部で間引く）
- 画素を等間隔サンプリング（全バンド合計で約2万点）→ 各画素の行からバンドを決め、
  バンドごとに256ビンのヒストグラムへ積む
- バンドごとに下位2%を `targetBlack`、上位98%を `targetWhite`
- 安全弁: `targetBlack <= 0.60` ／ `targetWhite >= targetBlack + 0.15`
  （真っ白な壁を見ても人まで潰さない床。無いと発散する。バンドごとに独立に効く）
- バンドごとに時定数1.5秒で指数平滑（初回は最大2秒ぶんとして速く寄せる）
- 普通の露出ならどのバンドも下位2%≒0・上位98%≒1になり、自動でも実質無補正になる
  （自己抑制。バンドごとに独立して起きる）
- `toneEnabled`/`autoLevels` の ON/OFF に関係なく常に計算する
  （管理画面のヘルプ行に白飛び率を常時出したいため。補正 OFF でも診断はできる）
- `AR_VERBOSE_LOG` 定義時、バンドごとの白飛び率・平均輝度を1行ログに出す
  （`CameraToneController.LogBandStats`。現場でどのバンドが飛んでいるか追うため）

### 呼び出し側 ①: `PoseLandmarkDetector.DetectFromCamera`

```
1. _webCamTexture.GetPixels32(_pixelBuffer)
2. DetectFrozenFrame()                                         ← 生のまま
3. tone.Measure(_pixelBuffer, width, height, Time.time)         ← 生のまま
4. tone.Apply(_pixelBuffer, width, height)                      ← ここで初めて書き換える
5. _inputTexture.SetPixelData / Apply / DetectAsync
```

順序が要点。4を2より先にすると、自動モードでLUTが動くたびにハッシュが変わり、
「カメラが止まっても二度と警告が出ない」事故になる。
`ARHanabi.Pose.Tone` の `ProfilerMarker` を既存4本（Readback/Upload/DetectAsync/Dispatch）
に揃えて追加し、コストを実測できるようにしてある。

### 呼び出し側 ②: `SelfieSegmentationController.RunSegmentation`

NHWC 変換ループを行単位に組み直し、行ごとに `CameraToneController.Instance
?.RowLutOrNull(y, height)` を引いてから、その行ぶんの画素だけ処理する
（`p.r * Inv255` 等をそのまま／`lut[p.r] * Inv255` に差し替える）。
画面の一部だけが白飛びしているケースに対応するため、LUT をループの外で
1回だけ引くのではなく行ごとに取り直す必要がある。Instance の null チェックだけは
行の外で1回に留める（`?.` は素の参照比較で、Unity の `==` オーバーロードとは違い
画素ごとに呼んでも問題にならないが、行単位に揃えてある）。

### 表示映像は変えない（意図的）

背景Quad（`Custom/BackgroundRemoval`）には掛けない。訴えは「人の判定が通らない」
であって見た目ではなく、補正を表示に回すと花火との合成の印象まで変わるため。
セグメンテーション側を直すので、人物の切り抜きは間接的に改善する。

---

## 管理画面 — 7枚目のタブ「明るさ補正」

ドーパミンタブが確立した「ボタン行＋スライダー格子」の複合ページをそのまま使う。
`AdminUIBuilder.BuildDopaminePage` を `BuildRowAndGridPage(panel, page, rowChildName,
gridChildName, buttonOrder, sliderSpecs, columns, log)` へ一般化し、
`BuildDopaminePage`/`BuildTonePage` はその1行呼び出しにした
（ドーパミンタブ追加時に `BuildSliderGridPage` を一般化したのと同じ機械的リファクタ）。

- ボタン2個: `明るさ補正 [ON/OFF]` / `自動 [ON/OFF]`
- スライダー3列: `黒レベル` / `白レベル` / `ガンマ`（min/max は `AdminUIBuilder.ToneSliders`
  が唯一の設定元。`CameraToneController` の `[Range]` と一致させてある）
- ラベルは自動ON中、保存値と実効値（自動が計算した値）を併記する
  （`黒レベル 0.00（自動 0.42）`。バンドごとに値が違う場合は
  `黒レベル 0.00（自動 0.00〜0.42）` と最小〜最大の範囲で見せる）。
  片方だけだと「動かしても効かない」か「表示と挙動が違う」の誤解が必ず起きる
  （ドーパミンの「検出の調整」併記と同じ理由）
- ヘルプ行に白飛び率・平均輝度を常時表示。明るさ補正タブを開いている間だけ4Hzで更新
  （毎フレーム書くとTMPのメッシュ再構築が無駄に走る）。30%を超えたら
  「カメラ側の露出を下げてください」を併記する

タブが7枚になっても右端の件数表示が潰れないことは `AdminUIBuilder.BuildTabBar`
のコメントで計算し直し済み（176px×7 + 列間12×6 = 1304px、パネル有効幅1872pxに対し
568px残る。「全12件/有効8件」の約300pxに対し約1.9倍の余裕）。

---

## 変更したファイル

| ファイル | 内容 |
|---|---|
| `Scripts/Core/CameraBackgroundController.cs` | 【別コミット】既存バグ修正。`ReleaseWebCamTexture()` を成功経路からタイムアウト分岐の中へ戻した |
| **新規** `Scripts/Core/CameraToneController.cs` | 設定・永続化・自動計測・LUT |
| `Scripts/Pose/PoseLandmarkDetector.cs` | `DetectFromCamera` に計測＋適用（順序厳守）、`ARHanabi.Pose.Tone` マーカー |
| `Scripts/Pose/SelfieSegmentationController.cs` | NHWC構築ループで同じLUTを通す |
| `Editor/AdminUIBuilder.cs` | 7枚目のタブ、ボタン2・スライダー3、`BuildRowAndGridPage`への一般化、幅見積もりコメント更新 |
| `Scripts/UI/AdminUIManager.cs` | `AdminTab.Tone`、トグル2・スライダー3、実効値併記、白飛び率の4Hz更新 |
| `Scenes/MainScene.unity` | 「Admin UI を再構築」の成果物（Unity Editor での実行が必要） |

**変更しないもの**: `BackgroundRemoval.shader` / `BackgroundRemovalEffect.cs` /
`GestureDetector.cs` / `DopamineModeController.cs` / `SettingsStore` のバージョン
（既存キーの意味・既定値を1つも変えていないため `SettingsVersion` の更新は不要）

参考にした既存コード: `DopamineModeController.cs`（設定の2層・永続化・自己生成・
`OnChanged`）、`GestureDetector.cs` の実効値プロパティ層と管理画面向けの読み取り専用面、
`RainbowTint.cs`（LUTの焼き直しタイミング）、`AdminUIBuilder.BuildDopaminePage`（複合ページ）、
`PoseLandmarkDetector.DetectFrozenFrame`（間引きサンプリング）

---

## 現場運用の注意

- 明るさ補正・自動レベルは他のカメラ設定（鏡像・カメラ index）と同じく前回の状態を
  引き継ぐ。ドーパミンモードのマスターのような「毎起動リセット」はしない
- 完全に255へ張り付いた画素はソフトの補正では戻らない。白飛び率が高いまま
  下がらない場合は、照明の向き・カメラの角度・カメラ側の露出固定ツールを検討する
  （Unity から UVC の露出を直接触るAPIは無い）
- 自動ONの間、黒レベル/白レベルのスライダーを動かしても実効値には即座に反映されない。
  スライダー自体は無効化していないので、次に自動をOFFにする予定なら
  先に仕込んでおける（OFFにした瞬間にそのまま効く）

## Unity Editor での作業が必要な手順

1. `MainScene.unity` を開く
2. メニュー `ARHanabi > Admin UI を再構築`
3. 気に入らなければ Ctrl+Z（Undo 1回で全部戻る）
4. 問題なければ Ctrl+S でシーンを保存

## 検証

**回帰**
- 「明るさ補正」OFFで、映像・検出・花火が従来どおり（`HasCorrection == false` でループごとスキップ）
- カメラのレンズを塞ぐ／ケーブルを抜く →「カメラのフレームが…更新されていません」が
  今までどおり出る（計測・ハッシュを生画素に対して行えている証拠）
- 補正ON/OFFを往復したあと「検出の調整」タブの値が触る前と同じであること
- 再起動して黒/白/ガンマ・2つのトグルが引き継がれること

**白飛びの解消（画面のどこが飛んでも直ること）**
- 普通の照明で、自動ONでも黒レベルがほぼ0のまま（無補正に自己抑制される）
- カメラの**上部**だけに強い光を当てる → 上のバンドだけ黒レベルが1〜2秒で追従し、
  上部の骨格検出（手を上げる・ジャンプ等）が通ること。下部は暗くならないこと
- カメラの**下部**だけに強い光を当てる（床の反射など） → 下のバンドだけが追従し、
  上部は影響を受けないこと（上下どちらが飛んでも同じように直ること）
- バンドの境目（画面の高さの途中）で補正の段差が見えないこと
- 補正OFFにすると、飛んでいた側の骨格検出が再び通らなくなることを見比べる
- 真っ白に飛ばしきった状態では白飛び率が高いままで、
  「カメラ側の露出を下げてください」が出ること
- `AR_VERBOSE_LOG` を有効にして、`[Tone] バンド別（下→上）白飛び率／平均輝度` が
  飛んでいる場所のバンドだけ高い値を示すこと

**負荷**
- Profilerの`ARHanabi.Pose.Tone`を見て、1280x720@30Hzで許容できなければ
  カメラ要求を640x480に下げて再測定する

**管理画面**
- タブが7枚並んでも右端の件数表示が潰れないこと
- 自動ONのとき黒/白のラベルに実効値が併記されること。自動OFFで併記が消えること

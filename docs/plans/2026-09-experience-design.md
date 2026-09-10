# 子どもを中心に、全年齢が楽しめる体験設計

> この計画書は NUTFES 企画局向けの共有用ドキュメントです。当初のプランから、実装するスコープを「子どもの基本ループ（判定・位置・コンボ）」と「一緒に（アンサンブル）」の4項目に確定し、アトラクト・お手本・フィナーレ・レア型＋隠し操作は実装しない方針に更新しています。実装済み／未実装は各セクションに【】で明記しています。

## スコープの確定について

当初のプランには「無人時の自動打ち上げ（アトラクト）」「お手本表示」「フィナーレ」
「レア型＋隠し操作」も含まれていたが、検討の結果これらは実装しない方針に確定した。
実装するのは「子どもの基本ループ」と「一緒に」の4項目のみ（詳しくは「決定したスコープ」参照）。
このプランはその方針変更を反映した最終版であり、以下は全てこの縮小スコープに基づく記述になっている。

## Context

AR花火は「ジェスチャーで花火が上がる」体験だが、今の作りは
**誰が来ても同じ反応**しか返さない。3種のジェスチャー（両手上げ／片手上げ／ジャンプ）が
それぞれ決まった花火を1発出すだけで、繰り返しても変わらず、一緒にやっても変わらない。

展示の前提（確認済み）:
- **無人**（たまに見に行くだけ）。操作を説明する人は居ない
- **自由に出入り、数分滞在**。同時に複数組が居る
- **親子連れが中心**。子どもが遊び、親が後ろで見ている。学生は別のタイミングで来る
- **子ども・学生を優先**。大人は後回しでよいが、一緒に楽しめるとなおよい

目指すのは、年齢ごとに「楽しいと感じる理由」が違うことを前提に、
**同じ1つの画面の前で、それぞれの理由が同時に満たされる**設計。

---

## 年齢で「楽しい理由」がどう違うか

同じ「手を上げたら花火が上がる」を体験しても、何を楽しんでいるかは年齢で違う。
ここを外すと、子ども向けに寄せた瞬間に学生が退屈し、逆も起きる。

### 小さい子（〜小学校低学年）— 「自分がやった」の確認を繰り返したい

- 楽しさの核は **原因→結果の発見**。「ぼくが手を上げたら花火が出た」を20回確認する
- 反応は **即（0.5秒以内）・確実（毎回）** でないと魔法が解ける。1回の空振りで冷める
- 大きな身体の動き（ジャンプ・両手をぶんぶん振る）が自然。**ポーズを保つのは苦手**
- 注意は数秒単位。1つ1つの動作が単独で報われる必要がある
- **文字は読まない**。画面内の説明文は存在しないのと同じ
- 「見て見て！」と親を見る。**親が反応すると倍楽しい**
- リスク: **連打**。30回ジャンプする。それが30発の花火の重なりになると画面が白い塊になり、
  肝心の「自分がやった」の対応関係が消える

### 小学校高学年〜中学生 — 仕組みを「攻略」したい

- ルールを見つけて使いこなしたい。「両手だと大きいのが出る」を自分で発見して兄弟に教える
- 兄弟・友達との競争と協力

### 高校生・大学生 — 仕組みの「裏」を見たい

- カメラと姿勢検出だと分かっている。「何がトリガーか」を試す
- 集団で「みんなで一緒にやろう」をやる。協力トリガーが刺さる

### 大人（親）— 子どもの喜びが自分の喜び

- 自分では操作しない。**子どもが成功する瞬間を見たい**。子どもの成功を目立たせるのが親への設計
- 1人で手を上げるのは恥ずかしい。**子と一緒なら上げる** → 協力トリガーが「上げていい理由」になる
- 作り込みを見ている。菊・牡丹・柳の違い、1発の綺麗さ。静かな1発を評価する

---

## 設計の原則（上の分析から導く）

1. **反応は即・確実・「自分の」と分かる**
   全てのジェスチャーに必ず反応し、その人の身体の上に「効いている」が見える。
   花火はその人の位置から上がる（今は常に画面中央から上がるので、複数人だと誰のか分からない）

2. **連打を壊さず、育てる**
   子どもは連打する。それを30発の重なりで壊すのではなく、
   「短時間に連続でやると花火が育つ／変わる」に変換する。連打欲求を別の報酬で受け止める

3. **一緒にやると特別**
   2人以上が同時に同じジェスチャー → 1人では出せないものが出る。
   親子のフック、学生グループのフック、両方をこれ1つで作る

上記3つが最終的に実装する4項目（判定・位置・コンボ・アンサンブル）に直結する原則。
「画面が教える（無人の前提）」「終わりがある（フィナーレ）」「発見がある（レア型）」も
検討したが、アトラクト・お手本・フィナーレ・レア型を実装しない方針としたため、
これらの原則には対応する実装を作らない。

---

## 調査で分かった現状（設計の起点）

| 事実 | 体験への影響 |
|---|---|
| シーンの `jumpThreshold: 1`（コード既定 0.06）。33ms で腰が肩幅1つ分動く条件 | **ジャンプが事実上反応しない**。子どもの一番自然な動作が死んでいる |
| `handUpThreshold: 1`（コード既定 0.15）。手首が肩より肩幅1つ分上 | 腕の短い子どもには届きにくい |
| `gestureCooldown: 0.005` | 連発防止が無効。30回ジャンプ＝30発の重なり |
| `launchAtScreenCenter: true` | 花火は常に画面中央から。複数の子が居ると誰の花火か分からない |
| `PoseFeedback` は人ごとに毎フレーム配信されるが、購読は `SkeletonRenderer` のみ | 人数・在席の材料はあるが、コンボ・アンサンブルには使わない（在席集計は不要と判断） |
| 「複数人が同時に同じジェスチャー」を見ている箇所は無い | `PoseEventBus.OnGestureDetected` が人物ID付きなので新規集計で作れる |

※ `jumpThreshold` / `handUpThreshold` は現場PCの PlayerPrefs で上書きされている可能性がある。
　意図的に厳しくした値かもしれないので、変更は「既定値を直す」として扱い、
　起動ログで実効値を確認できるようにしてある（実装済み）。

---

## 決定したスコープ（確定・縮小版）

### 子どもの基本ループ
1. **判定を子どもの体に合わせる** — ジャンプを「1フレームの速度」から「立っている高さからどれだけ上がったか」に判定し直す。手上げ閾値を腕の短い子が届く値に
2. **花火をその人の位置から上げる** — `launchAtScreenCenter` OFF ＋ 端に寄らない余白
3. **コンボ** — 同一人物の短い間隔の連続ジェスチャーは毎回反応するが軽い花火。続けると育ち、5回目で大玉。連打を「積み上げ」に変換する
   - 見せ方（管理画面で個別に ON/OFF）:
     - **数字がその人の位置に出る**（「2！」〜「5！」がポップして消える）
     - **昇りの光跡が育つ**（コンボが上がるほどロケットが太く・明るく・火の粉が多く）
   - 骨の色は使わない

### 一緒に
4. **アンサンブル** — 2人以上が0.8秒以内に同じジェスチャー → 1人では出ない特別な花火が2人の中間から。3人以上でさらに大きく。**個人の花火も出す**（「自分の花火が出なかった」を避ける）

### 入れないもの（当初の検討から除外）
- **無人時の自動打ち上げ（アトラクト）**
- **ジェスチャーのお手本表示**
- **フィナーレ（スターマイン）**
- **レア型の重み付け・隠し操作（両手長押しの特別演出）**
- 骨の色によるフィードバック（`enableInteractiveFeedback` は OFF のまま）
- 音でコンボを伝える
- 年齢の推定による体験の分岐
- 同時発数の明示的な上限

---

## アーキテクチャ

### 方針: 1つの Director ＋ 純粋 C# のロジッククラス

```
Assets/ARHanabi/Scripts/Experience/
  ExperienceDirector.cs    MonoBehaviour。PoseEventBus の購読・設定の持ち主。
                           SpaceModeController と同じ作法（masterEnabled && xxxEnabled の2層、
                           XxxEnabled=実効値 / XxxSetting=設定値、SettingsStore 永続化、
                           GetOrCreate() + [RuntimeInitializeOnLoadMethod] で自己生成）
  ComboTracker.cs          純粋C#。人ごとの連続カウンタ
  GestureEnsemble.cs       純粋C#。0.8秒窓の同時ジェスチャー判定
  FireworkPlan.cs          静的クラス。「状況 → LaunchRequest の列」の変換表（見せ方の差し替え点）
  ComboNumberOverlay.cs    MonoBehaviour。人の位置に「2！」を出す（world-space TextMeshPro）
Assets/ARHanabi/Scripts/Fireworks/
  LaunchRequest.cs         新規 struct。打ち上げ1発の指示書
```

在席人数の集計（PresenceTracker）や状態機械（Vacant/Attract/Occupied/Finale）は、
アトラクト・お手本・フィナーレを実装しないことにしたため不要と判断し、持たない。
コンボ・アンサンブルはどちらも「人ごとの直近の振る舞い」だけで完結する仕組みで、
在席状況を知らなくても動く。

**なぜこの分割か**
- **判定ロジックは全部 `float now` を引数で受ける純粋 C#**（`PoseTracker` と同じ形式）。Unity イベントも
  `Time.time` も使わないので EditMode テストで時系列を流し込める
- **現場での個別 ON/OFF** は Director の各段で `if (!XxxEnabled) skip` するだけ

### データフロー

```
GestureDetector ── PoseEventBus ──┬─ SkeletonRenderer（既存・変更なし）
                                  └─ ExperienceDirector
     OnPersonLost     → combo.Orphan(trackId, now)
     OnGestureDetected(trackId, g, pos):
       1. stage = combo.Register(trackId, g, pos, now) ← コンボ段階（1〜5）
       2. ensemble.Register(trackId, g, pos, now)      ← 保留（Update で発火）
       3. reqs = FireworkPlan.Individual(g, stage, pos) ← 段階に応じた個人の花火
       4. launcher.Launch(req) ×N                       ← 即
       5. OnComboAdvanced(trackId, stage, pos) 発火     ← 数字オーバーレイが購読
     Update():
       combo.Sweep(now)（メモリ整理） / アンサンブル保留の発火（TryFire）
                                  ▼
FireworkLauncher
  OnGestureDetected: if (ExperienceDirector.Instance?.RoutesGestures == true) return;  ← 二重発火防止
                     else LaunchForGesture(...)                                        ← Director 不在時の従来経路
  public Launch(in LaunchRequest)
```

**二重発火の回避**: Launcher の `OnGestureDetected` 先頭で Director が有効なら即 return。
購読順に依存しない（「発射の瞬間に確認する」既存方針）。Director のマスター OFF なら
Launcher の従来経路に完全に戻る＝リグレッションの逃げ道。

**アンサンブルは即発火ではなく収集遅延 0.3 秒**。個人の花火が先に上がり（昇り 0.45〜0.72秒）、
その 0.3 秒後にアンサンブルの昇りが始まる。「自分の花火 → 応えるように特別な花火」の順序が固定され、
3人目が 0.3 秒以内に来ても保留を昇格するだけで2回撃たない。

---

## 各機能の設計

### 1. `FireworkLauncher` — `LaunchRequest` 化と公開 API 【実装済み】

```csharp
public enum ImageDecision { Auto, ForceImage, ForceShell }

public struct LaunchRequest
{
    public Vector2       normalizedPos;   // 人の腰位置（0..1）
    public float         xOffsetViewport; // 左右分離・ばらつき
    public bool          isLarge;
    public float         startDelay;
    public float         volumeScale;
    public ShellPreset   forcedPreset;    // null = 抽選
    public ImageDecision image;
    public float         sizeScale;       // 1 = 通常。アンサンブル 1.25 / コンボ軽 0.7〜0.9
    public string[]      nameFilter;      // null = largeShellNames/smallShellNames の既定
    public int           comboStage;      // 昇りの光跡を育てるのに使う（0 = 通常）
}
```

- `LaunchSequence` を `LaunchRequest` 1個受け取る形に統合
- `public void Launch(in LaunchRequest)` と `public void LaunchForGesture(int trackId, GestureType, Vector2 pos)`
  （旧 `OnGestureDetected` の switch を公開。Director 不在時のフォールバック兼「通常演出」の窓口）
- `LaunchTest / LaunchTestShell` は `LaunchRequest` を組んで `Launch` を呼ぶ薄いラッパー（挙動不変）
- `sizeScale` は `LaunchShellWithPreset` の `common` に掛ける
- `comboStage` は `SpawnLaunchTrail` に渡し、`headSize` / `sparkCount` / `emissiveIntensity` を段階で掛ける。
  `ComboTrailEnabled` が OFF なら Director が 0 を渡す

**位置の決め方**: `ResolveCenterU(Vector2)` に集約。`launchAtScreenCenter` が OFF のときは
人の関節座標を Quad 経由で画面上の実際の位置に変換してから
`Mathf.Lerp(edgeMargin, 1 - edgeMargin, x)` で remap（`launchEdgeMarginViewport = 0.12`）。
端の人でもジッター込みで画面内に収まる。`launchAtScreenCenter` は `SettingsStore` 対応のプロパティ。

動画を貼った Quad は画面よりかなり大きく張り出しているため、関節の生座標をそのまま
ビューポート座標として使うと画面端でズレる。`PoseCoordinateUtil.LandmarkToQuadPoint` /
`LandmarkToViewport` を新設し、Quad 経由で正確な変換を行う（SkeletonRenderer と共通化）。

### 2. `GestureDetector` — 子どもの体に合わせる 【実装済み】

**ジャンプの再設計**（「瞬間の速度」→「立っている高さからどれだけ上がったか」）
- `PersonState` に腰・足首 y の履歴（時刻付き、0.8秒窓）を追加
- `baseline = 窓内の y の最大値`（＝いちばん低い位置）。直近 0.1 秒は除外（跳び始めで基準を汚さない）
- `rise = baseline - y`、`th = shoulderWidth × jumpRiseThreshold`（新フィールド、既定 **0.35**）
- 発火: `jumpArmed && rise > th && 立ち上がり時間 ≤ 0.25秒` かつ足首も同じ窓で `ankleRise > 0.5 × rise`
  （足が浮いている＝しゃがんで立つのを除外。足首の visibility が低ければ速度条件のみ）
- 発火後 `jumpArmed = false`。着地（`rise < 0.3×th`）かつ 0.4 秒経過で再武装 → 連続ジャンプは毎回反応してコンボが育つ
- 意味が変わるので旧 `jumpThreshold` は使わず `jumpRiseThreshold` を別名で追加（このリポジトリの明文ルール）

**手上げ**: `handUpThreshold` の既定を **0.5** に（手首が肩より肩幅の半分上）。

**PlayerPrefs の既存値対策**: `SettingsStore` に `SettingsVersion`（int）を追加し、
`GestureDetector.Awake` で version < 2 なら関連キーを `DeleteKey` して version=2 を書く。
現場PCの `handUpThreshold=1.0` / `gestureCooldown=0.005` が保存済みでも新既定に切り替わる。

### 3. コンボ（`ComboTracker` ＋ `FireworkPlan.Individual`）【実装済み】

```
状態（trackId ごと）: count, lastTime, lastPos, orphanedAt
Register(trackId, pos, now):
  now - lastTime > window(2.0秒) → count = 0
  count++
  count ≥ finisherCount(5) → count = 0、段階5（フィニッシャー）
Orphan(trackId, now)  ← OnPersonLost。即削除せず orphanedAt を記録
Adopt: 未知の trackId が来たとき、1.5秒以内かつ lastPos との距離 < 0.12 の孤児があれば引き継ぐ（振り直し対策）
Sweep(now): 1.5秒を超えて誰にも引き継がれなかった孤児を削除（メモリ増加対策）
```

ジェスチャーの種類は問わず数える（片手→両手→ジャンプでも育つ）。子どもは種類を選ばない。

| 段階 | 発数 | isLarge | sizeScale | 型（Inspector の `string[]` で差し替え可） |
|---|---|---|---|---|
| 1 | 従来（両手2／他1） | 従来 | 1.0 | 既定の抽選 |
| 2 | 1 | false | 0.7 | `comboLightNames` = 牡丹 / 花雷 |
| 3 | 1 | false | 0.85 | 同 |
| 4 | 1 | true | 0.9 | `comboBigNames` = 菊 / 変化菊 |
| 5 | 2（左右分離） | true | 1.15 | `comboFinisherNames` = 千輪菊 / 芯入り菊 / 冠 |

**見せ方（管理画面で個別 ON/OFF）**
- **数字**【実装済み】 — `ComboNumberOverlay` が Director の `OnComboAdvanced(trackId, stage, pos)` を購読。
  world-space `TextMeshPro`（UGUI ではない）を、花火と同じ横位置・固定の見やすい高さに置き、
  「2！」〜「5！」を0.2秒で拡大ポップ→0.8秒で上昇しながらフェード。段階1は出さない（初回は普通の花火でよい）。
  `ComboNumberEnabled`（管理画面の個別トグル）で ON/OFF できる
- **昇りの光跡**（実装済み） — `LaunchRequest.comboStage` → `SpawnLaunchTrail` で
  `headSize ×(1+0.15×stage)`、`sparkCount ×(1+0.3×stage)`、`emissiveIntensity ×(1+0.12×stage)`。
  段階5は色を白寄りに

### 4. アンサンブル（`GestureEnsemble` ＋ `FireworkPlan.Ensemble`）【実装済み】

```
固定長リングバッファ32（毎フレーム確保なし）
Register(trackId, gesture, pos, now):
  window(0.8秒) より古いエントリを捨てる
  同じ gesture で trackId が違い、かつ |pos - e.pos| > samePersonDistance(0.12) を集める
    （距離条件が振り直し対策: 同じ場所の「別ID」は同一人物とみなす）
  参加者 ≥ 2 → Pending { fireAt = 最初の一致から collectDelay(0.3) 後 }。以後は参加者を追加するだけ
TryFire(now): now ≥ fireAt → 結果（人数・平均位置）を返してクリア。参加者全員に refractory(1.5秒) を付ける
```

- `treatAnyHandUpAsSame` フラグ（片手／両手を同一視）を Inspector に残す。親子で片手／両手が揃わないことが多いので現場で ON にする可能性が高い
- level 2 → 特別な1発（`ensembleShellNames` = 型物・ハート / 芯入り菊、isLarge、sizeScale 1.25、中間位置）
- level ≥ 3 → ミニスターマイン: 3〜5発を `startDelay = i × 0.18` でずらして連続発射、うち1発を特別型
- 個人の花火は各人のジェスチャー時に既に出ているので「自分の花火が出なかった」は起きない

### 5. 管理画面 — 体験タブ（縮小版）【コード実装済み・Editor での再構築待ち】

`AdminUIBuilder.cs`
- `TabBarButtonOrder` に `"TabExperienceButton"`、`BuildTabContent` に `TabExperiencePage`
- ボタン5個を1行で: `ExpMasterButton`「体験演出」/ `ComboButton`「コンボ」/ `ComboNumberButton`「コンボの数字」/
  `ComboTrailButton`「コンボの光跡」/ `EnsembleButton`「いっしょに」
- `"Jump"` → `"JumpRise"` 差し替え（表示ラベルは既に「ジャンプの高さ」へ変更済み。GameObject名の
  リネームはこのタブ再構築のタイミングでまとめて行う）
- 基本タブに `"PersonPosButton"`「人の位置から打つ [ON/OFF]」（Launcher の設定なのでこちら）

`AdminUIManager.cs`
- `AdminTab.Experience` 追加、`SwitchTab` に1行、ヘルプ文言
- `Start()` で `_experience = ExperienceDirector.GetOrCreate()`、各ボタン `ApplyToggleVisual(btn, txt, "名前", _experience.XxxSetting)`（宇宙モードと同型）

---

## 変更するファイル

| ファイル | 内容 |
|---|---|
| **新規** `Scripts/Experience/ExperienceDirector.cs` | 購読・設定・コンボ／アンサンブルの配線【実装済み】 |
| **新規** `Scripts/Experience/ComboTracker.cs` | コンボ（純粋C#）【実装済み】 |
| **新規** `Scripts/Experience/GestureEnsemble.cs` | 同時判定（純粋C#）【実装済み】 |
| **新規** `Scripts/Experience/FireworkPlan.cs` | 状況→LaunchRequest 列の変換表【実装済み】 |
| **新規** `Scripts/Experience/ComboNumberOverlay.cs` | 数字ポップ【実装済み】 |
| **新規** `Scripts/Fireworks/LaunchRequest.cs` | 打ち上げ指示書【実装済み】 |
| `Scripts/Fireworks/FireworkLauncher.cs` | LaunchRequest 化、`Launch`/`LaunchForGesture` 公開、`ResolveCenterU` remap、Director への早期 return、comboStage → 光跡【実装済み】 |
| `Scripts/Pose/GestureDetector.cs` | ジャンプ再設計、`jumpRiseThreshold`、`handUpThreshold` 既定 0.5、SettingsVersion【実装済み】 |
| `Scripts/Core/PoseCoordinateUtil.cs` | Quad マッピングの static 化（SkeletonRenderer と共通化）【実装済み】 |
| `Scripts/Core/SettingsStore.cs` | `SettingsVersion`【実装済み】 |
| `Scripts/UI/AdminUIManager.cs` ＋ `Editor/AdminUIBuilder.cs` | 体験タブ（縮小版）、トグル5個、人の位置トグル【実装済み。Editor での「Admin UI を再構築」の実行が必要】 |
| `Scenes/MainScene.unity` | GestureDetector の実効値修正、Admin UI 再構築の結果【一部実装済み】 |

参考にする既存コード: `SpaceModeController.cs`（設定2層・永続化・自己生成）、`UfoSpawner.cs`（タイマー形式）、
`PoseTracker.cs`（`float now` を受ける純粋C#の形式）、
`SkeletonRenderer.TrySubscribe`（PoseEventBus の Awake 順への対処 — 新コンポーネントも同じ形にする）

---

## 実装順序（各段で単体に動作確認できる）

1. **Launcher の `LaunchRequest` 化**（挙動不変のリファクタ）＋ `Launch`/`LaunchForGesture` 公開 ＋ `ResolveCenterU` remap ＋
   座標マッピングの確認。Admin の既存テスト打上で回帰確認 【完了】
2. **GestureDetector**: ジャンプ再設計、`jumpRiseThreshold`、`handUpThreshold` 0.5、SettingsVersion。
   Director 無しで動く。ジャンプが反応するか実機で確認 【完了】
3. **Director 骨格 ＋ ComboTracker ＋ GestureEnsemble ＋ FireworkPlan ＋ 光跡の段階**。
   純粋クラスは EditMode テストが書ける 【完了】
4. **ComboNumberOverlay** 【完了】
5. **Admin 体験タブ（縮小版）**（Builder → Manager 実装済み。Unity Editor で
   「ARHanabi > Admin UI を再構築」の実行と保存が必要 ── 実装順序としては完了）

---

## 検証

**回帰（Director OFF）**
- 「体験演出」マスター OFF で、ジェスチャー→花火が従来どおり出ること（Launcher の従来経路）
- テスト打上・型指名テスト・画像花火のテストが従来どおり動くこと（LaunchRequest 化の回帰）

**子どもの基本ループ**
- ジャンプで花火が上がること（現状は上がらないはず）。しゃがんで立ち上がっても上がらないこと
- 手を肩の少し上まで上げれば反応すること（1.0 の高さまで要らない）
- 2人が離れて立ち、それぞれの位置から花火が上がること。端の人の花火が画面内に収まること
- 連続でジャンプ → 2〜4回目は軽い花火、5回目で大玉2発。数字「2！」〜「5！」がその人の上にポップすること。昇りのロケットが回数で太くなること
- 数字・光跡をそれぞれ管理画面で OFF にできること

**一緒に**
- 2人が同時（0.8秒以内）に両手を上げる → 各人の大玉2発のあと、0.3秒遅れて中間位置から特別な1発
- 3人 → ミニスターマイン
- `treatAnyHandUpAsSame` ON で片手＋両手でも成立すること

**⚠ 必ず確認すること**
1. 現場PCの PlayerPrefs（起動ログ `[Gesture] 保持時間 …` / `閾値（肩幅比）…` で実効値を確認。SettingsVersion で旧値を消す）
2. 腰 y の向き（既存の手上げ判定と同じ「小さい＝上」で揃えて実装済み。実機での符号確認はまだ）

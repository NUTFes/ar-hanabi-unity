using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

// ===== GestureDetector =====
// MediaPipe Pose のランドマークからジェスチャー（両手上げ／片手上げ／ジャンプ）を判定し、
// PoseEventBus 経由で花火の発射イベントを流す。
//
// ログについて（コードレビュー指摘 4.11）:
//   判定用の詳細ログは「毎フレーム × 最大5人」で出るため、以前は 1秒あたり1,000本超の
//   Debug.Log が発生していた（1本あたり10〜50µs なので実測で効くレベル）。
//   これらは ArLog.Verbose に置き換えてあり、AR_VERBOSE_LOG が未定義なら
//   呼び出しごと消える（文字列補間のコストもゼロになる）。
//   一方でジェスチャーが成立した瞬間のログは gestureCooldown で頻度が抑えられているため
//   Debug.Log のまま残している。
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。
//
// ── ⚠️ y は「下が 0 / 上が 1」（ここを間違えると全ての判定が裏返る）──
//   MediaPipe の landmark は本来「上が 0」だが、PoseLandmarkDetector が
//   WebCamTexture.GetPixels32()（下の行から並ぶ）をそのまま渡しているため、
//   表示映像に対しては上下が入れ替わり、Unity のスクリーン座標と同じ向きになる。
//   根拠と実機確認の経緯は PoseCoordinateUtil の冒頭コメントにある。
//
//   したがって:
//     ・「手を上げた」は wrist.y >  shoulderY + 閾値（小なりではない）
//     ・「立っている高さ」は窓内の y の最小値（最大値ではない）
//     ・「上昇量」は 今 − 基準（基準 − 今 ではない）
//
//   実際にこの3つがすべて逆に書かれていて、次の症状が出ていた。
//     ・腕を下ろしているだけで両手上げが成立する（手首は肩より下にあるため）。
//       成立しっぱなしなので発火はラッチで1回に抑えられ、
//       「新しい人が映るたびに1発上がる」「横に動いた／カメラに近づいただけで上がる」
//       ように見えていた（どちらもトラックIDが振り直される場面）。
//     ・本当に手を上げると条件が false になり、下ろした瞬間に成立し直す。
//       これが「ポーズを解いたあとに花火が上がる」の正体で、
//       かつては landmark の揺れが原因だと考えて holdGraceDuration を足していた。
//     ・ジャンプは上昇ではなく下降で成立していた（着地・しゃがみで発火）。
//
//   閾値を厳しくしても消えない類の誤検出はここを疑うこと。
//
// ── ジャンプ判定を作り直した理由（体験設計）──
//   旧実装は「1フレーム前との腰yの差分」（＝瞬間の上昇速度）で判定していた。
//   これは子どもの自然なジャンプでも簡単に閾値を割り込む一方、しゃがんで
//   ゆっくり立ち上がる動きとの区別がつかず、シーンの実効値（jumpThreshold=1）では
//   事実上ジャンプが反応しない状態になっていた。
//   そこで「直近しばらくの間でいちばん低かった位置（＝立っている高さ）から
//   どれだけ上がったか」を見る方式に変える。腰yのリングバッファを持ち、
//   基準（baseline）を「窓内での最大y（＝最も低い位置）」として、
//   そこからの上昇量で判定する。
//
// ── 手上げの閾値も見直した（体験設計）──
//   handUpThreshold の実効値（シーン保存値 1.0）は「手首が肩より肩幅1つ分上」を
//   要求しており、腕の短い子どもには物理的に届きにくい。既定を 0.5（肩幅の半分）に緩める。
//
// ── そのあと「緩めすぎ」を締め直した（この回の変更）──
//   現場で「ジャンプでも手上げでもない動きでぽんぽん打ち上がる」「ジャンプで
//   上げている感が薄い」という状態になった。原因は3つあり、順に効き目が大きい。
//
//     1. 見えていない関節の座標をそのまま使っていた
//        MediaPipe は関節が画面外でも隠れていても必ず座標を返し、確からしさは
//        visibility にしか出ない。手首が画面外に出たときの推測座標が肩より上に
//        来ると、手を上げていないのに手上げになる。カメラに近い子どもで頻発する。
//        → landmarkVisibility で、見えていない関節を使う判定を行わないようにした。
//
//     2. クールダウンをフレームの先頭で1回しか見ていなかった
//        ジャンプ判定 → 手上げ判定 の順に走るので、両手を上げながら跳ねると
//        同じフレームで両方成立し、1回のジェスチャーで花火が余分に上がっていた。
//        → 発射の直前に見る（CanFire）。1フレーム1発が構造的に保証される。
//
//     3. ジャンプの条件が跳ねていなくても満たせた
//        足首が見えないとチェックを丸ごと飛ばしていた（カメラに近いと足首は
//        画面外に出るので、いちばん緩い経路がいちばん起きやすかった）。
//        腰は歩く・しゃがむ・近づくだけでも動くので、これだけでは足りない。
//        → 高さ・速さ・足首の追随・連続フレーム数の4つで締める。
//
//   ⚠️ 締めるときは「閾値を上げる」より先に 1 を疑うこと。
//      閾値を上げると本物のジェスチャーまで通らなくなるが、
//      信頼度で弾くのは偽物にしか効かない（本物の通しやすさは変わらない）。
//
// ── ドーパミンモード中は「読む先」が変わる ──
//   DopamineModeController.LooseDetectionEnabled が true の間だけ、
//   判定は下の [SerializeField] ではなくモード側が持つもう1組の値を読む。
//   対象は 手上げ閾値 / ジャンプ高さ / クールダウン / 保持時間 /
//   片手の追加保持 / ジャンプ再武装 の6つで、すべて Eff〜 プロパティに集約してある。
//   判定もフィードバック配信も必ず Eff〜 を通すこと（下の「嘘の見た目」の注意を参照）。
//
//   ⚠️ landmarkVisibility と minShoulderWidth だけは緩めない（理由は各プロパティのコメント）。
//
// ── なぜ「モードONの間だけフィールドを上書きして、OFFで戻す」方式にしなかったのか ──
//   この2つの値は Admin 画面から調整され、PlayerPrefs に保存されて
//   複数日の展示をまたいで引き継がれる（下の「永続化」参照）。
//   上書き方式だと、ONのまま電源が落ちた／アプリが落ちた翌日、
//   緩めきった値が「保存された既定値」として残ったまま開場することになる。
//   しかもその状態は見た目に出ないので、現場では原因を特定しようがない。
//
//   「戻す処理」が存在する限り、戻し忘れと戻す前に落ちる事故は必ず起こりうる。
//   そこで保存値には最後まで一切触れず、読む瞬間に選ぶだけにした。
//   戻す処理が無ければ、その事故は原理的に起きない。
//   （同じ判断が DopamineModeController の冒頭にも書いてある）

public class GestureDetector : MonoBehaviour
{
    [Header("ジェスチャー判定設定")]
    [Tooltip("手が肩より何割上なら「上げた」と判定するか（肩幅に対する相対値）。\n" +
             "0.6 は「肩幅の6割」。子どもの短い腕でも真っすぐ伸ばせば届く高さ。\n" +
             "\n" +
             "── 0.5 から上げた理由 ──\n" +
             "「手上げでない動きでも打ち上がる」対策。ただし誤発火の主因は\n" +
             "閾値の高さより landmarkVisibility 側（画面外・隠れた手首の座標を\n" +
             "MediaPipe が推測で返す）なので、ここは上げすぎないこと。\n" +
             "1.0 は腕の短い子どもには物理的に届かない（それで 0.5 まで下げた経緯がある）")]
    [SerializeField] private float handUpThreshold    = 0.6f;

    [Tooltip("同じジェスチャーの連続発火を防ぐ秒数")]
    [SerializeField] private float gestureCooldown    = 2.0f;

    [Tooltip("ポーズを何秒維持したら発射するか。\n" +
             "この秒数に「達した瞬間」に発射する（ポーズを解いたときではない）。\n" +
             "\n" +
             "── 短くした理由（この回の変更）──\n" +
             "「ジェスチャーしたから花火が上がった」と結び付けて感じられるかは、\n" +
             "手を上げてから最初の反応（打ち上げ音＋昇りの光跡）までの時間で決まる。\n" +
             "ここに加えて検出パイプラインの遅れ（カメラ→読み戻し→MediaPipe推論、\n" +
             "30Hz で数十ms）が必ず乗るので、保持時間はできるだけ短く取る。\n" +
             "\n" +
             "短くしても誤発火が増えにくいのは、手上げの判定そのものが\n" +
             "handUpThreshold（肩幅比）で厳しく取ってあるため。\n" +
             "「たまたま手が上がる」ことがほぼ無いポーズなので、\n" +
             "長く保持させることで誤発火を防ぐ必要がない")]
    [SerializeField] private float poseHoldDuration   = 0.35f;

    [Tooltip("ポーズが一瞬途切れても保持を継続とみなす猶予[秒]。\n" +
             "\n" +
             "── これが無いと保持時間どおりに発射されない ──\n" +
             "判定は毎フレーム「手が上がっているか」を見て、上がっていなければ\n" +
             "保持タイマーを 0 に戻していた。ところが MediaPipe のランドマークは\n" +
             "小刻みに揺れるため、手首が閾値の境目にあると1フレームだけ\n" +
             "「下がった」と判定されることがある。そのたびにタイマーが振り出しに\n" +
             "戻るので、実際の発射は保持時間よりずっと遅く、しかも毎回ばらついていた\n" +
             "（＝「ポーズを解いたあとに上がった」ように見える正体はこれ）。\n" +
             "\n" +
             "検出の取りこぼし1〜数フレーム（30Hzなので0.03〜0.1秒）を\n" +
             "吸収できる程度にしておけばよい。長くしすぎると\n" +
             "本当にポーズを解いたあとにも発射されてしまう。\n" +
             "\n" +
             "発射後のラッチ解除にも同じ猶予を使っている。\n" +
             "こちらを猶予なしにすると、1フレームの取りこぼしでラッチが解けて\n" +
             "同じポーズのまま2発目が上がってしまう")]
    [SerializeField] private float holdGraceDuration  = 0.18f;

    [Tooltip("片手上げだけに足す追加の保持時間[秒]。\n" +
             "\n" +
             "── 保持時間を短くしたことで必要になった（この回の変更）──\n" +
             "両手を上げる動作は左右同時ではない。handUpThreshold は\n" +
             "「肩幅ぶん肩より上」という大きな動きなので、左右の手が判定を\n" +
             "満たす時刻は0.2〜0.4秒ずれることがある。\n" +
             "保持時間が1.0秒あったころはこのずれが埋もれていたが、\n" +
             "0.35秒に短くすると、両手を上げようとしたのに\n" +
             "先に上がった片手のほうが保持時間を満たして\n" +
             "「片手用の小玉が1発漏れる」ようになる。\n" +
             "\n" +
             "そこで片手側だけ少し待たせて、両手が揃うのを待つ余地を作る。\n" +
             "片手上げの実効保持時間は poseHoldDuration + この値。\n" +
             "両手上げ（大玉2発の主役ジェスチャー）は poseHoldDuration のまま最速。\n" +
             "\n" +
             "0 にすると従来どおり片手・両手が同じ保持時間になる")]
    [SerializeField] private float oneHandExtraHold   = 0.15f;

    [Tooltip("ジャンプの高さ判定（肩幅に対する相対値）。\n" +
             "直近0.8秒の中でいちばん低かった位置（＝立っている高さ）から\n" +
             "この割合ぶん腰が上がったらジャンプと判定する。\n" +
             "\n" +
             "── 0.35 から上げた理由 ──\n" +
             "「ジャンプでない動きでも打ち上がる」対策。腰は歩く・しゃがむ・\n" +
             "カメラに近づくだけでも動くので、本当に跳ねたときだけ超える高さにする。\n" +
             "\n" +
             "── 旧 jumpThreshold との違い ──\n" +
             "旧実装は「1フレーム前との差分」（瞬間の上昇速度）を見ていたが、\n" +
             "この値は「立っている高さからの絶対的な上昇量」を見る。意味が\n" +
             "まったく違うため、旧フィールドを流用せず別名にしてある\n" +
             "（保存済みの古い値が新しい意味で誤読されるのを防ぐため）")]
    [SerializeField] private float jumpRiseThreshold  = 0.45f;

    [Header("誤検出の除外")]
    [Tooltip("ランドマークの信頼度がこれ未満の関節は「見えていない」とみなし、\n" +
             "その関節を使う判定を行わない。\n" +
             "\n" +
             "── これが「ぽんぽん打ち上がる」の主因だった ──\n" +
             "MediaPipe は関節が画面外にあっても隠れていても、必ず座標を返す\n" +
             "（推測値に低い visibility が付くだけ）。手首が画面外に出ると\n" +
             "推測座標が肩より上に来ることがあり、手を上げていないのに\n" +
             "「手上げ」と判定されていた。カメラに近い子ども・見切れた人・\n" +
             "背景の人で特に起きやすい。\n" +
             "\n" +
             "0 にすると従来どおり信頼度を一切見なくなる。\n" +
             "\n" +
             "── 0.6 から下げた（この回の変更）──\n" +
             "0.6 は y 軸の向きが逆だったころ、その誤検出を打ち消すために付けた値。\n" +
             "本当の原因（比較の向き）を直した今、この強さは要らない。\n" +
             "MediaPipe は遠くの小さい人ほど信頼度を低く返すので、\n" +
             "厳しいままだと「画面にかなり近づかないと反応しない」状態になる。\n" +
             "画面外の関節はもっと低い値になるため、0.3 でも本来の目的は果たせる")]
    [SerializeField, Range(0f, 1f)] private float landmarkVisibility = 0.3f;

    [Tooltip("肩幅（正規化座標）がこれ未満の検出は判定に使わない。\n" +
             "\n" +
             "閾値はすべて肩幅に対する相対値なので、遠くの小さな人ほど\n" +
             "絶対量としての閾値が小さくなり、ランドマークの揺れだけで\n" +
             "条件を満たしてしまう。背景に小さく映り込んだ人がひとりでに\n" +
             "花火を上げるのを防ぐ。\n" +
             "\n" +
             "0.03 は画面幅の3%。1280x720 なら肩幅38px で、これ未満は\n" +
             "そもそも姿勢が当てにならない大きさ。\n" +
             "\n" +
             "── 0.05 から下げた（この回の変更）──\n" +
             "0.05 も y 軸の向きが逆だったころの誤検出対策で、今は要らない。\n" +
             "肩幅は距離に反比例するので、この値がそのまま「どこまで下がれるか」になる\n" +
             "（画角にもよるが、0.05 だと子どもは4m ほどで反応しなくなる）")]
    [SerializeField, Range(0.01f, 0.3f)] private float minShoulderWidth = 0.03f;

    // ── ジャンプ判定の内部定数 ──
    // Admin画面には出さない（現場で個別に触る値ではなく、判定アルゴリズムの
    // 一部として固定してある）。調整が要るとわかったら Inspector から変える

    // 腰・足首の「直近どれだけ低かったか」を見る窓の長さ。約0.8秒あれば、
    // 助走やしゃがみ込みを含む1回のジャンプ動作を確実に窓内に収められる
    private const float JumpBaselineWindowSeconds = 0.8f;

    // 基準（baseline）の計算から直近0.1秒を除外する。除外しないと、
    // ジャンプの立ち上がり自体がまだ窓の中に残っていて基準を汚し、
    // 「上がった量」が実際より小さく出てしまう
    private const float JumpBaselineExcludeRecentSeconds = 0.1f;

    // 「地面付近にいる」とみなす上昇量の比率（閾値に対する割合）。
    // 立ち上がり時間の計測開始と、発射後の再武装の両方で共通に使う
    private const float JumpGroundedRiseRatio = 0.3f;

    // 地面付近から閾値を超えるまでの時間がこれより長い場合は「ジャンプ」ではなく
    // 「ゆっくり立ち上がった」とみなして無視する。
    // 0.25 から下げた（跳ぶ動作は速い。ゆっくりした上下動を確実に落とすため）
    private const float JumpAscentSecondsMax = 0.20f;

    // 閾値を超えた状態が何回連続したら発火するか。
    //
    // ── 1フレームの跳ねを弾くために要る ──
    //   MediaPipe の腰座標は小刻みに揺れる。1フレームだけ閾値を超えた瞬間に
    //   撃つと、跳んでいないのに上がることがある。連続で超えたことを求めると
    //   ノイズはほぼ落ちる一方、遅れは推論1〜2回ぶん（30Hz で 30〜60ms）しか増えない。
    //   ジャンプの滞空時間（0.3秒前後）に対して十分短いので体感は変わらない
    private const int JumpRiseConsecutiveFrames = 2;

    // 発射後、再び地面付近に戻ってから次のジャンプを受け付けるまでの最短間隔。
    // これが無いと、着地の瞬間的な跳ね返り（バウンド）を2回目のジャンプとして
    // 拾ってしまうことがある
    private const float JumpRearmSeconds = 0.4f;

    // 足首の上昇量が腰の上昇量のこの割合を下回る場合は「しゃがんで立った」と
    // みなしてジャンプを無視する（足が地面についたまま腰だけ上がる動き）。
    //
    // 0.5 から上げた。本当に跳んでいれば足首は腰とほぼ同じだけ上がる（比は 1.0 に近い）。
    // 0.5 は「腰が上がった量の半分しか足が上がっていない」まで通してしまい、
    // 背伸び・かかと上げ・しゃがみからの立ち上がりが混ざっていた
    private const float AnkleRiseRatio = 0.7f;

    // 足首が見えないときに腰の上昇量へ掛ける割増。
    //
    // ── 足首チェックを「省略」から「割増」に変えた理由 ──
    //   以前は足首が見えなければチェックを丸ごと飛ばしていた。ところが
    //   カメラに近い子どもは足首が画面外に出るのが普通で、その状態では
    //   ジャンプ判定が腰の上昇量だけになり、いちばん緩い経路が
    //   いちばん起きやすいという逆転が起きていた。
    //   見えないときは代わりに腰の条件を厳しくして釣り合いを取る
    private const float JumpNoAnkleRiseMultiplier = 1.35f;

    // ── 設定のバージョン ──
    // handUpThreshold・gestureCooldown は名前を変えずに既定値を変えたため、
    // 現場PCの保存値がある場合はここで一度だけ消す（SettingsStore.DeleteKey 参照）。
    // jumpThreshold は新しいキー名（jumpRiseThreshold）に変わっているので
    // 自然に無効化される（消さなくても実害はないが、掃除のためまとめて消す）
    //
    // ── 3 に上げた理由 ──
    //   誤発火対策で handUpThreshold（0.5→0.6）と jumpRiseThreshold（0.35→0.45）の
    //   既定値を厳しくした。PlayerPrefs の保存値は Inspector の値より優先されるため、
    //   一度でも Admin 画面から触ったことのある現場PCでは、消さないと
    //   古い緩い値がそのまま使われ続けて「厳しくしたのに変わらない」ことになる
    private const int RequiredSettingsVersion = 3;

    // ── 永続化 ──
    // 会場・客層で毎回変えたくなる値なので、Admin画面（SETTINGS）から調整できる。
    // 展示は複数セッション・複数日にまたがって電源を落とすため、調整した値は
    // PlayerPrefs 経由で次回起動時にも引き継ぐ（SettingsStore 参照）。
    // キーが無い＝一度も Admin 画面から触っていない場合は、Inspector/シーンに
    // 保存されている値（= このフィールドの現在値）がそのまま使われる
    private void Awake()
    {
        if (SettingsStore.GetSettingsVersion() < RequiredSettingsVersion)
        {
            // 意味・既定値を変えた設定はここで一度だけ削除する。
            // 削除後は Inspector/シーンの新しい既定値がそのまま使われる
            SettingsStore.DeleteKey($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}");
            SettingsStore.DeleteKey($"{nameof(GestureDetector)}.jumpThreshold"); // 旧フィールド名（掃除用）
            SettingsStore.DeleteKey($"{nameof(GestureDetector)}.{nameof(jumpRiseThreshold)}");
            SettingsStore.DeleteKey($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}");
            SettingsStore.DeleteKey($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}");
            SettingsStore.SetSettingsVersion(RequiredSettingsVersion);
            Debug.Log($"[Gesture] 設定バージョンを {RequiredSettingsVersion} に更新し、旧保存値を削除しました");
        }

        handUpThreshold   = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}",   handUpThreshold);
        jumpRiseThreshold = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(jumpRiseThreshold)}", jumpRiseThreshold);
        gestureCooldown   = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}",   gestureCooldown);
        poseHoldDuration  = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}",  poseHoldDuration);

        // holdGraceDuration / oneHandExtraHold は Admin画面に出していない
        //（現場で触る値ではなく、判定アルゴリズムの内部的な定数に近い）ため永続化しない。
        // 調整が必要になったら Inspector から変える

        // 保持時間は PlayerPrefs が Inspector/シーンの値より優先されるので、
        // 「シーンを直したのに変わらない」を切り分けられるよう実効値をログに出す。
        // ジャンプ・手上げの実効閾値も、現場PCでの意図しない厳しさを切り分けられるよう併記する
        Debug.Log($"[Gesture] 保持時間 両手 {poseHoldDuration:F2}秒 / " +
                  $"片手 {poseHoldDuration + oneHandExtraHold:F2}秒 / " +
                  $"猶予 {holdGraceDuration:F2}秒 / 連発防止 {gestureCooldown:F2}秒");
        Debug.Log($"[Gesture] 閾値（肩幅比） 手上げ {handUpThreshold:F2} / ジャンプの高さ {jumpRiseThreshold:F2}");
        Debug.Log($"[Gesture] 誤検出の除外 関節の信頼度 {landmarkVisibility:F2}以上 / " +
                  $"肩幅 {minShoulderWidth:F2}以上 / " +
                  $"ジャンプは足首が腰の {AnkleRiseRatio:F2} 倍以上上がること" +
                  $"（足首が見えないときは腰の必要量を {JumpNoAnkleRiseMultiplier:F2} 倍に割増）");

        // 上の2行は「保存値」であって、常にこの値で判定されるとは限らない。
        // ドーパミンモード中だけ DopamineModeController 側の緩めた値に切り替わるので、
        // 現場で「ログの閾値と体感が合わない」となったときに真っ先に疑えるよう書いておく
        Debug.Log("[Gesture] 上記は通常時の値です。ドーパミンモード（検出ゆるめ）中は " +
                  "DopamineModeController の緩和値が使われます（保存値は書き換わりません）");
    }

    // ── Admin画面（SETTINGS）からの調整用 ──
    // set のたびに PlayerPrefs へ保存する。頻繁に呼ばれる値ではない
    // （ボタンクリック時のみ）ので、毎回 Save() を呼ぶコストは無視できる
    public float HandUpThreshold
    {
        get => handUpThreshold;
        set { handUpThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}", value); }
    }

    /// <summary>ジャンプの高さ判定（肩幅比）。旧 JumpThreshold の後継（意味が違うので別名）</summary>
    public float JumpRiseThreshold
    {
        get => jumpRiseThreshold;
        set { jumpRiseThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(jumpRiseThreshold)}", value); }
    }

    public float GestureCooldown
    {
        get => gestureCooldown;
        set { gestureCooldown = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}", value); }
    }

    public float PoseHoldDuration
    {
        get => poseHoldDuration;
        set { poseHoldDuration = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}", value); }
    }

    // ── 実効値プロパティ層 ──
    //
    // 判定に使う値は必ずここを通す。上の保存値（[SerializeField] + Admin の setter）は
    // ドーパミンモードが何をしても書き換わらない。読む瞬間にどちらを見るかが変わるだけ。
    //
    // ── なぜ毎回引くのか（キャッシュしないのか）──
    //   Admin 画面のトグルは判定の実行中（＝お客さんが目の前でポーズを取っている最中）に
    //   押される。イベントで配って各所にキャッシュさせると、配り漏れた場所だけが
    //   古い値のまま動き、「ONにしたのにジャンプだけ緩くならない」類の症状になる。
    //   1フレームあたり数回の分岐で済む値なので、毎回引くほうが確実。
    //
    // ── Instance が null でも動くこと ──
    //   DopamineModeController は RuntimeInitializeOnLoadMethod で自動生成されるが、
    //   生成前の1フレームや、テスト・シーン単体再生では null になりうる。
    //   その場合は常に保存値側（＝通常の判定）に倒れる。
    private static DopamineModeController Dopa => DopamineModeController.Instance;
    private static bool Loose => Dopa != null && Dopa.LooseDetectionEnabled;

    private float EffHandUpThreshold   => Loose ? Dopa.RelaxHandUpThreshold   : handUpThreshold;
    private float EffJumpRiseThreshold => Loose ? Dopa.RelaxJumpRiseThreshold : jumpRiseThreshold;
    private float EffGestureCooldown   => Loose ? Dopa.RelaxCooldown          : gestureCooldown;
    private float EffPoseHoldDuration  => Loose ? Dopa.RelaxPoseHold          : poseHoldDuration;
    private float EffOneHandExtraHold  => Loose ? Dopa.RelaxOneHandExtraHold  : oneHandExtraHold;

    // JumpRearmSeconds は Admin に出していない private const（定数側のコメントに
    // 「着地のバウンドを2回目のジャンプとして拾わないため」という理由が書いてある）。
    // const のまま残し、非上書き側でそれを返す
    private float EffJumpRearmSeconds  => Loose ? Dopa.RelaxJumpRearm         : JumpRearmSeconds;

    // ── ⚠️ landmarkVisibility と minShoulderWidth には実効値層を作らない ──
    //
    //   この2つは「閾値」ではなく「そもそもこの関節・この検出を信用してよいか」の足切り。
    //   緩める対象の6つが「どれくらいの動きでジェスチャーとみなすか」を決めているのに対し、
    //   こちらは「その座標が実在の人のものか」を決めている。意味の階層が違う。
    //
    //   ここを 0 に振ると、MediaPipe が画面外・隠れた関節に対して返す推測座標や、
    //   背景に小さく映り込んだ人の揺れがそのまま判定に流れ込む。結果として、
    //   誰もポーズを取っていない（極端には誰も居ない）のに花火が上がり続ける。
    //
    //   ドーパミンモードは「振り切った気持ちよさ」のための飛び道具だが、その気持ちよさは
    //   「自分がやったから上がった」という因果が成立していることの上に乗っている。
    //   勝手に上がる花火は、緩いのではなく壊れている。どれだけ振り切っても
    //   この因果だけは壊してはいけない、というのがここを緩めない理由。
    //
    //   緩めたくなったら、判定側ではなくカメラの位置・画角を疑うこと。

    // ── Admin 画面用の公開 API ──
    // Admin は保存値（上の setter 付きプロパティ）をスライダーで編集しつつ、
    // ドーパミンモード中は「実際にはこの値で判定されている」をラベルに併記する。
    // これが無いと、モード中にスライダーを動かしても挙動が変わらないように見えてしまう

    /// <summary>ドーパミンモードの制限解除が効いているか。Admin がラベルに実効値を併記するために読む</summary>
    public bool  DetectionOverridden        => Loose;
    public float EffectiveHandUpThreshold   => EffHandUpThreshold;
    public float EffectiveJumpRiseThreshold => EffJumpRiseThreshold;
    public float EffectiveGestureCooldown   => EffGestureCooldown;
    public float EffectivePoseHoldDuration  => EffPoseHoldDuration;

    // ── 人ごとの判定状態 ──
    private class PersonState
    {
        public float lastGestureTime      = -999f;

        public float bothHandsUpStartTime = -1f;
        public float oneHandUpStartTime   = -1f;

        // ポーズが途切れた時刻。-1 は「途切れていない」。
        // holdGraceDuration を過ぎるまでは保持の継続とみなす（下の ReleaseHold 参照）
        public float bothHandsLostTime    = -1f;
        public float oneHandLostTime      = -1f;

        public bool bothHandsFired        = false;
        public bool oneHandFired          = false;

        // ── ジャンプ判定用 ──
        // 腰・足首の (時刻, y) 履歴。JumpBaselineWindowSeconds を超えて古いものは
        // 毎フレーム先頭から取り除く（Queue なので O(1)）
        public readonly Queue<(float time, float y)> hipHistory   = new();
        public readonly Queue<(float time, float y)> ankleHistory = new();

        // 直近で「地面付近（rise が閾値の JumpGroundedRiseRatio 未満）」にいた時刻。
        // ここが更新され続けている間は「まだジャンプの立ち上がりが始まっていない」を意味し、
        // 閾値を超えた瞬間にこの時刻からの経過時間で「素早い動きか」を判定する
        public float jumpGroundedTime = -1f;

        // 発射済みなら次のジャンプを受け付けない（着地して再武装するまで）
        public bool  jumpArmed        = true;
        public float jumpFiredTime    = -999f;

        // 必要な高さを連続で超えた回数。途切れたら 0 に戻る
        //（1フレームだけのノイズで撃たないため。JumpRiseConsecutiveFrames 参照）
        public int   jumpRiseFrames   = 0;

        // ── 診断用 ──
        // 同じフレームで2回発火していないかを見るための記録。
        // クールダウンを発射直前に見る（CanFire）ようにして解消済みだが、
        // 「1回のジェスチャーなのに花火が多い」の実際の原因だったので見張りを残す
        public int         lastFireFrame   = -1;
        public GestureType lastFireGesture;
    }

    private readonly Dictionary<int, PersonState> _personStates = new();

    // ── ランドマーク処理（毎フレーム × 人数分呼ばれる）──
    // personIndex: 何人目か（PoseTracker が振る安定した trackId が渡される想定）
    public void ProcessLandmarks(int personIndex, List<NormalizedLandmark> landmarks)
    {
        if (landmarks == null || landmarks.Count < 29) return;

        if (!_personStates.TryGetValue(personIndex, out var state))
        {
            state = new PersonState();
            _personStates[personIndex] = state;
        }

        var leftShoulder  = landmarks[11];
        var rightShoulder = landmarks[12];
        var leftWrist     = landmarks[15];
        var rightWrist    = landmarks[16];
        var leftHip       = landmarks[23];
        var rightHip      = landmarks[24];
        var leftAnkle     = landmarks[27];
        var rightAnkle    = landmarks[28];

        var centerX   = (leftHip.x + rightHip.x) / 2f;
        var centerY   = (leftHip.y + rightHip.y) / 2f;
        var screenPos = new Vector2(centerX, centerY);

        float now = Time.time;

        // ── 肩幅を基準にスケールを計算 ──
        float shoulderWidth = Mathf.Abs(leftShoulder.x - rightShoulder.x);

        // 小さすぎる（＝遠い、または検出が不安定な）人は判定に使わない。
        // 閾値がすべて肩幅比なので、小さいほど絶対量としての閾値が小さくなり、
        // ランドマークの揺れだけで条件を満たしてしまう
        if (shoulderWidth < minShoulderWidth)
        {
            LogSkipped($"P{personIndex} 肩幅 {shoulderWidth:F3} が下限 {minShoulderWidth:F3} 未満", now);
            return;
        }

        // 肩・腰が見えていない検出はそもそも判定に使わない。
        // これらは手上げ・ジャンプの両方で基準に使うので、ここで一括して弾く
        if (!IsVisible(leftShoulder) || !IsVisible(rightShoulder) ||
            !IsVisible(leftHip)      || !IsVisible(rightHip))
        {
            LogSkipped($"P{personIndex} 肩または腰の信頼度が下限 {landmarkVisibility:F2} 未満", now);
            return;
        }

        // 閾値を肩幅に対する相対値で計算。
        // ドーパミンモード中はここで引かれる値だけが緩い方に差し替わる
        //（肩幅で正規化する構造自体は変えない。緩めても「遠い人ほど絶対量が小さい」は正しい）
        float dynamicHandUpThreshold = shoulderWidth * EffHandUpThreshold;
        float dynamicJumpThreshold   = shoulderWidth * EffJumpRiseThreshold;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} shoulderWidth={shoulderWidth:F3} " +
                      $"handUpThreshold={dynamicHandUpThreshold:F3} " +
                      $"jumpRiseThreshold={dynamicJumpThreshold:F3}");

        // ── ジャンプ判定 ──
        // 「立っている高さ（直近しばらくでいちばん低かった位置）からどれだけ上がったか」を見る。
        // 詳しい理由はクラス冒頭のコメントと各定数のコメントを参照
        float hipY   = (leftHip.y + rightHip.y) / 2f;
        float ankleY = (leftAnkle.y + rightAnkle.y) / 2f;
        UpdateHistory(state.hipHistory, now, hipY);
        // 足首の履歴も毎フレーム積む（ジャンプ判定が走る瞬間だけ積むと、
        // その瞬間には直近0.1秒しかデータが無く基準を計算できないため）
        UpdateHistory(state.ankleHistory, now, ankleY);

        // ── クールダウン中も状態機械は回し続ける ──
        //   以前はここを丸ごと canFire で囲っていたため、クールダウン中は
        //   jumpGroundedTime が更新されず、明けた直後に「地面付近にいた時刻」が
        //   古いまま残って立ち上がりの速さを誤判定していた。
        //   発射できるかは撃つ直前（CanFire）だけで見る
        float baseline = ComputeBaseline(state.hipHistory, now);
        if (baseline >= 0f)
        {
            // y は上が大きいので、上昇量は「今 − 基準」。
            // 逆にすると下降量になり、着地やしゃがみでジャンプ判定が出る
            float rise = hipY - baseline;

            // 足首が見えていれば「腰と一緒に足も上がったか」で跳ねたことを確かめられる。
            // 見えていないときは確かめようがないので、代わりに腰の条件を厳しくする
            //（JumpNoAnkleRiseMultiplier のコメント参照）
            bool  ankleVisible = IsVisible(leftAnkle) && IsVisible(rightAnkle);
            float requiredRise = dynamicJumpThreshold *
                                 (ankleVisible ? 1f : JumpNoAnkleRiseMultiplier);

            // 地面付近にいる間は基準時刻を更新し続ける。閾値を超えた瞬間、
            // この時刻からの経過時間が「立ち上がりの速さ」になる
            if (rise <= requiredRise * JumpGroundedRiseRatio || state.jumpGroundedTime < 0f)
                state.jumpGroundedTime = now;

            bool fastEnough = (now - state.jumpGroundedTime) <= JumpAscentSecondsMax;
            bool highEnough = rise > requiredRise;

            // 1フレームだけの跳ねを弾く。連続して超えた回数を数え、
            // 割り込んだら 0 に戻す（JumpRiseConsecutiveFrames のコメント参照）
            state.jumpRiseFrames = highEnough && fastEnough ? state.jumpRiseFrames + 1 : 0;

            // 毎フレーム出るので Verbose（実機での符号確認・閾値調整用）
            ArLog.Verbose($"[Pose] P{personIndex} 腰baseline={baseline:F3} hipY={hipY:F3} " +
                          $"rise={rise:F3} 必要={requiredRise:F3} fast={fastEnough} " +
                          $"連続={state.jumpRiseFrames} 足首見えてる={ankleVisible}");

            if (state.jumpArmed
                && state.jumpRiseFrames >= JumpRiseConsecutiveFrames
                && (!ankleVisible || PassesAnkleCheck(state, now, ankleY, rise))
                && CanFire(state, now))
            {
                FireGesture(personIndex, GestureType.Jump, screenPos, state);
                state.jumpArmed      = false;
                state.jumpFiredTime  = now;
                state.jumpRiseFrames = 0;
            }
            else if (!state.jumpArmed
                     && rise < requiredRise * JumpGroundedRiseRatio
                     && (now - state.jumpFiredTime) >= EffJumpRearmSeconds)
            {
                state.jumpArmed = true;
            }
        }

        // ── 手上げ判定 ──
        //
        // ⚠️ 手首は「見えている」ことを必ず確かめてから使う。
        //   MediaPipe は手首が画面外でも隠れていても座標を返す（推測値に低い
        //   visibility が付くだけ）。カメラに近い子どもは手を上げた瞬間に手首が
        //   画面の外へ出るし、体の後ろに回した手も推測になる。その推測座標が
        //   肩より上に来ると、手を上げていないのに「手上げ」と判定されていた。
        //   これが「ぽんぽん打ち上がる」のいちばん大きな原因
        float shoulderY   = (leftShoulder.y + rightShoulder.y) / 2f;
        bool  leftWristVisible  = IsVisible(leftWrist);
        bool  rightWristVisible = IsVisible(rightWrist);
        bool  leftHandUp  = leftWristVisible  && leftWrist.y  > (shoulderY + dynamicHandUpThreshold);
        bool  rightHandUp = rightWristVisible && rightWrist.y > (shoulderY + dynamicHandUpThreshold);

        // ── 手首だけ弾かれている状態を無音にしない ──
        //   肩と腰は見えているので人としては処理され続けるが、手首が両方とも
        //   信頼度の下限を割っていると、どれだけ手を上げても永久に成立しない。
        //   ログに何も出ないまま「反応しない」だけになるのがいちばん困るので、
        //   理由と実測値を残す（下限を下げる判断がログだけでできるように）
        if (!leftWristVisible && !rightWristVisible)
        {
            LogSkipped($"P{personIndex} 手首の信頼度が下限 {landmarkVisibility:F2} 未満" +
                       $"（左 {(leftWrist.visibility ?? 1f):F2} / 右 {(rightWrist.visibility ?? 1f):F2}）", now);
        }
        bool  bothHandsUp = leftHandUp && rightHandUp;
        bool  oneHandUp   = leftHandUp ^ rightHandUp;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} " +
                      $"shoulderY={shoulderY:F2} " +
                      $"leftWristY={leftWrist.y:F2}(見えてる={IsVisible(leftWrist)}) " +
                      $"rightWristY={rightWrist.y:F2}(見えてる={IsVisible(rightWrist)}) " +
                      $"leftUp={leftHandUp} rightUp={rightHandUp} " +
                      $"canFire={CanFire(state, now)}");

        // ── 手上げの発射判定 ──
        //
        // ここは「保持時間に達した瞬間」に撃つ（ポーズを解いたときではない）。
        //   heldDuration >= poseHoldDuration を満たした最初のフレームで FireGesture し、
        //   *Fired ラッチを立てて同じポーズ中の連射を止める。
        //   ラッチはポーズを（猶予を超えて）解いたときにだけ戻るので、
        //   1回の手上げにつき1発になる。
        //
        // 保持が途切れたときのリセットは ReleaseHold に切り出してある。
        // ここで「1フレームでも下がったら即リセット」にしていたのが、
        // 保持時間どおりに発射されない原因だった（holdGraceDuration のコメント参照）。

        // 両手上げ
        if (bothHandsUp)
        {
            if (state.bothHandsUpStartTime < 0f)
                state.bothHandsUpStartTime = now;

            state.bothHandsLostTime = -1f;

            // 両手が上がっている間は片手側の判定を完全に打ち消す。
            // 猶予を残すと「両手を上げ切る途中の片手状態」が生き続けて
            // 両手より先に片手が発射してしまう
            state.oneHandUpStartTime = -1f;
            state.oneHandLostTime    = -1f;
            state.oneHandFired       = false;

            float heldDuration = now - state.bothHandsUpStartTime;
            // ポーズを維持している間ずっと出るので Verbose
            ArLog.Verbose($"[Pose] P{personIndex} 両手上げ中: {heldDuration:F2}秒 / {EffPoseHoldDuration}秒");

            if (heldDuration >= EffPoseHoldDuration && !state.bothHandsFired && CanFire(state, now))
            {
                FireGesture(personIndex, GestureType.BothHandsUp, screenPos, state);
                state.bothHandsFired = true;
            }
        }
        else
        {
            ReleaseHold(now, ref state.bothHandsUpStartTime,
                             ref state.bothHandsLostTime,
                             ref state.bothHandsFired);
        }

        // 片手上げ
        if (oneHandUp && !bothHandsUp)
        {
            if (state.oneHandUpStartTime < 0f)
                state.oneHandUpStartTime = now;

            state.oneHandLostTime = -1f;

            // 片手だけは両手が揃うのを待つ余地を作るために少し長く保持させる
            //（oneHandExtraHold のコメント参照）
            float oneHandHold  = EffPoseHoldDuration + Mathf.Max(0f, EffOneHandExtraHold);
            float heldDuration = now - state.oneHandUpStartTime;
            // ポーズを維持している間ずっと出るので Verbose
            ArLog.Verbose($"[Pose] P{personIndex} 片手上げ中: {heldDuration:F2}秒 / {oneHandHold}秒");

            if (heldDuration >= oneHandHold && !state.oneHandFired && CanFire(state, now))
            {
                FireGesture(personIndex, GestureType.OneHandUp, screenPos, state);
                state.oneHandFired = true;
            }
        }
        else if (!bothHandsUp)
        {
            // 両手上げ中は上の分岐が片手側を明示的に打ち消しているので、
            // ここで猶予つきリセットを走らせない（走らせると打ち消しが
            // 猶予のあいだ効かず、両手の直前に片手が漏れて発射する）
            ReleaseHold(now, ref state.oneHandUpStartTime,
                             ref state.oneHandLostTime,
                             ref state.oneHandFired);
        }

        // ── 状態の配信 ──
        // 優先順位は Cooldown > Charging > Idle。
        // クールダウン中は canFire が false で実際には発射できないため、
        // 保持中の見た目を出すと「溜まっているのに撃てない」という嘘になる。
        //
        // ⚠️ ここも必ず Eff〜（実効値）で割ること。
        //   見た目の進捗と発射条件は同じ分母でなければならない。保存値のまま残すと、
        //   ドーパミンモード中だけ「ゲージは満タンなのに撃てない」（保存値のほうが緩い場合）／
        //   「一瞬で撃つのにゲージが追いつかない」（モードのほうが緩い＝通常こちら）という、
        //   判定は正しいのに見た目だけが嘘をつく状態になる。
        //   溜めゲージは「あと何秒で上がるか」を伝える部品なので、ここが嘘だと
        //   モードの緩さが体感として伝わらないどころか、操作感が壊れて見える
        var feedback = new PoseFeedback { trackId = personIndex };

        if (!CanFire(state, now))
        {
            feedback.state    = PoseFeedbackState.Cooldown;
            feedback.progress = EffGestureCooldown <= 0f
                ? 0f
                : 1f - Mathf.Clamp01((now - state.lastGestureTime) / EffGestureCooldown);
        }
        else if (state.bothHandsUpStartTime >= 0f)
        {
            feedback.state    = PoseFeedbackState.Charging;
            feedback.gesture  = GestureType.BothHandsUp;
            feedback.progress = EffPoseHoldDuration <= 0f
                ? 1f
                : Mathf.Clamp01((now - state.bothHandsUpStartTime) / EffPoseHoldDuration);
        }
        else if (state.oneHandUpStartTime >= 0f)
        {
            // 片手側は実効保持時間（保持時間 + 片手の追加保持）で割る。
            // 発射条件と同じ分母にしないと、進捗が満タンなのに撃たない状態ができる
            float oneHandHold = EffPoseHoldDuration + Mathf.Max(0f, EffOneHandExtraHold);

            feedback.state    = PoseFeedbackState.Charging;
            feedback.gesture  = GestureType.OneHandUp;
            feedback.progress = oneHandHold <= 0f
                ? 1f
                : Mathf.Clamp01((now - state.oneHandUpStartTime) / oneHandHold);
        }
        else
        {
            feedback.state = PoseFeedbackState.Idle;
        }

        PoseEventBus.Instance?.ReportFeedback(feedback);
    }

    // ── 判定に使わなかった検出の通知（間引き）──
    //
    // ── 完全に無音にはしない ──
    //   信頼度・肩幅で弾くのは「反応しない」方向の失敗なので、黙って捨てると
    //   現場では原因不明の不調にしか見えない（閾値を厳しくしすぎたのか、
    //   カメラが映っていないのか、区別がつかない）。
    //   毎フレーム × 人数分そのまま出すとログが埋まるので、秒単位に間引く。
    //   AR_VERBOSE_LOG を定義すれば、間引き前の毎フレームの値も見られる
    private const float SkipLogIntervalSeconds = 3f;
    private float _lastSkipLogTime = -999f;

    private void LogSkipped(string reason, float now)
    {
        ArLog.Verbose($"[Pose] {reason} のため判定しません");

        if (now - _lastSkipLogTime < SkipLogIntervalSeconds) return;
        _lastSkipLogTime = now;

        Debug.Log($"[Gesture] {reason} のため判定から除外しました" +
                  "（誰も反応しないときは landmarkVisibility / minShoulderWidth を下げる）");
    }

    // ── 発射できるか（クールダウン）──
    //
    // ⚠️ 必ず「発射する直前」に呼ぶこと。
    //   以前はフレームの先頭で1回だけ計算した bool をジャンプ判定・手上げ判定の
    //   両方で使い回していた。FireGesture が lastGestureTime を更新しても
    //   その bool は false にならないので、両手を上げながら跳ねると
    //   Jump と BothHandsUp が同じフレームで両方発火し、花火が余分に上がっていた。
    //   毎回ここを通せば、先に成立したほうだけが撃ち、もう一方は
    //   クールダウンで確実に落ちる（1フレーム1発が構造的に保証される）
    //
    // ドーパミンモード中はクールダウンが 0 まで落ちうるが、ここは `>` で比較しているので
    // 0 でも「同じフレームで2発」にはならない（now - lastGestureTime が 0 のため）。
    // 1フレーム1発の保証は閾値ではなく比較の向きが担保している
    private bool CanFire(PersonState state, float now)
        => (now - state.lastGestureTime) > EffGestureCooldown;

    // ── ランドマークが信用できるか ──
    //
    // MediaPipe は関節が画面外・隠れている場合でも座標を返し、確からしさは
    // visibility にだけ現れる。visibility を見ずに座標を使うと、
    // 「推測で置かれた手首」で手上げ判定が通ってしまう。
    // visibility が null の実装・モデルもあるため、その場合は見えている扱いにする
    //（従来の足首チェックと同じ方針）
    private bool IsVisible(NormalizedLandmark landmark)
        => (landmark.visibility ?? 1f) >= landmarkVisibility;

    // ── 履歴の更新 ──
    // 今回のサンプルを積み、JumpBaselineWindowSeconds を超えて古いものを取り除く。
    // Queue なので先頭（最古）からの除去が O(1)
    private static void UpdateHistory(Queue<(float time, float y)> history, float now, float y)
    {
        history.Enqueue((now, y));
        while (history.Count > 0 && now - history.Peek().time > JumpBaselineWindowSeconds)
            history.Dequeue();
    }

    // ── 基準（baseline）の計算 ──
    // 窓内（直近 JumpBaselineWindowSeconds 秒）のうち、直近 JumpBaselineExcludeRecentSeconds 秒を
    // 除いた範囲でいちばん小さい y（＝いちばん低い位置）を返す。該当データがなければ -1。
    //
    // ⚠️ y は「下が 0 / 上が 1」。だから「いちばん低い位置」は最小値であって最大値ではない
    //    （向きの根拠は PoseCoordinateUtil の冒頭コメント）。
    //    ここを最大値で取っていたため、rise が上昇量ではなく下降量になっていた
    private static float ComputeBaseline(Queue<(float time, float y)> history, float now)
    {
        float baseline = float.MaxValue;
        foreach (var (t, y) in history)
        {
            if (now - t < JumpBaselineExcludeRecentSeconds) continue;
            if (y < baseline) baseline = y;
        }
        // 「データ無し」は -1 で表す。y は 0〜1 なので実データと混ざらない
        return baseline == float.MaxValue ? -1f : baseline;
    }

    // ── 足首チェック ──
    // 腰は上がっているが足首がほとんど上がっていない（＝しゃがんで立った・背伸びした）を除外する。
    // 本当に跳んでいれば足首は腰とほぼ同じだけ上がる。
    //
    // 呼び出し側が「足首が見えている」ことを確認済みの前提。見えていない場合は
    // ここを呼ばず、代わりに腰の必要上昇量を割り増している（JumpNoAnkleRiseMultiplier）。
    // 履歴は毎フレーム（ProcessLandmarks 側で）積んである前提で、ここでは読むだけ
    private static bool PassesAnkleCheck(PersonState state, float now, float ankleY, float hipRise)
    {
        float ankleBaseline = ComputeBaseline(state.ankleHistory, now);
        if (ankleBaseline < 0f) return true; // データ不足時は速度条件のみに委ねる

        // 腰と同じ向きで測る（y は上が大きいので「今 − 基準」が上昇量）
        float ankleRise = ankleY - ankleBaseline;
        return ankleRise > hipRise * AnkleRiseRatio;
    }

    // ── 保持の解除（猶予つき）──
    //
    // ポーズが見えなくなったフレームで即リセットせず、holdGraceDuration を
    // 過ぎてから初めて保持タイマーとラッチを戻す。
    // MediaPipe のランドマークは小刻みに揺れるので、手首が閾値の境目にあると
    // 1フレームだけ「下がった」と判定されることがある。即リセットすると
    // そのたびにタイマーが振り出しに戻り、保持時間どおりに発射されない。
    //
    // まだ一度も上がっていない（startTime < 0 かつ未発射）ときは何もしない。
    // ここで lostTime を立ててしまうと、ポーズをしていない間ずっと
    // 猶予の判定が走り続けることになる。
    private void ReleaseHold(float now, ref float startTime, ref float lostTime, ref bool fired)
    {
        if (startTime < 0f && !fired)
        {
            lostTime = -1f;
            return;
        }

        if (lostTime < 0f) lostTime = now;

        if (now - lostTime > holdGraceDuration)
        {
            startTime = -1f;
            lostTime  = -1f;
            fired     = false;
        }
    }

    public void RemovePerson(int personIndex)
    {
        _personStates.Remove(personIndex);
    }

    // ── 発射 ──
    private void FireGesture(
        int personIndex, GestureType gesture,
        Vector2 screenPos, PersonState state)
    {
        float  now       = Time.time;
        string sinceText = state.lastGestureTime < 0f
            ? "初回"
            : $"{now - state.lastGestureTime:F2}秒";

        // ── 同一フレームでの二重発火の検出（見張り）──
        //   クールダウンを発射直前に見る（CanFire）ようにしたので、ここは
        //   もう起きないはず。ただし「両手を上げながら跳ねると Jump と
        //   BothHandsUp が同じフレームで両方成立して花火が余分に上がる」という
        //   実際に起きた不具合なので、再発したら黙って戻らないよう見張りを残す
        int frame = Time.frameCount;
        if (state.lastFireFrame == frame)
        {
            Debug.LogWarning(
                $"[Gesture] Person{personIndex}: 同じフレームで2回発火しました " +
                $"（{state.lastFireGesture} → {gesture}）。" +
                "クールダウンはフレーム先頭で判定しているため、同一フレーム内では効きません。" +
                "1回のジェスチャーで花火が余分に上がる原因になります");
        }
        state.lastFireFrame   = frame;
        state.lastFireGesture = gesture;

        state.lastGestureTime = now;

        if (PoseEventBus.Instance == null)
        {
            ArLog.Warn("[Gesture] PoseEventBus が存在しないためイベントを発行できません");
            return;
        }

        PoseEventBus.Instance.FireGesture(personIndex, gesture, screenPos);

        // クールダウンが効くので低頻度。運用上必要なログなので常時出す。
        // 前回発火からの間隔も出す（クールダウンが効いているかをログだけで確認できる）。
        //
        // ⚠️ 実効値を出すこと。保存値を出すと、ドーパミンモード中に
        //   「連発防止 2.00秒」と書きながら 0.1秒間隔で発火しているログが並び、
        //   クールダウンが壊れているようにしか読めなくなる。
        //   モード中はその旨も添えて、ログだけで原因の切り分けができるようにする
        Debug.Log($"[Gesture] Person{personIndex}: {gesture}（前回発火から {sinceText} / " +
                  $"連発防止 {EffGestureCooldown:F2}秒{(Loose ? "・ドーパミンモード" : "")}）");
    }
}

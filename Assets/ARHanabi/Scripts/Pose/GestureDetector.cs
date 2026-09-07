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

public class GestureDetector : MonoBehaviour
{
    [Header("ジェスチャー判定設定")]
    [Tooltip("手が肩より何割上なら「上げた」と判定するか（肩幅に対する相対値）")]
    [SerializeField] private float handUpThreshold    = 0.15f;

    [Tooltip("ジャンプ判定の閾値（肩幅に対する相対値。大きいほど誤検知しにくい）")]
    [SerializeField] private float jumpThreshold      = 0.06f;

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

    // ── 永続化 ──
    // 会場・客層で毎回変えたくなる値なので、Admin画面（SETTINGS）から調整できる。
    // 展示は複数セッション・複数日にまたがって電源を落とすため、調整した値は
    // PlayerPrefs 経由で次回起動時にも引き継ぐ（SettingsStore 参照）。
    // キーが無い＝一度も Admin 画面から触っていない場合は、Inspector/シーンに
    // 保存されている値（= このフィールドの現在値）がそのまま使われる
    private void Awake()
    {
        handUpThreshold  = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}",  handUpThreshold);
        jumpThreshold    = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(jumpThreshold)}",    jumpThreshold);
        gestureCooldown  = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}",  gestureCooldown);
        poseHoldDuration = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}", poseHoldDuration);

        // holdGraceDuration は Admin画面に出していない（現場で触る値ではなく、
        // 検出の取りこぼしを吸収するための内部的な定数に近い）ため永続化しない。
        // 調整が必要になったら Inspector から変える

        // 保持時間は PlayerPrefs が Inspector/シーンの値より優先されるので、
        // 「シーンを直したのに変わらない」を切り分けられるよう実効値をログに出す
        Debug.Log($"[Gesture] 保持時間 両手 {poseHoldDuration:F2}秒 / " +
                  $"片手 {poseHoldDuration + oneHandExtraHold:F2}秒 / " +
                  $"猶予 {holdGraceDuration:F2}秒 / 連発防止 {gestureCooldown:F2}秒");
    }

    // ── Admin画面（SETTINGS）からの調整用 ──
    // set のたびに PlayerPrefs へ保存する。頻繁に呼ばれる値ではない
    // （ボタンクリック時のみ）ので、毎回 Save() を呼ぶコストは無視できる
    public float HandUpThreshold
    {
        get => handUpThreshold;
        set { handUpThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}", value); }
    }

    public float JumpThreshold
    {
        get => jumpThreshold;
        set { jumpThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(jumpThreshold)}", value); }
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

    // ── 人ごとの判定状態 ──
    private class PersonState
    {
        public float prevHipY             = -1f;
        public float lastGestureTime      = -999f;

        public float bothHandsUpStartTime = -1f;
        public float oneHandUpStartTime   = -1f;

        // ポーズが途切れた時刻。-1 は「途切れていない」。
        // holdGraceDuration を過ぎるまでは保持の継続とみなす（下の ReleaseHold 参照）
        public float bothHandsLostTime    = -1f;
        public float oneHandLostTime      = -1f;

        public bool bothHandsFired        = false;
        public bool oneHandFired          = false;
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

        var centerX   = (leftHip.x + rightHip.x) / 2f;
        var centerY   = (leftHip.y + rightHip.y) / 2f;
        var screenPos = new Vector2(centerX, centerY);

        float now     = Time.time;
        bool  canFire = (now - state.lastGestureTime) > gestureCooldown;

        // ── 肩幅を基準にスケールを計算 ──
        float shoulderWidth = Mathf.Abs(leftShoulder.x - rightShoulder.x);
        // 肩幅が極端に小さい場合（検出不安定）はスキップ
        if (shoulderWidth < 0.01f) return;

        // 閾値を肩幅に対する相対値で計算
        float dynamicHandUpThreshold = shoulderWidth * handUpThreshold;
        float dynamicJumpThreshold   = shoulderWidth * jumpThreshold;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} shoulderWidth={shoulderWidth:F3} " +
                      $"handUpThreshold={dynamicHandUpThreshold:F3} " +
                      $"jumpThreshold={dynamicJumpThreshold:F3}");

        // ── ジャンプ判定 ──
        float hipY = (leftHip.y + rightHip.y) / 2f;
        if (state.prevHipY > 0f && canFire)
        {
            float deltaY = state.prevHipY - hipY;
            if (deltaY > dynamicJumpThreshold)
            {
                FireGesture(personIndex, GestureType.Jump, screenPos, state);
            }
        }
        state.prevHipY = hipY;

        // ── 手上げ判定 ──
        float shoulderY   = (leftShoulder.y + rightShoulder.y) / 2f;
        bool  leftHandUp  = leftWrist.y  < (shoulderY - dynamicHandUpThreshold);
        bool  rightHandUp = rightWrist.y < (shoulderY - dynamicHandUpThreshold);
        bool  bothHandsUp = leftHandUp && rightHandUp;
        bool  oneHandUp   = leftHandUp ^ rightHandUp;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} " +
                      $"shoulderY={shoulderY:F2} " +
                      $"leftWristY={leftWrist.y:F2} rightWristY={rightWrist.y:F2} " +
                      $"leftUp={leftHandUp} rightUp={rightHandUp} " +
                      $"canFire={canFire}");

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
            ArLog.Verbose($"[Pose] P{personIndex} 両手上げ中: {heldDuration:F2}秒 / {poseHoldDuration}秒");

            if (heldDuration >= poseHoldDuration && !state.bothHandsFired && canFire)
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
            float oneHandHold  = poseHoldDuration + Mathf.Max(0f, oneHandExtraHold);
            float heldDuration = now - state.oneHandUpStartTime;
            // ポーズを維持している間ずっと出るので Verbose
            ArLog.Verbose($"[Pose] P{personIndex} 片手上げ中: {heldDuration:F2}秒 / {oneHandHold}秒");

            if (heldDuration >= oneHandHold && !state.oneHandFired && canFire)
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
        var feedback = new PoseFeedback { trackId = personIndex };

        if (!canFire)
        {
            feedback.state    = PoseFeedbackState.Cooldown;
            feedback.progress = gestureCooldown <= 0f
                ? 0f
                : 1f - Mathf.Clamp01((now - state.lastGestureTime) / gestureCooldown);
        }
        else if (state.bothHandsUpStartTime >= 0f)
        {
            feedback.state    = PoseFeedbackState.Charging;
            feedback.gesture  = GestureType.BothHandsUp;
            feedback.progress = poseHoldDuration <= 0f
                ? 1f
                : Mathf.Clamp01((now - state.bothHandsUpStartTime) / poseHoldDuration);
        }
        else if (state.oneHandUpStartTime >= 0f)
        {
            // 片手側は実効保持時間（poseHoldDuration + oneHandExtraHold）で割る。
            // 発射条件と同じ分母にしないと、進捗が満タンなのに撃たない状態ができる
            float oneHandHold = poseHoldDuration + Mathf.Max(0f, oneHandExtraHold);

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
        state.lastGestureTime = Time.time;

        if (PoseEventBus.Instance == null)
        {
            ArLog.Warn("[Gesture] PoseEventBus が存在しないためイベントを発行できません");
            return;
        }

        PoseEventBus.Instance.FireGesture(personIndex, gesture, screenPos);

        // gestureCooldown が効くので低頻度。運用上必要なログなので常時出す
        Debug.Log($"[Gesture] Person{personIndex}: {gesture}");
    }
}

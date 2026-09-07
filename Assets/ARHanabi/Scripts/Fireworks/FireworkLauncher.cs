using UnityEngine;

// ===== FireworkLauncher =====
// PoseEventBus からジェスチャーイベントを受け取り、花火を打ち上げる。
//
// ── 画像花火と VFX 花火は「1発ごとに 5:5 で排他」──
//   以前は VFX Prefab を生成した直後に無条件で画像花火も生成していたため、
//   2種類が必ず重なって上がっていた。今は1発ごとに imageFireworkChance で
//   コイントスして、どちらか一方だけを打ち上げる。
//   両手上げは2発上がるので、その2発はそれぞれ独立に判定される。
//   （打てる画像花火が1件も無いときは VFX にフォールバックする。
//     選んだ側が空振りして「音だけ鳴って何も出ない」のを避けるため）
//
// ── 打ち上げ位置をビューポート基準にした理由 ──
//   旧実装は `worldPos.y = Random.Range(launchHeightMin, launchHeightMax)`（3〜8）で
//   ワールド絶対座標の Y を入れていた。しかしカメラは (0, 1, -10) / 垂直FOV 60° で、
//   花火は距離5（z = -5）に出る。その距離での視錐台の高さは 2*5*tan30° ≒ 5.77 なので、
//   画面に映る Y の範囲は およそ -1.89 〜 3.89 しかない。
//   つまり y=3〜8 の乱数では大半が画面上端の外に出ており、
//     ・「打ち上げ位置が上寄り」
//     ・「花火が小さい」（実際は下端の一部しか見えていなかった）
//   という2つの症状は同じ原因だった。
//
//   そこで ViewportToWorldPoint で「画面の何割の位置か」から決める。
//   0.5, 0.5 が常に画面のど真ん中なので、解像度やアスペクト比を変えても
//   中心がずれず、画面外にも飛ばない。
//
//   なお PoseCoordinateUtil.cs の冒頭には「FireworkLauncher は変換結果の y を
//   乱数で上書きして捨てており、使っているのは x だけ」と書かれているが、
//   この変更でその前提は成り立たなくなった（Launcher はもう PoseCoordinateUtil を
//   使わない）。PoseCoordinateUtil 自体は SkeletonRenderer のフォールバック経路で
//   現役なのでそのまま残してある。
//
// ── 廃止したフィールド ──
//   launchHeightMin / launchHeightMax / imageFireworkScale /
//   useFixedImageFireworkY / imageFireworkYOffset / imageFireworkYFixed
//   はすべて上記の理由で不要になった。MainScene.unity には古い値が残っているが、
//   対応するフィールドが無いので Unity は無視する（害はない）。
//   意味が変わるパラメータはあえて別名で追加してある。同名のまま初期値だけ
//   変えても、シーンに保存済みの値のほうが優先されて効かないため。

public class FireworkLauncher : MonoBehaviour
{
    [Header("打ち上げ花火の型（割物・ポカ物・小割物）")]
    [Tooltip("ON : ShellFireworkEffect でコードから花火を描く（菊・牡丹・柳・千輪など）\n" +
             "OFF: 従来の VFX Prefab を使う\n\n" +
             "従来のプレハブは Sphere で300粒を1回バーストするだけで、TrailModule も\n" +
             "ForceModule も無効だった。そのため尾を引かず・垂れず・多段階にできず、\n" +
             "「菊」「冠」「千輪」といった型を表現できなかった")]
    [SerializeField] private bool useShellPresets = true;

    [Tooltip("打ち上げる型をまとめた Asset（ARHanabi/花火プリセット集）。\n" +
             "これが割り当てられていて中身があれば、これを使う。\n" +
             "\n" +
             "型ごとの開き方・落ち方を詰めるときは、この Asset を再生中に編集する。\n" +
             "シーン上のコンポーネントの値は Play を抜けると巻き戻るが、\n" +
             "Asset の値は残るので「打つ→直す→打つ」を再コンパイル無しで回せる。\n" +
             "メニュー ARHanabi/花火プリセットの Asset を生成 で作れる")]
    [SerializeField] private ShellPresetLibrary presetLibrary;

    [Tooltip("打ち上げる型の一覧（旧来のInspector配列）。1発ごとにランダムに選ばれる。\n" +
             "presetLibrary が空のときだけ使う。どちらも空なら\n" +
             "ShellPreset.DefaultLibrary() の既定16種を使う")]
    [SerializeField] private ShellPreset[] shellPresets;

    [Tooltip("大玉（両手上げ）に使う型を名前で絞る。空なら全種から選ぶ。\n" +
             "例: 菊 / 芯入り菊 / 千輪菊 / 冠")]
    [SerializeField] private string[] largeShellNames;

    [Tooltip("小玉（片手上げ・ジャンプ）に使う型を名前で絞る。空なら全種から選ぶ。\n" +
             "例: 牡丹 / 柳 / 型物・リング")]
    [SerializeField] private string[] smallShellNames;

    [Tooltip("小玉の大きさ倍率（大玉に対する比）")]
    [SerializeField, Range(0.2f, 1f)] private float smallShellScale = 0.62f;

    [Header("花火Prefab（useShellPresets が OFF のときだけ使う）")]
    [SerializeField] private GameObject fireworkSmall;  // 片手・ジャンプ用
    [SerializeField] private GameObject fireworkLarge;  // 両手用

    [Header("打ち上げ段階（打ち上げ音と破裂音を分ける）")]
    [Tooltip("ON : 画面下から光跡が上昇し、到達してから開花する。\n" +
             "     打ち上げ音は上昇開始時、破裂音は開花時に鳴る。\n" +
             "OFF: 検知と同じフレームで開花する（従来）\n\n" +
             "従来は上昇の表現が無かったため、打ち上げ音を鳴らすと絵と食い違っていた。\n" +
             "音を分けるなら、その間を埋める上昇の絵が必要になる")]
    [SerializeField] private bool enableLaunchPhase = true;

    [Tooltip("上昇にかける秒数。打ち上げ音の長さに合わせる。\n\n" +
             "launch_whistle_firefly_01.wav は頭の無音（0〜0.253秒）を切り詰めて\n" +
             "尺が 1.00→0.75秒 になっている。この値を打ち上げ音の尺より長く\n" +
             "してしまうと、笛が鳴り終わってから破裂音が鳴るまでの間に無音が\n" +
             "できてしまい、1発なのに「音が途中で途切れて2つに分かれている」\n" +
             "ように聞こえる（実際にこの値のままにしていたら聞こえた）。\n" +
             "笛の尾（0.52〜0.75秒あたり）がまだ鳴っている間に破裂させることで\n" +
             "途切れなく繋げている。打ち上げ音を差し替えたら、この値もその\n" +
             "音の実際の尺に合わせて調整すること")]
    [SerializeField] private float launchToBurstDelay = 0.68f;

    [Tooltip("上昇の開始位置（画面の高さ方向。0=下端）")]
    [SerializeField, Range(0f, 0.5f)] private float launchFromViewportY = 0.02f;

    [Header("カメラ")]
    [SerializeField] private Camera mainCamera;

    [Header("打ち上げ位置")]
    [Tooltip("カメラから花火までの距離。画面に映る大きさの基準になる")]
    [SerializeField] private float launchDistance = 5f;

    [Tooltip("ON : 常に画面中央を基準に打ち上げる（中心がど真ん中に来る）\n" +
             "OFF: 従来どおり人の横位置に追従する")]
    [SerializeField] private bool launchAtScreenCenter = true;

    [Tooltip("打ち上げ位置に加えるばらつき。画面サイズに対する割合（±）。\n" +
             "x=0.08 なら画面幅の ±8% の範囲で左右に散る")]
    [SerializeField] private Vector2 launchViewportJitter = new Vector2(0.08f, 0.05f);

    [Tooltip("両手上げで2発上がるときの左右の間隔。画面幅に対する割合")]
    [SerializeField] private float pairSeparationViewport = 0.18f;

    [Tooltip("ジャンプで横にずれる最大量。画面幅に対する割合（±）")]
    [SerializeField] private float jumpSpreadViewport = 0.15f;

    [Header("花火の大きさ")]
    [Tooltip("画像花火が画面の高さの何割を占めるか。1.0 で画面いっぱい。\n" +
             "1.0 より大きくすると上下にはみ出す")]
    [SerializeField] private float imageScreenFillRatio = 1.0f;

    [Tooltip("VFX Prefab 花火の拡大率。プレハブが小さいので既定で拡大している。\n" +
             "拡大は X と Y だけに掛かる（奥行きまで広げると粒が背景 Quad の裏へ回るため）")]
    [SerializeField] private float vfxScale = 3f;

    [Tooltip("型花火（ShellFireworkEffect）が画面の高さの何割を占めるか。\n" +
             "1.0 で画面いっぱい。プリセットの burstSpeed と dragTau から広がりを計算する")]
    [SerializeField] private float shellScreenFillRatio = 0.9f;

    [Header("画像花火")]
    [Tooltip("OFF: 画像花火を一切打ち上げず、常に VFX Prefab にする")]
    [SerializeField] private bool enableImageFirework = true;

    [Tooltip("1発ごとに画像花火を選ぶ確率。0.5 で画像花火と VFX が 5:5。\n" +
             "打てる画像花火が無いときは VFX にフォールバックする")]
    [SerializeField, Range(0f, 1f)] private float imageFireworkChance = 0.5f;

    [Tooltip("ON: 粒の位置をランダムに散らして輪郭を崩し、火の粉寄りの見た目にする（比較検証用）")]
    [SerializeField] private bool imageScatterMode = false;

    [Tooltip("imageScatterMode が ON のときのずらし量。花火の大きさに対する割合")]
    [SerializeField] private float imageScatterAmount = 0.15f;

    [Header("画像花火 シェーダー設定")]
    [Tooltip("Assets/ARHanabi/Shaders/ParticleAdditive.shader をここにアサインする。\n" +
             "通常は Shader.Find で自動解決されるのでアサインは必須ではない")]
    [SerializeField] private Shader particleColorShader;

    // ── 永続化 ──
    // 投稿写真の量や API の調子は展示中に変わるため、Admin画面（SETTINGS）から
    // その場で調整できるようにしてある。展示は複数セッション・複数日にまたがって
    // 電源を落とすため、調整値は PlayerPrefs 経由で次回起動時にも引き継ぐ
    // （SettingsStore 参照）。キーが無ければ Inspector/シーンの値をそのまま使う
    private void Awake()
    {
        enableImageFirework = SettingsStore.GetBool($"{nameof(FireworkLauncher)}.{nameof(enableImageFirework)}", enableImageFirework);
        imageFireworkChance = SettingsStore.GetFloat($"{nameof(FireworkLauncher)}.{nameof(imageFireworkChance)}", imageFireworkChance);
    }

    // ── Admin画面（SETTINGS）からの調整用 ──
    public bool EnableImageFirework
    {
        get => enableImageFirework;
        set { enableImageFirework = value; SettingsStore.SetBool($"{nameof(FireworkLauncher)}.{nameof(enableImageFirework)}", value); }
    }

    public float ImageFireworkChance
    {
        get => imageFireworkChance;
        set { imageFireworkChance = value; SettingsStore.SetFloat($"{nameof(FireworkLauncher)}.{nameof(imageFireworkChance)}", value); }
    }

    // ── イベント購読 ──
    private void OnEnable()
    {
        if (PoseEventBus.Instance != null)
            PoseEventBus.Instance.OnGestureDetected += OnGestureDetected;
    }

    private void OnDisable()
    {
        if (PoseEventBus.Instance != null)
            PoseEventBus.Instance.OnGestureDetected -= OnGestureDetected;
    }

    // ── ジェスチャー受信 ──
    private void OnGestureDetected(int personIndex, GestureType gesture, Vector2 normalizedPos)
    {
        Debug.Log($"[Launcher] Person{personIndex} {gesture} pos={normalizedPos}");

        // オフセットはワールド単位ではなくビューポート（画面比率）単位で渡す。
        // ワールド単位だと画面のアスペクト比によって見かけの間隔が変わってしまう
        switch (gesture)
        {
            case GestureType.BothHandsUp:
                // 2発上がる。2発目を少しずらして「ドン、ドン」と聞かせる。
                // 完全に同時だと音量が倍になって不自然になるので、
                // 音だけでなく上昇そのものをずらす
                StartCoroutine(LaunchSequence(normalizedPos, -pairSeparationViewport * 0.5f,
                                              isLarge: true, startDelay: 0f, volumeScale: 1f));
                StartCoroutine(LaunchSequence(normalizedPos,  pairSeparationViewport * 0.5f,
                                              isLarge: true,
                                              startDelay: Random.Range(0.06f, 0.18f),
                                              volumeScale: 0.8f));
                break;

            case GestureType.OneHandUp:
                StartCoroutine(LaunchSequence(normalizedPos, 0f,
                                              isLarge: false, startDelay: 0f, volumeScale: 1f));
                break;

            case GestureType.Jump:
                StartCoroutine(LaunchSequence(normalizedPos,
                                              Random.Range(-jumpSpreadViewport, jumpSpreadViewport),
                                              isLarge: false, startDelay: 0f, volumeScale: 1f));
                break;
        }
    }

    // ── 打ち上げ → 開花のひと続き ──
    //
    // 打ち上げ音と破裂音を分けたので、その間に上昇の時間ができる。
    //   1. 打ち上げ音を鳴らし、画面下から光跡を上げる
    //   2. launchToBurstDelay 待つ
    //   3. 開花させ、破裂音を鳴らす
    //
    // 画像花火か型花火かはこの時点で確定させる。
    // 型によって開く高さ（launchViewportY）が違うので、
    // 上昇の到達点を決めるには先に型を選んでおく必要がある。
    private System.Collections.IEnumerator LaunchSequence(
        Vector2 normalizedPos, float xOffsetViewport,
        bool isLarge, float startDelay, float volumeScale,
        bool forceImage = false, bool forceDecided = false,
        ShellPreset forcedPreset = null)
    {
        if (startDelay > 0f) yield return new WaitForSeconds(startDelay);

        if (mainCamera == null)
        {
            Debug.LogError("[Launcher] mainCamera が設定されていません");
            yield break;
        }

        // 画像花火にするかを先に決める。
        // 打てるエントリが0件なら最初から型花火にする（以前は打ってから
        // フォールバックしていたが、それだと開く高さを先に決められない）。
        // forceDecided が true のときは呼び出し側が既に抽選している
        // forcedPreset は Admin画面の「花火」タブから型を指名して打つときに渡る。
        // その場合は画像花火の抽選そのものを飛ばす（指名した型を必ず打つのが目的なので、
        // 確率で画像花火に化けてしまうとテストにならない）
        bool useImage = forcedPreset != null
                        ? false
                        : forceDecided
                          ? forceImage
                          : enableImageFirework
                            && ActiveImageCount > 0
                            && Random.value < imageFireworkChance;

        ShellPreset preset = forcedPreset;
        if (preset == null && !useImage && useShellPresets)
        {
            preset = PickPreset(isLarge);
            if (preset == null)
            {
                Debug.LogError("[Launcher] 打ち上げる型が1つもありません");
                yield break;
            }
        }

        float viewportY = preset != null ? preset.launchViewportY : 0.5f;

        // finalSpreadX を持つ型は「最終位置を先に決めて出現位置を逆算する」経路を通る。
        // drift で大きく移動する型は、出現位置を散らすと着地点が画面外へ出てしまうため
        var burstPos = preset != null && preset.finalSpreadX > 0f
                       ? ResolveDriftingLaunchPosition(preset, normalizedPos, xOffsetViewport, isLarge)
                       : ResolveLaunchPosition(normalizedPos, xOffsetViewport, viewportY);

        // 宇宙モードの音ON/OFFをここで確認して反映する。
        // このクラスは SpaceModeController の状態をイベントで購読し続けているわけではなく、
        // 「発射の瞬間に確認する」だけの単純なやり方にしている。1ジェスチャーにつき
        // 数回しか呼ばれないコルーチンなので、都度チェックしても負荷にはならない
        if (FireworkAudioPlayer.Instance != null)
        {
            FireworkAudioPlayer.Instance.AudioVariant =
                SpaceModeController.Instance != null && SpaceModeController.Instance.SpaceAudioEnabled
                ? "Space" : "";
        }

        // ── 1. 打ち上げ ──
        // skipRisePhase の型（打ち下ろし＝上から降ってくる型）はここを丸ごと飛ばす。
        // 下からロケットが上がってから玉が降りてくると動きが往復して見えるため。
        // 打ち上げ音も一緒に省く（降ってくる玉に下からの笛は合わない）
        bool useRise = enableLaunchPhase
                       && launchToBurstDelay > 0f
                       && !(preset != null && preset.skipRisePhase);

        if (useRise)
        {
            var fromPos = ResolveLaunchPosition(normalizedPos, xOffsetViewport,
                                                launchFromViewportY);

            float rise = ResolveRiseSeconds(preset, viewportY);

            FireworkAudioPlayer.Instance?.PlayLaunch(fromPos);
            SpawnLaunchTrail(fromPos, burstPos, rise, isLarge,
                             preset != null ? preset.riseTrailScale : 1f);

            yield return new WaitForSeconds(rise);
        }

        // ── 2. 開花 ──
        var kind = FireworkAudioPlayer.FireworkKind.Explosion;

        if (useImage && TryLaunchImageFirework(burstPos))
        {
            kind = FireworkAudioPlayer.FireworkKind.Image;
        }
        else if (preset != null)
        {
            LaunchShellWithPreset(preset, burstPos, isLarge);
        }
        else
        {
            // useShellPresets が OFF のときは従来の VFX Prefab
            LaunchVfx(isLarge ? fireworkLarge : fireworkSmall, burstPos,
                      fellBackFromImage: useImage);
        }

        // preset の soundKey / crackleDelayOverride を渡すと、型ごとの音
        //（Sfx/Burst/<soundKey>/・Sfx/Crackle/<soundKey>/）が優先的に鳴る。
        // preset が null（画像花火 or useShellPresets OFF）なら null を渡し、
        // FireworkAudioPlayer 側で共通プールにフォールバックさせる
        // burstSoundDelay は「見せ場が生成の瞬間ではない型」のためのずらし。
        // 降ってきて最下点で開く型は、玉が現れた時点で鳴らすと
        // 実際の爆発と1秒以上ずれるので、開くタイミングまで遅らせる
        FireworkAudioPlayer.Instance?.PlayBurst(burstPos, kind, isLarge,
                                                soundKey: preset?.soundKey,
                                                delay: Mathf.Max(0f, preset?.burstSoundDelay ?? 0f),
                                                volumeScale: volumeScale,
                                                crackleDelayOverride: preset?.crackleDelayOverride ?? -1f);

        // 宇宙モードの UFO 演出（が有効なとき）へ開花位置を知らせる。
        // NotifyBurst は無効／未生成のときに no-op なので、ここでは
        // 宇宙モードの有無を気にせず常に呼んでよい（FireworkAudioPlayer.Instance?. と同じ考え方）
        SpaceModeController.NotifyBurst(burstPos);
    }

    // ── 昇り時間を型ごとに決める ──
    //
    // 従来は全ての型が launchToBurstDelay の一定時間で昇っていた。
    // ところが開く高さ（launchViewportY）は型ごとに違うので、高く開く
    // 柳（0.72）や冠（0.66）は同じ時間でより長い距離を飛ぶ、つまり
    // 「速く昇った」ように見えていた。上がるほど遅くなるのが実物なので逆。
    //
    // そこでまず飛距離で正規化して見かけの昇り速度を揃え、そのうえで
    // 型ごとの riseTimeScale を掛ける。中央（viewportY 0.5）で開く型が
    // ちょうど launchToBurstDelay になるので、既存の値の意味は変わらない。
    //
    // 最後に音の制約で丸める。打ち上げ笛は約0.75秒なので、これを超えると
    // 笛が鳴り終わってから破裂するまでに無音の隙間ができてしまう。
    private float ResolveRiseSeconds(ShellPreset preset, float viewportY)
    {
        if (preset == null) return launchToBurstDelay;

        // 中央開花を 1.0 とする飛距離の比。分母が 0 になり得るので保険を掛ける
        float reference = Mathf.Max(0.05f, 0.5f - launchFromViewportY);
        float span      = Mathf.Max(0.05f, viewportY - launchFromViewportY) / reference;

        return Mathf.Clamp(launchToBurstDelay * span * preset.riseTimeScale,
                           RiseSecondsMin, RiseSecondsMax);
    }

    // 上昇の光跡を出す
    private void SpawnLaunchTrail(Vector3 from, Vector3 to, float seconds, bool isLarge,
                                  float trailScale = 1f)
    {
        var go = new GameObject("LaunchTrail");
        go.transform.position = from;

        var fx = go.AddComponent<LaunchTrailEffect>();
        fx.SetShader(particleColorShader);
        fx.sparkCount = Mathf.Max(0, Mathf.RoundToInt(fx.sparkCount * trailScale));
        fx.Launch(from, to, seconds, isLarge ? 1f : 0.75f);
    }

    // ── Admin画面のテスト打ち上げ ──
    //
    // 以前は AdminUIManager が FireworkManager.LaunchRandom() を直接呼んでいたが、
    // それだと次の食い違いが出ていた。
    //   ・画像花火しか打たないので、エントリが0件だと何も出ない
    //   ・位置が testLaunchPosition (0,5,0) 固定で、実際の打ち上げ（z=-5）とは深度が違う
    //   ・imageScale もシェーダーも設定されないので見た目が実際と異なる
    //   ・開花音が鳴らない
    // テストと本番が違うとテストの意味が薄いので、ジェスチャーと同じ経路を通す。

    /// <summary>
    /// テスト打ち上げ。ジェスチャー時と同じ判定を通す。
    /// 取ってきた画像花火があれば imageFireworkChance で抽選し、
    /// 無ければ自動で型花火（こちらで作ったもの）にフォールバックする。
    /// </summary>
    /// <returns>実際に打ち上がった種類</returns>
    public FireworkAudioPlayer.FireworkKind LaunchTest(bool isLarge = true)
    {
        // 抽選結果を先に決めて呼び出し側へ返す（Admin画面のステータス表示用）。
        // 実際の打ち上げは同じ判定を通す LaunchSequence に任せる
        bool useImage = enableImageFirework
                     && ActiveImageCount > 0
                     && Random.value < imageFireworkChance;

        StartCoroutine(LaunchSequence(new Vector2(0.5f, 0.5f), 0f,
                                      isLarge, startDelay: 0f, volumeScale: 1f,
                                      forceImage: useImage, forceDecided: true));

        return useImage
               ? FireworkAudioPlayer.FireworkKind.Image
               : FireworkAudioPlayer.FireworkKind.Explosion;
    }

    /// <summary>
    /// 特定の画像花火を指定して打ち上げる（Admin画面で1件をプレビューする用）。
    /// 変換済みでない場合は false を返す。
    /// </summary>
    public bool LaunchTestImage(FireworkEntry entry, bool isLarge = true)
    {
        if (mainCamera == null)
        {
            Debug.LogError("[Launcher] mainCamera が設定されていません");
            return false;
        }

        var worldPos = ResolveLaunchPosition(new Vector2(0.5f, 0.5f), 0f);
        if (!TryLaunchImageFirework(worldPos, entry)) return false;

        FireworkAudioPlayer.Instance?.PlayBurst(
            worldPos, FireworkAudioPlayer.FireworkKind.Image, isLarge);
        return true;
    }

    /// <summary>
    /// 型を指名して打ち上げる（Admin画面の「花火」タブから使う）。
    ///
    /// ── なぜ必要か ──
    ///   PickPreset はランダムなので、従来は「柳だけを見たい」ができなかった。
    ///   型ごとに開き方・落ち方を詰めるには、狙った型を何度も打ち直せることが必須。
    ///   ジェスチャーも要らないので、Inspector で値を触ったらすぐ打てる。
    ///
    ///   打ち上げの経路自体は通常と同じ LaunchSequence を通す。
    ///   テストと本番で絵や音が違うとテストの意味が薄くなるため
    ///   （LaunchTest のコメントに書かれている、この方針をそのまま踏襲している）。
    /// </summary>
    public bool LaunchTestShell(ShellPreset preset, bool isLarge = true)
    {
        if (mainCamera == null)
        {
            Debug.LogError("[Launcher] mainCamera が設定されていません");
            return false;
        }

        if (preset == null)
        {
            Debug.LogWarning("[Launcher] 指名された型が null です");
            return false;
        }

        StartCoroutine(LaunchSequence(new Vector2(0.5f, 0.5f), 0f,
                                      isLarge, startDelay: 0f, volumeScale: 1f,
                                      forcedPreset: preset));
        return true;
    }

    /// <summary>打てる画像花火（isActive かつ変換済み）の件数</summary>
    public int ActiveImageCount =>
        FireworkManager.Instance != null
        ? FireworkManager.Instance.GetActiveEntries().Count
        : 0;

    // ── 型花火（割物・ポカ物・小割物）の開花 ──
    // 型と開花位置は LaunchSequence が先に決めている
    // （型ごとに開く高さが違うため、上昇の到達点を出すには先に型が必要）
    private void LaunchShellWithPreset(ShellPreset preset, Vector3 worldPos, bool isLarge)
    {
        var go = new GameObject($"Shell_{preset.name}");
        go.transform.position = worldPos;

        var fx = go.AddComponent<ShellFireworkEffect>();
        fx.SetShader(particleColorShader);

        // ── 位置用と描画サイズ用で倍率を分ける ──
        //
        // 画面に収まる倍率を求める。星が到達する半径は burstSpeed × dragTau で決まる
        //（v0·τ·(1-e^-∞) = v0·τ）ので、それが画面の半分に収まるよう正規化する。
        // こうすると型ごとに burstSpeed や dragTau が違っても画面上の大きさが揃う。
        //
        // ところがこの倍率をそのまま星の描画サイズにも掛けると、倍率が dragTau に
        // 反比例するため「開く速さを変えただけで粒の大きさが変わる」という罠になっていた。
        // 型ごとに開き方を詰めるたびに starSize を同倍率で手直しする必要があり、
        // 調整が常に2値同時修正になっていた。
        //
        // そこで描画サイズ側は固定の基準半径（菊の burstSpeed 9 × dragTau 0.46）で
        // 正規化する。dragTau に依存しなくなるので、dragTau は「開く速さ」だけの
        // つまみになり、starSize は「菊を基準にした粒の大きさ」という一定の意味を持つ。
        float radius   = preset.burstSpeed * Mathf.Max(0.01f, preset.dragTau);
        float halfView = FrustumHeightAt(launchDistance) * 0.5f * shellScreenFillRatio;

        // 型ごと／大玉小玉の倍率は位置にもサイズにも等しく効かせる
        // （小玉は広がりも粒も小さくなってほしい）
        float common = preset.sizeMultiplier * (isLarge ? 1f : smallShellScale);

        float posScale  = (radius > 0.001f ? halfView / radius : 1f) * common;
        float sizeScale = halfView / ShellSizeReferenceRadius        * common;

        fx.Launch(preset, posScale, sizeScale);

        Debug.Log($"[Launcher] 型花火 {preset.name}（{preset.category}）: {worldPos} " +
                  $"pos={posScale:F2} size={sizeScale:F2}");
    }

    // 大玉／小玉ごとに型を選ぶ。名前で絞り込んでいなければ全種から選ぶ。
    //
    // 宇宙モード（SpaceModeController.FireworkMode）による category での絞り込みも
    // ここで併せて行う。名前フィルタと category フィルタは独立な軸なので、
    // 同じ1パスの中で両方の条件を満たすものだけを _pickBuffer に集める。
    //   Off       … category が "宇宙" では“ない”ものだけ（今までの11種と同じ）
    //   SpaceOnly … category が "宇宙" の“もの”だけ
    //   Mix       … category では絞らない（今まで通り全部が対象）
    // SpaceModeController がまだ存在しない（Instance が null）シーンでは Off 扱いにして
    // 今までの挙動から一切変えない。
    //
    // 名前フィルタ・category フィルタのどちらか（または両方）で該当が0件になったときは
    // 全種から選ぶ（無音の空振りを作らない、既存の方針をそのまま踏襲）
    private ShellPreset PickPreset(bool isLarge)
    {
        var library = ResolveLibrary();
        if (library.Count == 0) return null;

        var nameFilter = isLarge ? largeShellNames : smallShellNames;
        var mode = SpaceModeController.Instance?.FireworkMode
                   ?? SpaceModeController.SpaceFireworkMode.Off;

        bool hasNameFilter = nameFilter != null && nameFilter.Length > 0;
        bool hasCategoryFilter = mode != SpaceModeController.SpaceFireworkMode.Mix;

        if (!hasNameFilter && !hasCategoryFilter)
            return library[Random.Range(0, library.Count)];

        _pickBuffer.Clear();
        for (int i = 0; i < library.Count; i++)
        {
            var candidate = library[i];
            if (candidate == null) continue;

            if (hasNameFilter)
            {
                bool nameMatches = false;
                for (int f = 0; f < nameFilter.Length; f++)
                {
                    if (candidate.name == nameFilter[f]) { nameMatches = true; break; }
                }
                if (!nameMatches) continue;
            }

            if (hasCategoryFilter)
            {
                bool isSpace = candidate.category == "宇宙";
                bool categoryMatches = mode == SpaceModeController.SpaceFireworkMode.SpaceOnly
                                       ? isSpace
                                       : !isSpace; // Off
                if (!categoryMatches) continue;
            }

            _pickBuffer.Add(candidate);
        }

        if (_pickBuffer.Count == 0) return library[Random.Range(0, library.Count)];
        return _pickBuffer[Random.Range(0, _pickBuffer.Count)];
    }

    // 星の描画サイズを正規化するときの基準半径。
    // 菊（burstSpeed 9 × dragTau 0.46）の到達半径そのもの。
    // 「菊を基準にした粒の大きさ」という starSize の意味を固定するための定数なので、
    // ここを動かすと全16種の見かけの粒サイズが一斉に変わる。触らないこと。
    private const float ShellSizeReferenceRadius = 4.14f;

    // 昇り時間の上下限[秒]。
    // 上限は打ち上げ笛（Sfx/Launch/Common/launch_whistle_firefly_01.wav、約0.75秒）の制約。
    // これを超えると笛が鳴り終わってから破裂するまでに無音の隙間ができ、
    // 「1発なのに音が2つに分かれている」ように聞こえる（このファイルの
    // launchToBurstDelay のコメント参照）。下限は光跡が読めなくなる境界。
    private const float RiseSecondsMin = 0.45f;
    private const float RiseSecondsMax = 0.72f;

    // ── 型の一覧をどこから取るか ──
    //   presetLibrary（Asset）→ shellPresets[]（旧来のInspector配列）→ DefaultLibrary()
    // Asset は「あれば使う」上書きに過ぎず、未設定でも誤って消されても
    // コード側の既定16種で必ず花火が出るようにしてある。
    //
    // 結果をキャッシュしないのは、調整中は再生したまま Asset を編集する前提で、
    // キャッシュすると編集が次の発射に反映されなくなるため。
    // 戻り値を IReadOnlyList にしてあるので、Asset の List をコピーせずそのまま返せる
    //（毎発射でのアロケーションを避ける）。
    private System.Collections.Generic.IReadOnlyList<ShellPreset> ResolveLibrary()
    {
        if (presetLibrary != null && presetLibrary.HasAny)
            return presetLibrary.presets;

        if (shellPresets != null && shellPresets.Length > 0)
            return shellPresets;

        return _defaultLibrary ??= ShellPreset.DefaultLibrary().ToArray();
    }

    /// <summary>Admin画面が型の一覧を出すために使う（読み取り専用の用途）</summary>
    public System.Collections.Generic.IReadOnlyList<ShellPreset> GetShellPresets() => ResolveLibrary();

    // 既定ライブラリは初回に1回だけ組み立てて使い回す
    private ShellPreset[] _defaultLibrary;

    // 名前で絞り込むときの一時リスト（毎回確保しない）
    private readonly System.Collections.Generic.List<ShellPreset> _pickBuffer = new();

    // ── 移動する型の打ち上げ座標 ──
    //
    // drift で大きく移動する型（打ち下ろしの彗星など）は、出現位置を散らすと
    // 移動したあとの着地点が画面外へ出てしまう。
    // そこで順序を逆にして、
    //   1. 見せ場である「最終位置」を正規分布で決める（中央がいちばん出やすい）
    //   2. そこから移動量を引いて出現位置を逆算する
    // とすれば、着地点は必ず画面に収まる。
    // 出現位置が画面外になることはあるが、「画面外から入ってくる」動きになるだけ。
    private Vector3 ResolveDriftingLaunchPosition(ShellPreset preset, Vector2 normalizedPos,
                                                  float xOffsetViewport, bool isLarge)
    {
        var travel = DriftTravelViewport(preset, isLarge);

        // ── 最終位置（横）──
        // ±2σ で打ち切る。正規分布をそのまま使うと稀に大きく外れ、
        // クランプで画面端に張り付く発射が混ざるため
        float center = launchAtScreenCenter ? 0.5f : Mathf.Clamp01(normalizedPos.x);
        float spread = Mathf.Clamp(SampleStandardNormal(), -2f, 2f) * preset.finalSpreadX;
        float uFinal = center + spread + xOffsetViewport;

        // ── 出現位置は最終位置から逆算 ──
        float uStart = uFinal - travel.x;
        float vStart = preset.launchViewportY
                     + Random.Range(-launchViewportJitter.y, launchViewportJitter.y);

        // 画面外から入ってくるのは許すが、極端に遠くからは出さない
        //（遠すぎると、見えないところで尾を引いている時間が長くなる）
        uStart = Mathf.Clamp(uStart, -0.25f, 1.25f);
        vStart = Mathf.Clamp(vStart, -0.25f, 1.25f);

        return mainCamera.ViewportToWorldPoint(new Vector3(uStart, vStart, launchDistance));
    }

    /// <summary>
    /// この型が寿命いっぱいで移動する量（ビューポート比）。
    ///
    /// drift も重力も「広がり半径に対する比」で持っているので、
    /// posScale を掛けた時点でどちらも halfView 単位の移動量になる
    ///   drift の移動 = driftDirection · driftRatio · halfView · common
    ///   重力の落下   = sagRatio · halfView · common
    /// あとは視錐台の縦横で割ってビューポート比に直すだけ。
    ///
    /// 縦と横で割る値が違うことに注意。画面は横に長い（16:9 なら約1.78倍）ので、
    /// ワールド座標で同じだけ動いても画面上の横移動は縮んで見える。
    /// </summary>
    private Vector2 DriftTravelViewport(ShellPreset preset, bool isLarge)
    {
        float halfView = FrustumHeightAt(launchDistance) * 0.5f * shellScreenFillRatio;
        float common   = preset.sizeMultiplier * (isLarge ? 1f : smallShellScale);

        var dir = preset.driftDirection.sqrMagnitude > 1e-6f
                  ? preset.driftDirection.normalized
                  : Vector3.zero;

        Vector3 world = dir * (preset.driftRatio * halfView * common)
                      + Vector3.down * (preset.sagRatio * halfView * common);

        float frustumHeight = FrustumHeightAt(launchDistance);
        float frustumWidth  = frustumHeight * mainCamera.aspect;

        return new Vector2(world.x / frustumWidth, world.y / frustumHeight);
    }

    // 標準正規分布の乱数（Box-Muller法）。
    // UnityEngine.Random は一様分布しか持たないので自前で作る。
    // 一様分布だと端も中央も同じ確率で出るが、
    // 「中央がいちばん出やすく、外れるほど稀」にしたいのでこちらを使う。
    private static float SampleStandardNormal()
    {
        // Log(0) が -∞ になるので (0,1] に寄せる
        float u1 = 1f - Random.value;
        float u2 = Random.value;
        return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
    }

    // ── 打ち上げ座標の決定 ──
    // viewportY で高さを指定する。0.5 が画面のど真ん中。
    // 垂れる型（冠・柳）は高く開かないと落ちる部分が画面外に出るため、
    // 型ごとに高さを変えられるようにしてある
    private Vector3 ResolveLaunchPosition(Vector2 normalizedPos, float xOffsetViewport,
                                          float viewportY = 0.5f)
    {
        // 0.5, 0.5 が画面のど真ん中。ここを基準にすることで
        // 解像度・アスペクト比・FOV が変わっても中心がずれない
        float u = launchAtScreenCenter ? 0.5f : Mathf.Clamp01(normalizedPos.x);
        float v = viewportY;

        u += xOffsetViewport + Random.Range(-launchViewportJitter.x, launchViewportJitter.x);
        v += Random.Range(-launchViewportJitter.y, launchViewportJitter.y);

        // 画面外へ出ないように丸める
        u = Mathf.Clamp01(u);
        v = Mathf.Clamp01(v);

        return mainCamera.ViewportToWorldPoint(new Vector3(u, v, launchDistance));
    }

    // ── VFX Prefab 打ち上げ ──
    private void LaunchVfx(GameObject prefab, Vector3 worldPos, bool fellBackFromImage)
    {
        if (prefab == null)
        {
            Debug.LogError("[Launcher] Prefab が設定されていません");
            return;
        }

        var fw = Instantiate(prefab, worldPos, Quaternion.identity);

        // プレハブの ParticleSystem は scalingMode = Local になっており、
        // Local は「その ParticleSystem 自身の transform スケールだけ」を見て
        // 親のスケールを無視する。そのため生成したルートを拡大しても子の粒
        // （FireworkBurst）には効かない。Hierarchy に切り替えてルート1箇所で
        // 効かせるようにしている（プレハブ側を書き換えずに済ませるため）。
        var systems = fw.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            var main = systems[i].main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }

        // ── 拡大するのは X と Y だけ。Z は絶対に触らない ──
        //   FireworkBurst は Sphere シェイプ（全方向）で startSpeed 5 / lifetime 1.5 なので、
        //   粒は前後方向にも約 7.5 ユニット飛ぶ。花火は z = -5 に出て CameraBackground の
        //   Quad は z = 5 にあるため、等倍なら奥へ +2.5 までで Quad の手前に収まる。
        //   ところが Z まで一緒に拡大すると 3倍で +17.5、両手用（プレハブ自身が2倍）では
        //   さらに奥まで飛び、粒が背景 Quad の裏に回って見えなくなる。
        //   このシーンはカメラが +Z をまっすぐ向いた 2D 合成なので、
        //   奥行き方向に広がっても見た目の得は何も無い。
        //
        //   掛け算にするのは、fireworkSmall と fireworkLarge がプレハブ自身の
        //   localScale（1 と 2）で大小を作り分けているため。固定値を代入すると
        //   片手用と両手用が同じ大きさになってしまう。
        var scale = fw.transform.localScale;
        fw.transform.localScale = new Vector3(scale.x * vfxScale, scale.y * vfxScale, scale.z);

        Debug.Log($"[Launcher] VFX 生成: {worldPos} scale={fw.transform.localScale}" +
                  (fellBackFromImage ? "（画像花火が無いためフォールバック）" : ""));
        Destroy(fw, 5f);
    }

    // ── 画像花火の発射 ──
    // 打ち上げられたら true。打てるエントリが無ければ false（呼び出し側が型花火に切り替える）
    //
    // forced に指定があればそのエントリを打つ（Admin画面で1件をプレビューする用）。
    // null なら isActive かつ変換済みのエントリからランダムに選ぶ。
    private bool TryLaunchImageFirework(Vector3 worldPos, FireworkEntry forced = null)
    {
        var manager = FireworkManager.Instance;
        if (manager == null) return false;

        var entry = forced;

        if (entry == null)
        {
            var actives = manager.GetActiveEntries();
            if (actives.Count == 0) return false;
            entry = actives[Random.Range(0, actives.Count)];
        }
        else if (!entry.isConverted || entry.particleData == null)
        {
            // 未変換のものを渡された場合は打てない。呼び出し側で通知する
            Debug.LogWarning($"[Launcher] 未変換のため打ち上げできません: {entry.displayName}");
            return false;
        }

        var go = new GameObject($"ImageFW_{entry.displayName}");
        go.transform.position = worldPos;

        var fx = go.AddComponent<ImageFireworkEffect>();

        // 画面の高さいっぱいに広げる。視錐台の高さから毎回計算するので、
        // FOV や launchDistance を変えても勝手に追従する
        fx.imageScale    = FrustumHeightAt(launchDistance) * imageScreenFillRatio;
        fx.scatterMode   = imageScatterMode;
        fx.scatterAmount = imageScatterAmount;

        // シェーダーを注入してから Launch
        fx.SetShader(particleColorShader);
        fx.Launch(entry.particleData);

        Debug.Log($"[Launcher] 画像花火 生成: {entry.displayName} @ {worldPos} " +
                  $"scale={fx.imageScale:F2}");
        return true;
    }

    // カメラから distance の位置における視錐台の高さ（ワールド単位）
    private float FrustumHeightAt(float distance)
    {
        if (mainCamera == null) return 1f;

        if (mainCamera.orthographic)
            return mainCamera.orthographicSize * 2f;

        return 2f * distance * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }
}

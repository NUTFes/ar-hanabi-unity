using System.Collections.Generic;
using UnityEngine;

// ===== FireworkAudioPlayer =====
// 花火の効果音を再生するシングルトン。
//
// 責務:
//   ・打ち上げ音（上昇中）と破裂音（開花時）を別々に鳴らす
//   ・花火が出たワールド座標から3D定位で鳴らす
//   ・複数クリップからランダムに選び、ピッチと音量を振って反復感を消す
//   ・両手ジェスチャーで2発上がるときに音が二重で倍音量にならないようにする
//
// ── クリップを Resources から自動で読む理由 ──
//   以前は Inspector の List にクリップを割り当てる方式にしていたが、
//   シーンに保存された FireworkAudioPlayer には新しい List フィールドが
//   存在しないため空のままになり、「音が一切鳴らない」状態になった。
//   Editor 拡張で配線する手順を用意していたが、それを実行しないと
//   音が出ないというのは壊れやすい。
//
//   そこで Resources/Sfx/ 配下を実行時に読み込む。
//     Assets/ARHanabi/Resources/Sfx/Launch/   … 打ち上げ音
//     Assets/ARHanabi/Resources/Sfx/Burst/    … 破裂音（共通プール）
//     Assets/ARHanabi/Resources/Sfx/Crackle/  … パチパチ（共通プール。開花後に遅れて鳴らす）
//   フォルダに wav を置くだけで増える。exe に配って別PCで動かす前提でも同じように働く。
//   Inspector で明示的に割り当てた分があれば、そちらを優先して併用する。
//
// ── 型ごとの鳴らし分け（soundKey）──
//   ShellPreset（菊・柳・千輪など）ごとに soundKey を持たせておくと、
//     Assets/ARHanabi/Resources/Sfx/Burst/<soundKey>/
//     Assets/ARHanabi/Resources/Sfx/Crackle/<soundKey>/
//   を優先的に探す。そのキーのフォルダが無い、または中身が空なら
//   共通プール（Sfx/Burst・Sfx/Crackle 直下）に自動でフォールバックする。
//   これにより soundKey 付きの音をまだ用意していない型でも、必ず何かは鳴る。
//   このクラスは ShellPreset を一切知らない（文字列キーを受け取って
//   フォルダを引くだけ）。型の知識は FireworkLauncher 側に閉じている。
//
//   ── 現状（この回で変更）──
//   「1花火＝1音で十分」という方針に合わせて、冠・柳・彩色柳・千輪菊も含めて
//   全ての型が Burst/<soundKey>/ 側の単一の音だけで鳴るようにした。
//   Sfx/Crackle/ 配下は現在どの型のフォルダも無く（空）、上のフォールバックにより
//   自然と「パチパチは鳴らない」状態になっている。仕組み自体は削除していないので、
//   将来また2段階の音を作りたくなったら Sfx/Crackle/<soundKey>/ に wav を置き、
//   ShellPreset.crackleDelayOverride で遅れ秒数を設定すればそのまま復活する。
//
//   注意: 冠・柳・彩色柳・千輪菊用に Firefly で作った専用音は「開花後にゆっくり
//   垂れ落ちる／遅れてポンと弾ける」という“2枚目”専用の音として作ったため、
//   単独では頭に破裂の一撃が無い。そのままだと「爆発音が無い」と聞こえてしまうため、
//   Burst/<soundKey>/ の wav 自体を「共通の爆発音＋この専用音（元の
//   crackleDelayOverride と同じ遅れ幅）」を ffmpeg であらかじめ1本に合成した
//   ものへ差し替えてある。詳しい経緯は ShellPreset.crackleDelayOverride の
//   コメントを参照。
//
// ── 宇宙モード用の音のバリエーション（AudioVariant）──
//   SpaceModeController が宇宙モードの音ON/OFFを切り替えたときに、
//   このクラスが差し替え先として参照するのが AudioVariant。
//     ""      (既定) … 従来通り Sfx/Burst/<soundKey>/       → 共通プールの順
//     "Space"        … Sfx/Burst/Space/<soundKey>/ → Sfx/Burst/<soundKey>/ → 共通プールの順
//   soundKey が「型ごと」の分岐、AudioVariant が「モードごと」の分岐で、
//   互いに独立な軸なので Space/<soundKey>/ という1段深い階層で両方を表す。
//   打ち上げ音（Launch）にも同じ考え方で Sfx/Launch/Space/ を用意し、
//   無ければ共通プールにフォールバックする（こちらは soundKey が無いぶん単純）。
//   AudioVariant を空のままにしておけば、誰も宇宙モードに触れていない今までの
//   経路と完全に同じになる（挙動が変わらないことを保証するための既定値）。
//
// ── 3D定位のためにモノラル素材が必要 ──
//   Unity は spatialBlend > 0 でもステレオ素材だと定位が効かない。
//   素材はモノラルに変換しておくこと（Import 設定の Force To Mono でも可）。
//
// ── AudioSource をプールする理由 ──
//   AudioSource.PlayClipAtPoint は毎回 GameObject を生成して破棄するため、
//   短時間に何発も上がる展示ではゴミが積み上がる。
//   固定数の AudioSource を使い回して位置だけ動かす。

[RequireComponent(typeof(AudioSource))]
public class FireworkAudioPlayer : MonoBehaviour
{
    public static FireworkAudioPlayer Instance { get; private set; }

    /// <summary>花火の種類。破裂音を鳴らし分けるために使う</summary>
    public enum FireworkKind
    {
        /// <summary>爆発型（型花火 / VFX Prefab の花火）</summary>
        Explosion,
        /// <summary>画像型（取ってきた画像から作る花火）</summary>
        Image,
    }

    /// <summary>
    /// "" なら従来通り Sfx/Burst/&lt;soundKey&gt;/ → 共通プールの順で探す。
    /// "Space" なら Sfx/Burst/Space/&lt;soundKey&gt;/ → Sfx/Burst/&lt;soundKey&gt;/ → 共通プールの
    /// 順で探す。SpaceModeController が音のON/OFFを切り替えるときにこれを書き換えるだけでよい。
    /// </summary>
    public string AudioVariant { get; set; } = "";

    // Resources 配下の読み込み先。フォルダに置くだけで音が増える
    private const string LaunchDir  = "Sfx/Launch";
    private const string BurstDir   = "Sfx/Burst";
    private const string CrackleDir = "Sfx/Crackle";

    // 共通プール（どの型にも当てはまらないときのフォールバック）専用のフォルダ。
    //
    // ── なぜ Sfx/Burst 直下ではなく Common/ に分けたか ──
    //   Resources.LoadAll は再帰的にサブフォルダまで拾う。以前は共通プールを
    //   Sfx/Burst から読んでいたため、型ごとの音（Kiku/ Botan/ …）まで
    //   共通プールに混ざっていた。型付きの音が全部揃っている間は表に出なかったが、
    //   宇宙モード用の Sfx/Burst/Space/ を足した時点で
    //   「通常モードの画像花火がUFOの音を鳴らす」ことが起きるようになった
    //   （画像花火は専用音が無いと共通プールへフォールバックするため）。
    //   共通プールだけ別フォルダに閉じ込めれば、サブフォルダを何段増やしても
    //   共通プールが汚染されない。
    private const string CommonBurstDir = "Sfx/Burst/Common";

    // 打ち上げ音の共通プール専用のフォルダ。理由は CommonBurstDir と全く同じ。
    //
    // ── なぜ Sfx/Launch 直下ではなく Common/ に分けたか ──
    //   Resources.LoadAll は再帰的なので、共通プールを Sfx/Launch から読むと
    //   宇宙モード用の Sfx/Launch/Space/ の音まで共通プールに入ってしまい、
    //   宇宙モードがOFFでも通常花火の打ち上げ音に「シュワーッ」が混ざる
    //   （Launch 配下は通常の笛が1本しか無いので、約半数が宇宙の音で上がっていた）。
    //   共通プールだけ Common/ に閉じ込めれば、バリアント用のサブフォルダを
    //   何段増やしても共通プールが汚染されない。
    //
    //   なお ResolveLaunchClips が使うバリアント引きは、意図して
    //   Sfx/Launch/<AudioVariant>/ を見るので LaunchDir のままでよい。
    private const string CommonLaunchDir = "Sfx/Launch/Common";

    // ── Inspector ──
    [Header("打ち上げ音（上昇中に鳴る）")]
    [Tooltip("空でよい。Resources/Sfx/Launch/ から自動で読み込む。\n" +
             "ここに入れた分は自動読み込みぶんと併用される")]
    [SerializeField] private List<AudioClip> launchClips = new();

    [Tooltip("打ち上げ音の音量")]
    [SerializeField, Range(0f, 1f)] private float launchVolume = 0.55f;

    [Header("破裂音（開花時に鳴る）")]
    [Tooltip("空でよい。Resources/Sfx/Burst/ から自動で読み込む")]
    [SerializeField] private List<AudioClip> burstClips = new();

    [Tooltip("画像型の花火に別の破裂音を使う場合だけ入れる。\n" +
             "空なら burstClips と同じ音を使う")]
    [SerializeField] private List<AudioClip> imageBurstClips = new();

    [Tooltip("大玉（両手上げ）の破裂音の音量")]
    [SerializeField, Range(0f, 1f)] private float burstVolumeLarge = 1.0f;

    [Tooltip("小玉（片手上げ・ジャンプ）の破裂音の音量")]
    [SerializeField, Range(0f, 1f)] private float burstVolumeSmall = 0.72f;

    [Tooltip("小玉のピッチ倍率。上げると軽く小さい破裂に聞こえる")]
    [SerializeField] private float burstPitchSmall = 1.18f;

    [Header("パチパチ（開花後に遅れて鳴る）")]
    [Tooltip("空でよい。Resources/Sfx/Crackle/ から自動で読み込む。\n" +
             "1本も無ければ鳴らさない")]
    [SerializeField] private List<AudioClip> crackleClips = new();

    [Tooltip("破裂からパチパチまでの遅れ[秒]。星が拡散し始める頃に合わせる")]
    [SerializeField] private float crackleDelay = 0.55f;

    [SerializeField, Range(0f, 1f)] private float crackleVolume = 0.45f;

    [Header("ゆらぎ（反復感を消す）")]
    [Tooltip("ピッチのランダム幅（±）")]
    [SerializeField] private float pitchJitter = 0.07f;

    [Tooltip("音量のランダム幅（±）")]
    [SerializeField] private float volumeJitter = 0.12f;

    [Header("3D定位")]
    [Tooltip("0 = 常に中央から / 1 = 完全に3D。0.6 前後が展示では扱いやすい")]
    [SerializeField, Range(0f, 1f)] private float spatialBlend = 0.6f;

    [Tooltip("この距離までは減衰しない")]
    [SerializeField] private float minDistance = 6f;
    [SerializeField] private float maxDistance = 60f;

    [Header("同時発音")]
    [Tooltip("使い回す AudioSource の数。これを超えると古い音が上書きされる")]
    [SerializeField] private int voiceCount = 12;

    [Tooltip("両手上げで2発上がるとき、2発目をこの範囲でずらす[秒]。\n" +
             "同時に重ねると音量が倍になって不自然なので、わずかにずらして\n" +
             "「ドン、ドン」と聞かせる")]
    [SerializeField] private Vector2 pairDelayRange = new Vector2(0.035f, 0.09f);

    [Tooltip("2発目の音量倍率")]
    [SerializeField, Range(0f, 1f)] private float pairSecondVolume = 0.8f;

    // ── 内部 ──
    private AudioSource[] _voices;
    private int           _nextVoice;

    // Resources から読んだ分と Inspector 指定分を結合したもの（共通プール）
    private readonly List<AudioClip> _launch  = new();
    private readonly List<AudioClip> _burst   = new();
    private readonly List<AudioClip> _image   = new();
    private readonly List<AudioClip> _crackle = new();

    // soundKey ごとのクリップ一覧。初回参照時に Resources.LoadAll で引いてキャッシュする。
    // 空リストも含めてキャッシュするので、フォルダが存在しないキーでも
    // 毎回 Resources.LoadAll を呼び直すことはない
    private readonly Dictionary<string, List<AudioClip>> _burstByKey   = new();
    private readonly Dictionary<string, List<AudioClip>> _crackleByKey = new();

    // AudioVariant（宇宙モードなど）用の打ち上げ音キャッシュ。
    // キーは AudioVariant の値そのもの（例: "Space"）
    private readonly Dictionary<string, List<AudioClip>> _launchByKey  = new();

    private bool _warnedNoBurst;

    // ── ライフサイクル ──
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        LoadClips();
        CreateVoices();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Resources 配下と Inspector 指定を結合する。
    // Resources.LoadAll は Editor でもビルド後でも同じように働くので、
    // 「フォルダに置くだけ」で音が増える
    private void LoadClips()
    {
        Merge(_launch,  launchClips,      CommonLaunchDir);
        Merge(_burst,   burstClips,       CommonBurstDir);
        Merge(_crackle, crackleClips,     CrackleDir);

        // 画像型は専用の音があればそれを、無ければ破裂音を共用する
        _image.Clear();
        foreach (var c in imageBurstClips) if (c != null) _image.Add(c);
        if (_image.Count == 0) _image.AddRange(_burst);

        Debug.Log($"[FWAudio] クリップ読み込み: 打ち上げ {_launch.Count} / " +
                  $"破裂 {_burst.Count} / 画像型破裂 {_image.Count} / " +
                  $"パチパチ {_crackle.Count}");

        if (_burst.Count == 0)
        {
            Debug.LogWarning($"[FWAudio] 共通の破裂音が1本もありません。" +
                             $"Assets/ARHanabi/Resources/{CommonBurstDir}/ に wav を置いてください");
        }
    }

    private static void Merge(List<AudioClip> dst, List<AudioClip> inspector, string resourceDir)
    {
        dst.Clear();

        foreach (var c in inspector) if (c != null && !dst.Contains(c)) dst.Add(c);

        var loaded = Resources.LoadAll<AudioClip>(resourceDir);
        foreach (var c in loaded) if (c != null && !dst.Contains(c)) dst.Add(c);
    }

    // soundKey 付きのサブフォルダを引く。結果は空リストでもキャッシュする。
    // ShellPreset.DefaultLibrary() には無い soundKey が渡っても
    // Resources.LoadAll は例外を出さず空配列を返すので、呼び出し側は
    // 常にこの戻り値の Count を見てから使うだけでよい
    private static List<AudioClip> GetByKey(Dictionary<string, List<AudioClip>> cache,
                                            string baseDir, string soundKey)
    {
        if (string.IsNullOrEmpty(soundKey)) return null;

        if (cache.TryGetValue(soundKey, out var list)) return list;

        var loaded = Resources.LoadAll<AudioClip>($"{baseDir}/{soundKey}");
        list = new List<AudioClip>(loaded);
        cache[soundKey] = list;
        return list;
    }

    // 3D再生用の AudioSource を子オブジェクトとして確保する
    private void CreateVoices()
    {
        int n = Mathf.Max(1, voiceCount);
        _voices = new AudioSource[n];

        for (int i = 0; i < n; i++)
        {
            var go = new GameObject($"SfxVoice_{i}");
            go.transform.SetParent(transform, false);

            var src = go.AddComponent<AudioSource>();
            src.playOnAwake  = false;
            src.loop         = false;
            src.spatialBlend = spatialBlend;
            src.rolloffMode  = AudioRolloffMode.Linear;
            src.minDistance  = minDistance;
            src.maxDistance  = maxDistance;
            _voices[i] = src;
        }
    }

    // ── 打ち上げ音 ──

    /// <summary>
    /// 打ち上げ音を鳴らす。上昇の開始位置（画面下あたり）から鳴らすと定位が合う。
    /// </summary>
    public void PlayLaunch(Vector3 worldPosition)
    {
        var clips = ResolveLaunchClips();
        if (clips.Count == 0) return;   // 素材が無いだけなので警告は出さない
        PlayOne(clips, worldPosition, launchVolume, 1f, 0f);
    }

    // AudioVariant（宇宙モードなど）が設定されていれば Sfx/Launch/<AudioVariant>/ を
    // 優先して探す。そのフォルダが無い／空なら共通プール _launch にフォールバックする。
    // soundKey のような型ごとの分岐は無いので、GetByKey には AudioVariant 自体を
    // “キー”として渡すだけでよい（GetByKey は文字列を1つ受け取ってフォルダを引くだけの
    // 汎用処理なので、そのまま使い回せる）
    private List<AudioClip> ResolveLaunchClips()
    {
        if (string.IsNullOrEmpty(AudioVariant)) return _launch;

        var variant = GetByKey(_launchByKey, LaunchDir, AudioVariant);
        return (variant != null && variant.Count > 0) ? variant : _launch;
    }

    // ── 破裂音 ──

    /// <summary>
    /// 破裂音を鳴らす。花火が開いたワールド座標から3D定位で再生する。
    /// パチパチ素材があれば crackleDelay（または crackleDelayOverride）だけ遅らせて続けて鳴らす。
    /// </summary>
    /// <param name="soundKey">
    /// 型ごとの音を選ぶキー（ShellPreset.soundKey）。
    /// null/空、またはそのキー専用のクリップが1本も無ければ共通プールにフォールバックする。
    /// kind が Image のときは無視する（画像花火の型別ルーティングは今のところ無いため）。
    /// </param>
    /// <param name="crackleDelayOverride">
    /// 0以上ならこの秒数を使う。負値なら crackleDelay（既定値）を使う。
    /// </param>
    public void PlayBurst(Vector3 worldPosition, FireworkKind kind, bool isLarge,
                          string soundKey = null, float delay = 0f, float volumeScale = 1f,
                          float crackleDelayOverride = -1f)
    {
        var clips = ResolveBurstClips(kind, soundKey);

        if (clips.Count == 0)
        {
            if (!_warnedNoBurst)
            {
                Debug.LogWarning($"[FWAudio] 破裂音が1本もありません。" +
                                 $"Assets/ARHanabi/Resources/{BurstDir}/ に wav を置いてください");
                _warnedNoBurst = true;
            }
            return;
        }

        float volume = (isLarge ? burstVolumeLarge : burstVolumeSmall) * volumeScale;
        float pitch  = isLarge ? 1f : burstPitchSmall;

        PlayOne(clips, worldPosition, volume, pitch, delay);

        // 開花のあと、星が拡散し始める頃にパチパチ／落下音を重ねる。
        // 型専用のパチパチが無ければ共通プールにフォールバックする
        var crackleClips = kind == FireworkKind.Explosion
                           ? GetByKey(_crackleByKey, CrackleDir, soundKey)
                           : null;
        if (crackleClips == null || crackleClips.Count == 0) crackleClips = _crackle;

        if (crackleClips.Count > 0)
        {
            float useDelay = crackleDelayOverride >= 0f ? crackleDelayOverride : crackleDelay;
            PlayOne(crackleClips, worldPosition, crackleVolume * volumeScale, 1f,
                    delay + useDelay);
        }
    }

    // soundKey 専用のクリップがあればそれを、無ければ既存の共通プールを返す。
    // 画像型（Image）は型別ルーティングの対象外なので、常に _image を返す。
    //
    // AudioVariant（宇宙モードなど）が設定されているときは、まず
    // Sfx/Burst/<AudioVariant>/<soundKey>/ を試す（3段フォールバックの1段目）。
    // 見つからなければ従来の Sfx/Burst/<soundKey>/ → 共通プールの順（2・3段目）に
    // そのまま流れる。行き止まりを作らないのはこのファイル全体の方針と同じ
    private List<AudioClip> ResolveBurstClips(FireworkKind kind, string soundKey)
    {
        if (kind == FireworkKind.Image) return _image;

        if (!string.IsNullOrEmpty(AudioVariant) && !string.IsNullOrEmpty(soundKey))
        {
            var variantTyped = GetByKey(_burstByKey, BurstDir, $"{AudioVariant}/{soundKey}");
            if (variantTyped != null && variantTyped.Count > 0) return variantTyped;
        }

        var typed = GetByKey(_burstByKey, BurstDir, soundKey);
        return (typed != null && typed.Count > 0) ? typed : _burst;
    }

    /// <summary>
    /// 同じジェスチャーで2発上がるときに使う。
    /// 完全に同時だと音量が倍になって不自然なので、2発目をわずかにずらす。
    ///
    /// 【注意】現在の FireworkLauncher は両手上げの2発を独立した
    /// LaunchSequence コルーチンとして起動し、それぞれが個別に PlayBurst を呼ぶため、
    /// このメソッドは呼ばれていない（上昇フェーズを追加した際に置き換わった）。
    /// API として使い勝手が良いので残してあるが、削除しても現状の動作は変わらない。
    /// </summary>
    public void PlayBurstPair(Vector3 firstPosition, Vector3 secondPosition,
                              FireworkKind firstKind, FireworkKind secondKind, bool isLarge,
                              string firstSoundKey = null, string secondSoundKey = null)
    {
        PlayBurst(firstPosition, firstKind, isLarge, firstSoundKey);

        float delay = Random.Range(pairDelayRange.x, pairDelayRange.y);
        PlayBurst(secondPosition, secondKind, isLarge, secondSoundKey, delay, pairSecondVolume);
    }

    // ── 共通の再生処理 ──
    private void PlayOne(List<AudioClip> clips, Vector3 worldPosition,
                         float volume, float pitch, float delay)
    {
        var clip = clips[Random.Range(0, clips.Count)];
        if (clip == null) return;

        var src = NextVoice();
        src.transform.position = worldPosition;
        src.clip         = clip;
        src.spatialBlend = spatialBlend;
        src.minDistance  = minDistance;
        src.maxDistance  = maxDistance;

        src.volume = Mathf.Clamp01(volume * (1f + Random.Range(-volumeJitter, volumeJitter)));
        src.pitch  = pitch * (1f + Random.Range(-pitchJitter, pitchJitter));

        if (delay > 0f) src.PlayDelayed(delay);
        else            src.Play();
    }

    // 使い回しの AudioSource を順番に返す。
    // 鳴っていないものを優先し、全部鳴っていたら一番古いものを奪う
    private AudioSource NextVoice()
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            var src = _voices[(_nextVoice + i) % _voices.Length];
            if (!src.isPlaying)
            {
                _nextVoice = (_nextVoice + i + 1) % _voices.Length;
                return src;
            }
        }

        var oldest = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % _voices.Length;
        return oldest;
    }
}

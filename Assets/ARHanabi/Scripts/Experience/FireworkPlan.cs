using UnityEngine;

// ===== FireworkPlan =====
// 「状況 → 打ち上げ内容（LaunchRequest）」の変換表。ExperienceDirector から呼ばれる。
// Director 側は「いつ・何回」を決め、こちらは「どんな LaunchRequest にするか」だけを決める。
// 見せ方（型の絞り込み・大きさ・volumeScale など）を変えたいときはここだけ触ればよい。
//
// 個人のコンボ演出（Individual）と一緒に判定の演出（Ensemble）を持つ。
// アトラクト・フィナーレ・隠し操作は実装しない方針になったため、ここには無い
public static class FireworkPlan
{
    // ── 個人のコンボ演出 ──
    // ComboTracker.Register が返す段階（1〜finisherCount）を、実際に打つ花火の内容に変換する。
    //   段階1: 従来どおり（両手=大玉2発、片手/ジャンプ=1発）。コンボの初回はいつもの花火でよい
    //   段階2・3: 小さく軽い1発（連打を「積み上げ」として見せる。1発ごとに作り物感が出ないよう軽め）
    //   段階4: 大玉1発
    //   段階5（フィニッシャー）: 大玉2発（左右分離。段階1の両手上げと同じ見せ方）
    //
    // trailStage は昇りの光跡を育てる段階（0=通常。ComboTrailEnabled が OFF なら
    // 呼び出し側が 0 を渡す。FireworkLauncher 側の見た目のみに影響し、型の抽選には関与しない）
    public static LaunchRequest[] Individual(
        GestureType gesture, int comboStage, Vector2 normalizedPos,
        float pairSeparationViewport, float jumpSpreadViewport, int trailStage,
        string[] comboLightNames, string[] comboBigNames, string[] comboFinisherNames)
    {
        switch (comboStage)
        {
            case <= 1:
                return IndividualStage1(gesture, normalizedPos, trailStage,
                                        pairSeparationViewport, jumpSpreadViewport);

            case 2:
                return new[] { BuildStageShot(normalizedPos, 0f, false, 0.7f, comboLightNames, trailStage) };

            case 3:
                return new[] { BuildStageShot(normalizedPos, 0f, false, 0.85f, comboLightNames, trailStage) };

            case 4:
                return new[] { BuildStageShot(normalizedPos, 0f, true, 0.9f, comboBigNames, trailStage) };

            default: // 5以上（フィニッシャー）
                return new[]
                {
                    BuildStageShot(normalizedPos, -pairSeparationViewport * 0.5f, true, 1.15f,
                                   comboFinisherNames, trailStage),
                    BuildStageShot(normalizedPos, pairSeparationViewport * 0.5f, true, 1.15f,
                                   comboFinisherNames, trailStage,
                                   startDelay: Random.Range(0.06f, 0.18f), volumeScale: 0.8f),
                };
        }
    }

    // 段階1（コンボの初回）は従来の LaunchForGesture の switch と同じ内容にする。
    // Director が RoutesGestures=true でジェスチャーを引き取ったあとも、
    // 最初の1回の見た目・音は変えないため
    private static LaunchRequest[] IndividualStage1(
        GestureType gesture, Vector2 pos, int trailStage,
        float pairSeparationViewport, float jumpSpreadViewport)
    {
        switch (gesture)
        {
            case GestureType.BothHandsUp:
                return new[]
                {
                    new LaunchRequest
                    {
                        normalizedPos = pos, xOffsetViewport = -pairSeparationViewport * 0.5f,
                        isLarge = true, volumeScale = 1f, comboStage = trailStage,
                    },
                    new LaunchRequest
                    {
                        normalizedPos = pos, xOffsetViewport = pairSeparationViewport * 0.5f,
                        isLarge = true, volumeScale = 0.8f, comboStage = trailStage,
                        startDelay = Random.Range(0.06f, 0.18f),
                    },
                };

            case GestureType.OneHandUp:
                return new[]
                {
                    new LaunchRequest { normalizedPos = pos, isLarge = false, volumeScale = 1f, comboStage = trailStage },
                };

            case GestureType.Jump:
                return new[]
                {
                    new LaunchRequest
                    {
                        normalizedPos = pos, isLarge = false, volumeScale = 1f, comboStage = trailStage,
                        xOffsetViewport = Random.Range(-jumpSpreadViewport, jumpSpreadViewport),
                    },
                };

            default:
                // 未知の型は安全側で小玉1発にする
                return new[]
                {
                    new LaunchRequest { normalizedPos = pos, isLarge = false, volumeScale = 1f, comboStage = trailStage },
                };
        }
    }

    private static LaunchRequest BuildStageShot(
        Vector2 pos, float xOffset, bool isLarge, float sizeScale,
        string[] nameFilter, int trailStage, float startDelay = 0f, float volumeScale = 1f)
    {
        return new LaunchRequest
        {
            normalizedPos   = pos,
            xOffsetViewport = xOffset,
            isLarge         = isLarge,
            sizeScale       = sizeScale,
            nameFilter      = nameFilter,
            comboStage      = trailStage,
            startDelay      = startDelay,
            volumeScale     = volumeScale,
        };
    }

    // ── 一緒に判定の演出 ──
    //   2人: 特別な1発（中間位置、大玉、少し大きめ）
    //   3人以上: ミニスターマイン（3〜5発を少しずつずらして連続発射。うち1発だけ特別な型）
    //
    // centerPos は関節座標（landmark空間）の平均。GestureEnsemble.FireResult.center をそのまま渡す想定
    // （landmark空間での平均→1回のQuad変換は、変換後の平均と同じ結果になる。線形変換のため）
    public static LaunchRequest[] Ensemble(int level, Vector2 centerPos, string[] ensembleShellNames)
    {
        if (level <= 2)
        {
            return new[]
            {
                new LaunchRequest
                {
                    normalizedPos = centerPos,
                    isLarge       = true,
                    sizeScale     = 1.25f,
                    nameFilter    = ensembleShellNames,
                    volumeScale   = 1f,
                },
            };
        }

        int shotCount     = Mathf.Clamp(level, 3, 5);
        var shots         = new LaunchRequest[shotCount];
        int specialIndex  = Random.Range(0, shotCount);

        for (int i = 0; i < shotCount; i++)
        {
            shots[i] = new LaunchRequest
            {
                normalizedPos   = centerPos,
                xOffsetViewport = Random.Range(-0.15f, 0.15f),
                isLarge         = true,
                sizeScale       = i == specialIndex ? 1.1f : 1f,
                nameFilter      = i == specialIndex ? ensembleShellNames : null,
                startDelay      = i * 0.18f,
                volumeScale     = i == 0 ? 1f : 0.85f,
            };
        }

        return shots;
    }
}

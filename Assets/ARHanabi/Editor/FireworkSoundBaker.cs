#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// ===== FireworkSoundBaker =====
// FireworkSoundSynth で合成した効果音を WAV として書き出す Editor 拡張。
//
// 使い方:
//   1. メニュー ARHanabi > 花火の効果音を生成
//   2. Assets/ARHanabi/Audio/Generated/ に .wav が並ぶ
//   3. Project ウィンドウで .wav を選ぶと Inspector 下部で試聴できる
//   4. 気に入らない音は FireworkSoundSynth.DefaultSet() の数値を変えて再実行する
//
// 何度実行しても同じ結果になる（合成側が自前の決定論的乱数を使っている）。
// 上書きなので、手で差し替えたファイルを残したい場合は Generated の外へ move すること。
//
// AdminUIBuilder と同じ「メニューから叩く道具」の形に揃えてある。

public static class FireworkSoundBaker
{
    private const string OutputDir = "Assets/ARHanabi/Audio/Generated";

    [MenuItem("ARHanabi/花火の効果音を生成", false, 200)]
    public static void Bake()
    {
        Directory.CreateDirectory(OutputDir);

        var recipes = FireworkSoundSynth.DefaultSet();
        var log     = new StringBuilder();
        log.AppendLine($"[SoundBaker] {recipes.Count} 本を生成 → {OutputDir}");

        int written = 0;

        foreach (var recipe in recipes)
        {
            var samples = FireworkSoundSynth.Render(recipe);
            var wav     = FireworkSoundSynth.EncodeWav16(samples, FireworkSoundSynth.SampleRate);

            string path = $"{OutputDir}/{recipe.name}.wav";
            File.WriteAllBytes(path, wav);
            written++;

            float seconds = samples.Length / (float)FireworkSoundSynth.SampleRate;
            log.AppendLine($"  {recipe.name,-22} {seconds,5:F2}s  " +
                           $"peak={FireworkSoundSynth.Peak(samples):F3} " +
                           $"rms={FireworkSoundSynth.Rms(samples):F3}  " +
                           $"{wav.Length / 1024,4} KB");
        }

        AssetDatabase.Refresh();

        // 3D 定位（花火が出た画面位置から鳴らす）を将来効かせられるよう、
        // モノラルのまま強制ロードにしておく。SFX なので圧縮せず生で持つ
        foreach (var recipe in recipes)
            ApplyImportSettings($"{OutputDir}/{recipe.name}.wav");

        log.AppendLine($"[SoundBaker] 完了。{written} 本。Project ウィンドウで選ぶと試聴できます");
        Debug.Log(log.ToString());
    }

    [MenuItem("ARHanabi/生成した効果音のフォルダを開く", false, 201)]
    public static void RevealOutputFolder()
    {
        Directory.CreateDirectory(OutputDir);
        EditorUtility.RevealInFinder(OutputDir);
    }

    // 効果音は短いので、解凍待ちが起きないよう非圧縮＋メモリ常駐にする
    private static void ApplyImportSettings(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
        if (importer == null) return;

        // preloadAudioData は Unity 6 で AudioImporter 直下から
        // AudioImporterSampleSettings（プラットフォーム別設定）へ移動している
        var settings = importer.defaultSampleSettings;
        settings.loadType          = AudioClipLoadType.DecompressOnLoad;
        settings.compressionFormat = AudioCompressionFormat.PCM;
        settings.preloadAudioData  = true;

        importer.defaultSampleSettings = settings;
        importer.forceToMono           = true;   // 3D 定位にはモノラルが必要

        importer.SaveAndReimport();
    }
}
#endif

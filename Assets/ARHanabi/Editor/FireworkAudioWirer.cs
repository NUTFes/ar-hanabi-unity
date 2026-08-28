#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// ===== FireworkAudioWirer =====
// Resources/Sfx/ 配下の効果音クリップのインポート設定を揃える Editor 拡張。
//
// ── 設計の変遷（読む人が古い版と混同しないための記録）──
//   最初の実装は「シーン上の FireworkAudioPlayer の List<AudioClip> フィールドに
//   SerializedObject で直接クリップを割り当てる」方式だった。
//   しかしシーンに保存された FireworkAudioPlayer には新しいフィールドが存在しないため
//   割り当てが反映されず、「配線メニューを実行しないと音が鳴らない」壊れやすい状態になった。
//
//   そこで FireworkAudioPlayer 側を Resources.LoadAll() による自動読み込みに変更した
//   （Assets/ARHanabi/Resources/Sfx/{Launch,Burst,Crackle}/ に置くだけで反映される）。
//   クリップの割り当て自体はもうこのツールの仕事ではない。
//
//   残っている仕事は「インポート設定を揃えること」だけ。
//   3D定位にはモノラル素材が必要で、効果音は短いので非圧縮＋メモリ常駐が望ましい。
//   Firefly などで新しく生成したファイルをフォルダに置いたら、このメニューを
//   実行すれば設定が揃う（実行しなくても再生自体はできる。設定を最適化するだけ）。
//
// 使い方:
//   メニュー ARHanabi > 効果音のインポート設定を整える

public static class FireworkAudioWirer
{
    private static readonly string[] TargetDirs =
    {
        "Assets/ARHanabi/Resources/Sfx/Launch",
        "Assets/ARHanabi/Resources/Sfx/Burst",
        "Assets/ARHanabi/Resources/Sfx/Crackle",
    };

    [MenuItem("ARHanabi/効果音のインポート設定を整える", false, 220)]
    public static void Wire()
    {
        var log = new StringBuilder();
        log.AppendLine("[AudioWirer] インポート設定の確認を開始");

        int total = 0, changed = 0;

        foreach (var dir in TargetDirs)
        {
            var clips = LoadClips(dir, log);
            total += clips.Count;

            foreach (var clip in clips)
            {
                if (ApplyImportSettings(AssetDatabase.GetAssetPath(clip), log))
                    changed++;
            }
        }

        log.AppendLine($"[AudioWirer] 完了。{total} 本を確認、{changed} 本の設定を調整しました。");
        if (total == 0)
        {
            log.AppendLine("  [WARN] クリップが1本もありません。" +
                           "Assets/ARHanabi/Resources/Sfx/{Launch,Burst,Crackle}/ に wav を置いてください");
        }
        Debug.Log(log.ToString());
    }

    // 指定フォルダの AudioClip を名前順に集める（無ければ空リスト）
    private static List<AudioClip> LoadClips(string dir, StringBuilder log)
    {
        var result = new List<AudioClip>();
        if (!Directory.Exists(dir))
        {
            log.AppendLine($"  {dir} は存在しません（スキップ）");
            return result;
        }

        var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { dir });
        var paths = new List<string>();
        foreach (var guid in guids) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
        paths.Sort();

        foreach (var path in paths)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            result.Add(clip);
            log.AppendLine($"  見つけた: {path} ({clip.length:F2}s {clip.channels}ch {clip.frequency}Hz)");
        }
        return result;
    }

    // 3D定位にはモノラルが必要。効果音は短いので非圧縮＋メモリ常駐にする。
    // 変更が無ければ SaveAndReimport を呼ばない（毎回全ファイルを再インポートしないため）
    private static bool ApplyImportSettings(string assetPath, StringBuilder log)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
        if (importer == null) return false;

        var settings = importer.defaultSampleSettings;
        bool changed = false;

        if (settings.loadType != AudioClipLoadType.DecompressOnLoad)
        {
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            changed = true;
        }
        if (settings.compressionFormat != AudioCompressionFormat.PCM)
        {
            settings.compressionFormat = AudioCompressionFormat.PCM;
            changed = true;
        }
        if (!settings.preloadAudioData)
        {
            settings.preloadAudioData = true;
            changed = true;
        }
        if (!importer.forceToMono)
        {
            importer.forceToMono = true;
            changed = true;
        }

        if (!changed) return false;

        importer.defaultSampleSettings = settings;
        importer.SaveAndReimport();
        log.AppendLine($"  インポート設定を調整: {Path.GetFileName(assetPath)}（モノラル・非圧縮・常駐）");
        return true;
    }
}
#endif

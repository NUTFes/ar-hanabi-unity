#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// ===== FireworkSourceAnalyzer =====
// 既存の音源（fireworks.mp3 など）を Unity にデコードさせて
//   ・PCM を WAV として書き出す
//   ・スペクトルとエンベロープを Console に出す
// ための Editor 拡張。
//
// ── なぜ必要か ──
//   合成音を「実録音に寄せる」には、まず実録音の数値が要る。ところが素材は MP3 で、
//   デコーダを別途用意しないと中身の PCM が取れない。
//   Unity はインポート時に既にデコードしているので、AudioClip.GetData() で借りればよい。
//   書き出した WAV は、そのまま合成の素材（スライス元・グラニュラーの種）にも使える。
//
// 使い方:
//   1. Project ウィンドウで AudioClip を選ぶ（複数選択可）
//      何も選ばなければ Assets/ARHanabi/Audio/fireworks.mp3 を対象にする
//   2. メニュー ARHanabi > 音源を解析して WAV に書き出す
//   3. Assets/ARHanabi/Audio/Analyzed/<名前>_decoded.wav ができる
//   4. Console に測定値が出る（重心・平坦度・帯域・エンベロープ）
//
// ── 出力の扱い ──
//   このツールが書き出すのは元素材をデコードしただけのファイル。
//   効果音ラボの FAQ ではアプリへの組み込みを再配布とみなさないので、
//   コミットしても規約違反ではない（判断基準は「効果音そのものを配る行為か」）。
//   それでも出力先の Analyzed/ を .gitignore に入れてあるのは、
//   元素材から機械的に作り直せる中間ファイルで履歴に残す価値が無いため。
//   他サイトの素材を解析する場合は、その素材の規約を個別に確認すること。
//
// 注意:
//   AudioClip.GetData() を使うには、そのクリップの Load Type が
//   Decompress On Load でないと中身が取れないことがある。
//   取れなかった場合は Console に理由を出すので、Inspector で切り替えてから再実行する。

public static class FireworkSourceAnalyzer
{
    private const string OutputDir    = "Assets/ARHanabi/Audio/Analyzed";
    private const string DefaultAsset = "Assets/ARHanabi/Audio/fireworks.mp3";

    [MenuItem("ARHanabi/音源を解析して WAV に書き出す", false, 210)]
    public static void AnalyzeSelection()
    {
        var clips = Selection.GetFiltered<AudioClip>(SelectionMode.Assets);

        if (clips == null || clips.Length == 0)
        {
            var fallback = AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultAsset);
            if (fallback == null)
            {
                Debug.LogWarning($"[SourceAnalyzer] AudioClip が選択されておらず、" +
                                 $"既定の {DefaultAsset} も見つかりません");
                return;
            }
            clips = new[] { fallback };
            Debug.Log($"[SourceAnalyzer] 選択が無いので既定の {DefaultAsset} を対象にします");
        }

        Directory.CreateDirectory(OutputDir);

        var log = new StringBuilder();
        log.AppendLine($"[SourceAnalyzer] {clips.Length} 件を解析");

        foreach (var clip in clips)
        {
            if (!TryGetMono(clip, out var mono, out string error))
            {
                log.AppendLine($"  [SKIP] {clip.name}: {error}");
                continue;
            }

            // ── WAV に書き出す（合成の素材としても使えるようにする）──
            var wav  = FireworkSoundSynth.EncodeWav16(mono, clip.frequency);
            string path = $"{OutputDir}/{clip.name}_decoded.wav";
            File.WriteAllBytes(path, wav);

            // ── 測る ──
            var report = AudioSpectrum.Analyze(mono, clip.frequency);
            report.name = $"{clip.name}  (元: {clip.channels}ch {clip.frequency}Hz " +
                          $"{clip.length:F2}s / {clip.loadType})";

            log.AppendLine();
            log.Append(AudioSpectrum.Format(report));
            log.AppendLine($"  → {path} ({wav.Length / 1024} KB)");
        }

        AssetDatabase.Refresh();
        log.AppendLine();
        log.AppendLine($"[メモ] {OutputDir} は解析用の中間ファイルです。" +
                       ".gitignore で除外してあります（いつでも作り直せるため）");
        Debug.Log(log.ToString());
    }

    // AudioClip からモノラルの float 配列を取り出す。
    // ステレオはチャンネル平均でモノ化する（3D 定位に使うにはモノラルが必要なため）。
    private static bool TryGetMono(AudioClip clip, out float[] mono, out string error)
    {
        mono  = null;
        error = null;

        if (clip == null) { error = "clip が null"; return false; }

        int total = clip.samples * clip.channels;
        if (total <= 0) { error = "サンプル数が 0"; return false; }

        var interleaved = new float[total];
        if (!clip.GetData(interleaved, 0))
        {
            error = $"GetData に失敗（loadType={clip.loadType}）。" +
                    "Inspector で Load Type を Decompress On Load にしてから再実行してください";
            return false;
        }

        int ch = clip.channels;
        if (ch == 1)
        {
            mono = interleaved;
            return true;
        }

        mono = new float[clip.samples];
        for (int i = 0; i < clip.samples; i++)
        {
            float sum = 0f;
            for (int c = 0; c < ch; c++) sum += interleaved[i * ch + c];
            mono[i] = sum / ch;
        }
        return true;
    }
}
#endif

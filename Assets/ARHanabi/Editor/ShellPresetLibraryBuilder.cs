#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

// ===== ShellPresetLibraryBuilder =====
// ShellPreset.DefaultLibrary()（コード側の既定16種）から
// Assets/ARHanabi/Settings/ShellPresets.asset を作る／作り直すメニュー。
//
// ── なぜ「作り直す」を上書きではなく確認付きにしたか ──
//   この Asset は調整の作業ファイルそのものになる（再生中に触った値がここに残る）。
//   メニューを押しただけで数時間分の調整が既定値へ巻き戻ると被害が大きいので、
//   既に中身がある場合は必ずダイアログで確認する。
//
// ── AdminUIBuilder と同じ流儀 ──
//   Rebuild 系のメニューは ARHanabi/ 配下に集約されている（AdminUIBuilder 参照）。
//   ここも同じ場所に置いて、運用手順を1箇所で覚えられるようにしてある。

public static class ShellPresetLibraryBuilder
{
    private const string AssetDir  = "Assets/ARHanabi/Settings";
    private const string AssetPath = AssetDir + "/ShellPresets.asset";

    [MenuItem("ARHanabi/花火プリセットの Asset を生成", false, 110)]
    public static void Generate()
    {
        var existing = AssetDatabase.LoadAssetAtPath<ShellPresetLibrary>(AssetPath);

        if (existing != null && existing.HasAny)
        {
            bool ok = EditorUtility.DisplayDialog(
                "花火プリセットの Asset を作り直す",
                $"{AssetPath} には既に {existing.presets.Count} 件入っています。\n\n" +
                "コード側の既定16種（ShellPreset.DefaultLibrary）で上書きすると、\n" +
                "この Asset で調整した値は失われます。続けますか？",
                "上書きする", "やめる");

            if (!ok) return;

            Undo.RecordObject(existing, "花火プリセットを既定へ戻す");
            existing.ResetToDefaultLibrary();
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ShellPresets] {AssetPath} を既定 {existing.presets.Count} 件で上書きしました");
            Selection.activeObject = existing;
            return;
        }

        if (!Directory.Exists(AssetDir))
            Directory.CreateDirectory(AssetDir);

        var library = existing != null
                      ? existing
                      : ScriptableObject.CreateInstance<ShellPresetLibrary>();

        library.ResetToDefaultLibrary();

        if (existing == null)
            AssetDatabase.CreateAsset(library, AssetPath);
        else
            EditorUtility.SetDirty(library);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[ShellPresets] {AssetPath} を生成しました（{library.presets.Count} 件）。\n" +
                  "MainScene の FireworkLauncher の presetLibrary にこの Asset を割り当ててください");

        Selection.activeObject = library;
    }
}
#endif

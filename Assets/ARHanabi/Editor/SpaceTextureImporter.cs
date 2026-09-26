#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// ===== SpaceTextureImporter =====
// Assets/ARHanabi/Resources/Space/ 配下のテクスチャのインポート設定を自動で整える。
//
// ── なぜ AssetPostprocessor（自動）にしたか ──
//   効果音は FireworkAudioWirer のメニュー実行でインポート設定を整えているが、
//   あちらは「wav を後から足すことが多い」ため手動でよかった。
//   こちらのコックピット枠は、素材を差し替えるたびに毎回メニューを実行させると
//   忘れた瞬間に見た目が壊れる（下記の既定値のまま入ってしまう）。
//   このプロジェクト全体の「機能追加に手作業を要求しない」方針に合わせて、
//   インポート時に自動で適用されるようにした。
//
// ── 既定のインポート設定で困ること ──
//   alphaIsTransparency = 0
//     Unity が「アルファは透明度である」と知らないため、透明部分の色を考慮した
//     にじみ処理（alpha-aware color bleed）が行われない。結果、半透明の縁
//     （枠の内縁を走るシアンの発光ラインなど）の周りに黒いフリンジが出る。
//   wrapMode = Repeat
//     枠は画面いっぱいに1枚貼るだけなので繰り返す必要がない。バイリニア補間が
//     画像の端で反対側の端を拾うと、枠の外周に細い線が出ることがある。
//   textureCompression = Compressed
//     DXT/BC 圧縮はブロック単位で色を丸めるため、1〜2px の細い発光ラインや
//     5x7 ドットの小さな文字（"AURORA X-1" など）がにじんで潰れる。
//     この枠は画面に1枚しか出ないので、非圧縮にしても割に合う。
//   mipmap = 有効
//     Quad は視錐台にぴったり合わせて等倍で描くので mip は使われない。
//     切っておけばメモリも節約でき、縮小によるボケの心配もなくなる。
public class SpaceTextureImporter : AssetPostprocessor
{
    private const string TargetDir = "Assets/ARHanabi/Resources/Space/";

    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(TargetDir, System.StringComparison.OrdinalIgnoreCase)) return;

        var importer = (TextureImporter)assetImporter;

        importer.textureType         = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.alphaSource         = TextureImporterAlphaSource.FromInput;
        importer.wrapMode            = TextureWrapMode.Clamp;
        importer.filterMode          = FilterMode.Bilinear;
        importer.mipmapEnabled       = false;
        importer.npotScale           = TextureImporterNPOTScale.None;
        importer.maxTextureSize      = 2048;
        importer.textureCompression  = TextureImporterCompression.Uncompressed;

        Debug.Log($"[SpaceTex] インポート設定を適用: {assetPath}" +
                  "（透明度あり・Clamp・非圧縮・mipmapなし）");
    }
}
#endif

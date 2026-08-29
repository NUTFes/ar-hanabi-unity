using System;
using UnityEngine;

// ===== FireworkEntry =====
// 花火1件分のデータを保持するクラス
//
// 現在: ローカル画像から生成（Admin画面で登録）
// 将来: GET /fireworks のレスポンス1件に対応
//        id, isShareable, imageUrl をAPIから取得し
//        localTexture をダウンロードして差し替える

[Serializable]
public class FireworkEntry
{
    // ── ローカル管理フィールド ──
    public string      displayName;    // 管理画面での表示名
    public Texture2D   localTexture;   // ローカル画像（現在のメイン入力）
    public bool        isActive;       // 有効/無効（Activateされているか）

    // ── 変換済みキャッシュ ──
    public bool        isConverted;
    public ParticleData particleData;

    // 画像花火の細かさ（n×n の n）。1件ごとに Admin画面から変えられる。
    // 0 = 未設定で、この場合は FireworkManager.conversionSettings.resolution が使われる。
    // 変換時に実際に使った値がここへ書き戻されるので、変換後は必ず具体値が入る。
    //
    // 永続化しないのは、エントリ自体がメモリ上にしか無く、
    // 起動のたびにAPIから取り直されるため（保存しても対応先が消えている）
    public int         resolution;

    // ── 将来のAPI連携フィールド（現在未使用）──
    public int    id       = -1;
    public bool   isShareable;
    public string imageUrl;
    public string createdAt;

    // ローカル画像用コンストラクタ
    public FireworkEntry(string name, Texture2D texture)
    {
        displayName  = name;
        localTexture = texture;
        isActive     = false;
        isConverted  = false;
        createdAt    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // API用コンストラクタ（将来実装）
    public FireworkEntry(int apiId, string name, string url, bool shareable)
    {
        id          = apiId;
        displayName = name;
        imageUrl    = url;
        isShareable = shareable;
        isActive    = false;
        isConverted = false;
    }
}

// ===== ParticleData =====
// 変換済みパーティクルデータ（Web側の ParticleData 型と対応）
[Serializable]
public class ParticleData
{
    public int           width;
    public int           height;
    public ParticlePoint[] particles;

    public ParticleData(int w, int h, ParticlePoint[] pts)
    {
        width     = w;
        height    = h;
        particles = pts;
    }
}

// ===== ParticlePoint =====
// 1パーティクルの位置と色（Web側の { x, y, r, g, b, size } と対応）
//
// a（アルファ）はUnity側の追加フィールド。
// ImageToParticles の whiteAlpha を反映させるために持たせている。
// 既存の呼び出しを壊さないよう a は末尾のデフォルト引数（省略時は不透明）。
[Serializable]
public struct ParticlePoint
{
    public float x;    // 0.0〜1.0 正規化座標
    public float y;    // 0.0〜1.0 正規化座標
    public byte  r, g, b, a;
    public float size;

    public ParticlePoint(float x, float y, byte r, byte g, byte b, float size = 1f, byte a = 255)
    {
        this.x = x; this.y = y;
        this.r = r; this.g = g; this.b = b; this.a = a;
        this.size = size;
    }

    public Color32 ToColor32() => new Color32(r, g, b, a);
    public Color   ToColor()   => new Color(r / 255f, g / 255f, b / 255f, a / 255f);
}
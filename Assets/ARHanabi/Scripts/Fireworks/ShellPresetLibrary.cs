using System.Collections.Generic;
using UnityEngine;

// ===== ShellPresetLibrary =====
// 打ち上げ花火の型（ShellPreset）をまとめて持つ Asset。
//
// ── なぜ Asset にしたか。Inspector で編集したいからではない ──
//   本当の理由は「再生中に編集した値が、再生を止めても残る」から。
//
//   花火は1発が数秒しか映らないので、値を詰めるには
//   「打つ → 見る → 直す → もう一度打つ」を短く回すしかない。
//   ところが従来はプリセットが ShellPreset.DefaultLibrary() のハードコードで、
//   1値変えるたびに C# 編集 → 再コンパイル（十数秒）→ 再生し直し、だった。
//
//   かといって FireworkLauncher の Inspector 配列（shellPresets）に置いても、
//   シーン上のコンポーネントの値は Play を抜けた瞬間に巻き戻る。
//   つまり「再生中に良い値を見つけたのに、止めたら消える」。
//
//   ScriptableObject の Asset はこの巻き戻りが起きない。
//   再生したまま Inspector で触って打ち直し、良い値が出たらそのまま止めればよい。
//   これが型ごとの調整を現実的な速さにする唯一の手段だったので Asset にした。
//
// ── DefaultLibrary() を消していない理由 ──
//   この Asset が未設定でも（あるいは誤って消されても）花火が出なくなると困る。
//   FireworkLauncher.PickPreset は
//     presetLibrary → shellPresets[]（旧来のInspector配列）→ DefaultLibrary()
//   の順に解決するので、Asset は「あれば使う」上書きに過ぎない。
//   DefaultLibrary() はコード側の既定であり、この Asset の生成元でもある
//   （メニュー ARHanabi/花火プリセットの Asset を生成）。

[CreateAssetMenu(fileName = "ShellPresets",
                 menuName = "ARHanabi/花火プリセット集",
                 order    = 100)]
public class ShellPresetLibrary : ScriptableObject
{
    [Tooltip("打ち上げ花火の型。空のときは FireworkLauncher が\n" +
             "ShellPreset.DefaultLibrary() の既定16種にフォールバックする")]
    public List<ShellPreset> presets = new();

    /// <summary>中身が1件でもあるか。空の Asset を割り当てても既定へ落とせるようにする</summary>
    public bool HasAny => presets != null && presets.Count > 0;

    /// <summary>コード側の既定16種で中身を差し替える（Editor の生成メニューから使う）</summary>
    public void ResetToDefaultLibrary()
    {
        presets = ShellPreset.DefaultLibrary();
    }
}

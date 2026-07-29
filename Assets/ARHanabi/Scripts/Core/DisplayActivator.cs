using UnityEngine;

// ===== DisplayActivator =====
// マルチディスプレイ構成のセットアップ担当。
//
// MainScene の構成:
//   Main Camera  … 花火を映すカメラ（targetDisplay: 1）
//   Admin Camera … 管理画面のカメラ（targetDisplay: 0）
//
// ディスプレイが2台あるときは Display 1 を Activate() すればそのまま動くが、
// 1台しか無い環境では targetDisplay: 1 の描画先が存在せず、
// 花火が一切映らないまま無言で終わる。その場合は targetDisplay を 0 に
// フォールバックさせて、少なくとも花火が見える状態にする。
//
// ── エディタでフォールバックしてはいけない理由 ──
// Display.displays.Length は Unity Editor では実際のモニタ数に関係なく
// 常に 1 を返す（マルチディスプレイは standalone プレイヤーのみで機能する）。
// そのためエディタでこのフォールバックを走らせると、毎回 Main Camera の
// targetDisplay が 1 → 0 に書き換えられ、Game view の Display 2 に向く
// カメラが無くなって "No cameras rendering" になってしまう。
// エディタでは Display 2 は targetDisplay の指定だけで正しく描画されるので、
// フォールバックはプレイヤービルド時のみ行う。

public class DisplayActivator : MonoBehaviour
{
    // ── Inspector ──
    [Header("フォールバック")]
    [Tooltip("ディスプレイが1台しかない場合、targetDisplay >= 1 のカメラを Display 0 に切り替える\n" +
             "※ エディタでは Display.displays.Length が常に 1 になるため無効（Game view の\n" +
             "　 Display 2 が映らなくなるのを避けるため）")]
    [SerializeField] private bool fallbackToPrimaryDisplay = true;

    // ── ライフサイクル ──
    private void Start()
    {
        int displayCount = Display.displays.Length;

        if (displayCount > 1)
        {
            Display.displays[1].Activate();
            Debug.Log($"[Display] {displayCount} displays detected. Activated Display 1");
            return;
        }

        // エディタでは displayCount が常に 1 なので、ここに来ても実機の台数は分からない。
        // Game view は targetDisplay だけで Display 2 を描けるため何もしない。
        if (Application.isEditor)
        {
            Debug.Log("[Display] Editor では Display.displays.Length が常に 1 のため " +
                      "フォールバックしない（Game view の Display 切り替えで確認できる）");
            return;
        }

        // ── ここから1台構成（プレイヤービルドのみ）──
        if (!fallbackToPrimaryDisplay)
        {
            Debug.LogWarning("[Display] Only 1 display detected and fallbackToPrimaryDisplay is OFF. " +
                             "Cameras with targetDisplay >= 1 will render nowhere");
            return;
        }

        FallbackCamerasToPrimaryDisplay();
    }

    // ── 1台構成へのフォールバック ──
    private void FallbackCamerasToPrimaryDisplay()
    {
        // Camera.allCameras は非アクティブなカメラを含まないので使わない
        var cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        int moved = 0;
        foreach (var cam in cameras)
        {
            if (cam == null || cam.targetDisplay < 1) continue;

            int from = cam.targetDisplay;
            cam.targetDisplay = 0;
            moved++;

            Debug.LogWarning($"[Display] Only 1 display detected: switched camera '{cam.name}' " +
                             $"from Display {from} to Display 0 " +
                             $"（1台構成のため '{cam.name}' の描画先を Display {from} → Display 0 に切り替えた）");
        }

        if (moved == 0)
            Debug.Log("[Display] Only 1 display detected. No camera needed a fallback");
        else
            Debug.LogWarning($"[Display] Fallback applied to {moved} camera(s) at runtime. " +
                             "シーンの targetDisplay 設定自体は変更していないので、" +
                             "2台構成の環境では従来どおり Display 1 に描画される");
    }
}

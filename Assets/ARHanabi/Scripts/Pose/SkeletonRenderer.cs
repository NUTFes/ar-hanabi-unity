using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

public class SkeletonRenderer : MonoBehaviour
{
    [Header("設定")]
    [SerializeField] private Camera mainCamera;
    [SerializeField] private float drawDistance = 9f;   // CameraBackground(Z=5)より手前
    [SerializeField] private float lineWidth = 0.02f;
    [SerializeField] private Material lineMaterial;

    // MediaPipe Pose ランドマーク接続定義（22点）
    private static readonly (int, int)[] _connections = new[]
    {
        // 上半身
        (11, 12), (11, 13), (13, 15),
        (12, 14), (14, 16),
        // 体幹
        (11, 23), (12, 24), (23, 24),
        // 下半身
        (23, 25), (25, 27), (27, 29), (29, 31),
        (24, 26), (26, 28), (28, 30), (30, 32),
    };

    // 人ごとのLineRendererを管理
    private readonly Dictionary<int, List<LineRenderer>> _personLines = new();
    private readonly Dictionary<int, float> _lastUpdateTime = new();
    private const float _TimeoutSeconds = 0.5f; // この秒数更新がなければ非表示

    private void Update()
    {
        // タイムアウトした人のスケルトンを非表示
        var now = Time.time;
        foreach (var kvp in _lastUpdateTime)
        {
            if (now - kvp.Value > _TimeoutSeconds && _personLines.ContainsKey(kvp.Key))
            {
                SetPersonVisible(kvp.Key, false);
            }
        }
    }

    public void UpdateSkeleton(int personIndex, List<NormalizedLandmark> landmarks)
    {
        // _connections が参照する最大インデックスは32 → 33点必要
        const int requiredLandmarkCount = 33;
        if (landmarks == null || landmarks.Count < requiredLandmarkCount)
        {
            // ランドマーク数が不足している場合は描画をスキップ（前回の姿勢のまま表示維持）
            return;
        }

        _lastUpdateTime[personIndex] = Time.time;

        // 初回のみLineRendererを生成
        if (!_personLines.ContainsKey(personIndex))
        {
            CreateLineRenderers(personIndex);
        }

        SetPersonVisible(personIndex, true);

        var lines = _personLines[personIndex];
        for (int i = 0; i < _connections.Length; i++)
        {
            var (a, b) = _connections[i];

            // 念のための二重チェック（将来 _connections が変更されても安全に）
            if (a >= landmarks.Count || b >= landmarks.Count) continue;

            var posA = LandmarkToWorld(landmarks[a]);
            var posB = LandmarkToWorld(landmarks[b]);

            lines[i].SetPosition(0, posA);
            lines[i].SetPosition(1, posB);
        }
    }

    private Vector3 LandmarkToWorld(NormalizedLandmark landmark)
    {
        // MediaPipeの正規化座標(0〜1)をスクリーン座標経由でワールド座標に変換
        float screenX = (landmark.x) * Screen.width;  // 左右反転
        float screenY = (landmark.y) * Screen.height; // 上下反転
        var screenPos = new Vector3(screenX, screenY, drawDistance);
        return mainCamera.ScreenToWorldPoint(screenPos);
    }

    private void CreateLineRenderers(int personIndex)
    {
        var lines = new List<LineRenderer>();
        // 人ごとに色を変える
        var color = GetPersonColor(personIndex);

        foreach (var _ in _connections)
        {
            var go = new GameObject($"SkeletonLine_P{personIndex}");
            go.transform.SetParent(transform);

            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMaterial;
            lr.startColor = color;
            lr.endColor = color;
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.positionCount = 2;
            lr.useWorldSpace = true;

            lines.Add(lr);
        }

        _personLines[personIndex] = lines;
    }

    private void SetPersonVisible(int personIndex, bool visible)
    {
        if (!_personLines.ContainsKey(personIndex)) return;
        foreach (var lr in _personLines[personIndex])
        {
            if (lr != null) lr.enabled = visible;
        }
    }

    private Color GetPersonColor(int index)
    {
        // 人ごとに異なる色
        var colors = new[]
        {
            new Color(0.2f, 0.8f, 1.0f),  // 水色
            new Color(1.0f, 0.6f, 0.2f),  // オレンジ
            new Color(0.4f, 1.0f, 0.4f),  // 緑
            new Color(1.0f, 0.4f, 0.8f),  // ピンク
            new Color(1.0f, 1.0f, 0.4f),  // 黄色
        };
        return colors[index % colors.Length];
    }

    private void OnDestroy()
    {
        foreach (var lines in _personLines.Values)
        {
            foreach (var lr in lines)
            {
                if (lr != null) Destroy(lr.gameObject);
            }
        }
    }
}
AR-Hanabi-Unity

企画局ワークショップで使用しているAR花火のUnityシステム用リポジトリです。

## セットアップ手順

### 1. リポジトリのクローン

```bash
git clone git@github.com:NUTFes/ar-hanabi-unity.git
cd ar-hanabi-unity
```

### 2. MediaPipeUnityPlugin の導入

このリポジトリはファイルサイズの関係上、MediaPipeUnityPlugin を含んでいません。
以下の手順で手動導入してください。

#### 2-1. パッケージのダウンロード

以下のリリースページから `com.github.homuler.mediapipe-0.16.3.tgz` をダウンロードしてください。

🔗 https://github.com/homuler/MediaPipeUnityPlugin/releases/tag/v0.16.3

#### 2-2. ファイルの配置

ダウンロードした tgz ファイルを以下のパスに配置してください：

```
Packages/MediaPipe/com.github.homuler.mediapipe-0.16.3.tgz
```

#### 2-3. Unityでプロジェクトを開く

Unity Hub からプロジェクトを開くと、自動的にパッケージが読み込まれます。
Unity のバージョンは **6000.0.x (Unity 6)** 以上を使用してください。

### 3. モデルファイルの確認

以下のファイルがすでにリポジトリに含まれています：

```
Assets/StreamingAssets/MediaPipe/face_detection_short_range.bytes
```

含まれていない場合は、上記リリースページの `MediaPipeUnityPlugin-all.zip` を解凍し、
該当ファイルを上記パスに配置してください。

### 4. 動作確認

`Assets/ARHanabi/Scenes/FaceDetectionTest.unity` を開いて再生し、
カメラ映像と顔検出ログが表示されれば導入完了です。
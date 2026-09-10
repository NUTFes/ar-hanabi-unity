using UnityEngine;

// ===== SettingsStore =====
// Admin画面から変更した設定値を PlayerPrefs で永続化するための、薄いヘルパー。
//
// ── なぜこれが要るようになったか ──
//   これまでこのプロジェクトには永続化の仕組みが一切無かった
//   （CameraBackgroundController.cs のコメントに「PlayerPrefs 等は使わない」と
//   明記されていたのがその唯一の痕跡）。展示は複数セッション・複数日に渡って
//   電源を落として運用するため、Admin画面で調整した値（ジェスチャー感度・
//   花火の出し方など）がアプリ再起動のたびにコードの初期値へ巻き戻るのは
//   運用上の欠陥だと判断し、最小限の永続化層を足すことにした。
//
// ── 「毎回このキー名を書く」を避けるための薄いラッパーである理由 ──
//   PlayerPrefs 自体は薄い static API なので、これをラップするクラスを
//   わざわざ用意するのは一見過剰に見えるかもしれない。ここで足しているのは
//   ただ1点、「キーが無ければコード側の現在値（Inspector/シーンの値）を
//   そのまま使う」という初回起動時のフォールバックだけ。これを各コンポーネントに
//   バラバラに書くと「HasKey を書き忘れて既定値のまま気づかない」事故が
//   起きやすいため、1箇所にまとめてある。
//
// ── キーの命名規則 ──
//   "ARHanabi.<コンポーネント名>.<フィールド名>" で統一する。
//   将来コンポーネントが増えても衝突しないようにするため。
public static class SettingsStore
{
    private const string Prefix = "ARHanabi.";

    public static float GetFloat(string key, float currentValue)
    {
        return PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetFloat(Prefix + key) : currentValue;
    }

    public static void SetFloat(string key, float value)
    {
        PlayerPrefs.SetFloat(Prefix + key, value);
        PlayerPrefs.Save();
    }

    public static bool GetBool(string key, bool currentValue)
    {
        return PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetInt(Prefix + key) != 0 : currentValue;
    }

    public static void SetBool(string key, bool value)
    {
        PlayerPrefs.SetInt(Prefix + key, value ? 1 : 0);
        PlayerPrefs.Save();
    }

    public static int GetInt(string key, int currentValue)
    {
        return PlayerPrefs.HasKey(Prefix + key) ? PlayerPrefs.GetInt(Prefix + key) : currentValue;
    }

    public static void SetInt(string key, int value)
    {
        PlayerPrefs.SetInt(Prefix + key, value);
        PlayerPrefs.Save();
    }

    /// <summary>保存済みの値を消す（キーが無ければ何もしない）。既定値へ戻す用途</summary>
    public static void DeleteKey(string key)
    {
        PlayerPrefs.DeleteKey(Prefix + key);
    }

    // ── 設定のバージョン管理 ──
    // 「同じキー名のまま意味・既定値だけを変える」と、現場PCに保存済みの
    // 古い値が新しい既定値より優先されてしまい、変更が一切効かなくなる
    //（このリポジトリでは意味が変わるパラメータを別名で追加するのが原則だが、
    //  gestureCooldown のように名前は同じで「安全側の既定値に戻したい」ケースもある）。
    //
    // 各コンポーネントの Awake で
    //   if (SettingsStore.GetSettingsVersion() < N) { 関連キーを DeleteKey; SetSettingsVersion(N); }
    // という形で使う（GestureDetector.Awake 参照）。バージョンは1つだけの
    // グローバルなカウンタなので、複数コンポーネントが同時に上げても衝突しない
    // （それぞれ「自分に関係あるキーだけ」を消すため）。
    private const string SettingsVersionKey = "SettingsVersion";

    public static int GetSettingsVersion() => GetInt(SettingsVersionKey, 0);
    public static void SetSettingsVersion(int version) => SetInt(SettingsVersionKey, version);
}

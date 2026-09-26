using System;
using System.Text;

// ===== AudioSpectrum =====
// PCM を測るための解析ユーティリティ。
//
// ── なぜ必要になったか ──
//   合成した効果音が「ノイズっぽい」と分かっても、耳の代わりに使える数値が無いと
//   直したのか壊したのか判断できない。そこで次の指標を出せるようにしてある。
//     ・スペクトル平坦度 … 1 に近いほど白色ノイズそのもの。0 に近いほど音程感がある
//     ・スペクトル重心   … 高いほどシャリシャリ／ヒスに寄る
//     ・A特性 RMS        … 「聞こえる大きさ」。層を混ぜる比率を決めるときはこれで揃える
//     ・オクターブバンド … どの帯域が余っている／足りないかが分かる
//     ・RMS エンベロープ … 打ち上げ／開花／余韻の切れ目を見つけてスライスに使う
//
//   爆発音として妥当な目安は 重心 200〜600Hz / 平坦度 0.15未満。
//   合成側を作り直すときは、実録音を Analyze して出た値を目標にすればよい。
//
// ── Unity に依存させていない理由 ──
//   UnityEngine の型を使っていないので、この1ファイルを Unity の外へ持ち出して
//   コンソールアプリとして走らせられる。Unity 内では FireworkSourceAnalyzer が呼ぶ。

public static class AudioSpectrum
{
    public sealed class Report
    {
        public string name = "";
        public int    sampleRate;
        public int    sampleCount;
        public float  seconds;

        public float  peak;
        public float  rms;
        /// <summary>A特性を掛けた RMS。層ごとの「聞こえる大きさ」の比較に使う</summary>
        public float  aWeightedRms;

        public float  centroidHz;
        public float  flatness;
        /// <summary>500Hz 以下が全エネルギーに占める割合（0..1）</summary>
        public float  lowRatio500;
        /// <summary>2kHz 以上が全エネルギーに占める割合（0..1）。ヒスの量の目安</summary>
        public float  highRatio2k;

        public static readonly double[] OctaveCenters =
            { 31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        /// <summary>オクターブバンドごとのレベル（dBFS）。OctaveCenters と同じ並び</summary>
        public double[] octaveDbfs = new double[OctaveCenters.Length];

        /// <summary>ブロックごとの RMS。構造（打ち上げ／開花／余韻）を見るのに使う</summary>
        public float[] envelope = Array.Empty<float>();
        public float   envelopeBlockSec;
    }

    private const int FftSize = 4096;

    public static Report Analyze(float[] mono, int sampleRate, float envelopeBlockSec = 0.02f)
    {
        var r = new Report
        {
            sampleRate       = sampleRate,
            sampleCount      = mono.Length,
            seconds          = mono.Length / (float)sampleRate,
            envelopeBlockSec = envelopeBlockSec,
        };

        if (mono.Length == 0) return r;

        // ── 時間領域 ──
        double sumSq = 0.0;
        for (int i = 0; i < mono.Length; i++)
        {
            float a = Math.Abs(mono[i]);
            if (a > r.peak) r.peak = a;
            sumSq += (double)mono[i] * mono[i];
        }
        r.rms = (float)Math.Sqrt(sumSq / mono.Length);

        // ── RMS エンベロープ ──
        int block  = Math.Max(1, (int)(envelopeBlockSec * sampleRate));
        int blocks = Math.Max(1, mono.Length / block);
        r.envelope = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            double s = 0.0;
            int from = b * block;
            int to   = Math.Min(mono.Length, from + block);
            for (int i = from; i < to; i++) s += (double)mono[i] * mono[i];
            r.envelope[b] = (float)Math.Sqrt(s / Math.Max(1, to - from));
        }

        // ── 周波数領域（ハン窓・50%オーバーラップの平均振幅スペクトル）──
        int N = FftSize;
        if (mono.Length < N) return r;   // 短すぎる素材はスペクトルを出さない

        var win = new double[N];
        for (int i = 0; i < N; i++) win[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (N - 1));

        var mag = new double[N / 2];
        var re  = new double[N];
        var im  = new double[N];
        int frames = 0;

        for (int off = 0; off + N <= mono.Length; off += N / 2)
        {
            for (int i = 0; i < N; i++) { re[i] = mono[off + i] * win[i]; im[i] = 0.0; }
            Fft(re, im);
            for (int k = 0; k < N / 2; k++) mag[k] += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            frames++;
        }
        if (frames == 0) return r;
        for (int k = 0; k < N / 2; k++) mag[k] /= frames;

        // 窓とFFT長で決まるスケールを戻して、フルスケール正弦が 0dBFS になるよう合わせる
        // （ハン窓のコヒーレントゲイン 0.5、片側スペクトルなので ×2）
        double scale = 2.0 / (N * 0.5);

        double num = 0.0, den = 0.0;
        double logSum = 0.0, arith = 0.0;
        double all = 0.0, low = 0.0, high = 0.0;
        double aSq = 0.0;
        var bandEnergy = new double[Report.OctaveCenters.Length];

        for (int k = 1; k < N / 2; k++)
        {
            double f = k * (double)sampleRate / N;
            double m = mag[k] * scale;
            double e = m * m;

            num += f * m;
            den += m;

            logSum += Math.Log(m + 1e-12);
            arith  += m;

            all += e;
            if (f <= 500.0)  low  += e;
            if (f >= 2000.0) high += e;

            // A特性で重み付けしたエネルギー
            double aGain = AWeightGain(f);
            aSq += e * aGain * aGain;

            for (int b = 0; b < Report.OctaveCenters.Length; b++)
            {
                double c  = Report.OctaveCenters[b];
                double lo = c / Math.Sqrt(2.0);
                double hi = c * Math.Sqrt(2.0);
                if (f >= lo && f < hi) { bandEnergy[b] += e; break; }
            }
        }

        int bins = N / 2 - 1;
        r.centroidHz  = den   > 0 ? (float)(num / den) : 0f;
        r.flatness    = arith > 0 ? (float)(Math.Exp(logSum / bins) / (arith / bins)) : 0f;
        r.lowRatio500 = all   > 0 ? (float)(low  / all) : 0f;
        r.highRatio2k = all   > 0 ? (float)(high / all) : 0f;

        // スペクトルから求めた A特性 RMS（正弦の実効値にするため 2 で割る）
        r.aWeightedRms = (float)Math.Sqrt(aSq / 2.0);

        for (int b = 0; b < bandEnergy.Length; b++)
            r.octaveDbfs[b] = bandEnergy[b] > 0 ? 10.0 * Math.Log10(bandEnergy[b] / 2.0) : -120.0;

        return r;
    }

    // ── A特性（IEC 61672 のアナログ式）──
    // 3〜4kHz を持ち上げ、低域と超高域を落とす。人の耳の感度に近い重み。
    private static double AWeightGain(double f)
    {
        double f2  = f * f;
        double num = 12194.0 * 12194.0 * f2 * f2;
        double den = (f2 + 20.6 * 20.6)
                   * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9))
                   * (f2 + 12194.0 * 12194.0);
        if (den <= 0.0) return 0.0;

        // ×1.2589 (= +2.0dB) は 1kHz で利得 1 になるようにする規格上の補正
        return (num / den) * 1.2589254117941673;
    }

    // ── 整形出力 ──
    public static string Format(Report r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Spectrum] {r.name}");
        sb.AppendLine($"  {r.seconds:F2}s  {r.sampleRate}Hz  {r.sampleCount} samples");
        sb.AppendLine($"  peak={r.peak:F3}  rms={r.rms:F4}  A特性rms={r.aWeightedRms:F4}");
        sb.AppendLine($"  重心={r.centroidHz:F0}Hz  平坦度={r.flatness:F3}  " +
                      $"500Hz以下={100f * r.lowRatio500:F1}%  2kHz以上={100f * r.highRatio2k:F2}%");

        sb.Append("  帯域(dBFS): ");
        for (int b = 0; b < r.octaveDbfs.Length; b++)
        {
            double c = Report.OctaveCenters[b];
            string label = c >= 1000 ? $"{c / 1000:F0}k" : $"{c:F0}";
            sb.Append($"{label}:{r.octaveDbfs[b],6:F1} ");
        }
        sb.AppendLine();

        sb.AppendLine($"  エンベロープ ({r.envelopeBlockSec * 1000:F0}ms/桁):");
        sb.AppendLine("  " + Sparkline(r.envelope, 100));

        sb.AppendLine("  目安: 爆発音は 重心 200〜600Hz / 平坦度 0.15未満。");
        sb.AppendLine("        平坦度 0.3超・重心 2kHz超は白色ノイズ（ヒス）に近い。");
        return sb.ToString();
    }

    // エンベロープを固定幅のASCIIグラフにする。打ち上げ／開花／余韻の切れ目を目で拾うため
    public static string Sparkline(float[] values, int width)
    {
        if (values == null || values.Length == 0) return "(なし)";

        const string levels = " .:-=+*#%@";

        float max = 0f;
        for (int i = 0; i < values.Length; i++) if (values[i] > max) max = values[i];
        if (max <= 0f) return new string(' ', width);

        var sb = new StringBuilder(width);
        for (int x = 0; x < width; x++)
        {
            int from = (int)((long)x * values.Length / width);
            int to   = (int)((long)(x + 1) * values.Length / width);
            if (to <= from) to = from + 1;

            float peak = 0f;
            for (int i = from; i < to && i < values.Length; i++) if (values[i] > peak) peak = values[i];

            // dB で段階を割る（線形だと小さい部分が全部つぶれて見えない）
            float db  = 20f * (float)Math.Log10(peak / max + 1e-6);
            int   lvl = (int)Math.Round((db + 60f) / 60f * (levels.Length - 1));
            if (lvl < 0) lvl = 0;
            if (lvl > levels.Length - 1) lvl = levels.Length - 1;
            sb.Append(levels[lvl]);
        }
        return sb.ToString();
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                double tr = re[i]; re[i] = re[j]; re[j] = tr;
                double ti = im[i]; im[i] = im[j]; im[j] = ti;
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);

            for (int i = 0; i < n; i += len)
            {
                double cr = 1.0, ci = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci;
                    double xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr;        im[a] += xi;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}

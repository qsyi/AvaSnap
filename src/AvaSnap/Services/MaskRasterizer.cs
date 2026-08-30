namespace AvaSnap.Services;

/// <summary>マスク編集の1操作。マスクは <see cref="MaskOpKind"/> の順序付きリストとして
/// 保持し(<see cref="MaskRasterizer.Bake"/> で R8 カバレッジへ焼く)、Undo は
/// リストの切り詰めだけで済む。座標はすべて「切り抜き後キャンバスの正規化 [0,1]」
/// なので、切り抜きを後から変えても見えているキャンバスに追従する。
///
/// - <see cref="MaskOpKind.Fill"/>: 全面を <see cref="FillValue"/>(0..1) で上書き
///   (全消去=1 / 全塗り=0)。
/// - <see cref="MaskOpKind.LinearGradient"/>: (Ax,Ay)→(Bx,By) に沿って 効果0→効果1
///   の smoothstep ランプで上書き。始点より外は0、終点より外は1で固定。
/// - <see cref="MaskOpKind.RadialGradient"/>: 中心(Ax,Ay)→縁(Bx,By)。中心 効果1、
///   縁 効果0 で上書き(<c>反転</c>トグルはサンプル時に 1-cov で評価するのでここでは扱わない)。
/// - <see cref="MaskOpKind.PenStroke"/>: なぞった所に黒を塗る = 効果を0へ
///   (<c>coverage = min(coverage, 1 - brush)</c>)。
/// - <see cref="MaskOpKind.EraseStroke"/>: 黒を消す = 効果を1へ戻す(ペンの修正用、
///   <c>coverage = max(coverage, brush)</c>)。</summary>
public enum MaskOpKind { Fill, LinearGradient, RadialGradient, PenStroke, EraseStroke }

public readonly record struct MaskStrokePoint(double X, double Y) : IEquatable<MaskStrokePoint>;

/// <summary>不変。等価判定はレコードの自動生成に任せる(<see cref="Points"/> は
/// <see cref="Views.EquatableArray{T}"/> なので構造比較になる)。</summary>
public sealed record MaskOp(
    MaskOpKind Kind,
    double Ax, double Ay, double Bx, double By,
    double FillValue,
    Views.EquatableArray<MaskStrokePoint> Points,
    double Size,     // ブラシ直径 = キャンバス短辺に対する割合 (0..1)
    double Feather)  // 0 = くっきり, 1 = 中心から外へなだらかに
{
    public static MaskOp MakeFill(double value) =>
        new(MaskOpKind.Fill, 0, 0, 0, 0, value, new Views.EquatableArray<MaskStrokePoint>(Array.Empty<MaskStrokePoint>()), 0, 0);

    public static MaskOp MakeGradient(bool radial, double ax, double ay, double bx, double by) =>
        new(radial ? MaskOpKind.RadialGradient : MaskOpKind.LinearGradient, ax, ay, bx, by, 0,
            new Views.EquatableArray<MaskStrokePoint>(Array.Empty<MaskStrokePoint>()), 0, 0);

    public static MaskOp MakeStroke(bool erase, MaskStrokePoint[] points, double size, double feather) =>
        new(erase ? MaskOpKind.EraseStroke : MaskOpKind.PenStroke, 0, 0, 0, 0, 0,
            new Views.EquatableArray<MaskStrokePoint>(points), size, feather);
}

/// <summary>マスクの op リストを R8 カバレッジ byte[] へ焼く。1 バイト = 効果の効き具合:
/// <c>0</c> = 効果0(黒) / <c>255</c> = 効果1(白 = 既定)。空の op リスト = 全面 白。
/// <see cref="ShapeRasterizer"/> と違い RenderTargetBitmap を使わない純 byte[] 演算なので
/// UI スレッド外・レンダーの Task.Run 内から呼べる。</summary>
public static class MaskRasterizer
{
    /// <summary>ベイクバッファの長辺上限。マスクはソフトな選択なので 1024 で十分、
    /// サンプル時にバイリニア拡大する。</summary>
    public const int MaxDimension = 1024;

    /// <summary>キャンバスのアスペクト比に合わせ、長辺 <see cref="MaxDimension"/> の
    /// ベイク解像度を返す。</summary>
    public static (int Width, int Height) BakeSizeFor(double canvasWidth, double canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return (MaxDimension, MaxDimension);
        double longSide = Math.Max(canvasWidth, canvasHeight);
        double scale = MaxDimension / longSide;
        int w = Math.Max(2, (int)Math.Round(canvasWidth * scale));
        int h = Math.Max(2, (int)Math.Round(canvasHeight * scale));
        return (w, h);
    }

    public static byte[] Bake(IReadOnlyList<MaskOp> ops, int width, int height)
    {
        width = Math.Clamp(width, 2, 4096);
        height = Math.Clamp(height, 2, 4096);
        var cov = new byte[width * height];
        Array.Fill(cov, (byte)255); // 既定 = 全面 白(効果1)
        for (int i = 0; i < ops.Count; i++) Apply(ops[i], cov, width, height);
        return cov;
    }

    /// <summary>正規化 UV [0,1] のカバレッジを R8 バッファからバイリニアで読む。
    /// 範囲外は端をクランプ(切り抜き移動で少しはみ出しても破綻しない)。
    /// 戻り値 0..1。</summary>
    public static double SampleBilinear(byte[] cov, int width, int height, double u, double v)
    {
        double fx = Math.Clamp(u, 0, 1) * (width - 1);
        double fy = Math.Clamp(v, 0, 1) * (height - 1);
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        int x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
        double tx = fx - x0, ty = fy - y0;
        double c00 = cov[y0 * width + x0], c10 = cov[y0 * width + x1];
        double c01 = cov[y1 * width + x0], c11 = cov[y1 * width + x1];
        double top = c00 + (c10 - c00) * tx;
        double bot = c01 + (c11 - c01) * tx;
        return (top + (bot - top) * ty) / 255.0;
    }

    /// <summary>確定済みカバレッジのコピーに、作業中の1ストロークだけを重ねて返す。
    /// 編集中のライブプレビューで、毎回 op リスト全体を焼き直さないための入口。</summary>
    public static byte[] WithPendingStroke(
        byte[] baseCoverage, int width, int height,
        bool erase, IReadOnlyList<MaskStrokePoint> points, double size, double feather)
    {
        var cov = (byte[])baseCoverage.Clone();
        if (points.Count == 0) return cov;
        var arr = points as MaskStrokePoint[] ?? points.ToArray();
        ApplyStroke(MaskOp.MakeStroke(erase, arr, size, feather), cov, width, height, erase);
        return cov;
    }

    private static double Smoothstep(double t) => t * t * (3.0 - 2.0 * t);

    private static void Apply(MaskOp op, byte[] cov, int w, int h)
    {
        switch (op.Kind)
        {
            case MaskOpKind.Fill:
                Array.Fill(cov, (byte)Math.Clamp(op.FillValue * 255.0, 0, 255));
                break;

            case MaskOpKind.LinearGradient:
            {
                double ax = op.Ax * w, ay = op.Ay * h;
                double dx = op.Bx * w - ax, dy = op.By * h - ay;
                double len2 = dx * dx + dy * dy;
                if (len2 < 1e-6) { Array.Fill(cov, (byte)255); break; }
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        double t = ((x + 0.5 - ax) * dx + (y + 0.5 - ay) * dy) / len2;
                        cov[row + x] = (byte)(Smoothstep(Math.Clamp(t, 0, 1)) * 255.0);
                    }
                }
                break;
            }

            case MaskOpKind.RadialGradient:
            {
                double cx = op.Ax * w, cy = op.Ay * h;
                double ex = op.Bx * w - cx, ey = op.By * h - cy;
                double radius = Math.Sqrt(ex * ex + ey * ey);
                if (radius < 1e-6) { Array.Fill(cov, (byte)255); break; }
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
                        double d = Math.Sqrt(ddx * ddx + ddy * ddy) / radius;
                        cov[row + x] = (byte)(Smoothstep(Math.Clamp(1.0 - d, 0, 1)) * 255.0); // 中心=1, 縁=0
                    }
                }
                break;
            }

            case MaskOpKind.PenStroke:
            case MaskOpKind.EraseStroke:
                ApplyStroke(op, cov, w, h, erase: op.Kind == MaskOpKind.EraseStroke);
                break;
        }
    }

    private static void ApplyStroke(MaskOp op, byte[] cov, int w, int h, bool erase)
    {
        var pts = op.Points.AsArray();
        if (pts.Length == 0) return;

        double shortSide = Math.Min(w, h);
        double radius = Math.Max(1.0, op.Size * shortSide * 0.5);
        double innerR = radius * Math.Clamp(1.0 - op.Feather, 0, 1); // feather 1 => 芯なし
        double band = Math.Max(1e-6, radius - innerR);

        // このストローク1本ぶんの寄与を貯めるバッファ(同一ストローク内は最大値)。
        var stamp = new byte[w * h];

        void Stamp(double px, double py)
        {
            int x0 = Math.Max(0, (int)Math.Floor(px - radius));
            int x1 = Math.Min(w - 1, (int)Math.Ceiling(px + radius));
            int y0 = Math.Max(0, (int)Math.Floor(py - radius));
            int y1 = Math.Min(h - 1, (int)Math.Ceiling(py + radius));
            for (int y = y0; y <= y1; y++)
            {
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    double ddx = x + 0.5 - px, ddy = y + 0.5 - py;
                    double dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                    double a = dist <= innerR ? 1.0
                             : dist >= radius ? 0.0
                             : Smoothstep(1.0 - (dist - innerR) / band);
                    if (a <= 0) continue;
                    byte v = (byte)(a * 255.0);
                    if (v > stamp[row + x]) stamp[row + x] = v;
                }
            }
        }

        Stamp(pts[0].X * w, pts[0].Y * h);
        for (int i = 1; i < pts.Length; i++)
        {
            double x0 = pts[i - 1].X * w, y0 = pts[i - 1].Y * h;
            double x1 = pts[i].X * w, y1 = pts[i].Y * h;
            double segLen = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Math.Max(1, (int)Math.Ceiling(segLen / Math.Max(1.0, radius / 3.0)));
            for (int s = 1; s <= steps; s++)
            {
                double f = (double)s / steps;
                Stamp(x0 + (x1 - x0) * f, y0 + (y1 - y0) * f);
            }
        }

        for (int i = 0; i < cov.Length; i++)
        {
            byte s = stamp[i];
            if (s == 0) continue;
            cov[i] = erase
                ? Math.Max(cov[i], s)                 // 消しゴム: 黒を消す(効果1へ)
                : Math.Min(cov[i], (byte)(255 - s));  // ペン: 黒を塗る(効果0へ)
        }
    }
}

// 图标生成工具：把源 PNG 去白边、裁切、生成多尺寸 ICO（传统 BMP 帧）
// 用法: IconTool.exe <源图.png> <输出目录>

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconTool
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: IconTool.exe <源图.png> <输出目录>");
            return 2;
        }

        string source = args[0];
        string outDir = args[1];
        if (!File.Exists(source)) { Console.WriteLine("找不到源图: " + source); return 3; }
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

        Bitmap work;
        using (Image raw = Image.FromFile(source))
        {
            work = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(work)) g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
        }

        int fixedPixels = Defringe(work);
        Console.WriteLine("去白边: 修正半透明像素 " + fixedPixels + " 个");

        Rectangle bounds = ContentBounds(work, 8);
        Console.WriteLine("内容边界: " + bounds.X + "," + bounds.Y + "  " + bounds.Width + "x" + bounds.Height);

        int side = Math.Max(bounds.Width, bounds.Height);
        int margin = 8;
        Bitmap canvas = new Bitmap(side + margin * 2, side + margin * 2, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            int dx = (canvas.Width - bounds.Width) / 2;
            int dy = (canvas.Height - bounds.Height) / 2;
            g.DrawImage(work, new Rectangle(dx, dy, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        }

        string characterPath = Path.Combine(outDir, "deepseek_whale_character.png");
        canvas.Save(characterPath, ImageFormat.Png);
        Console.WriteLine("输出角色图: " + characterPath + " (" + canvas.Width + "x" + canvas.Height + ")");

        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (Bitmap scaled = new Bitmap(sizes[i], sizes[i], PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(canvas, new Rectangle(0, 0, sizes[i], sizes[i]));
                }
                frames[i] = BuildBmpFrame(scaled);
            }
        }

        string icoPath = Path.Combine(outDir, "deepseek_whale.ico");
        WriteIco(icoPath, sizes, frames);
        Console.WriteLine("输出图标: " + icoPath + " (" + new FileInfo(icoPath).Length / 1024 + " KB)");

        canvas.Dispose();
        work.Dispose();
        return 0;
    }

    // 白底抠图留下的白边：对半透明像素做去白底反解 前景 = (观测 - (1-α)×255) / α
    private static int Defringe(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int count = 0;
        try
        {
            int stride = data.Stride;
            byte[] buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    int a = buf[i + 3];
                    if (a == 0 || a == 255) continue;
                    if (a < 8) { buf[i + 3] = 0; continue; }
                    double alpha = a / 255.0;
                    double pull = (1.0 - alpha) * 255.0;
                    buf[i] = Clamp((buf[i] - pull) / alpha);
                    buf[i + 1] = Clamp((buf[i + 1] - pull) / alpha);
                    buf[i + 2] = Clamp((buf[i + 2] - pull) / alpha);
                    count++;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
        return count;
    }

    private static byte Clamp(double v)
    {
        if (v < 0) return 0;
        if (v > 255) return 255;
        return (byte)Math.Round(v);
    }

    private static Rectangle ContentBounds(Bitmap bmp, int threshold)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        try
        {
            int stride = data.Stride;
            byte[] buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (buf[y * stride + x * 4 + 3] <= threshold) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        if (maxX < 0) return new Rectangle(0, 0, w, h);
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // 一个 ICO 帧：BITMAPINFOHEADER(高度×2) + 自下而上 BGRA + 1bpp AND 掩码
    private static byte[] BuildBmpFrame(Bitmap bmp)
    {
        int size = bmp.Width;
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        byte[] pixels = new byte[stride * size];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        int maskStride = ((size + 31) / 32) * 4;
        byte[] mask = new byte[maskStride * size];
        for (int y = 0; y < size; y++)
        {
            int row = size - 1 - y;
            for (int x = 0; x < size; x++)
            {
                if (pixels[y * stride + x * 4 + 3] >= 128) continue;
                mask[row * maskStride + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        MemoryStream ms = new MemoryStream();
        BinaryWriter bw = new BinaryWriter(ms);
        bw.Write((uint)40);
        bw.Write(size);
        bw.Write(size * 2);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write((uint)0);
        bw.Write((uint)(size * size * 4));
        bw.Write(0);
        bw.Write(0);
        bw.Write((uint)0);
        bw.Write((uint)0);
        for (int y = size - 1; y >= 0; y--) bw.Write(pixels, y * stride, size * 4);
        bw.Write(mask, 0, mask.Length);
        bw.Flush();
        byte[] result = ms.ToArray();
        bw.Close();
        ms.Dispose();
        return result;
    }

    private static void WriteIco(string path, int[] sizes, byte[][] frames)
    {
        if (File.Exists(path)) File.Delete(path);
        FileStream fs = File.Create(path);
        BinaryWriter bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)frames.Length);
        int offset = 6 + 16 * frames.Length;
        for (int i = 0; i < frames.Length; i++)
        {
            byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
            bw.Write(dim);
            bw.Write(dim);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)frames[i].Length);
            bw.Write((uint)offset);
            offset += frames[i].Length;
        }
        foreach (byte[] frame in frames) bw.Write(frame);
        bw.Flush();
        bw.Close();
        fs.Close();
    }
}

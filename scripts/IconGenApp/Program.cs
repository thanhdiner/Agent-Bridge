using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconGenApp;

class Program
{
    static void Main()
    {
        int size = 256;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Draw Orange Rounded Rectangle
        using var brush = new SolidBrush(Color.FromArgb(255, 244, 124, 32));
        var rect = new RectangleF(8, 8, 240, 240);
        float radius = 48.0f;

        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius, radius, 180, 90);
        path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 90);
        path.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(rect.X, rect.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);

        // Draw Crisp White Vector Icon in center: Code / Shapes symbol
        using var whitePen = new Pen(Color.White, 14f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        // Left bracket <
        g.DrawLine(whitePen, 95, 90, 65, 128);
        g.DrawLine(whitePen, 65, 128, 95, 166);

        // Right bracket >
        g.DrawLine(whitePen, 161, 90, 191, 128);
        g.DrawLine(whitePen, 191, 128, 161, 166);

        // Center node
        using var whiteBrush = new SolidBrush(Color.White);
        g.FillEllipse(whiteBrush, 116, 116, 24, 24);

        g.Dispose();

        // Save 512x512 PNG
        using var bmp512 = new Bitmap(bmp, 512, 512);
        string baseDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        string pngPath = Path.Combine(baseDir, "src", "AgentBridge.Desktop", "Assets", "agentbridge-icon-512.png");
        bmp512.Save(pngPath, ImageFormat.Png);

        // Save valid ICO containing PNG payload (Standard PNG-compressed ICO format supported by WPF)
        using var msPng = new MemoryStream();
        bmp.Save(msPng, ImageFormat.Png);
        byte[] pngBytes = msPng.ToArray();

        string icoPath = Path.Combine(baseDir, "src", "AgentBridge.Desktop", "Assets", "agentbridge.ico");
        using var fs = File.Create(icoPath);
        using var writer = new BinaryWriter(fs);

        // ICO Header
        writer.Write((ushort)0); // Reserved
        writer.Write((ushort)1); // Type 1 = ICO
        writer.Write((ushort)1); // 1 Image

        // ICONDIRENTRY
        writer.Write((byte)0); // Width 256 -> 0
        writer.Write((byte)0); // Height 256 -> 0
        writer.Write((byte)0); // Color count
        writer.Write((byte)0); // Reserved
        writer.Write((ushort)1); // Color planes
        writer.Write((ushort)32); // Bits per pixel
        writer.Write((uint)pngBytes.Length); // Image size
        writer.Write((uint)22); // Offset to image data (6 header + 16 entry = 22)

        // Write PNG payload
        writer.Write(pngBytes);
        writer.Flush();

        Console.WriteLine("Valid WPF ICO and PNG generated successfully at " + icoPath);
    }
}

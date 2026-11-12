using System;
using System.Windows;
using System.Windows.Media;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>Draws each segment's downloaded bytes at its position in the file, like IDM's connection bar.</summary>
public sealed class SegmentBar : FrameworkElement
{
    private static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF1)));
    private static readonly Brush Done = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x8D, 0xF0)));
    private static readonly Brush Head = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x91)));
    private static readonly Pen Border = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0xCD, 0xD6)), 1));

    public DownloadItem? Item { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Track, Border, bounds);
        var item = Item;
        if (item is null || bounds.Width <= 2) return;

        double w = bounds.Width, h = bounds.Height;

        if (item.IsTorrent)
        {
            if (item.PieceMap is { Length: > 0 } map)
            {
                double bw = w / map.Length;
                for (int i = 0; i < map.Length; i++)
                {
                    if (map[i] <= 0) continue;
                    // Partially complete buckets are drawn shorter so progress inside a bucket is visible.
                    double bh = map[i] >= 1 ? h : Math.Max(2, h * map[i]);
                    dc.DrawRectangle(Done, null, new Rect(i * bw, h - bh, bw + 0.5, bh));
                }
            }
            else if (item.IsCompleted)
            {
                dc.DrawRectangle(Done, null, bounds);
            }
            return;
        }

        long total = item.TotalSize;
        if (total <= 0)
        {
            if (item.IsCompleted) dc.DrawRectangle(Done, null, bounds);
            return;
        }

        foreach (var s in item.SnapshotSegments())
        {
            double x = s.Start / (double)total * w;
            double dw = Math.Min(s.Downloaded / (double)total * w, w - x);
            if (dw > 0) dc.DrawRectangle(Done, null, new Rect(x, 0, dw, h));
            if (s.Active && !s.Complete) dc.DrawRectangle(Head, null, new Rect(Math.Max(0, x + dw - 1), 0, 2, h));
        }
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

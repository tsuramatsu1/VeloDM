using System;
using System.Windows;
using System.Windows.Media;
using VeloDM.Core;

namespace VeloDM.App;

/// <summary>Draws each segment's downloaded bytes at its position in the file, like IDM's connection bar.</summary>
public sealed class SegmentBar : FrameworkElement
{
    // Looked up on every draw so the bar follows the light/dark palette.
    private Brush Track => (Brush)FindResource("TrackBrush");
    private Brush Done => (Brush)FindResource("ProgressBrush");
    private Brush Head => (Brush)FindResource("ProgressHeadBrush");

    public DownloadItem? Item { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Track, new Pen((Brush)FindResource("TrackBorderBrush"), 1), bounds);
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

        if (item.IsStream)
        {
            // A stream has no fixed layout to map: show plain progress.
            double done = item.Progress / 100.0 * w;
            if (done > 0) dc.DrawRectangle(Done, null, new Rect(0, 0, done, h));
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
}

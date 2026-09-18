using OpenCvSharp;

namespace OCRPrototype.Services;

public sealed record CardRegions(IReadOnlyList<Rect> TextLines, Rect? Photo);

public sealed class DynamicRegionLocator
{
    public CardRegions Locate(PreparedCard card, CascadeClassifier faces, int limit)
    {
        using var detection = new Mat();
        Cv2.EqualizeHist(card.Gray, detection);
        var found = faces.DetectMultiScale(detection, 1.1, 5, HaarDetectionTypes.ScaleImage,
            new Size(card.Gray.Width / 20, card.Gray.Height / 12));
        // Ambiguous face detections do not silently select somebody else's portrait.
        Rect? photo = found.Length == 1 ? Expand(found[0], card.Gray.Size(), .28, .45) : null;

        using var ink = new Mat(); using var joined = new Mat();
        Cv2.BitwiseNot(card.Binary, ink);
        if (photo is { } p) Cv2.Rectangle(ink, p, Scalar.Black, -1);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(Math.Max(9, card.Gray.Width / 65), 3));
        Cv2.MorphologyEx(ink, joined, MorphTypes.Close, kernel);
        Cv2.FindContours(joined, out Point[][] contours, out _, RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);
        var fragments = contours.Select(Cv2.BoundingRect)
            .Where(r => r.Width > card.Gray.Width * .018 && r.Height > card.Gray.Height * .012
                && r.Height < card.Gray.Height * .12 && r.Width < card.Gray.Width * .95)
            .OrderBy(r => r.Y + r.Height / 2).ToList();
        var lines = new List<Rect>();
        foreach (var r in fragments)
        {
            int index = lines.FindIndex(l => Math.Abs(l.Y + l.Height / 2d - r.Y - r.Height / 2d)
                < Math.Min(l.Height, r.Height) * .6
                && HorizontalGap(l, r) < card.Gray.Width * .12);
            if (index < 0) lines.Add(r); else lines[index] = Union(lines[index], r);
        }
        // Rank before bounding work; preserve reading order after selecting candidates.
        var selected = lines.Where(r => r.Width > card.Gray.Width * .06)
            .OrderByDescending(r => r.Width * r.Height).Take(limit)
            .Select(r => Expand(r, card.Gray.Size(), .025, .22)).OrderBy(r => r.Y).ToArray();
        return new(selected, photo);
    }

    private static int HorizontalGap(Rect a, Rect b) => Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.Right, b.Right));
    public static Rect Union(Rect a, Rect b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.Right, b.Right) - Math.Min(a.X, b.X), Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Y, b.Y));
    public static Rect Expand(Rect r, Size size, double horizontal, double vertical)
    {
        int dx = Math.Max(3, (int)(r.Width * horizontal));
        int dy = Math.Max(3, (int)(r.Height * vertical));
        int x = Math.Max(0, r.X - dx), y = Math.Max(0, r.Y - dy);
        return new(x, y, Math.Min(size.Width, r.Right + dx) - x, Math.Min(size.Height, r.Bottom + dy) - y);
    }
}

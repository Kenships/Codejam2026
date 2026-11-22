using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Small vector icons for the leaderboard, drawn with Painter2D so they scale cleanly:
/// the crown (lucide "Crown") and the rank-change arrows (lucide "TrendingUp/Down").
/// Shapes are laid out on lucide's 24x24 grid and fitted into the element.
/// </summary>
public class LeaderboardGlyph : VisualElement
{
    public enum Shape
    {
        Crown,
        Up,
        Down
    }

    private Shape _shape;
    private Color _tint;

    public LeaderboardGlyph(Shape shape, Color tint)
    {
        _shape = shape;
        _tint = tint;
        pickingMode = PickingMode.Ignore;
        generateVisualContent += Draw;
    }

    public Shape GlyphShape
    {
        get => _shape;
        set
        {
            _shape = value;
            MarkDirtyRepaint();
        }
    }

    public Color Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            MarkDirtyRepaint();
        }
    }

    private void Draw(MeshGenerationContext context)
    {
        Rect rect = contentRect;
        if (rect.width <= 0f || rect.height <= 0f)
            return;

        float scale = Mathf.Min(rect.width, rect.height) / 24f;
        Vector2 origin = new(
            rect.x + (rect.width - 24f * scale) * 0.5f,
            rect.y + (rect.height - 24f * scale) * 0.5f
        );

        Vector2 P(float x, float y) => origin + new Vector2(x * scale, y * scale);

        Painter2D painter = context.painter2D;
        painter.fillColor = _tint;

        switch (_shape)
        {
            case Shape.Crown:
                // Body: three points with dips between them
                painter.BeginPath();
                painter.MoveTo(P(2f, 5f));
                painter.LineTo(P(7.5f, 11f));
                painter.LineTo(P(12f, 3.5f));
                painter.LineTo(P(16.5f, 11f));
                painter.LineTo(P(22f, 5f));
                painter.LineTo(P(18.8f, 17f));
                painter.LineTo(P(5.2f, 17f));
                painter.ClosePath();
                painter.Fill();

                // Band
                painter.BeginPath();
                painter.MoveTo(P(5.2f, 18.6f));
                painter.LineTo(P(18.8f, 18.6f));
                painter.LineTo(P(18.8f, 21f));
                painter.LineTo(P(5.2f, 21f));
                painter.ClosePath();
                painter.Fill();
                break;

            case Shape.Up:
                painter.BeginPath();
                painter.MoveTo(P(12f, 4f));
                painter.LineTo(P(22f, 19f));
                painter.LineTo(P(2f, 19f));
                painter.ClosePath();
                painter.Fill();
                break;

            case Shape.Down:
                painter.BeginPath();
                painter.MoveTo(P(2f, 5f));
                painter.LineTo(P(22f, 5f));
                painter.LineTo(P(12f, 20f));
                painter.ClosePath();
                painter.Fill();
                break;
        }
    }
}

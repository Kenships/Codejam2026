using UnityEngine;

/// <summary>
/// Seats for player icons placed around a centred card, matching the join screen: top-left
/// first, going clockwise. Up to InnerRingSeats sit on an inner ring; past that an outer ring
/// opens (its seats fall between the inner ones) and both rings spread out ("crowded"), so the
/// icons shrink. Used by the drawing phase (submitted drawings) and guessing phase (guesses).
/// </summary>
public static class PlayerRingLayout
{
    public const int InnerRingSeats = 12;
    public const int OuterRingSeats = GameSessionData.MaxPlayersLimit - InnerRingSeats;

    /// <summary>Class to toggle on the icons layer while the outer ring is in use.</summary>
    public const string CrowdedClass = "is-crowded";

    /// <summary>True once <paramref name="iconCount"/> icons need the outer ring.</summary>
    public static bool IsCrowded(int iconCount) => iconCount > InnerRingSeats;

    /// <summary>
    /// Panel position (y down) of a seat inside <paramref name="layerRect"/>, the full-screen
    /// layer the icons are absolutely positioned in. Seat 0 = 135 deg (top-left), clockwise.
    /// </summary>
    public static Vector2 PanelPosition(Rect layerRect, int seatIndex, int iconCount)
    {
        float w = layerRect.width > 1f ? layerRect.width : 1920f;
        float h = layerRect.height > 1f ? layerRect.height : 1080f;

        float cx = w * 0.5f;
        float cy = h * 0.5f;

        bool crowded = IsCrowded(iconCount);

        float rx, ry, angleDeg;
        if (seatIndex < InnerRingSeats)
        {
            rx = w * (crowded ? 0.335f : 0.36f);
            ry = h * (crowded ? 0.33f : 0.35f);
            angleDeg = 135f - seatIndex * (360f / InnerRingSeats);
        }
        else
        {
            rx = w * 0.455f;
            ry = h * 0.43f;
            int outerIndex = (seatIndex - InnerRingSeats) % OuterRingSeats;
            angleDeg = 135f - (outerIndex + 0.5f) * (360f / OuterRingSeats);
        }

        // Panel Y grows downward, so negate Sin for top-left = 135 deg going clockwise
        float rad = angleDeg * Mathf.Deg2Rad;
        return new Vector2(cx + Mathf.Cos(rad) * rx, cy - Mathf.Sin(rad) * ry);
    }
}

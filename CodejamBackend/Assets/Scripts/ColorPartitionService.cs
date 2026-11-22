using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Splits a fixed total area evenly between a list of colours (one per
/// person), breaks each colour's share into random pieces whose areas
/// all lie within [minUnitArea, maxUnitArea], then shuffles every piece
/// into one spawn queue of (color, radius) - the format
/// ColorPhysics.SpawnQueue takes.
///
/// Areas are circle areas (pi * r^2), matching how ColorPhysics merges
/// and AreaCameraZoom measure them.
/// </summary>
public class ColorPartitionService
{
    private readonly System.Random _random;

    /// <param name="seed">Fixed seed for a reproducible result, or null
    /// for a different result every time.</param>
    public ColorPartitionService(int? seed = null)
    {
        _random = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
    }

    public Queue<(Color color, float radius)> CreateSpawnQueue(
        IReadOnlyList<Color> colors,
        float totalArea,
        float minUnitArea,
        float maxUnitArea)
    {
        Queue<(Color color, float radius)> queue = new();

        if (colors == null || colors.Count == 0 || totalArea <= 0f)
            return queue;

        if (minUnitArea <= 0f)
            throw new ArgumentOutOfRangeException(
                nameof(minUnitArea), minUnitArea, "Must be greater than 0.");

        if (maxUnitArea < minUnitArea)
            throw new ArgumentException(
                $"maxUnitArea ({maxUnitArea}) must be >= minUnitArea ({minUnitArea}).");

        float areaPerColor = totalArea / colors.Count;

        List<(Color color, float radius)> pieces = new();

        foreach (Color color in colors)
        {
            foreach (float area in PartitionArea(areaPerColor, minUnitArea, maxUnitArea))
                pieces.Add((color, AreaToRadius(area)));
        }

        Shuffle(pieces);

        foreach ((Color color, float radius) piece in pieces)
            queue.Enqueue(piece);

        return queue;
    }

    /// <summary>
    /// Random partition of <paramref name="area"/> into pieces that each
    /// lie in [min, max] and sum exactly to the area.
    /// </summary>
    public List<float> PartitionArea(float area, float min, float max)
    {
        List<float> pieces = new();

        // Any valid piece count k must satisfy k * min <= area <= k * max
        const float epsilon = 1e-5f;
        int minCount = Math.Max(1, Mathf.CeilToInt(area / max - epsilon));
        int maxCount = Mathf.FloorToInt(area / min + epsilon);

        if (minCount > maxCount)
        {
            // No count works (e.g. area 1, min 0.6, max 0.9). Use the most
            // pieces that still respect the minimum and split evenly, so
            // only the maximum is exceeded.
            int count = Math.Max(1, maxCount);

            Debug.LogWarning(
                $"ColorPartitionService: area {area} can't be split into " +
                $"pieces within [{min}, {max}]; using {count} piece(s) of " +
                $"{area / count}.");

            for (int i = 0; i < count; i++)
                pieces.Add(area / count);

            return pieces;
        }

        int pieceCount = _random.Next(minCount, maxCount + 1);
        float remaining = area;

        // Each piece is drawn from the range that still leaves the rest
        // splittable within [min, max], so the last piece always fits.
        for (int remainingCount = pieceCount; remainingCount > 1; remainingCount--)
        {
            int after = remainingCount - 1;

            float lower = Mathf.Max(min, remaining - after * max);
            float upper = Mathf.Min(max, remaining - after * min);

            float piece = Mathf.Lerp(lower, upper, (float)_random.NextDouble());

            pieces.Add(piece);
            remaining -= piece;
        }

        // Exactly what's left, so the pieces sum to the area
        pieces.Add(Mathf.Clamp(remaining, min, max));

        return pieces;
    }

    /// <summary>
    /// Picks [minUnitArea, maxUnitArea] for splitting <paramref name="totalArea"/>
    /// evenly between <paramref name="colorCount"/> colours so the piece count stays
    /// bounded: each colour gets between ceil(k / 2) and k pieces, where
    /// k = clamp(floor(maxPieces / colorCount), minPiecesPerColor, maxPiecesPerColor).
    /// Few colours -> few, large pieces; many colours -> more, smaller pieces, with the
    /// total capped at maxPieces (only exceeded when needed to give every colour
    /// minPiecesPerColor pieces).
    /// </summary>
    public static (float minUnitArea, float maxUnitArea) GetUnitAreaRange(
        int colorCount,
        float totalArea,
        int maxPieces,
        int minPiecesPerColor,
        int maxPiecesPerColor)
    {
        colorCount = Math.Max(1, colorCount);
        minPiecesPerColor = Math.Max(1, minPiecesPerColor);
        maxPiecesPerColor = Math.Max(minPiecesPerColor, maxPiecesPerColor);

        int mostPerColor = Mathf.Clamp(maxPieces / colorCount, minPiecesPerColor, maxPiecesPerColor);
        int fewestPerColor = Math.Max(minPiecesPerColor, (mostPerColor + 1) / 2);

        float areaPerColor = totalArea / colorCount;

        // k pieces fit iff k * min <= areaPerColor <= k * max, so these bounds allow
        // exactly fewestPerColor..mostPerColor pieces per colour.
        return (areaPerColor / mostPerColor, areaPerColor / fewestPerColor);
    }

    public static float AreaToRadius(float area) =>
        Mathf.Sqrt(Mathf.Max(0f, area) / Mathf.PI);

    // Fisher-Yates
    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

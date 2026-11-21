using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using UnityEngine;
using Random = UnityEngine.Random;

public class ColorVerletCluster : MonoBehaviour
{
    [SerializeField] private ColorBody colorBodyPrefab;
    
    [Title("Particles")]
    [SerializeField] private List<ColorBody> _colorBody = new();

    [Title("Collision")]
    [SerializeField] private float colorBodyScale = 1f;
    [SerializeField] private float colorBodyGap = 0.05f;
    [SerializeField, Min(1)] private int constraintIterations = 8;

    [Title("Verlet")]
    [SerializeField, Range(0f, 1f)]
    private float verletDamping = 0.98f;

    [SerializeField] private float minVelocity = 0.01f;

    [Title("HSV Sorting")]
    [SerializeField, Range(0f, 1f)]
    private float hsvSortStrength = 0.15f;

    [SerializeField, Min(0.01f)]
    private float clusterSpread = 0.8f;

    [SerializeField, Range(0f, 1f)]
    private float hueSeam = 0.5f;

    [Title("Cohesion")]
    [SerializeField, Range(0f, 1f)]
    private float cohesionStrength = 0.02f;

    private readonly List<ColorBody> _active = new();
    private readonly HashSet<ColorBody> _initialized = new();

    private void FixedUpdate()
    {
        _active.Clear();

        foreach (ColorBody body in _colorBody)
        {
            if (body == null)
                continue;

            if (_initialized.Add(body))
            {
                body.CurrentPos = body.transform.position;
                body.LastPos = body.CurrentPos;
            }

            _active.Add(body);
        }

        int count = _active.Count;

        if (count == 0)
            return;

        float dt = Time.fixedDeltaTime;
        float minDistance = Mathf.Max(
            0.0001f,
            colorBodyScale + colorBodyGap
        );

        Vector3 right = transform.right;
        Vector3 up = transform.up;
        Vector3 forward = transform.forward;

        // 1. Calculate HSV ranks
        float[] hues = new float[count];
        float[] saturations = new float[count];
        float[] hueRanks = new float[count];
        float[] saturationRanks = new float[count];

        for (int i = 0; i < count; i++)
        {
            Color.RGBToHSV(
                _active[i].Color,
                out float h,
                out float s,
                out _
            );

            hues[i] = Mathf.Repeat(h - hueSeam, 1f);
            saturations[i] = s;
        }

        CalculateRanks(hues, hueRanks);
        CalculateRanks(saturations, saturationRanks);

        float avgHue = 0f;
        float avgSaturation = 0f;

        for (int i = 0; i < count; i++)
        {
            avgHue += hueRanks[i];
            avgSaturation += saturationRanks[i];
        }

        avgHue /= count;
        avgSaturation /= count;

        float clusterSize =
            minDistance * Mathf.Sqrt(count) * clusterSpread;

        // 2. Verlet integration
        foreach (ColorBody body in _active)
        {
            Vector3 velocity =
                (body.CurrentPos - body.LastPos) *
                verletDamping;

            if (velocity.magnitude / dt < minVelocity)
                velocity = Vector3.zero;

            Vector3 nextPos = body.CurrentPos + velocity;

            body.LastPos = body.CurrentPos;
            body.CurrentPos = nextPos;
        }

        // 3. Constraints
        for (int iteration = 0;
             iteration < constraintIterations;
             iteration++)
        {
            Vector3 center = Vector3.zero;

            foreach (ColorBody body in _active)
                center += body.CurrentPos;

            center /= count;

            // Relative HSV sorting and cohesion
            for (int i = 0; i < count; i++)
            {
                ColorBody body = _active[i];

                Vector3 desiredOffset =
                    right * ((hueRanks[i] - avgHue) * clusterSize) +
                    up * ((saturationRanks[i] - avgSaturation) * clusterSize);

                Vector3 currentOffset =
                    body.CurrentPos - center;

                Vector3 sortCorrection =
                    (desiredOffset - currentOffset) *
                    hsvSortStrength;

                Vector3 cohesionCorrection =
                    -currentOffset * cohesionStrength;

                Vector3 planeCorrection =
                    -forward * Vector3.Dot(
                        currentOffset,
                        forward
                    );

                Vector3 correction =
                    sortCorrection +
                    cohesionCorrection +
                    planeCorrection;

                body.CurrentPos += correction;
                body.LastPos += correction;
            }

            // Collision constraints
            for (int i = 0; i < count - 1; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    ColorBody a = _active[i];
                    ColorBody b = _active[j];

                    Vector3 delta =
                        b.CurrentPos - a.CurrentPos;

                    float distance = delta.magnitude;

                    if (distance >= minDistance)
                        continue;

                    Vector3 dir;

                    if (distance > 0.00001f)
                    {
                        dir = delta / distance;
                    }
                    else
                    {
                        float angle =
                            (i * 0.618f + j * 0.382f) *
                            Mathf.PI * 2f;

                        dir =
                            right * Mathf.Cos(angle) +
                            up * Mathf.Sin(angle);
                    }

                    float overlap = minDistance - distance;

                    Vector3 correction =
                        dir * (overlap * 0.5f);

                    a.CurrentPos -= correction;
                    b.CurrentPos += correction;

                    a.LastPos -= correction;
                    b.LastPos += correction;
                }
            }
        }

        // 4. Apply positions
        foreach (ColorBody body in _active)
        {
            body.transform.position = body.CurrentPos;
        }
    }

    private static void CalculateRanks(
        float[] values,
        float[] ranks)
    {
        int count = values.Length;
        int[] order = new int[count];

        for (int i = 0; i < count; i++)
            order[i] = i;

        Array.Sort(
            order,
            (a, b) => values[a].CompareTo(values[b])
        );

        int start = 0;

        while (start < count)
        {
            int end = start + 1;

            while (
                end < count &&
                Mathf.Abs(values[order[end]] - values[order[start]]) < 0.00001f)
            {
                end++;
            }

            float rank =
                (start + end - 1) * 0.5f /
                Mathf.Max(1, count - 1);

            for (int i = start; i < end; i++)
                ranks[order[i]] = rank;

            start = end;
        }
    }

    public void AddBody(ColorBody body)
    {
        if (body == null || _colorBody.Contains(body))
            return;

        body.CurrentPos = body.transform.position;
        body.LastPos = body.CurrentPos;

        _colorBody.Add(body);
        _initialized.Add(body);
    }

    public void RemoveBody(ColorBody body)
    {
        _colorBody.Remove(body);
        _initialized.Remove(body);
    }
    
    [Button]
    public void SpawnRandomColor()
    {
        var colorBody = Instantiate(colorBodyPrefab, Random.onUnitCircle * 5f, Quaternion.identity);
        colorBody.transform.localScale = Vector3.one * colorBodyScale;
        colorBody.Color = Random.ColorHSV(0f, 1f, 0.2f, 1f, 1f, 1f);
        AddBody(colorBody);
    }
}



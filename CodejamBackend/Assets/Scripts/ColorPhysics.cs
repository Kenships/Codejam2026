using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using UnityEngine;
using Random = UnityEngine.Random;

public class ColorPhysics : MonoBehaviour
{
    [SerializeField] private ColorBody colorBodyPrefab;

    [Title("Particles")] 
    [SerializeField] private List<ColorBody> colorBody;

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

    [Title("Centering")]
    [SerializeField, Range(0f, 1f)]
    private float centeringStrength = 0.05f;

    [Title("Ambient Wave")]
    [SerializeField] private bool waveEnabled = true;

    // Peak acceleration (units/s^2) applied at the centre of the line
    [SerializeField, Min(0f)] private float waveForce = 3f;

    // Half-width of the band around the line that gets pushed
    [SerializeField, Min(0.01f)] private float waveWidth = 0.75f;

    // How fast the line sweeps across the cluster (units/s)
    [SerializeField, Min(0f)] private float waveSpeed = 2f;

    // Direction the line travels, in degrees from transform.right
    [SerializeField, Range(0f, 360f)] private float waveAngle = 0f;

    // Extra distance travelled past the cluster before the line wraps around
    [SerializeField, Min(0f)] private float waveGap = 1f;

    private readonly List<ColorBody> _active = new();
    private readonly HashSet<ColorBody> _initialized = new();

    private float _waveOffset;
    private float _waveLinePos;
    private Vector3 _waveSweepDir;
    private Vector3 _wavePushDir;

    private void FixedUpdate()
    {
        _active.Clear();

        foreach (ColorBody body in colorBody)
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

        // 2. Advance the sweeping wave line
        if (waveEnabled)
            UpdateWaveLine(dt, right, up, forward);

        // 3. Verlet integration
        foreach (ColorBody body in _active)
        {
            Vector3 velocity =
                (body.CurrentPos - body.LastPos) *
                verletDamping;

            if (velocity.magnitude / dt < minVelocity)
                velocity = Vector3.zero;

            Vector3 nextPos = body.CurrentPos + velocity;

            if (waveEnabled)
                nextPos += WaveAcceleration(body.CurrentPos) * (dt * dt);

            body.LastPos = body.CurrentPos;
            body.CurrentPos = nextPos;
        }

        // 4. Constraints
        for (int iteration = 0;
             iteration < constraintIterations;
             iteration++)
        {
            Vector3 center = Vector3.zero;

            foreach (ColorBody body in _active)
                center += body.CurrentPos;

            center /= count;

            // Pulls the whole cluster toward this transform
            Vector3 centeringCorrection =
                (transform.position - center) * centeringStrength;

            // Relative HSV sorting and cohesion
            for (int i = 0; i < count; i++)
            {
                ColorBody body = _active[i];
                
                float minDistance = Mathf.Max(
                    0.0001f,
                    body.Radius + colorBodyGap
                );
                float clusterSize =
                    minDistance * Mathf.Sqrt(count) * clusterSpread;

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
                    planeCorrection +
                    centeringCorrection;

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
                    
                    float minDistance = Mathf.Max(
                        0.0001f,
                        a.Radius + b.Radius + colorBodyGap
                    );

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

        // 5. Apply positions
        foreach (ColorBody body in _active)
        {
            body.transform.position = body.CurrentPos;
        }
    }

    private void UpdateWaveLine(
        float dt,
        Vector3 right,
        Vector3 up,
        Vector3 forward)
    {
        Quaternion rotation = Quaternion.AngleAxis(waveAngle, forward);

        // The line travels along sweepDir and pushes perpendicular to it
        // (a transverse wave), which is "up" at the default angle.
        _waveSweepDir = rotation * right;
        _wavePushDir = rotation * up;

        float minProj = float.MaxValue;
        float maxProj = float.MinValue;

        foreach (ColorBody body in _active)
        {
            float proj = Vector3.Dot(body.CurrentPos, _waveSweepDir);
            float r = body.Radius;

            minProj = Mathf.Min(minProj, proj - r);
            maxProj = Mathf.Max(maxProj, proj + r);
        }

        // Sweep from just before the cluster to just past it, then wrap
        float span = (maxProj - minProj) + waveWidth * 2f + waveGap;

        _waveOffset = Mathf.Repeat(_waveOffset + waveSpeed * dt, span);
        _waveLinePos = minProj - waveWidth + _waveOffset;
    }

    private Vector3 WaveAcceleration(Vector3 position)
    {
        float distance =
            (Vector3.Dot(position, _waveSweepDir) - _waveLinePos) / waveWidth;

        if (distance <= -1f || distance >= 1f)
            return Vector3.zero;

        // Smooth cosine bump: 1 on the line, 0 at the band edge
        float falloff = 0.5f * (1f + Mathf.Cos(distance * Mathf.PI));

        return _wavePushDir * (waveForce * falloff);
    }

    private void OnDrawGizmosSelected()
    {
        if (!waveEnabled || !Application.isPlaying || _active.Count == 0)
            return;

        Vector3 alongLine = _wavePushDir;
        Vector3 linePoint =
            _waveSweepDir * _waveLinePos +
            alongLine * Vector3.Dot(transform.position, alongLine);

        const float halfLength = 20f;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(
            linePoint - alongLine * halfLength,
            linePoint + alongLine * halfLength
        );

        Gizmos.color = new Color(0f, 1f, 1f, 0.3f);

        foreach (float side in new[] { -1f, 1f })
        {
            Vector3 edge = linePoint + _waveSweepDir * (waveWidth * side);
            Gizmos.DrawLine(
                edge - alongLine * halfLength,
                edge + alongLine * halfLength
            );
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
        if (body == null || colorBody.Contains(body))
            return;

        body.CurrentPos = body.transform.position;
        body.LastPos = body.CurrentPos;

        colorBody.Add(body);
        _initialized.Add(body);
    }

    public void RemoveBody(ColorBody body)
    {
        colorBody.Remove(body);
        _initialized.Remove(body);
    }
    
    [Button]
    public void SpawnRandomColor()
    {
        var colorBody = Instantiate(colorBodyPrefab, Random.onUnitCircle * 5f, Quaternion.identity);
        colorBody.transform.localScale = Vector3.one * Random.Range(0.6f, 1.2f);
        colorBody.Color = Random.ColorHSV(0f, 1f, 0.2f, 1f, 1f, 1f);
        AddBody(colorBody);
    }

    [Button]
    public void Merge()
    {
        
    }
}



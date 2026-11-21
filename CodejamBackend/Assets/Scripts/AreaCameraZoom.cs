using Alchemy.Inspector;
using UnityEngine;

/// <summary>
/// Zooms an orthographic camera out as the total area of all ColorBody
/// circles grows, so they keep filling roughly the same share of the
/// screen. Starts at a fixed zoom, which is also the closest it will get.
/// </summary>
[RequireComponent(typeof(Camera))]
public class AreaCameraZoom : MonoBehaviour
{
    [SerializeField, Required] private ColorPhysics physics;

    [Title("Zoom")]
    // Orthographic size at start; also the minimum (most zoomed-in) size
    [SerializeField, Min(0.01f)] private float startSize = 5f;

    // Fraction of the visible screen area the circles should cover once
    // they outgrow the start size (lower = more empty space around them)
    [SerializeField, Range(0.01f, 1f)] private float screenFill = 0.3f;

    // Optional cap on how far it can zoom out (0 = no limit)
    [SerializeField, Min(0f)] private float maxSize = 0f;

    // Roughly how long it takes to catch up to the target size (seconds)
    [SerializeField, Min(0f)] private float smoothTime = 0.5f;

    private Camera _camera;
    private float _zoomVelocity;

    public float TotalArea { get; private set; }
    public float TargetSize { get; private set; }

    private void Awake()
    {
        _camera = GetComponent<Camera>();
        _camera.orthographic = true;
        _camera.orthographicSize = startSize;
        TargetSize = startSize;
    }

    // LateUpdate so it sees this frame's merges and scale animations
    private void LateUpdate()
    {
        if (physics == null)
            return;

        TotalArea = CalculateTotalArea();
        TargetSize = CalculateTargetSize(TotalArea);

        _camera.orthographicSize = smoothTime > 0f
            ? Mathf.SmoothDamp(
                _camera.orthographicSize,
                TargetSize,
                ref _zoomVelocity,
                smoothTime
            )
            : TargetSize;
    }

    private float CalculateTotalArea()
    {
        float area = 0f;

        foreach (ColorBody body in physics.Bodies)
        {
            if (body == null)
                continue;

            float radius = body.Radius;
            area += Mathf.PI * radius * radius;
        }

        return area;
    }

    /// <summary>
    /// The visible area of an orthographic camera is
    /// (2 * size)^2 * aspect. Solving area * screenFill = totalArea for
    /// size gives the size at which the circles cover screenFill of it.
    /// </summary>
    private float CalculateTargetSize(float totalArea)
    {
        float aspect = Mathf.Max(0.01f, _camera.aspect);
        float neededSize =
            0.5f * Mathf.Sqrt(totalArea / (screenFill * aspect));

        float size = Mathf.Max(startSize, neededSize);

        if (maxSize > 0f)
            size = Mathf.Min(size, Mathf.Max(startSize, maxSize));

        return size;
    }
}

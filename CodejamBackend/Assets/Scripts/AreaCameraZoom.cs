using Alchemy.Inspector;
using UnityEngine;

/// <summary>
/// Zooms an orthographic camera out as the total area of all ColorBody
/// circles grows, so they keep filling roughly the same share of the
/// screen. Starts at a fixed zoom, which is also the closest it will get.
/// A focus rect (SetFocusRect) narrows that to part of the screen, e.g.
/// the stage a UI panel leaves free, and slides the camera so the circles
/// sit in the middle of it.
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

    private static readonly Rect FullScreen = new(0f, 0f, 1f, 1f);
    private static readonly Vector2 ScreenCenter = new(0.5f, 0.5f);

    private Camera _camera;
    private float _zoomVelocity;

    // Part of the screen (viewport coords, 0-1) to frame the circles in;
    // null = the whole screen
    private Rect? _focusRect;

    // Smoothed viewport point the camera's home position is shown at
    private Vector2 _focusCenter = ScreenCenter;
    private Vector2 _focusVelocity;

    // Where the camera sat before a focus rect first moved it
    private Vector3 _homePosition;
    private bool _isOffset;

    public float TotalArea { get; private set; }
    public float TargetSize { get; private set; }

    private void Awake()
    {
        _camera = GetComponent<Camera>();
        _camera.orthographic = true;
        _camera.orthographicSize = startSize;
        TargetSize = startSize;
    }

    /// <summary>
    /// Frames the circles inside part of the screen instead of all of it:
    /// they fill screenFill of that rect, and the camera slides so its home
    /// position (the physics centre) shows in the rect's middle. The rect is
    /// in viewport coordinates (0-1, origin bottom-left).
    /// </summary>
    public void SetFocusRect(Rect viewportRect) => _focusRect = viewportRect;

    /// <summary>Goes back to framing the circles on the whole screen.</summary>
    public void ClearFocusRect() => _focusRect = null;

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

        UpdateFocusOffset();
    }

    /// <summary>
    /// Slides the camera so its home position lands on the (smoothed)
    /// focus rect centre. Leaves the camera alone while there's no focus
    /// rect and it's already back home.
    /// </summary>
    private void UpdateFocusOffset()
    {
        Vector2 target = _focusRect?.center ?? ScreenCenter;

        if (!_isOffset)
        {
            if (target == ScreenCenter)
                return;

            _homePosition = transform.position;
            _isOffset = true;
        }

        _focusCenter = smoothTime > 0f
            ? Vector2.SmoothDamp(_focusCenter, target, ref _focusVelocity, smoothTime)
            : target;

        // Back home: snap the last bit and stop driving the position
        if (_focusRect == null && (_focusCenter - ScreenCenter).sqrMagnitude < 0.000001f)
        {
            _focusCenter = ScreenCenter;
            _focusVelocity = Vector2.zero;
            transform.position = _homePosition;
            _isOffset = false;
            return;
        }

        float height = _camera.orthographicSize * 2f;
        float width = height * _camera.aspect;
        Vector3 shift = new(
            (_focusCenter.x - 0.5f) * width,
            (_focusCenter.y - 0.5f) * height,
            0f
        );

        transform.position = _homePosition - shift;
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
    /// (2 * size)^2 * aspect, times the focus rect's share of the screen.
    /// Solving area * screenFill = totalArea for size gives the size at
    /// which the circles cover screenFill of it.
    /// </summary>
    private float CalculateTargetSize(float totalArea)
    {
        float aspect = Mathf.Max(0.01f, _camera.aspect);
        Rect focus = _focusRect ?? FullScreen;
        float focusShare = Mathf.Clamp(focus.width * focus.height, 0.01f, 1f);
        float neededSize =
            0.5f * Mathf.Sqrt(totalArea / (screenFill * aspect * focusShare));

        float size = Mathf.Max(startSize, neededSize);

        if (maxSize > 0f)
            size = Mathf.Min(size, Mathf.Max(startSize, maxSize));

        return size;
    }
}

using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using PrimeTween;
using UnityEngine;

/// <summary>
/// The true colour reveal at the end of a round. Takes the last body out
/// of the physics, zooms the camera so two bodies of its size fit side by
/// side, slides it to the left, then grows a body on the right whose colour
/// blends through the guessed colours and converges on the true colour.
/// </summary>
public class RevealController : MonoBehaviour
{
    [SerializeField, Required] private ColorPhysics colorPhysics;

    // Camera to zoom (defaults to Camera.main). Its AreaCameraZoom, if any,
    // is paused while the reveal holds the camera.
    [SerializeField] private Camera revealCamera;

    [Title("Layout")]
    // How long the zoom and the slide to the left take
    [SerializeField, Min(0.01f)] private float slideDuration = 1f;
    [SerializeField] private Ease slideEase = Ease.InOutQuad;

    // Space between the merged body and the true colour body
    [SerializeField, Min(0f)] private float gap = 0.5f;

    // Extra room around the pair (1 = they touch the screen edges)
    [SerializeField, Min(1f)] private float padding = 1.25f;

    [Title("True Color Body")]
    [SerializeField, Min(0.01f)] private float growDuration = 0.5f;
    [SerializeField, Min(0f)] private float growOvershoot = 1f;

    // How long it blends through the guessed colours before settling on
    // the true colour (runs alongside the grow)
    [SerializeField, Min(0.01f)] private float colorBlendDuration = 1.5f;

    // Ease over the whole blend; an Out ease slows down as it converges
    [SerializeField] private Ease colorBlendEase = Ease.OutQuad;

    [SerializeField] private bool playEffect = true;

    private const float StartRadius = 0.001f;

    private Sequence _tween;
    private AreaCameraZoom _pausedZoom;

    /// <summary>The final merged body, once it has left the physics.</summary>
    public ColorBody MergedBody { get; private set; }

    /// <summary>The true colour body shown next to it.</summary>
    public ColorBody TrueBody { get; private set; }

    public bool IsRevealing => _tween.isAlive;

    private void Awake()
    {
        if (colorPhysics == null)
            colorPhysics = FindFirstObjectByType<ColorPhysics>();
    }

    private void OnDestroy() => StopTween();

    /// <summary>
    /// Takes the remaining body out of the physics and reveals it next to
    /// the true colour. Yield on it to wait for the whole reveal.
    /// </summary>
    public IEnumerator RevealLast(IReadOnlyList<Color> guessedColors, Color trueColor)
    {
        ColorBody last = TakeLastBody();

        if (last != null)
            yield return Reveal(last, guessedColors, trueColor);
    }

    /// <summary>Reveals a body that's already out of the physics.</summary>
    public IEnumerator Reveal(
        ColorBody merged,
        IReadOnlyList<Color> guessedColors,
        Color trueColor)
    {
        MergedBody = merged;

        float radius = merged.Radius;
        float offset = radius + gap * 0.5f;

        Camera cam = revealCamera != null ? revealCamera : Camera.main;

        Vector3 center = cam != null ? cam.transform.position : merged.transform.position;
        center.z = merged.transform.position.z;

        Vector3 leftPos = center - Vector3.right * offset;
        Vector3 rightPos = center + Vector3.right * offset;

        // 1. Make room and slide the merged body left
        yield return SlideRoutine(merged, leftPos, cam, radius, offset);

        // 2. Grow the true colour body beside it
        yield return GrowRoutine(rightPos, radius, guessedColors, trueColor);
    }

    /// <summary>
    /// Removes the reveal's bodies and gives the camera back to its
    /// AreaCameraZoom.
    /// </summary>
    [Button]
    public void Clear()
    {
        StopTween();

        if (MergedBody != null)
            Destroy(MergedBody.gameObject);

        if (TrueBody != null)
            Destroy(TrueBody.gameObject);

        MergedBody = null;
        TrueBody = null;

        if (_pausedZoom != null)
            _pausedZoom.enabled = true;

        _pausedZoom = null;
    }

    #region Steps

    /// <summary>
    /// Removes the remaining body from the physics (the GameObject stays).
    /// If merging left more than one, the largest is used.
    /// </summary>
    private ColorBody TakeLastBody()
    {
        if (colorPhysics == null)
        {
            Debug.LogError("RevealController: ColorPhysics is not assigned.");
            return null;
        }

        IReadOnlyList<ColorBody> bodies = colorPhysics.Bodies;
        ColorBody last = null;

        foreach (ColorBody body in bodies)
        {
            if (body != null && (last == null || body.Radius > last.Radius))
                last = body;
        }

        if (last == null)
        {
            Debug.LogWarning("RevealController: no body left to reveal.");
            return null;
        }

        if (bodies.Count > 1)
            Debug.LogWarning($"RevealController: {bodies.Count} bodies left after merging; revealing the largest.");

        // Settle any overshoot/squash so the measured radius is final
        last.CompleteScaleTween();
        colorPhysics.RemoveBody(last);

        return last;
    }

    private IEnumerator SlideRoutine(
        ColorBody merged,
        Vector3 leftPos,
        Camera cam,
        float radius,
        float offset)
    {
        _tween = Sequence.Create()
            .Group(Tween.Position(merged.transform, leftPos, slideDuration, slideEase))
            .Group(Tween.Rotation(merged.transform, Quaternion.identity, slideDuration, slideEase));

        if (cam != null && cam.orthographic)
        {
            // Stop the area zoom from pulling the camera back in, since the
            // physics no longer has any bodies
            _pausedZoom = cam.GetComponent<AreaCameraZoom>();

            if (_pausedZoom != null)
                _pausedZoom.enabled = false;

            // Half-height that fits both circles (width / aspect) with padding
            float aspect = Mathf.Max(0.01f, cam.aspect);
            float size = Mathf.Max(radius, (offset + radius) / aspect) * padding;

            _tween.Group(Tween.CameraOrthographicSize(cam, size, slideDuration, slideEase));
        }

        yield return _tween.ToYieldInstruction();
    }

    private IEnumerator GrowRoutine(
        Vector3 position,
        float radius,
        IReadOnlyList<Color> guessedColors,
        Color trueColor)
    {
        // Guesses first, true colour last
        List<Color> path = new();

        if (guessedColors != null)
            path.AddRange(guessedColors);

        path.Add(trueColor);

        ColorBody truth = colorPhysics.CreateBody(path[0], StartRadius, position);
        TrueBody = truth;

        if (playEffect)
            truth.PlayMergeEffect();

        // Grow and colour blend start together. The scale tween is made
        // here rather than with ColorBody.ScaleRadius, because that keeps
        // its own handle to the tween, and PrimeTween doesn't allow
        // stopping a tween directly once a Sequence owns it.
        _tween = Sequence.Create()
            .Group(Tween.Scale(
                truth.transform,
                Vector3.one * (StartRadius * 2f),
                Vector3.one * (radius * 2f),
                growDuration,
                Easing.Overshoot(growOvershoot)
            ))
            .Group(Tween.Custom(
                truth,
                0f,
                1f,
                colorBlendDuration,
                (body, t) => body.Color = EvaluateColorPath(path, t),
                colorBlendEase
            ));

        yield return _tween.ToYieldInstruction();

        // Land exactly on the true colour
        truth.Color = trueColor;
    }

    #endregion

    #region Utilities

    private void StopTween()
    {
        if (_tween.isAlive)
            _tween.Stop();
    }

    /// <summary>
    /// Colour at t (0-1) along a path of colours split into equal segments,
    /// blending each segment in HSV (shortest way round the hue wheel), the
    /// same way merges mix colours.
    /// </summary>
    private static Color EvaluateColorPath(IReadOnlyList<Color> path, float t)
    {
        if (path.Count == 1)
            return path[0];

        float scaled = Mathf.Clamp01(t) * (path.Count - 1);
        int index = Mathf.Min(Mathf.FloorToInt(scaled), path.Count - 2);
        float local = scaled - index;

        return ColorPhysics.AverageHSV(
            path[index],
            path[index + 1],
            1f - local,
            local
        );
    }

    #endregion
}

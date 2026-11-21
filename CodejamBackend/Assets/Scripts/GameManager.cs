using System.Collections.Generic;
using Alchemy.Inspector;
using Core.Utility;
using PrimeTween;
using UnityEngine;

public class GameManager : Singleton<GameManager>
{
    [SerializeField, Required] private ColorPhysics colorPhysics;

    [Title("Colors")]
    // Number of distinct colours (one per person)
    [SerializeField, Min(1)] private int colorCount = 4;

    [SerializeField, Range(0f, 1f)] private float saturation = 1f;
    [SerializeField, Range(0f, 1f)] private float value = 1f;

    [Title("Area")]
    // Total circle area shared evenly between all colours
    [SerializeField, Min(0.01f)] private float totalArea = 30f;

    // Bounds on the area of each spawned piece
    [SerializeField, Min(0.001f)] private float minUnitArea = 0.3f;
    [SerializeField, Min(0.001f)] private float maxUnitArea = 1.5f;

    [Title("Sequence")]
    // Pause after the last body spawns before everything starts merging
    [SerializeField, Min(0f)] private float mergeDelay = 2f;

    [Title("True Color")]
    // The colour the merged result is compared against (used by the button)
    [SerializeField] private Color trueColor = Color.white;

    [Title("Reveal")]
    // Camera to zoom for the reveal (defaults to Camera.main). Its
    // AreaCameraZoom, if any, is paused while the reveal holds the camera.
    [SerializeField] private Camera revealCamera;

    // Pause after the last merge before the final body leaves the physics
    [SerializeField, Range(1f, 2f)] private float removeDelay = 1.5f;

    // How long the zoom and the slide to the left take
    [SerializeField, Min(0.01f)] private float revealDuration = 1f;

    // Space between the merged body and the true colour body
    [SerializeField, Min(0f)] private float revealGap = 0.5f;

    // Extra room around the pair (1 = they touch the screen edges)
    [SerializeField, Min(1f)] private float revealPadding = 1.25f;

    [SerializeField, Min(0.01f)] private float growDuration = 0.5f;
    [SerializeField, Min(0f)] private float growOvershoot = 1f;

    // How long the true colour body blends through the guessed colours
    // before settling on the true colour (runs alongside the grow)
    [SerializeField, Min(0.01f)] private float colorBlendDuration = 1.5f;

    // Ease over the whole blend; an Out ease slows down as it converges
    [SerializeField] private Ease colorBlendEase = Ease.OutQuad;
    [SerializeField] private bool playEffectOnReveal = true;

    [Title("Randomness")]
    // Use a fixed seed to get the same colours and pieces every time
    [SerializeField] private bool useSeed;
    [SerializeField, ShowIf(nameof(useSeed))] private int seed;

    private Coroutine _sequenceRoutine;
    private Sequence _revealTween;
    private AreaCameraZoom _pausedZoom;

    public bool IsRunning => _sequenceRoutine != null;

    /// <summary>The final merged body, once it has left the physics.</summary>
    public ColorBody MergedBody { get; private set; }

    /// <summary>The true colour body shown next to it in the reveal.</summary>
    public ColorBody TrueBody { get; private set; }

    /// <summary>Runs the sequence with the Inspector's trueColor.</summary>
    [Button]
    public void GenerateAndSpawn() => GenerateAndSpawn(trueColor);

    /// <summary>
    /// Generates colorCount distinct colours, then runs the sequence:
    /// spawn every partitioned piece, wait mergeDelay, merge them all,
    /// then reveal the result next to trueColor.
    /// </summary>
    public void GenerateAndSpawn(Color trueColor)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("GameManager.GenerateAndSpawn only works in Play mode.");
            return;
        }

        if (colorPhysics == null)
        {
            Debug.LogError("GameManager: ColorPhysics is not assigned.");
            return;
        }

        System.Random random = useSeed
            ? new System.Random(seed)
            : new System.Random();

        List<Color> colors = GenerateDistinctColors(colorCount, random);

        ColorPartitionService partitionService = new(random.Next());

        Queue<(Color color, float radius)> spawnQueue =
            partitionService.CreateSpawnQueue(
                colors,
                totalArea,
                minUnitArea,
                maxUnitArea
            );

        if (_sequenceRoutine != null)
            StopCoroutine(_sequenceRoutine);

        ClearReveal();

        _sequenceRoutine = StartCoroutine(
            SpawnThenMergeRoutine(spawnQueue, colors, trueColor)
        );
    }

    private System.Collections.IEnumerator SpawnThenMergeRoutine(
        Queue<(Color color, float radius)> spawnQueue,
        IReadOnlyList<Color> guessedColors,
        Color trueColor)
    {
        // 1. Spawn every piece at ColorPhysics' spawn rate
        colorPhysics.SpawnQueue(spawnQueue);

        while (colorPhysics.IsSpawning)
            yield return null;

        // 2. Let them settle for a moment
        if (mergeDelay > 0f)
            yield return new WaitForSeconds(mergeDelay);

        // 3. Merge everything down, and wait until it's finished
        colorPhysics.SquashAll();

        while (colorPhysics.IsSquashing)
            yield return null;

        // 4. Hold on the result, then take it out of the simulation
        yield return new WaitForSeconds(removeDelay);

        ColorBody last = TakeLastBody();

        // 5. Make room, slide it left and grow the true colour beside it
        if (last != null)
            yield return RevealRoutine(last, guessedColors, trueColor);

        _sequenceRoutine = null;
    }

    #region Reveal

    /// <summary>
    /// Removes the remaining body from the physics (the GameObject stays).
    /// If merging left more than one, the largest is used.
    /// </summary>
    private ColorBody TakeLastBody()
    {
        IReadOnlyList<ColorBody> bodies = colorPhysics.Bodies;
        ColorBody last = null;

        foreach (ColorBody body in bodies)
        {
            if (body != null && (last == null || body.Radius > last.Radius))
                last = body;
        }

        if (last == null)
        {
            Debug.LogWarning("GameManager: no body left to reveal.");
            return null;
        }

        if (bodies.Count > 1)
            Debug.LogWarning($"GameManager: {bodies.Count} bodies left after merging; revealing the largest.");

        // Settle any overshoot/squash so the measured radius is final
        last.CompleteScaleTween();
        colorPhysics.RemoveBody(last);

        MergedBody = last;
        return last;
    }

    /// <summary>
    /// Zooms the camera so two bodies of the merged size fit side by side,
    /// slides the merged body to the left half, then spawns a body on the
    /// right that grows to the same size while its colour blends through
    /// the guessed colours and converges on the true colour.
    /// </summary>
    private System.Collections.IEnumerator RevealRoutine(
        ColorBody merged,
        IReadOnlyList<Color> guessedColors,
        Color trueColor)
    {
        Camera cam = revealCamera != null ? revealCamera : Camera.main;

        float radius = merged.Radius;
        float offset = radius + revealGap * 0.5f;

        Vector3 center = cam != null ? cam.transform.position : merged.transform.position;
        center.z = merged.transform.position.z;

        Vector3 leftPos = center - Vector3.right * offset;
        Vector3 rightPos = center + Vector3.right * offset;

        _revealTween = Sequence.Create()
            .Group(Tween.Position(merged.transform, leftPos, revealDuration, Ease.InOutQuad))
            .Group(Tween.Rotation(merged.transform, Quaternion.identity, revealDuration, Ease.InOutQuad));

        if (cam != null && cam.orthographic)
        {
            // Stop the area zoom from pulling the camera back in
            _pausedZoom = cam.GetComponent<AreaCameraZoom>();

            if (_pausedZoom != null)
                _pausedZoom.enabled = false;

            // Half-height that fits both circles (width / aspect) with padding
            float aspect = Mathf.Max(0.01f, cam.aspect);
            float size = Mathf.Max(radius, (offset + radius) / aspect) * revealPadding;

            _revealTween.Group(
                Tween.CameraOrthographicSize(cam, size, revealDuration, Ease.InOutQuad)
            );
        }

        yield return _revealTween.ToYieldInstruction();

        // Guesses first, true colour last
        List<Color> path = new();

        if (guessedColors != null)
            path.AddRange(guessedColors);

        path.Add(trueColor);

        const float startRadius = 0.001f;

        ColorBody truth = colorPhysics.CreateBody(path[0], startRadius, rightPos);
        TrueBody = truth;

        if (playEffectOnReveal)
            truth.PlayMergeEffect();

        // Grow and colour blend start together. The scale tween is made
        // here rather than with ColorBody.ScaleRadius, because that keeps
        // its own handle to the tween, and PrimeTween doesn't allow
        // stopping a tween directly once a Sequence owns it.
        _revealTween = Sequence.Create()
            .Group(Tween.Scale(
                truth.transform,
                Vector3.one * (startRadius * 2f),
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

        yield return _revealTween.ToYieldInstruction();

        // Land exactly on the true colour
        truth.Color = trueColor;
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

    /// <summary>
    /// Removes the reveal's bodies and gives the camera back to its
    /// AreaCameraZoom. Called automatically when a new sequence starts.
    /// </summary>
    [Button]
    public void ClearReveal()
    {
        if (_revealTween.isAlive)
            _revealTween.Stop();

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

    #endregion

    /// <summary>
    /// Hues evenly spaced around the colour wheel from a random start, so
    /// every colour is as far from its neighbours as possible.
    /// </summary>
    private List<Color> GenerateDistinctColors(int count, System.Random random)
    {
        List<Color> colors = new(count);
        float startHue = (float)random.NextDouble();

        for (int i = 0; i < count; i++)
        {
            float hue = Mathf.Repeat(startHue + (float)i / count, 1f);
            colors.Add(Color.HSVToRGB(hue, saturation, value));
        }

        return colors;
    }
}

using System.Collections.Generic;
using Alchemy.Inspector;
using Core.Utility;
using UnityEngine;

public class GameManager : Singleton<GameManager>
{
    [SerializeField, Required] private ColorPhysics colorPhysics;

    // Plays the true colour reveal at the end. Falls back to (or adds) one
    // on this GameObject if left empty.
    [SerializeField] private RevealController revealController;

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

    // Pause after the last merge before the final body leaves the physics
    [SerializeField, Range(1f, 2f)] private float removeDelay = 1.5f;

    [Title("Randomness")]
    // Use a fixed seed to get the same colours and pieces every time
    [SerializeField] private bool useSeed;
    [SerializeField, ShowIf(nameof(useSeed))] private int seed;

    private Coroutine _sequenceRoutine;

    public bool IsRunning => _sequenceRoutine != null;

    public RevealController Reveal => revealController;

    protected override void Awake()
    {
        base.Awake();

        if (revealController != null)
            return;

        revealController = GetComponent<RevealController>();

        if (revealController == null)
        {
            Debug.LogWarning("GameManager: no RevealController assigned; adding one with default settings. Add it in the scene to tune it.");
            revealController = gameObject.AddComponent<RevealController>();
        }
    }

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

        revealController.Clear();

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

        // 4. Hold on the result
        yield return new WaitForSeconds(removeDelay);

        // 5. Take it out of the simulation and reveal the true colour
        yield return revealController.RevealLast(guessedColors, trueColor);

        _sequenceRoutine = null;
    }

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

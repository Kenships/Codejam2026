using System;
using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

public class ColorPhysics : MonoBehaviour
{
    #region Inspector

    [SerializeField] private ColorBody colorBodyPrefab;

    // Optional sprite for every body (empty = keep the prefab's sprite).
    // Collisions stay circular whatever the sprite looks like.
    [SerializeField] private Sprite bodySprite;

    [Title("Particles")]
    // Bodies placed in the scene; copied into the runtime list on Awake
    [SerializeField, FormerlySerializedAs("colorBody")]
    private List<ColorBody> initialBodies = new();

    // Live list, deliberately NOT serialized. During Play mode the
    // Inspector writes its cached copy of serialized fields back whenever
    // anything is edited or a button pressed; if this list were serialized,
    // that stale copy would drop bodies spawned since its last refresh
    // (leaving them alive but unregistered) and restore destroyed ones.
    [ShowInInspector, ReadOnly]
    private readonly List<ColorBody> _bodies = new();

    [Title("Collision")]
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

    // Extra distance travelled past the cluster before the train wraps around
    [SerializeField, Min(0f)] private float waveGap = 1f;

    // Number of lines sweeping at once, following each other as a train
    [SerializeField, Min(1)] private int waveCount = 1;

    // Centre-to-centre distance between consecutive lines in the train
    // (below waveWidth * 2 their bands overlap and the pushes add up)
    [SerializeField, Min(0f)] private float waveSpacing = 2.5f;

    [Title("Merging")]
    [SerializeField, AssetsOnly] private AudioClip mergeSound;
    // How fast a merging pair closes the distance between them (units/s)
    [SerializeField, Min(0f)] private float mergeSpeed = 1.5f;

    // How fast each body turns to face its partner (degrees/s)
    [SerializeField, Min(1f)] private float mergeAlignSpeed = 540f;

    // How fast bodies that aren't merging turn back to rotation 0
    // (degrees/s), so they don't keep the angle from a merge alignment
    [SerializeField, Min(0f)] private float rotationReturnSpeed = 180f;

    // Give up on a pair that hasn't touched within this many seconds
    // (e.g. blocked by another body) and re-pair both with someone else
    [SerializeField, Min(0.1f)] private float mergeApproachTimeout = 2f;

    // How long a failed pair is kept from being picked together again
    [SerializeField, Min(0f)] private float mergeRetryCooldown = 3f;

    // How long the pair presses against each other before overlapping
    [SerializeField, Min(0.01f)] private float mergePressDuration = 0.4f;

    // Peak squash for an equal-sized pair; the larger body squashes more
    [SerializeField, Range(0f, 0.6f)] private float mergeMaxSquash = 0.2f;

    // Cap on sideways widening while squashed (1.15 = at most 15% wider)
    [SerializeField, Min(1f)] private float mergeMaxStretch = 1.15f;

    // How long the pair takes to tween back to round once they start
    // overlapping (also used when a merge is cancelled)
    [SerializeField, Min(0.01f)] private float mergeUnsquashDuration = 0.25f;

    // Fuse once the centres are within (larger radius - smaller radius)
    // + this. 0 = the smaller body is fully inside the larger one;
    // higher fuses earlier, while it still pokes out.
    [SerializeField, Min(0f)] private float mergeFuseOffset = 0.05f;

    // Fuse an overlapping pair anyway after this long, in case neighbours
    // keep the smaller body from fully sinking in (seconds)
    [SerializeField, Min(0.1f)] private float mergeOverlapTimeout = 1.5f;

    [Title("Merge Spawn")]
    // How squashed the merged body starts before springing round
    [SerializeField, Range(0f, 0.6f)] private float mergeSpawnSquash = 0.1f;

    [SerializeField, Min(0.01f)] private float mergeSpawnDuration = 0.35f;

    // 1 = standard OutBack overshoot, 0 = none
    [SerializeField, Min(0f)] private float mergeSpawnOvershoot = 1f;

    [Title("Spawning")]
    // Distance from the origin new bodies spawn at (random direction)
    [SerializeField, Min(0f)] private float spawnDistance = 5f;

    [Tooltip("Every cube in a SpawnQueue appears within this many seconds, however many there " +
             "are: the gap between spawns shrinks as the count grows (several per frame if needed).")]
    [SerializeField, Min(0f)] private float spawnWindowSeconds = 2f;

    [Tooltip("Longest gap between two spawns, so a handful of cubes doesn't trickle in across " +
             "the whole window.")]
    [FormerlySerializedAs("spawnInterval")]
    [SerializeField, Min(0.01f)] private float maxSpawnInterval = 0.15f;

    [Title("Burst")]
    [Tooltip("How long SpawnBurst pieces fly free (no pull toward the cluster) before the " +
             "physics gathers them in, so the burst reads as a pop where it happened.")]
    [SerializeField, Min(0f)] private float burstHoldSeconds = 0.5f;

    [Tooltip("Extra per-step slowdown while a burst piece flies free (1 = none): they shoot " +
             "out and quickly settle, instead of sailing across the screen.")]
    [SerializeField, Range(0.5f, 1f)] private float burstDrag = 0.88f;

    [Tooltip("Size a burst piece starts at, as a fraction of its final size; it then tweens up " +
             "to its correct size over Burst Grow Duration.")]
    [SerializeField, Range(0.01f, 1f)] private float burstStartScale = 0.05f;

    [Tooltip("How long a burst piece takes to grow to its correct size. Keep it under the " +
             "GameManager's merge delay so pieces are full size before they merge.")]
    [SerializeField, Min(0.01f)] private float burstGrowDuration = 0.45f;

    // 0 = no overshoot; higher springs past the final size before settling
    [SerializeField, Min(0f)] private float burstGrowOvershoot = 1.2f;

    [Title("Squash All")]
    // Delay between starting merges (seconds)
    [SerializeField, Min(0.01f)] private float squashAllInterval = 0.25f;

    // How long only one body must remain before SquashAll stops, so
    // particles added in the meantime are still merged (seconds)
    [SerializeField, Min(0f)] private float squashAllGracePeriod = 2.5f;

    [Title("Merge Into Cube")]
    // Sprite for the merged cube (empty = a generated white square)
    [SerializeField] private Sprite cubeSprite;

    // How long the physics clumps every body together before they burst
    // into the cube
    [SerializeField, Min(0.01f)] private float cubeCollapseDuration = 0.5f;

    // Over the clumping, cohesion (each body's pull toward the cluster
    // centre, per step) ramps up to this...
    [SerializeField, Range(0f, 1f)] private float cubeCollapseCohesion = 0.4f;

    // ...and centering (the whole cluster's pull toward this transform)
    // is multiplied up to this
    [SerializeField, Min(1f)] private float cubeCenteringMultiplier = 4f;

    // The cube then pops in from this fraction of its size, overshooting
    // (near 1 since the clump is already about the cube's size)
    [SerializeField, Range(0f, 1f)] private float cubePopStartScale = 0.8f;
    [SerializeField, Min(0.01f)] private float cubePopDuration = 0.5f;
    [SerializeField, Min(0f)] private float cubePopOvershoot = 3f;

    // How long the cube sits after popping in before it bursts
    [SerializeField, Min(0f)] private float cubeBurstDelay = 0.4f;

    // The burst: swells to this multiple of its size while fading out
    [SerializeField, Min(0.01f)] private float cubeBurstDuration = 0.2f;
    [SerializeField, Min(1f)] private float cubeBurstScale = 1.4f;

    // Played on the burst (empty = mergeSound)
    [SerializeField] private AudioClip cubeBurstSound;

    #endregion

    #region State

    private enum MergePhase
    {
        Approach,   // Pulled together while rotating into alignment
        Press,      // Touching and squashing, but still colliding
        Overlap     // Allowed to pass into each other until they fuse
    }

    private class MergePair
    {
        public ColorBody A;
        public ColorBody B;
        public MergePhase Phase;
        public float ApproachTime;
        public float OverlapTime;
        public float PressProgress;
        public float SquashA;
        public float SquashB;

        public bool Contains(ColorBody body) => body == A || body == B;

        public float SquashOf(ColorBody body) => body == A ? SquashA : SquashB;
    }

    // Absolute cap so a large body in a lopsided pair can't flatten fully
    private const float MaxBodySquash = 0.6f;

    // Float slack for "touching": collision holds a merging pair at exactly
    // rA + rB, so the press check needs a hair of tolerance to trigger
    private const float ContactTolerance = 0.001f;

    // Alignment is considered done below this angle (degrees)
    private const float AlignedAngle = 2f;

    private readonly List<ColorBody> _active = new();
    private readonly HashSet<ColorBody> _initialized = new();

    // Cached once per physics step
    private Vector3 _right;
    private Vector3 _up;
    private Vector3 _forward;

    // Per-body desired offset from the cluster centre, in rank space
    private float[] _hueOffsets = Array.Empty<float>();
    private float[] _saturationOffsets = Array.Empty<float>();

    private float _waveOffset;
    private float _waveLinePos;
    private Vector3 _waveSweepDir;
    private Vector3 _wavePushDir;

    private readonly List<MergePair> _mergePairs = new();
    private readonly Dictionary<ColorBody, MergePair> _mergeLookup = new();

    // Pairs that timed out, keyed by ordered instance IDs -> time they
    // may be paired again
    private readonly Dictionary<(int, int), float> _failedPairs = new();
    private readonly List<MergePair> _timedOutPairs = new();

    private Coroutine _squashAllRoutine;
    private Coroutine _cubeRoutine;
    private PrimeTween.Sequence _cubeCollapseTween;

    // While MergeAllIntoCube collapses the bodies: 0 -> 1 over the collapse
    private bool _collapsing;
    private float _collapseRamp;
    private static Sprite _generatedSquare;

    // SpawnBurst pieces still flying free -> Time.time they rejoin the cluster
    private readonly Dictionary<ColorBody, float> _burstUntil = new();
    private readonly List<ColorBody> _expiredBursts = new();

    private readonly Queue<(Color color, float radius)> _spawnQueue = new();
    private Coroutine _spawnRoutine;
    // Gap between spawns for what's queued now (see SpawnIntervalFor)
    private float _spawnInterval;

    #endregion

    #region Physics Loop

    private void Awake()
    {
        foreach (ColorBody body in initialBodies)
            AddBody(body);
    }

    private void FixedUpdate()
    {
        DropInvalidMergePairs();
        GatherActiveBodies();

        if (_active.Count == 0)
            return;

        float dt = Time.fixedDeltaTime;

        _right = transform.right;
        _up = transform.up;
        _forward = transform.forward;

        CalculateSortTargets();         // 1. Where each body wants to be

        if (waveEnabled)
            UpdateWaveLine(dt);         // 2. Advance the sweeping wave

        Integrate(dt);                  // 3. Verlet step (+ wave force)
        SolveConstraints(dt);           // 4. Sorting, merging, collisions
        UpdateMerges(dt);               // 5. Align, squash, advance phases
        RelaxRotations(dt);             // 6. Free bodies turn back to 0
        ApplyPositions();               // 7. Write back to transforms
        ResolveMerges();                // 8. Fuse pairs that have met
    }

    /// <summary>
    /// Turns every body that isn't in a merge back toward rotation 0
    /// (this transform's rotation, i.e. identity unless ColorPhysics is
    /// rotated). Merging bodies are left to UpdateMerges' alignment.
    /// </summary>
    private void RelaxRotations(float dt)
    {
        Quaternion rest = transform.rotation;
        float maxStep = rotationReturnSpeed * dt;

        foreach (ColorBody body in _active)
        {
            if (_mergeLookup.ContainsKey(body))
                continue;

            Transform t = body.transform;

            t.rotation = Quaternion.RotateTowards(t.rotation, rest, maxStep);
        }
    }

    #endregion

    #region Physics Steps

    private void GatherActiveBodies()
    {
        _active.Clear();

        foreach (ColorBody body in _bodies)
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

        // Burst pieces whose free flight is over (or that merged away) rejoin the cluster
        if (_burstUntil.Count > 0)
        {
            _expiredBursts.Clear();
            foreach (KeyValuePair<ColorBody, float> pair in _burstUntil)
            {
                if (pair.Key == null || Time.time >= pair.Value)
                    _expiredBursts.Add(pair.Key);
            }

            foreach (ColorBody body in _expiredBursts)
                _burstUntil.Remove(body);
        }
    }

    private bool IsBursting(ColorBody body) => _burstUntil.ContainsKey(body);

    /// <summary>
    /// Ranks bodies by hue and saturation, then stores each body's rank
    /// relative to the average. These become its target offset on the
    /// right (hue) and up (saturation) axes.
    /// </summary>
    private void CalculateSortTargets()
    {
        int count = _active.Count;

        float[] hues = new float[count];
        float[] saturations = new float[count];

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

        if (_hueOffsets.Length != count)
        {
            _hueOffsets = new float[count];
            _saturationOffsets = new float[count];
        }

        CalculateRanks(hues, _hueOffsets);
        CalculateRanks(saturations, _saturationOffsets);

        SubtractMean(_hueOffsets);
        SubtractMean(_saturationOffsets);
    }

    private void Integrate(float dt)
    {
        foreach (ColorBody body in _active)
        {
            Vector3 velocity =
                (body.CurrentPos - body.LastPos) *
                verletDamping;

            if (IsBursting(body))
                velocity *= burstDrag;

            if (velocity.magnitude / dt < minVelocity)
                velocity = Vector3.zero;

            Vector3 nextPos = body.CurrentPos + velocity;

            // The wave fades out during a cube collapse
            if (waveEnabled)
                nextPos += WaveAcceleration(body.CurrentPos) *
                           (dt * dt * (_collapsing ? 1f - _collapseRamp : 1f));

            body.LastPos = body.CurrentPos;
            body.CurrentPos = nextPos;
        }
    }

    private void SolveConstraints(float dt)
    {
        float iterationDt = dt / constraintIterations;

        for (int iteration = 0;
             iteration < constraintIterations;
             iteration++)
        {
            ApplyClusterCorrections();
            ApplyMergePull(iterationDt);

            // During a cube collapse nothing repels, so cohesion can pull
            // every body into the same point
            if (!_collapsing)
                SolveCollisions();
        }
    }

    /// <summary>
    /// HSV sorting, cohesion, plane flattening and centering.
    /// Moves both CurrentPos and LastPos so no velocity is injected.
    /// </summary>
    private void ApplyClusterCorrections()
    {
        int count = _active.Count;
        Vector3 center = GetClusterCenter();

        // During MergeAllIntoCube: cohesion and centering ramp up, sorting
        // fades out, so the physics itself crushes the bodies together
        float ramp = _collapsing ? _collapseRamp : 0f;

        float centering = Mathf.Min(
            1f,
            centeringStrength * Mathf.Lerp(1f, cubeCenteringMultiplier, ramp)
        );
        float cohesion = Mathf.Lerp(cohesionStrength, cubeCollapseCohesion, ramp);
        float sortStrength = hsvSortStrength * (1f - ramp);

        // Pulls the whole cluster toward this transform
        Vector3 centeringCorrection =
            (transform.position - center) * centering;

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
                _right * (_hueOffsets[i] * clusterSize) +
                _up * (_saturationOffsets[i] * clusterSize);

            Vector3 currentOffset = body.CurrentPos - center;

            Vector3 planeCorrection =
                -_forward * Vector3.Dot(currentOffset, _forward);

            // Burst pieces fly free for a moment: no pull toward the cluster yet
            if (IsBursting(body))
            {
                body.CurrentPos += planeCorrection;
                body.LastPos += planeCorrection;
                continue;
            }

            Vector3 correction = planeCorrection + centeringCorrection;

            // Merging bodies ignore sorting/cohesion so they can
            // actually reach each other instead of being held apart
            if (!_mergeLookup.ContainsKey(body))
            {
                Vector3 sortCorrection =
                    (desiredOffset - currentOffset) * sortStrength;

                Vector3 cohesionCorrection =
                    -currentOffset * cohesion;

                correction += sortCorrection + cohesionCorrection;
            }

            body.CurrentPos += correction;
            body.LastPos += correction;
        }
    }

    private void SolveCollisions()
    {
        int count = _active.Count;

        for (int i = 0; i < count - 1; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                ColorBody a = _active[i];
                ColorBody b = _active[j];

                if (!TryGetMinDistance(a, b, out float minDistance))
                    continue;

                Vector3 delta = b.CurrentPos - a.CurrentPos;
                float distance = delta.magnitude;

                if (distance >= minDistance)
                    continue;

                Vector3 dir = distance > 0.00001f
                    ? delta / distance
                    : FallbackDirection(i, j);

                float overlap = minDistance - distance;
                GetCollisionShares(a, b, out float shareA, out float shareB);

                Vector3 correctionA = dir * (overlap * shareA);
                Vector3 correctionB = dir * (overlap * shareB);

                a.CurrentPos -= correctionA;
                b.CurrentPos += correctionB;

                a.LastPos -= correctionA;
                b.LastPos += correctionB;
            }
        }
    }

    /// <summary>
    /// How much of a collision correction each body takes. Normally split
    /// evenly, but a merging body has priority over a non-merging one:
    /// the bystander takes the whole push. Otherwise neighbours (still
    /// pulled by sorting/cohesion) can hold a pair apart indefinitely.
    /// </summary>
    private void GetCollisionShares(
        ColorBody a,
        ColorBody b,
        out float shareA,
        out float shareB)
    {
        bool aMerging = _mergeLookup.ContainsKey(a);
        bool bMerging = _mergeLookup.ContainsKey(b);

        if (aMerging == bMerging)
        {
            shareA = 0.5f;
            shareB = 0.5f;
            return;
        }

        shareA = aMerging ? 0f : 1f;
        shareB = 1f - shareA;
    }

    /// <summary>
    /// Contact distance between two bodies. A merging pair ignores
    /// colorBodyGap so they touch edge to edge, and while pressing uses
    /// their squashed half-heights (they face each other along local Y),
    /// so they visibly push closer as they flatten. Returns false for a
    /// pair in the Overlap phase, which shouldn't collide at all.
    /// </summary>
    private bool TryGetMinDistance(
        ColorBody a,
        ColorBody b,
        out float minDistance)
    {
        if (_mergeLookup.TryGetValue(a, out MergePair pair) &&
            pair.Contains(b))
        {
            if (pair.Phase == MergePhase.Overlap)
            {
                minDistance = 0f;
                return false;
            }

            minDistance = Mathf.Max(
                0.0001f,
                a.Radius * (1f - pair.SquashOf(a)) +
                b.Radius * (1f - pair.SquashOf(b))
            );
            return true;
        }

        minDistance = Mathf.Max(0.0001f, a.Radius + b.Radius + colorBodyGap);
        return true;
    }

    private void ApplyPositions()
    {
        foreach (ColorBody body in _active)
            body.transform.position = body.CurrentPos;
    }

    #endregion

    #region Ambient Wave

    private void UpdateWaveLine(float dt)
    {
        Quaternion rotation = Quaternion.AngleAxis(waveAngle, _forward);

        // The line travels along sweepDir and pushes perpendicular to it
        // (a transverse wave), which is "up" at the default angle.
        _waveSweepDir = rotation * _right;
        _wavePushDir = rotation * _up;

        float minProj = float.MaxValue;
        float maxProj = float.MinValue;

        foreach (ColorBody body in _active)
        {
            float proj = Vector3.Dot(body.CurrentPos, _waveSweepDir);
            float r = body.Radius;

            minProj = Mathf.Min(minProj, proj - r);
            maxProj = Mathf.Max(maxProj, proj + r);
        }

        // Sweep the lead line from just before the cluster until the last
        // line of the train is just past it, rest for waveGap, then wrap
        float trainLength = (waveCount - 1) * waveSpacing;
        float span =
            (maxProj - minProj) + waveWidth * 2f + trainLength + waveGap;

        _waveOffset = Mathf.Repeat(_waveOffset + waveSpeed * dt, span);
        _waveLinePos = minProj - waveWidth + _waveOffset;
    }

    // Line 0 leads; each following line trails by waveSpacing
    private float WaveLinePos(int index) =>
        _waveLinePos - index * waveSpacing;

    private Vector3 WaveAcceleration(Vector3 position)
    {
        float proj = Vector3.Dot(position, _waveSweepDir);
        float total = 0f;

        for (int i = 0; i < waveCount; i++)
        {
            float distance = (proj - WaveLinePos(i)) / waveWidth;

            if (distance <= -1f || distance >= 1f)
                continue;

            // Smooth cosine bump: 1 on the line, 0 at the band edge
            total += 0.5f * (1f + Mathf.Cos(distance * Mathf.PI));
        }

        return _wavePushDir * (waveForce * total);
    }

    private void OnDrawGizmosSelected()
    {
        if (!waveEnabled || !Application.isPlaying || _active.Count == 0)
            return;

        Vector3 alongLine = _wavePushDir;
        Vector3 lineOrigin =
            alongLine * Vector3.Dot(transform.position, alongLine);

        const float halfLength = 20f;
        Color bandColor = new(0f, 1f, 1f, 0.3f);

        for (int i = 0; i < waveCount; i++)
        {
            Vector3 linePoint = lineOrigin + _waveSweepDir * WaveLinePos(i);

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(
                linePoint - alongLine * halfLength,
                linePoint + alongLine * halfLength
            );

            Gizmos.color = bandColor;

            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 edge = linePoint + _waveSweepDir * (waveWidth * side);
                Gizmos.DrawLine(
                    edge - alongLine * halfLength,
                    edge + alongLine * halfLength
                );
            }
        }
    }

    #endregion

    #region Merging

    /// <summary>
    /// Picks a random non-merging body and pairs it with its closest
    /// non-merging neighbour. The pair then goes through
    /// Approach -> Press -> Overlap and fuses once their centres are
    /// within (larger radius - smaller radius) + mergeFuseOffset.
    /// </summary>
    [Button]
    public void Merge()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("ColorPhysics.Merge only works in Play mode.");
            return;
        }

        if (!TryStartMerge())
            Debug.Log("ColorPhysics.Merge: fewer than two bodies available.");
    }

    /// <summary>
    /// Keeps starting merges every squashAllInterval seconds (several can
    /// run at once) until only one body has been left for
    /// squashAllGracePeriod seconds.
    /// </summary>
    [Button]
    public void SquashAll()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("ColorPhysics.SquashAll only works in Play mode.");
            return;
        }

        if (_squashAllRoutine != null)
            StopCoroutine(_squashAllRoutine);

        _squashAllRoutine = StartCoroutine(SquashAllRoutine());
    }

    private IEnumerator SquashAllRoutine()
    {
        WaitForSeconds wait = new(squashAllInterval);
        float idleTime = 0f;

        // Only stop once there's been a single body (or none) for the
        // whole grace period, so particles added in the meantime still
        // get merged instead of the routine ending the moment it hits one
        while (idleTime < squashAllGracePeriod)
        {
            if (CountLiveBodies() > 1)
            {
                idleTime = 0f;

                // False while every remaining body is already mid-merge;
                // just wait for those to fuse and try again
                TryStartMerge();
                yield return wait;
            }
            else
            {
                idleTime += Time.deltaTime;
                yield return null;
            }
        }

        _squashAllRoutine = null;
    }

    public bool IsMergingIntoCube => _cubeRoutine != null;

    /// <summary>
    /// Merges everything at once: the physics clumps them up (cohesion and
    /// centering ramp up, sorting and the wave fade out), then they burst
    /// into a single cube that pops in with the total
    /// area and the area-weighted HSV average colour. After cubeBurstDelay
    /// it bursts (merge effect left behind, swell and fade) and is removed,
    /// leaving no bodies.
    /// </summary>
    [Button]
    public void MergeAllIntoCube()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("ColorPhysics.MergeAllIntoCube only works in Play mode.");
            return;
        }

        if (_cubeRoutine != null)
            return;

        _cubeRoutine = StartCoroutine(MergeAllIntoCubeRoutine());
    }

    private IEnumerator MergeAllIntoCubeRoutine()
    {
        // Let any queued spawns land first so they're included
        while (IsSpawning)
            yield return null;

        // Pair-by-pair merging would fight the collapse
        if (_squashAllRoutine != null)
        {
            StopCoroutine(_squashAllRoutine);
            _squashAllRoutine = null;
        }

        List<ColorBody> bodies = new();

        foreach (ColorBody body in _bodies)
        {
            if (body != null)
                bodies.Add(body);
        }

        if (bodies.Count == 0)
        {
            _cubeRoutine = null;
            yield break;
        }

        // The collapse merges everything, so drop any pair merges (and
        // finish their unsquash so the shrink below owns the scale)
        foreach (ColorBody body in bodies)
        {
            CancelMerge(body);
            body.CompleteScaleTween();
        }

        // Starting sizes and the totals, before anything shrinks
        Dictionary<ColorBody, float> startRadii = new(bodies.Count);
        float totalArea = 0f;
        Color color = bodies[0].Color;

        foreach (ColorBody body in bodies)
        {
            float startRadius = body.Radius;
            float area = startRadius * startRadius;

            startRadii[body] = startRadius;

            // Fold colours in one at a time (like the overlap shader)
            if (totalArea > 0f)
                color = AverageHSV(color, body.Color, totalArea, area);

            totalArea += area;
        }

        // Same area as all of them together, like a normal merge
        float radius = Mathf.Sqrt(totalArea);

        // Hand the clumping to the physics: cohesion and centering ramp up
        // while sorting and the wave fade out (see ApplyClusterCorrections).
        // Collisions are off (see SolveConstraints), so they converge on a
        // single point instead of packing into a clump.
        _collapsing = true;
        _collapseRamp = 0f;

        float elapsed = 0f;

        while (elapsed < cubeCollapseDuration)
        {
            yield return new WaitForFixedUpdate();

            elapsed += Time.fixedDeltaTime;

            // Ease out: they snap together fast, then hold the clump
            float t = Mathf.Clamp01(elapsed / cubeCollapseDuration);
            _collapseRamp = 1f - (1f - t) * (1f - t);
        }

        _collapsing = false;
        _collapseRamp = 0f;

        // The cube appears where they ended up (area-weighted)
        Vector3 center = Vector3.zero;
        float centerWeight = 0f;

        foreach (ColorBody body in bodies)
        {
            if (body == null)
                continue;

            float area = startRadii[body] * startRadii[body];
            center += body.CurrentPos * area;
            centerWeight += area;
        }

        center = centerWeight > 0f ? center / centerWeight : transform.position;

        foreach (ColorBody body in bodies)
        {
            if (body == null)
                continue;

            RemoveBody(body);
            Destroy(body.gameObject);
        }

        // Pop the cube in at the centre
        ColorBody cube = Instantiate(colorBodyPrefab, center, Quaternion.identity, bodies[0].transform.parent);

        cube.ScaleRadius(
            start: radius * cubePopStartScale,
            end: radius,
            duration: cubePopDuration,
            overshoot: cubePopOvershoot
        );

        cube.Color = color;
        AddBody(cube);

        // After AddBody, which applies bodySprite
        cube.Sprite = cubeSprite != null ? cubeSprite : GetGeneratedSquare();

        PlaySound(mergeSound);

        yield return new WaitForSeconds(cubePopDuration + cubeBurstDelay);

        // Burst: out of the physics, effect left behind, swell and fade
        RemoveBody(cube);
        cube.CompleteScaleTween();
        cube.PlayDetachedEffect();
        PlaySound(cubeBurstSound != null ? cubeBurstSound : mergeSound);

        _cubeCollapseTween = PrimeTween.Sequence.Create()
            .Group(PrimeTween.Tween.Scale(
                cube.transform,
                cube.transform.localScale * cubeBurstScale,
                cubeBurstDuration,
                PrimeTween.Ease.OutQuad
            ))
            .Group(PrimeTween.Tween.Alpha(
                cube.Renderer,
                0f,
                cubeBurstDuration,
                PrimeTween.Ease.OutQuad
            ));

        yield return _cubeCollapseTween.ToYieldInstruction();

        if (cube != null)
            Destroy(cube.gameObject);

        _cubeRoutine = null;
    }

    private static void PlaySound(AudioClip clip)
    {
        if (clip == null || AudioManager.Instance == null)
            return;

        AudioManager.Instance.NewAudio(clip)
            .RandomizePitch()
            .Create()
            .Play();
    }

    /// <summary>A 1x1 unit white square, made once, for when no cubeSprite is set.</summary>
    private static Sprite GetGeneratedSquare()
    {
        if (_generatedSquare != null)
            return _generatedSquare;

        const int size = 4;

        Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
        {
            name = "GeneratedSquare",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color32[] pixels = new Color32[size * size];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(255, 255, 255, 255);

        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        // pixelsPerUnit = size, so it's 1 unit across like the Circle sprite
        _generatedSquare = Sprite.Create(
            texture,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            size
        );

        _generatedSquare.name = "GeneratedSquare";
        _generatedSquare.hideFlags = HideFlags.HideAndDontSave;

        return _generatedSquare;
    }

    private int CountLiveBodies()
    {
        int count = 0;

        foreach (ColorBody body in _bodies)
        {
            if (body != null)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Pairs a random free body with its closest free neighbour.
    /// Returns false if fewer than two bodies are free.
    /// </summary>
    private bool TryStartMerge()
    {
        // A cube collapse is already merging everything
        if (_collapsing)
            return false;

        List<ColorBody> available = GetAvailableBodies();

        if (available.Count < 2)
            return false;

        ColorBody first = available[Random.Range(0, available.Count)];

        return TryPairWithClosest(first, available);
    }

    private List<ColorBody> GetAvailableBodies()
    {
        List<ColorBody> available = new();

        foreach (ColorBody body in _bodies)
        {
            if (body != null && !_mergeLookup.ContainsKey(body))
                available.Add(body);
        }

        return available;
    }

    private bool TryPairWithClosest(
        ColorBody first,
        List<ColorBody> available)
    {
        if (first == null || _mergeLookup.ContainsKey(first))
            return false;

        ColorBody second = FindClosest(first, available);

        if (second == null)
            return false;

        // A recently merged body may still be springing; settle it so
        // its radius is final before we start squashing it
        first.CompleteScaleTween();
        second.CompleteScaleTween();

        MergePair pair = new() { A = first, B = second };

        _mergePairs.Add(pair);
        _mergeLookup[first] = pair;
        _mergeLookup[second] = pair;

        return true;
    }

    /// <summary>
    /// Closest free body by edge-to-edge distance, so a large neighbour
    /// isn't penalised for having its centre further away. Skips partners
    /// this body recently failed to merge with, unless they're the only
    /// option left.
    /// </summary>
    private ColorBody FindClosest(
        ColorBody target,
        List<ColorBody> candidates)
    {
        ColorBody closest = null;
        ColorBody closestFailed = null;
        float closestGap = float.MaxValue;
        float closestFailedGap = float.MaxValue;

        foreach (ColorBody other in candidates)
        {
            if (other == target || other == null ||
                _mergeLookup.ContainsKey(other))
                continue;

            float gap =
                Vector3.Distance(
                    target.transform.position,
                    other.transform.position
                ) - target.Radius - other.Radius;

            if (IsRecentlyFailed(target, other))
            {
                if (gap < closestFailedGap)
                {
                    closestFailedGap = gap;
                    closestFailed = other;
                }
            }
            else if (gap < closestGap)
            {
                closestGap = gap;
                closest = other;
            }
        }

        return closest != null ? closest : closestFailed;
    }

    /// <summary>
    /// Cancels pairs that timed out in Approach, remembers them so they
    /// aren't immediately re-picked, and re-pairs each freed body with
    /// its closest other free body.
    /// </summary>
    private void RepairTimedOutPairs()
    {
        if (_timedOutPairs.Count == 0)
            return;

        foreach (MergePair pair in _timedOutPairs)
        {
            CancelMerge(pair.A);
            RecordFailedPair(pair.A, pair.B);
        }

        foreach (MergePair pair in _timedOutPairs)
        {
            List<ColorBody> available = GetAvailableBodies();

            TryPairWithClosest(pair.A, available);
            TryPairWithClosest(pair.B, available);
        }

        _timedOutPairs.Clear();
    }

    private static (int, int) PairKey(ColorBody a, ColorBody b)
    {
        int idA = a.GetInstanceID();
        int idB = b.GetInstanceID();

        return idA < idB ? (idA, idB) : (idB, idA);
    }

    private void RecordFailedPair(ColorBody a, ColorBody b)
    {
        if (a == null || b == null)
            return;

        PruneFailedPairs();
        _failedPairs[PairKey(a, b)] = Time.time + mergeRetryCooldown;
    }

    private bool IsRecentlyFailed(ColorBody a, ColorBody b) =>
        _failedPairs.TryGetValue(PairKey(a, b), out float until) &&
        Time.time < until;

    // Drop expired entries (including ones for since-destroyed bodies)
    private void PruneFailedPairs()
    {
        List<(int, int)> expired = null;

        foreach (KeyValuePair<(int, int), float> entry in _failedPairs)
        {
            if (Time.time >= entry.Value)
                (expired ??= new()).Add(entry.Key);
        }

        if (expired == null)
            return;

        foreach ((int, int) key in expired)
            _failedPairs.Remove(key);
    }

    private void ApplyMergePull(float stepDt)
    {
        float maxStep = mergeSpeed * stepDt;

        foreach (MergePair pair in _mergePairs)
        {
            ColorBody a = pair.A;
            ColorBody b = pair.B;

            Vector3 delta = b.CurrentPos - a.CurrentPos;
            float distance = delta.magnitude;

            if (distance < 0.00001f)
                continue;

            // Each body moves half the step, never past the midpoint.
            // Applied to LastPos too so it doesn't inject velocity.
            float step = Mathf.Min(maxStep * 0.5f, distance * 0.5f);
            Vector3 correction = delta / distance * step;

            a.CurrentPos += correction;
            a.LastPos += correction;
            b.CurrentPos -= correction;
            b.LastPos -= correction;
        }
    }

    /// <summary>
    /// Keeps each pair facing each other and advances its phase:
    /// Approach until aligned and touching, Press while the squash
    /// builds up, then Overlap (tweening back to round) until
    /// ResolveMerges fuses it.
    /// </summary>
    private void UpdateMerges(float dt)
    {
        foreach (MergePair pair in _mergePairs)
        {
            Vector3 axis = Vector3.ProjectOnPlane(
                pair.B.CurrentPos - pair.A.CurrentPos,
                _forward
            );
            float distance = axis.magnitude;

            bool aligned = true;

            if (distance > 0.00001f)
            {
                axis /= distance;

                bool alignedA = RotateToFace(pair.A, axis, dt);
                bool alignedB = RotateToFace(pair.B, axis, dt);

                aligned = alignedA && alignedB;
            }

            switch (pair.Phase)
            {
                case MergePhase.Approach:
                    // Edges actually touching - no colorBodyGap
                    float contact =
                        pair.A.Radius + pair.B.Radius + ContactTolerance;

                    if (aligned && distance <= contact)
                    {
                        pair.Phase = MergePhase.Press;
                        break;
                    }

                    // Blocked (e.g. a body in between, or two pairs
                    // jamming each other): give up and re-pair after
                    // the loop, since we can't edit the list mid-foreach
                    pair.ApproachTime += dt;

                    if (pair.ApproachTime >= mergeApproachTimeout)
                        _timedOutPairs.Add(pair);
                    break;

                case MergePhase.Press:
                    pair.PressProgress = Mathf.Min(
                        1f,
                        pair.PressProgress + dt / mergePressDuration
                    );

                    ApplyPressSquash(pair);

                    if (pair.PressProgress >= 1f)
                    {
                        // The "wall" gives: they start overlapping and
                        // relax back to round while sliding together
                        pair.Phase = MergePhase.Overlap;
                        pair.A.Unsquash(mergeUnsquashDuration);
                        pair.B.Unsquash(mergeUnsquashDuration);
                    }
                    break;

                case MergePhase.Overlap:
                    // Unsquash tween runs on its own; ResolveMerges
                    // fuses when close enough, or once this runs out
                    pair.OverlapTime += dt;
                    break;
            }
        }

        RepairTimedOutPairs();
    }

    /// <summary>
    /// Turns the body so its local Y lies along the axis between the pair
    /// (either direction - the squash is symmetric, so pick the nearer).
    /// Returns true once it's within AlignedAngle.
    /// </summary>
    private bool RotateToFace(ColorBody body, Vector3 axis, float dt)
    {
        Quaternion current = body.transform.rotation;
        Quaternion facing = Quaternion.LookRotation(_forward, axis);
        Quaternion flipped = Quaternion.LookRotation(_forward, -axis);

        Quaternion target =
            Quaternion.Angle(current, facing) <=
            Quaternion.Angle(current, flipped)
                ? facing
                : flipped;

        body.transform.rotation = Quaternion.RotateTowards(
            current,
            target,
            mergeAlignSpeed * dt
        );

        return Quaternion.Angle(body.transform.rotation, target) <= AlignedAngle;
    }

    /// <summary>
    /// Squash grows with an ease-out over the press. Each body's share is
    /// weighted by its fraction of the pair's area, so the larger body
    /// deforms more (an equal pair both reach mergeMaxSquash).
    /// </summary>
    private void ApplyPressSquash(MergePair pair)
    {
        float t = pair.PressProgress;
        float eased = 1f - (1f - t) * (1f - t);

        float areaA = pair.A.Radius * pair.A.Radius;
        float areaB = pair.B.Radius * pair.B.Radius;
        float totalArea = Mathf.Max(0.00001f, areaA + areaB);

        pair.SquashA = PressSquash(areaA / totalArea, eased);
        pair.SquashB = PressSquash(areaB / totalArea, eased);

        pair.A.SetSquash(pair.SquashA, mergeMaxStretch);
        pair.B.SetSquash(pair.SquashB, mergeMaxStretch);
    }

    private float PressSquash(float areaShare, float eased) =>
        Mathf.Min(MaxBodySquash, mergeMaxSquash * 2f * areaShare * eased);

    private void ResolveMerges()
    {
        // Reverse order: SpawnMerged -> RemoveBody removes pair i
        for (int i = _mergePairs.Count - 1; i >= 0; i--)
        {
            MergePair pair = _mergePairs[i];

            if (pair.Phase != MergePhase.Overlap)
                continue;

            float distance = Vector3.Distance(
                pair.A.CurrentPos,
                pair.B.CurrentPos
            );

            // How far the smaller centre can be from the larger one while
            // fully inside it, plus the adjustable slack
            float fuseDistance =
                Mathf.Abs(pair.A.Radius - pair.B.Radius) +
                mergeFuseOffset;

            // Already overlapping, so fusing a bit early if something keeps
            // holding them apart looks fine and can't leave them stuck
            bool timedOut = pair.OverlapTime >= mergeOverlapTimeout;

            if (distance <= fuseDistance + 0.0001f || timedOut)
                SpawnMerged(pair.A, pair.B);
        }
    }

    private void SpawnMerged(ColorBody a, ColorBody b)
    {
        Vector3 position = (a.CurrentPos + b.CurrentPos) * 0.5f;
        Vector3 velocity =
            ((a.CurrentPos - a.LastPos) + (b.CurrentPos - b.LastPos)) * 0.5f;

        float areaA = a.Radius * a.Radius;
        float areaB = b.Radius * b.Radius;

        // Preserve total area: pi*r^2 = pi*ra^2 + pi*rb^2
        float radius = Mathf.Sqrt(areaA + areaB);

        // Weight by area (pi cancels out in the ratio)
        Color color = AverageHSV(a.Color, b.Color, areaA, areaB);

        // Same rotation as the (aligned) pair, so the starting squash
        // lies along the same axis they were pressing on
        ColorBody merged = Instantiate(
            colorBodyPrefab,
            position,
            a.transform.rotation,
            a.transform.parent
        );

        merged.ScaleRadius(
            start: Mathf.Max(a.Radius, b.Radius),
            end: radius,
            duration: mergeSpawnDuration,
            startSquash: mergeSpawnSquash,
            overshoot: mergeSpawnOvershoot,
            maxStretch: mergeMaxStretch
        );
        merged.Color = color;           // also recolours the merge effect
        merged.PlayMergeEffect();

        RemoveBody(a);
        RemoveBody(b);
        Destroy(a.gameObject);
        Destroy(b.gameObject);

        AddBody(merged);
        
        AudioManager.Instance.NewAudio(mergeSound)
            .RandomizePitch()
            .Create()
            .Play();

        // Carry over the pair's average momentum
        merged.LastPos = merged.CurrentPos - velocity;
    }

    /// <summary>
    /// Weighted HSV average. weightA/weightB are relative (e.g. areas);
    /// a larger weight pulls the result toward that colour.
    /// </summary>
    public static Color AverageHSV(
        Color a,
        Color b,
        float weightA,
        float weightB)
    {
        Color.RGBToHSV(a, out float ha, out float sa, out float va);
        Color.RGBToHSV(b, out float hb, out float sb, out float vb);

        float totalWeight = weightA + weightB;

        // t = 0 -> all A, t = 1 -> all B
        float t = totalWeight > 0.00001f ? weightB / totalWeight : 0.5f;

        // Hue is circular: interpolate along the shortest arc so that
        // e.g. 0.95 and 0.05 blend through 0.0 (red), not 0.5 (cyan).
        // Greys have no meaningful hue, so take the other body's hue.
        float h;

        if (sa < 0.0001f)
            h = hb;
        else if (sb < 0.0001f)
            h = ha;
        else
            h = Mathf.Repeat(
                ha + Mathf.DeltaAngle(ha * 360f, hb * 360f) / 360f * t,
                1f
            );

        Color result = Color.HSVToRGB(
            h,
            Mathf.Lerp(sa, sb, t),
            Mathf.Lerp(va, vb, t)
        );

        result.a = Mathf.Lerp(a.a, b.a, t);
        return result;
    }

    private void CancelMerge(ColorBody body)
    {
        if (body == null ||
            !_mergeLookup.TryGetValue(body, out MergePair pair))
            return;

        _mergePairs.Remove(pair);
        _mergeLookup.Remove(pair.A);
        _mergeLookup.Remove(pair.B);

        RestoreShape(pair);
    }

    // Handles bodies destroyed or removed from the list externally
    private void DropInvalidMergePairs()
    {
        bool removedAny = false;

        for (int i = _mergePairs.Count - 1; i >= 0; i--)
        {
            MergePair pair = _mergePairs[i];

            if (pair.A != null && pair.B != null &&
                _bodies.Contains(pair.A) && _bodies.Contains(pair.B))
                continue;

            _mergePairs.RemoveAt(i);
            RestoreShape(pair);
            removedAny = true;
        }

        if (!removedAny)
            return;

        // Destroyed bodies make stale dictionary keys, so rebuild
        _mergeLookup.Clear();

        foreach (MergePair pair in _mergePairs)
        {
            _mergeLookup[pair.A] = pair;
            _mergeLookup[pair.B] = pair;
        }
    }

    // Smoothly un-squash whichever bodies of a cancelled pair still exist
    private void RestoreShape(MergePair pair)
    {
        if (pair.A != null)
            pair.A.Unsquash(mergeUnsquashDuration);

        if (pair.B != null)
            pair.B.Unsquash(mergeUnsquashDuration);
    }

    #endregion

    #region Body Management

    public IReadOnlyList<ColorBody> Bodies => _bodies;

    public void AddBody(ColorBody body)
    {
        if (body == null)
            return;

        // Outside Play mode, record it in the scene's starting list instead
        if (!Application.isPlaying)
        {
            if (!initialBodies.Contains(body))
                initialBodies.Add(body);

            return;
        }

        if (_bodies.Contains(body))
            return;

        body.CurrentPos = body.transform.position;
        body.LastPos = body.CurrentPos;

        ApplyBodySprite(body);

        _bodies.Add(body);
        _initialized.Add(body);
    }

    /// <summary>
    /// The sprite every body uses (null = the prefab's own). Setting it at
    /// runtime also swaps the sprite on every body already simulated.
    /// </summary>
    public Sprite BodySprite
    {
        get => bodySprite;
        set
        {
            bodySprite = value;

            foreach (ColorBody body in _bodies)
            {
                if (body != null)
                    ApplyBodySprite(body);
            }
        }
    }

    private void ApplyBodySprite(ColorBody body)
    {
        if (bodySprite != null)
            body.Sprite = bodySprite;
    }

    public void RemoveBody(ColorBody body)
    {
        CancelMerge(body);
        _bodies.Remove(body);
        _initialized.Remove(body);
        _burstUntil.Remove(body);
    }

    /// <summary>
    /// Stops any active spawning/merging and destroys every ColorBody managed by this physics instance.
    /// </summary>
    public void ClearAllBodies()
    {
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }

        if (_squashAllRoutine != null)
        {
            StopCoroutine(_squashAllRoutine);
            _squashAllRoutine = null;
        }

        _spawnQueue.Clear();
        _mergePairs.Clear();
        _mergeLookup.Clear();
        _failedPairs.Clear();
        _timedOutPairs.Clear();

        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            ColorBody body = _bodies[i];
            if (body != null)
            {
                if (Application.isPlaying)
                    Destroy(body.gameObject);
                else
                    DestroyImmediate(body.gameObject);
            }
        }

        _bodies.Clear();
        _active.Clear();
        _initialized.Clear();
        _burstUntil.Clear();
    }

    [Button]
    public void SpawnRandomColor()
    {
        SpawnBody(Random.ColorHSV(0f, 1f, 1f, 1f, 1f, 1f), 0.5f);
    }

    /// <summary>
    /// Spawns one body per (color, radius) config, spread evenly so they all
    /// appear within spawnWindowSeconds (but no slower than maxSpawnInterval).
    /// The given queue is drained into an internal one, so calling this again
    /// while spawning appends to the line; whatever is still waiting then gets
    /// a fresh window.
    /// </summary>
    public void SpawnQueue(Queue<(Color color, float radius)> configs)
    {
        if (configs == null)
            return;

        while (configs.Count > 0)
            _spawnQueue.Enqueue(configs.Dequeue());

        if (!Application.isPlaying)
        {
            // No coroutines in Edit mode; spawn everything immediately
            while (_spawnQueue.Count > 0)
            {
                (Color color, float radius) = _spawnQueue.Dequeue();
                SpawnBody(color, radius);
            }

            return;
        }

        _spawnInterval = SpawnIntervalFor(_spawnQueue.Count);
        _spawnRoutine ??= StartCoroutine(SpawnQueueRoutine());
    }

    public bool IsSpawning => _spawnRoutine != null;

    public bool IsSquashing => _squashAllRoutine != null;

    /// <summary>Seconds from the first spawn to the last for this many queued bodies.</summary>
    public float SpawnDurationFor(int count) => Mathf.Max(0, count - 1) * SpawnIntervalFor(count);

    private float SpawnIntervalFor(int count) =>
        count <= 1 ? 0f : Mathf.Min(maxSpawnInterval, spawnWindowSeconds / (count - 1));

    private IEnumerator SpawnQueueRoutine()
    {
        float nextSpawnAt = Time.time;

        while (_spawnQueue.Count > 0)
        {
            // Everything that's due, so short gaps still keep the window at high counts
            while (_spawnQueue.Count > 0 && Time.time >= nextSpawnAt)
            {
                (Color color, float radius) = _spawnQueue.Dequeue();
                SpawnBody(color, radius);
                nextSpawnAt += _spawnInterval;
            }

            if (_spawnQueue.Count > 0)
                yield return null;
        }

        _spawnRoutine = null;
    }

    /// <summary>
    /// The random spawn routine: places a body at a random point on a
    /// circle of spawnDistance around the origin with the given colour
    /// and radius (scale = diameter, since Radius = localScale * 0.5).
    /// </summary>
    public ColorBody SpawnBody(Color color, float radius)
    {
        ColorBody body = CreateBody(
            color,
            radius,
            Random.onUnitCircle * spawnDistance
        );

        AddBody(body);

        return body;
    }

    /// <summary>
    /// Bursts one body per (color, radius) piece out of a point, all at
    /// once: each starts a little way from <paramref name="origin"/>, pops
    /// in from small and flies outward at up to <paramref name="speed"/>
    /// units/s, free of the cluster's pull for burstHoldSeconds so the pop
    /// happens where it started. Leaves the merge effect at the burst point
    /// and plays the merge sound once.
    /// </summary>
    public void SpawnBurst(
        Vector3 origin,
        IReadOnlyList<(Color color, float radius)> pieces,
        float speed,
        float spread = 0.25f)
    {
        if (pieces == null || pieces.Count == 0)
            return;

        Vector3 right = transform.right;
        Vector3 up = transform.up;
        float dt = Time.fixedDeltaTime;

        // Even spacing around the circle (with jitter) so pieces don't all fly the same way
        float baseAngle = Random.Range(0f, Mathf.PI * 2f);

        // The biggest piece carries the effect, so the pop is sized like the burst
        int effectIndex = 0;
        for (int i = 1; i < pieces.Count; i++)
        {
            if (pieces[i].radius > pieces[effectIndex].radius)
                effectIndex = i;
        }

        float holdUntil = Time.time + burstHoldSeconds;

        for (int i = 0; i < pieces.Count; i++)
        {
            (Color color, float radius) = pieces[i];

            float angle = baseAngle +
                          (i + Random.Range(-0.3f, 0.3f)) * (Mathf.PI * 2f / pieces.Count);
            Vector3 dir = right * Mathf.Cos(angle) + up * Mathf.Sin(angle);

            if (i == effectIndex)
            {
                // Left behind at the burst point at full size, before the piece shrinks to pop in
                ColorBody effectBody = CreateBody(color, radius, origin);
                effectBody.PlayDetachedEffect();
                Destroy(effectBody.gameObject);
            }

            ColorBody body = CreateBody(color, radius, origin + dir * (spread * Random.Range(0.5f, 1f)));
            AddBody(body);

            // Verlet velocity is CurrentPos - LastPos per step
            body.LastPos = body.CurrentPos - dir * (speed * Random.Range(0.6f, 1f) * dt);

            // Grows from tiny to its correct size; collisions follow the growing radius, so
            // the pieces shove each other apart as they swell
            body.ScaleRadius(radius * burstStartScale, radius, burstGrowDuration, overshoot: burstGrowOvershoot);

            if (burstHoldSeconds > 0f)
                _burstUntil[body] = holdUntil;
        }

        PlaySound(mergeSound);
    }

    /// <summary>
    /// Instantiates a ColorBody from the prefab without adding it to the
    /// simulation (e.g. for display after the physics is done with it).
    /// </summary>
    public ColorBody CreateBody(Color color, float radius, Vector3 position)
    {
        ColorBody body = Instantiate(
            colorBodyPrefab,
            position,
            Quaternion.identity
        );

        body.transform.localScale = Vector3.one * (Mathf.Max(0.001f, radius) * 2f);
        body.Color = color;
        ApplyBodySprite(body);

        return body;
    }

    #endregion

    #region Utilities

    private Vector3 GetClusterCenter()
    {
        Vector3 center = Vector3.zero;

        foreach (ColorBody body in _active)
            center += body.CurrentPos;

        return center / _active.Count;
    }

    // Deterministic push direction for two bodies at the exact same spot
    private Vector3 FallbackDirection(int i, int j)
    {
        float angle = (i * 0.618f + j * 0.382f) * Mathf.PI * 2f;
        return _right * Mathf.Cos(angle) + _up * Mathf.Sin(angle);
    }

    private static void SubtractMean(float[] values)
    {
        if (values.Length == 0)
            return;

        float mean = 0f;

        foreach (float v in values)
            mean += v;

        mean /= values.Length;

        for (int i = 0; i < values.Length; i++)
            values[i] -= mean;
    }

    /// <summary>
    /// Normalised rank (0..1) of each value; ties share their average rank.
    /// </summary>
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

    #endregion
}

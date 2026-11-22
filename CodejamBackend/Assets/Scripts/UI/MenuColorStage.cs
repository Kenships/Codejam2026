using System;
using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Manages the world-space ColorBody cubes for the Menu / Host Lobby screen:
/// - Caps the total number of cubes on screen at MaxTotalCubes (30), raised only as far as a
///   big room needs to fit every player's cube plus a few greyscale ones (CubeBudget).
/// - Seats up to 12 players on one ring around the card, and up to 32 on two rings.
/// - Spawns differently sized greyscale cubes all around the screen that slowly meander
///   around the center UI card and occasionally merge with each other.
/// - If a free cube grows too large (>= popRadiusThreshold), it pops — playing the same
///   particle and audio effect as a merge, except it despawns.
/// - When a player joins, assigns them a clockwise seat starting from top-left, grabs the
///   two closest greyscale cubes, and merges them into the player's character cube coloured
///   with the player's picked colour.
/// - When the player character cube is created, spawns very small cubes and applies an
///   outward impulse force so they fly out from the newly joined player.
/// </summary>
public class MenuColorStage : MonoBehaviour
{
    public const int MaxPlayers = GameSessionData.MaxPlayersLimit;
    /// <summary>Seats on the inner ring; players past this sit on an outer ring.</summary>
    public const int InnerRingSeats = 12;
    private const int OuterRingSeats = MaxPlayers - InnerRingSeats;
    public const int MaxTotalCubes = 30;
    /// <summary>Greyscale cubes kept around even in a full room, so joins can still merge two.</summary>
    private const int MinFreeCubes = 4;
    private const int MaxShaderNeighbors = 4;
    private const float MaxBodySquash = 0.55f;
    private const float ContactTolerance = 0.02f;
    private const float AlignedAngle = 4f;

    [Title("References")]
    [SerializeField, Required] private ColorBody colorBodyPrefab;
    [SerializeField] private Material overlapMaterial;
    [SerializeField, AssetsOnly] private AudioClip mergeSound;
    [SerializeField] private Camera stageCamera;

    [Title("Greyscale Cubes Pool (Max 30 Total)")]
    [Tooltip("Target number of free meandering cubes (total cubes including player characters never exceeds 30).")]
    [SerializeField, Range(10, MaxTotalCubes)] private int targetFreeCubes = 22;
    [SerializeField, Range(0.1f, 1f)] private float minGreyValue = 0.28f;
    [SerializeField, Range(0.1f, 1f)] private float maxGreyValue = 0.88f;

    [Title("Cube Sizes & Popping")]
    [SerializeField, Min(0.08f)] private float minCubeRadius = 0.18f;
    [SerializeField, Min(0.15f)] private float maxCubeRadius = 0.46f;
    [Tooltip("If a free cube reaches or exceeds this radius after merging, it pops and despawns.")]
    [SerializeField, Min(0.3f)] private float popRadiusThreshold = 0.52f;
    [SerializeField, Min(0.3f)] private float minCharacterRadius = 0.46f;
    [SerializeField, Min(0.35f)] private float maxCharacterRadius = 0.64f;

    [Title("Seat Rings")]
    [Tooltip("Where the single ring sits (up to 12 players), from the card's edge (0) to the " +
             "screen's edge (1), on X and Y.")]
    [SerializeField] private Vector2 singleRingPlacement = new(0.58f, 0.62f);
    [Tooltip("Where the inner ring moves to once the outer ring is in use (more than 12 players).")]
    [SerializeField] private Vector2 crowdedInnerRingPlacement = new(0.12f, 0.15f);
    [Tooltip("Where the outer ring sits (players 13+). Its seats fall between the inner ones.")]
    [SerializeField] private Vector2 outerRingPlacement = new(1f, 1f);
    [Tooltip("Size of outer-ring player cubes relative to inner-ring ones.")]
    [SerializeField, Range(0.4f, 1f)] private float outerRingCharacterScale = 0.75f;

    [Title("Player Join Burst (Very Small Cubes)")]
    [SerializeField, Range(2, 6)] private int joinBurstCubeCount = 4;
    [SerializeField, Min(0.05f)] private float minBurstCubeRadius = 0.10f;
    [SerializeField, Min(0.06f)] private float maxBurstCubeRadius = 0.15f;
    [SerializeField, Min(1f)] private float joinBurstImpulseSpeed = 5.2f;

    [Title("Meandering & Collision")]
    [SerializeField, Min(0.05f)] private float meanderSpeed = 0.55f;
    [SerializeField, Min(0.05f)] private float noiseFrequency = 0.18f;
    [SerializeField, Min(0f)] private float colorBodyGap = 0.08f;
    [SerializeField, Min(1)] private int collisionIterations = 4;

    [Title("Intermittent Spawn & Ambient Merge")]
    [SerializeField, Min(0.3f)] private float minIntermittentSpawnInterval = 1.2f;
    [SerializeField, Min(0.5f)] private float maxIntermittentSpawnInterval = 2.4f;
    [SerializeField, Min(1f)] private float minAmbientMergeInterval = 2.5f;
    [SerializeField, Min(1.5f)] private float maxAmbientMergeInterval = 4.8f;

    [Title("Merging Animation")]
    [SerializeField, Min(0.5f)] private float ambientMergeSpeed = 1.6f;
    [SerializeField, Min(1f)] private float characterMergeSpeed = 4.6f;
    [SerializeField, Min(90f)] private float mergeAlignSpeed = 540f;
    [SerializeField, Min(30f)] private float rotationReturnSpeed = 140f;
    [SerializeField, Min(0.05f)] private float mergePressDuration = 0.32f;
    [SerializeField, Range(0f, 0.5f)] private float mergeMaxSquash = 0.16f;
    [SerializeField, Min(1f)] private float mergeMaxStretch = 1.12f;
    [SerializeField, Min(0.05f)] private float mergeUnsquashDuration = 0.22f;
    [SerializeField, Min(0.05f)] private float mergeSpawnDuration = 0.35f;
    [SerializeField, Range(0f, 0.5f)] private float mergeSpawnSquash = 0.12f;
    [SerializeField, Min(0f)] private float mergeSpawnOvershoot = 1.05f;

    // Shader property IDs matching ColorOverlapBlender
    private static readonly int SelfColorId = Shader.PropertyToID("_SelfColor");
    private static readonly int SelfWeightId = Shader.PropertyToID("_SelfWeight");
    private static readonly int SelfFrameId = Shader.PropertyToID("_SelfFrame");
    private static readonly int SelfExtentsId = Shader.PropertyToID("_SelfExtents");
    private static readonly int NeighborCountId = Shader.PropertyToID("_NeighborCount");
    private static readonly int NeighborColorsId = Shader.PropertyToID("_NeighborColors");
    private static readonly int NeighborFramesId = Shader.PropertyToID("_NeighborFrames");
    private static readonly int NeighborExtentsId = Shader.PropertyToID("_NeighborExtents");

    private enum MergePhase
    {
        Approach,
        Press,
        Overlap
    }

    private sealed class FreeCubeState
    {
        public ColorBody Body;
        public Vector2 Velocity;
        public Vector2 ImpulseVelocity;
        public Vector2 NoiseSeed;
        public float SpeedMultiplier;
    }

    private sealed class ActiveMerge
    {
        public ColorBody A;
        public ColorBody B;
        public MergePhase Phase;
        public float PhaseTime;
        public float PressProgress;
        public float SquashA;
        public float SquashB;

        // If true, this merge forms a player character at TargetSeatIndex
        public bool IsPlayerJoinMerge;
        public string PlayerId;
        public int TargetSeatIndex;
        public Vector3 TargetPosition;
    }

    public sealed class PlayerCharacterView
    {
        public string PlayerId;
        public string PlayerName;
        public Color PlayerColor = Color.cyan;
        public int SeatIndex;
        public bool Connected = true;
        public ColorBody CharacterBody;
        public SpriteRenderer PfpRenderer;
        public Sprite PfpSprite;
        public Texture2D ProcessedPfpTexture;
        public Vector3 BaseSeatPosition;
        public float FloatSeed;
        public bool IsForming;
    }

    private struct ShaderShape
    {
        public SpriteRenderer Renderer;
        public Color Color;
        public Vector2 Center;
        public Vector2 AxisX;
        public Vector2 Extents;
        public float BoundRadius;
        public float Weight;
    }

    private readonly List<FreeCubeState> _freeCubes = new();
    private readonly Dictionary<ColorBody, FreeCubeState> _freeLookup = new();
    private readonly List<ActiveMerge> _merges = new();
    private readonly Dictionary<ColorBody, ActiveMerge> _mergeLookup = new();
    private readonly Dictionary<string, PlayerCharacterView> _players = new();
    private readonly PlayerCharacterView[] _seats = new PlayerCharacterView[MaxPlayers];

    // All active ColorBodies (free + merging + player characters) for collision & overlap shader
    private readonly List<ColorBody> _allBodies = new();
    private readonly List<ShaderShape> _shapes = new();
    private readonly List<(int index, float depth)> _neighbors = new();
    private readonly Vector4[] _shaderColors = new Vector4[MaxShaderNeighbors];
    private readonly Vector4[] _shaderFrames = new Vector4[MaxShaderNeighbors];
    private readonly Vector4[] _shaderExtents = new Vector4[MaxShaderNeighbors];
    private MaterialPropertyBlock _propertyBlock;

    private Vector2 _centerExclusionHalfExtents = new(2.35f, 1.95f);
    private Vector2 _centerExclusionCenter = Vector2.zero;
    private float _nextSpawnTime;
    private float _nextAmbientMergeTime;
    private bool _initialized;

    public Camera StageCamera => stageCamera != null ? stageCamera : Camera.main;
    public IReadOnlyDictionary<string, PlayerCharacterView> PlayerViews => _players;

    /// <summary>True while anyone sits on the outer ring (more than InnerRingSeats players).</summary>
    public bool IsOuterRingInUse
    {
        get
        {
            for (int s = InnerRingSeats; s < MaxPlayers; s++)
            {
                if (_seats[s] != null)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Most cubes on stage: MaxTotalCubes, raised in a big room so every player's cube fits
    /// with MinFreeCubes greyscale cubes still around to merge for the next join.
    /// </summary>
    private int CubeBudget => Mathf.Max(MaxTotalCubes, _players.Count + MinFreeCubes);

    /// <summary>Total number of active cubes currently in the stage (never exceeds CubeBudget).</summary>
    public int TotalActiveCubes
    {
        get
        {
            int count = _freeCubes.Count + _merges.Count * 2;
            foreach (PlayerCharacterView view in _players.Values)
            {
                if (view.CharacterBody != null)
                    count++;
            }
            return count;
        }
    }

    /// <summary>Updates the world-space exclusion box for the center UI card.</summary>
    public void SetCenterCardWorldBounds(Vector2 center, Vector2 halfExtents)
    {
        _centerExclusionCenter = center;
        _centerExclusionHalfExtents = new Vector2(
            Mathf.Max(1.5f, halfExtents.x),
            Mathf.Max(1.3f, halfExtents.y)
        );
    }

    private void Reset()
    {
        AutoAssignDefaultAssets();
    }

    private void Awake()
    {
        AutoAssignDefaultAssets();

        if (stageCamera == null)
            stageCamera = Camera.main;
    }

    private void AutoAssignDefaultAssets()
    {
#if UNITY_EDITOR
        if (colorBodyPrefab == null)
            colorBodyPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<ColorBody>("Assets/Prefabs/ColorBody.prefab");

        if (overlapMaterial == null)
            overlapMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Shaders/ColorOverlapBlendMat.mat");

        if (mergeSound == null)
            mergeSound = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Media/BubbleMerge.mp3");
#endif
    }

    private void Start()
    {
        EnsureInitialPool();
        ScheduleNextIntermittentSpawn();
        ScheduleNextAmbientMerge();
    }

    private void OnDestroy()
    {
        foreach (PlayerCharacterView view in _players.Values)
        {
            CleanupPlayerPfpAssets(view);
        }
    }

    #region Pool & Spawning (Strictly <= 30 Cubes)

    private int GetDesiredFreeCubeCount()
    {
        int reservedByPlayersAndMerges = _merges.Count * 2;
        foreach (PlayerCharacterView view in _players.Values)
        {
            if (view.CharacterBody != null)
                reservedByPlayersAndMerges++;
        }

        int maxAllowedFree = Mathf.Max(0, CubeBudget - reservedByPlayersAndMerges);
        return Mathf.Min(targetFreeCubes, maxAllowedFree);
    }

    public void EnsureInitialPool()
    {
        if (colorBodyPrefab == null)
            return;

        int desired = GetDesiredFreeCubeCount();
        int safety = 0;

        while (_freeCubes.Count < desired && TotalActiveCubes < CubeBudget && safety < CubeBudget)
        {
            safety++;
            SpawnFreeGreyscaleCube(animatePop: _initialized);
        }

        _initialized = true;
    }

    /// <summary>
    /// Ensures there are at least <paramref name="minFreeNeeded"/> free cubes available
    /// while strictly respecting MaxTotalCubes (30).
    /// </summary>
    private void EnsureFreeCubesAvailable(int minFreeNeeded)
    {
        if (colorBodyPrefab == null)
            return;

        int safety = 0;
        while (_freeCubes.Count < minFreeNeeded && TotalActiveCubes < CubeBudget && safety < CubeBudget)
        {
            safety++;
            SpawnFreeGreyscaleCube(animatePop: true);
        }
    }

    /// <summary>
    /// Frees up room in the 30-cube budget by popping the largest free cubes if needed.
    /// </summary>
    private void MakeRoomInCubeBudget(int slotsNeeded)
    {
        int safety = 0;
        while (TotalActiveCubes + slotsNeeded > CubeBudget && _freeCubes.Count > 0 && safety < CubeBudget)
        {
            safety++;
            int largestIndex = 0;
            float largestRadius = -1f;

            for (int i = 0; i < _freeCubes.Count; i++)
            {
                ColorBody b = _freeCubes[i].Body;
                if (b != null && b.Radius > largestRadius)
                {
                    largestRadius = b.Radius;
                    largestIndex = i;
                }
            }

            ColorBody toPop = _freeCubes[largestIndex].Body;
            RemoveFreeCubeAt(largestIndex);
            if (toPop != null)
                PopAndDespawnBody(toPop, playSfx: false);
        }
    }

    private ColorBody SpawnFreeGreyscaleCube(bool animatePop)
    {
        if (colorBodyPrefab == null || TotalActiveCubes >= CubeBudget)
            return null;

        float radius = SampleVariedCubeRadius();
        Vector3 spawnPos = FindOpenSpawnPosition(radius);

        return SpawnFreeGreyscaleCubeAt(spawnPos, radius, Vector2.zero, animatePop);
    }

    private ColorBody SpawnFreeGreyscaleCubeAt(
        Vector3 spawnPos,
        float radius,
        Vector2 initialImpulse,
        bool animatePop)
    {
        if (colorBodyPrefab == null || TotalActiveCubes >= CubeBudget)
            return null;

        float grey = Random.Range(minGreyValue, maxGreyValue);
        Color color = new(grey, grey, grey, 1f);

        ColorBody body = Instantiate(
            colorBodyPrefab,
            spawnPos,
            Quaternion.identity,
            transform
        );

        body.Color = color;

        if (animatePop && Application.isPlaying)
        {
            body.ScaleRadius(
                start: 0.02f,
                end: radius,
                duration: 0.28f,
                startSquash: 0.08f,
                overshoot: 1.0f,
                maxStretch: mergeMaxStretch
            );
        }
        else
        {
            body.transform.localScale = Vector3.one * (radius * 2f);
        }

        body.CurrentPos = spawnPos;
        body.LastPos = spawnPos;

        FreeCubeState state = new()
        {
            Body = body,
            Velocity = Random.insideUnitCircle * (meanderSpeed * 0.5f),
            ImpulseVelocity = initialImpulse,
            NoiseSeed = new Vector2(Random.Range(0f, 100f), Random.Range(0f, 100f)),
            SpeedMultiplier = Mathf.Lerp(1.15f, 0.72f, Mathf.InverseLerp(minCubeRadius, maxCubeRadius, radius))
        };

        _freeCubes.Add(state);
        _freeLookup[body] = state;

        return body;
    }

    /// <summary>
    /// Spawns very small cubes around a newly created player character cube and applies
    /// outward radial force so they fly out from the player into the arena.
    /// </summary>
    private void SpawnJoinBurstMiniCubes(Vector3 origin, float characterRadius)
    {
        if (colorBodyPrefab == null)
            return;

        int count = joinBurstCubeCount;
        // Ensure we never exceed 30 total cubes
        MakeRoomInCubeBudget(count);

        int availableSlots = Mathf.Max(0, CubeBudget - TotalActiveCubes);
        count = Mathf.Min(count, availableSlots);

        if (count <= 0)
            return;

        // Bias directions slightly toward the screen center / open arena so they don't immediately hit a wall
        Vector2 toArenaCenter = ((Vector2)_centerExclusionCenter - (Vector2)origin);
        float baseAngle = toArenaCenter.sqrMagnitude > 0.01f
            ? Mathf.Atan2(toArenaCenter.y, toArenaCenter.x)
            : Random.Range(0f, Mathf.PI * 2f);

        for (int i = 0; i < count; i++)
        {
            float spread = ((i - (count - 1) * 0.5f) / Mathf.Max(1f, count)) * (Mathf.PI * 1.35f);
            float angle = baseAngle + spread + Random.Range(-0.25f, 0.25f);
            Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));

            float miniRadius = Random.Range(minBurstCubeRadius, maxBurstCubeRadius);
            Vector3 spawnPos = origin + (Vector3)(dir * (characterRadius + miniRadius + 0.06f));
            Vector2 impulse = dir * (joinBurstImpulseSpeed * Random.Range(0.85f, 1.2f));

            SpawnFreeGreyscaleCubeAt(spawnPos, miniRadius, impulse, animatePop: true);
        }
    }

    /// <summary>
    /// Multi-tier size sampling so small, medium, and large cubes are always well-represented.
    /// </summary>
    private float SampleVariedCubeRadius()
    {
        float roll = Random.value;
        float t;

        if (roll < 0.42f)
            t = Random.Range(0f, 0.35f);        // Small cubes
        else if (roll < 0.82f)
            t = Random.Range(0.35f, 0.75f);     // Medium cubes
        else
            t = Random.Range(0.75f, 1f);        // Large cubes

        return Mathf.Lerp(minCubeRadius, maxCubeRadius, t);
    }

    private Vector3 FindOpenSpawnPosition(float radius)
    {
        GetScreenWorldHalfExtents(out float halfW, out float halfH);

        float marginX = Mathf.Max(0.5f, radius + 0.25f);
        float marginY = Mathf.Max(0.5f, radius + 0.25f);

        Vector3 bestCandidate = new(halfW - marginX, halfH - marginY, 0f);
        float bestClearance = float.NegativeInfinity;

        for (int attempt = 0; attempt < 24; attempt++)
        {
            Vector2 candidate = new(
                Random.Range(-halfW + marginX, halfW - marginX),
                Random.Range(-halfH + marginY, halfH - marginY)
            );

            if (IsInsideCenterExclusion(candidate, radius + 0.35f))
                continue;

            float minGap = float.MaxValue;

            for (int s = 0; s < MaxPlayers; s++)
            {
                if (_seats[s] == null)
                    continue;

                float dist = Vector2.Distance(candidate, _seats[s].BaseSeatPosition) - (maxCharacterRadius + radius + 0.5f);
                if (dist < minGap)
                    minGap = dist;
            }

            for (int i = 0; i < _freeCubes.Count; i++)
            {
                ColorBody other = _freeCubes[i].Body;
                if (other == null)
                    continue;

                float dist = Vector2.Distance(candidate, other.CurrentPos) - (other.Radius + radius);
                if (dist < minGap)
                    minGap = dist;
            }

            if (minGap > bestClearance)
            {
                bestClearance = minGap;
                bestCandidate = new Vector3(candidate.x, candidate.y, 0f);

                if (minGap >= colorBodyGap)
                    break;
            }
        }

        return bestCandidate;
    }

    private void ScheduleNextIntermittentSpawn()
    {
        _nextSpawnTime = Time.time + Random.Range(minIntermittentSpawnInterval, maxIntermittentSpawnInterval);
    }

    private void ScheduleNextAmbientMerge()
    {
        _nextAmbientMergeTime = Time.time + Random.Range(minAmbientMergeInterval, maxAmbientMergeInterval);
    }

    #endregion

    #region Player Join / Leave / PFP

    /// <summary>
    /// Assigns a clockwise seat starting from top-left, grabs the two closest greyscale cubes,
    /// and merges them at that seat to become the player's coloured character cube.
    /// </summary>
    public void AddOrUpdatePlayerCharacter(
        string playerId,
        string playerName,
        bool connected,
        Texture2D avatar = null)
    {
        AddOrUpdatePlayerCharacter(playerId, playerName, Color.cyan, connected, avatar);
    }

    /// <summary>
    /// Assigns a clockwise seat starting from top-left, grabs the two closest greyscale cubes,
    /// and merges them at that seat to become the player's coloured character cube.
    /// </summary>
    public void AddOrUpdatePlayerCharacter(
        string playerId,
        string playerName,
        Color playerColor,
        bool connected = true,
        Texture2D avatar = null)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return;

        if (_players.TryGetValue(playerId, out PlayerCharacterView existing))
        {
            existing.PlayerName = playerName;
            existing.PlayerColor = playerColor;
            existing.Connected = connected;

            if (existing.CharacterBody != null)
                existing.CharacterBody.Color = playerColor;

            if (avatar != null)
                ApplyAvatarToCharacter(existing, avatar);

            return;
        }

        int seatIndex = FindFirstOpenClockwiseSeat();
        if (seatIndex < 0)
            return;

        // Guarantee at least 2 free cubes exist to merge for this player
        if (_freeCubes.Count < 2)
        {
            MakeRoomInCubeBudget(2 - _freeCubes.Count);
            EnsureFreeCubesAvailable(2);
        }

        Vector3 seatPos = GetClockwiseSeatWorldPosition(seatIndex);

        PlayerCharacterView view = new()
        {
            PlayerId = playerId,
            PlayerName = playerName,
            PlayerColor = playerColor,
            SeatIndex = seatIndex,
            Connected = connected,
            BaseSeatPosition = seatPos,
            FloatSeed = Random.Range(0f, 100f),
            IsForming = true
        };

        _players[playerId] = view;
        _seats[seatIndex] = view;

        if (avatar != null)
            PreparePlayerAvatarSprite(view, avatar);

        if (TryClaimTwoClosestFreeCubes(seatPos, out ColorBody first, out ColorBody second))
        {
            StartPlayerJoinMerge(view, first, second);
        }
        else
        {
            MakeRoomInCubeBudget(1);
            SpawnFinishedPlayerCharacter(view, seatPos, 0.54f * CharacterScaleFor(seatIndex), playerColor);
        }
    }

    /// <summary>Updates a player's PFP texture on their character cube.</summary>
    public void SetPlayerCharacterAvatar(string playerId, Texture2D avatar)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !_players.TryGetValue(playerId, out PlayerCharacterView view))
            return;

        ApplyAvatarToCharacter(view, avatar);
    }

    /// <summary>Updates a player's connection state.</summary>
    public void SetPlayerCharacterConnected(string playerId, bool connected)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !_players.TryGetValue(playerId, out PlayerCharacterView view))
            return;

        view.Connected = connected;
    }

    /// <summary>
    /// Despawns the player's character cube (playing the merge/pop effect) and allows new greyscale cubes to spawn.
    /// </summary>
    public void RemovePlayerCharacter(string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId) || !_players.TryGetValue(playerId, out PlayerCharacterView view))
            return;

        _players.Remove(playerId);

        if (view.SeatIndex >= 0 && view.SeatIndex < MaxPlayers && _seats[view.SeatIndex] == view)
            _seats[view.SeatIndex] = null;

        for (int i = _merges.Count - 1; i >= 0; i--)
        {
            ActiveMerge merge = _merges[i];
            if (merge.IsPlayerJoinMerge && merge.PlayerId == playerId)
            {
                _merges.RemoveAt(i);
                if (merge.A != null)
                {
                    _mergeLookup.Remove(merge.A);
                    PopAndDespawnBody(merge.A, playSfx: false);
                }
                if (merge.B != null)
                {
                    _mergeLookup.Remove(merge.B);
                    PopAndDespawnBody(merge.B, playSfx: false);
                }
            }
        }

        if (view.CharacterBody != null)
        {
            PopAndDespawnBody(view.CharacterBody, playSfx: true);
            view.CharacterBody = null;
        }

        CleanupPlayerPfpAssets(view);

        // Replenish a couple of greyscale cubes within the 30-cube limit
        int toSpawn = Mathf.Min(2, GetDesiredFreeCubeCount() - _freeCubes.Count);
        for (int i = 0; i < toSpawn; i++)
        {
            SpawnFreeGreyscaleCube(animatePop: true);
        }
    }

    /// <summary>Despawns all player characters.</summary>
    public void ClearAllPlayerCharacters()
    {
        List<string> ids = new(_players.Keys);
        foreach (string id in ids)
        {
            RemovePlayerCharacter(id);
        }
    }

    public bool TryGetPlayerCharacterAnchor(
        string playerId,
        out Vector3 worldCenter,
        out float radius)
    {
        worldCenter = Vector3.zero;
        radius = 0.5f;

        if (!_players.TryGetValue(playerId, out PlayerCharacterView view))
            return false;

        if (view.CharacterBody != null)
        {
            worldCenter = view.CharacterBody.transform.position;
            radius = Mathf.Max(0.35f, view.CharacterBody.Radius);
            return true;
        }

        for (int i = 0; i < _merges.Count; i++)
        {
            ActiveMerge m = _merges[i];
            if (m.IsPlayerJoinMerge && m.PlayerId == playerId && m.A != null && m.B != null)
            {
                worldCenter = (m.A.transform.position + m.B.transform.position) * 0.5f;
                radius = Mathf.Max(m.A.Radius, m.B.Radius);
                return true;
            }
        }

        worldCenter = view.BaseSeatPosition;
        radius = minCharacterRadius;
        return true;
    }

    private int FindFirstOpenClockwiseSeat()
    {
        for (int i = 0; i < MaxPlayers; i++)
        {
            if (_seats[i] == null)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Computes the seat position around the center join screen, clockwise from the top-left.
    /// Seats 0-11 are the inner ring (seat 0 = 135 degrees, -30 degrees per seat); seats 12+
    /// are an outer ring whose seats fall between the inner ones. While nobody is on the outer
    /// ring, the inner ring sits where the single ring always did; once someone is, it pulls
    /// in toward the card to make room (seats glide there, see UpdateSeatPositions).
    /// </summary>
    /// <summary>Outer-ring ("back row") player cubes are smaller.</summary>
    private float CharacterScaleFor(int seatIndex) =>
        seatIndex >= InnerRingSeats ? outerRingCharacterScale : 1f;

    public Vector3 GetClockwiseSeatWorldPosition(int seatIndex)
    {
        GetScreenWorldHalfExtents(out float halfW, out float halfH);

        float minRingX = _centerExclusionHalfExtents.x + 1.45f;
        float maxRingX = Mathf.Max(minRingX + 0.5f, halfW - 1.35f);

        float minRingY = _centerExclusionHalfExtents.y + 1.05f;
        float maxRingY = Mathf.Max(minRingY + 0.25f, halfH - 1.15f);

        Vector2 placement;
        float angleDeg;
        if (seatIndex < InnerRingSeats)
        {
            placement = IsOuterRingInUse ? crowdedInnerRingPlacement : singleRingPlacement;
            angleDeg = 135f - seatIndex * (360f / InnerRingSeats);
        }
        else
        {
            placement = outerRingPlacement;
            int outerIndex = (seatIndex - InnerRingSeats) % OuterRingSeats;
            angleDeg = 135f - (outerIndex + 0.5f) * (360f / OuterRingSeats);
        }

        float ringX = Mathf.Lerp(minRingX, maxRingX, placement.x);
        float ringY = Mathf.Lerp(minRingY, maxRingY, placement.y);
        float rad = angleDeg * Mathf.Deg2Rad;

        float x = _centerExclusionCenter.x + Mathf.Cos(rad) * ringX;
        float y = _centerExclusionCenter.y + 0.15f + Mathf.Sin(rad) * ringY;

        return new Vector3(x, y, 0f);
    }

    private bool TryClaimTwoClosestFreeCubes(
        Vector3 targetPos,
        out ColorBody first,
        out ColorBody second)
    {
        first = null;
        second = null;

        if (_freeCubes.Count < 2)
            return false;

        int bestIndexA = -1;
        float bestDistA = float.MaxValue;

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            ColorBody body = _freeCubes[i].Body;
            if (body == null)
                continue;

            float d = Vector3.SqrMagnitude(body.CurrentPos - targetPos);
            if (d < bestDistA)
            {
                bestDistA = d;
                bestIndexA = i;
            }
        }

        if (bestIndexA < 0)
            return false;

        first = _freeCubes[bestIndexA].Body;
        RemoveFreeCubeAt(bestIndexA);

        int bestIndexB = -1;
        float bestDistB = float.MaxValue;

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            ColorBody body = _freeCubes[i].Body;
            if (body == null)
                continue;

            float d = Vector3.SqrMagnitude(body.CurrentPos - targetPos) * 0.65f +
                      Vector3.SqrMagnitude(body.CurrentPos - first.CurrentPos) * 0.35f;
            if (d < bestDistB)
            {
                bestDistB = d;
                bestIndexB = i;
            }
        }

        if (bestIndexB < 0)
            return false;

        second = _freeCubes[bestIndexB].Body;
        RemoveFreeCubeAt(bestIndexB);

        return true;
    }

    private void RemoveFreeCubeAt(int index)
    {
        ColorBody body = _freeCubes[index].Body;
        if (body != null)
            _freeLookup.Remove(body);

        _freeCubes.RemoveAt(index);
    }

    private void StartPlayerJoinMerge(PlayerCharacterView view, ColorBody a, ColorBody b)
    {
        a.CompleteScaleTween();
        b.CompleteScaleTween();

        ActiveMerge merge = new()
        {
            A = a,
            B = b,
            Phase = MergePhase.Approach,
            IsPlayerJoinMerge = true,
            PlayerId = view.PlayerId,
            TargetSeatIndex = view.SeatIndex,
            TargetPosition = view.BaseSeatPosition
        };

        _merges.Add(merge);
        _mergeLookup[a] = merge;
        _mergeLookup[b] = merge;
    }

    /// <summary>
    /// Plays the exact same particle and audio effect as a merge, hides the cube sprite
    /// (and any child PFP sprite), and destroys the GameObject after the particles finish.
    /// </summary>
    private void PopAndDespawnBody(ColorBody body, bool playSfx = true)
    {
        if (body == null)
            return;

        body.CompleteScaleTween();
        body.PlayMergeEffect();

        if (playSfx)
            PlayMergeSfx();

        // Hide all SpriteRenderers on the cube (main sprite + any PFP overlay) while keeping
        // the GameObject alive at its current scale so the child ParticleSystem plays in full!
        foreach (SpriteRenderer sr in body.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.enabled = false;
        }

        if (Application.isPlaying)
            Destroy(body.gameObject, 1.25f);
        else
            DestroyImmediate(body.gameObject);
    }

    private void PreparePlayerAvatarSprite(PlayerCharacterView view, Texture2D sourceAvatar)
    {
        CleanupPlayerPfpAssets(view);

        if (sourceAvatar == null)
            return;

        Texture2D processed = CreateDisplayPfpTexture(sourceAvatar);
        Texture2D texToUse = processed != null ? processed : sourceAvatar;

        view.ProcessedPfpTexture = processed;
        view.PfpSprite = Sprite.Create(
            texToUse,
            new Rect(0f, 0f, texToUse.width, texToUse.height),
            new Vector2(0.5f, 0.5f),
            Mathf.Max(1f, Mathf.Max(texToUse.width, texToUse.height))
        );
    }

    private void ApplyAvatarToCharacter(PlayerCharacterView view, Texture2D avatar)
    {
        PreparePlayerAvatarSprite(view, avatar);
        AttachPfpRendererIfNeeded(view);
    }

    private void AttachPfpRendererIfNeeded(PlayerCharacterView view)
    {
        if (view.CharacterBody == null)
            return;

        if (view.PfpSprite == null)
        {
            if (view.PfpRenderer != null)
                view.PfpRenderer.gameObject.SetActive(false);
            return;
        }

        if (view.PfpRenderer == null)
        {
            GameObject pfpObj = new("PlayerPfp");
            pfpObj.transform.SetParent(view.CharacterBody.transform, false);
            pfpObj.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            pfpObj.transform.localRotation = Quaternion.identity;
            pfpObj.transform.localScale = new Vector3(0.76f, 0.76f, 1f);

            view.PfpRenderer = pfpObj.AddComponent<SpriteRenderer>();
            if (view.CharacterBody.Renderer != null)
            {
                view.PfpRenderer.sortingLayerID = view.CharacterBody.Renderer.sortingLayerID;
                view.PfpRenderer.sortingOrder = view.CharacterBody.Renderer.sortingOrder + 3;
            }
        }

        view.PfpRenderer.gameObject.SetActive(true);
        view.PfpRenderer.sprite = view.PfpSprite;
        view.PfpRenderer.color = Color.white;
    }

    private static Texture2D CreateDisplayPfpTexture(Texture2D source)
    {
        if (source == null || !source.isReadable)
            return null;

        Color32[] pixels = source.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 c = pixels[i];
            int maxChannel = Mathf.Max(c.r, Mathf.Max(c.g, c.b));

            if (maxChannel < 35 && c.a > 0)
            {
                pixels[i] = new Color32(12, 12, 12, 145);
            }
        }

        Texture2D copy = new(source.width, source.height, TextureFormat.RGBA32, false)
        {
            filterMode = source.filterMode,
            wrapMode = TextureWrapMode.Clamp
        };
        copy.SetPixels32(pixels);
        copy.Apply(false, false);
        return copy;
    }

    private static void CleanupPlayerPfpAssets(PlayerCharacterView view)
    {
        if (view == null)
            return;

        if (view.PfpSprite != null)
        {
            Destroy(view.PfpSprite);
            view.PfpSprite = null;
        }

        if (view.ProcessedPfpTexture != null)
        {
            Destroy(view.ProcessedPfpTexture);
            view.ProcessedPfpTexture = null;
        }
    }

    #endregion

    #region Simulation Loop

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        PruneDestroyedBodies();
        UpdateScheduledSpawnsAndMerges();
        UpdateSeatPositions(dt);
        StepFreeCubesMeander(dt);
        StepActiveMerges(dt);
        SolveCollisions();
        RelaxRotations(dt);
        ApplyTransforms();
        ResolveFinishedMerges();
    }

    private void LateUpdate()
    {
        UpdateOverlapShaderBlocks();
    }

    private void PruneDestroyedBodies()
    {
        for (int i = _freeCubes.Count - 1; i >= 0; i--)
        {
            if (_freeCubes[i].Body == null)
                _freeCubes.RemoveAt(i);
        }

        for (int i = _merges.Count - 1; i >= 0; i--)
        {
            ActiveMerge m = _merges[i];
            if (m.A == null || m.B == null)
            {
                if (m.A != null) _mergeLookup.Remove(m.A);
                if (m.B != null) _mergeLookup.Remove(m.B);
                _merges.RemoveAt(i);
            }
        }
    }

    private void UpdateScheduledSpawnsAndMerges()
    {
        int desiredFree = GetDesiredFreeCubeCount();

        // Intermittent spawning, strictly capped at MaxTotalCubes (30)
        if (Time.time >= _nextSpawnTime)
        {
            if (_freeCubes.Count < desiredFree && TotalActiveCubes < CubeBudget)
                SpawnFreeGreyscaleCube(animatePop: true);

            ScheduleNextIntermittentSpawn();
        }

        // Occasional ambient merge between nearby free greyscale cubes
        if (Time.time >= _nextAmbientMergeTime)
        {
            if (_freeCubes.Count >= 6)
                TryStartAmbientMerge();

            ScheduleNextAmbientMerge();
        }
    }

    private bool TryStartAmbientMerge()
    {
        if (_freeCubes.Count < 2)
            return false;

        int pickA = Random.Range(0, _freeCubes.Count);
        ColorBody bodyA = _freeCubes[pickA].Body;
        if (bodyA == null)
            return false;

        int bestB = -1;
        float bestGap = float.MaxValue;

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            if (i == pickA)
                continue;

            ColorBody bodyB = _freeCubes[i].Body;
            if (bodyB == null)
                continue;

            float gap = Vector3.Distance(bodyA.CurrentPos, bodyB.CurrentPos) - bodyA.Radius - bodyB.Radius;

            if (gap < bestGap && gap < 3.4f)
            {
                bestGap = gap;
                bestB = i;
            }
        }

        if (bestB < 0)
            return false;

        ColorBody second = _freeCubes[bestB].Body;

        if (pickA > bestB)
        {
            RemoveFreeCubeAt(pickA);
            RemoveFreeCubeAt(bestB);
        }
        else
        {
            RemoveFreeCubeAt(bestB);
            RemoveFreeCubeAt(pickA);
        }

        bodyA.CompleteScaleTween();
        second.CompleteScaleTween();

        ActiveMerge merge = new()
        {
            A = bodyA,
            B = second,
            Phase = MergePhase.Approach,
            IsPlayerJoinMerge = false
        };

        _merges.Add(merge);
        _mergeLookup[bodyA] = merge;
        _mergeLookup[second] = merge;

        return true;
    }

    private void UpdateSeatPositions(float dt)
    {
        float time = Time.time;

        for (int s = 0; s < MaxPlayers; s++)
        {
            PlayerCharacterView view = _seats[s];
            if (view == null)
                continue;

            view.BaseSeatPosition = GetClockwiseSeatWorldPosition(s);

            if (view.CharacterBody != null)
            {
                float bobX = Mathf.Sin(time * 1.15f + view.FloatSeed) * 0.07f;
                float bobY = Mathf.Cos(time * 1.45f + view.FloatSeed * 1.3f) * 0.08f;
                Vector3 target = view.BaseSeatPosition + new Vector3(bobX, bobY, 0f);

                view.CharacterBody.LastPos = view.CharacterBody.CurrentPos;
                view.CharacterBody.CurrentPos = Vector3.Lerp(
                    view.CharacterBody.CurrentPos,
                    target,
                    1f - Mathf.Exp(-dt * 8f)
                );
            }
        }

        for (int i = 0; i < _merges.Count; i++)
        {
            ActiveMerge m = _merges[i];
            if (m.IsPlayerJoinMerge)
                m.TargetPosition = GetClockwiseSeatWorldPosition(m.TargetSeatIndex);
        }
    }

    private void StepFreeCubesMeander(float dt)
    {
        GetScreenWorldHalfExtents(out float halfW, out float halfH);
        float time = Time.time * noiseFrequency;

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            FreeCubeState state = _freeCubes[i];
            ColorBody body = state.Body;
            if (body == null)
                continue;

            float r = body.Radius;
            Vector2 pos = body.CurrentPos;

            // Decay any burst impulse smoothly
            state.ImpulseVelocity = Vector2.Lerp(state.ImpulseVelocity, Vector2.zero, 1f - Mathf.Exp(-dt * 3.2f));

            // Smooth Perlin wandering vector
            float nx = Mathf.PerlinNoise(state.NoiseSeed.x + time, state.NoiseSeed.y) * 2f - 1f;
            float ny = Mathf.PerlinNoise(state.NoiseSeed.x, state.NoiseSeed.y + time) * 2f - 1f;
            Vector2 wanderDir = new(nx, ny);
            if (wanderDir.sqrMagnitude > 1f)
                wanderDir.Normalize();

            Vector2 desiredVel = wanderDir * (meanderSpeed * state.SpeedMultiplier);

            // Softly steer away from outer screen edges
            float boundX = halfW - r - 0.35f;
            float boundY = halfH - r - 0.35f;

            if (pos.x > boundX)
                desiredVel.x -= (pos.x - boundX) * 2.2f;
            else if (pos.x < -boundX)
                desiredVel.x += (-boundX - pos.x) * 2.2f;

            if (pos.y > boundY)
                desiredVel.y -= (pos.y - boundY) * 2.2f;
            else if (pos.y < -boundY)
                desiredVel.y += (-boundY - pos.y) * 2.2f;

            // Steer around the center UI card
            Vector2 toCenter = pos - _centerExclusionCenter;
            float padX = _centerExclusionHalfExtents.x + r + 0.3f;
            float padY = _centerExclusionHalfExtents.y + r + 0.3f;

            if (Mathf.Abs(toCenter.x) < padX && Mathf.Abs(toCenter.y) < padY)
            {
                float penX = padX - Mathf.Abs(toCenter.x);
                float penY = padY - Mathf.Abs(toCenter.y);

                if (penX < penY)
                    desiredVel.x += Mathf.Sign(toCenter.x == 0f ? 1f : toCenter.x) * (penX * 3.5f + 0.6f);
                else
                    desiredVel.y += Mathf.Sign(toCenter.y == 0f ? 1f : toCenter.y) * (penY * 3.5f + 0.6f);
            }

            // Steer around seated player characters
            for (int s = 0; s < MaxPlayers; s++)
            {
                PlayerCharacterView seatView = _seats[s];
                if (seatView == null)
                    continue;

                Vector2 seatCenter = (Vector2)seatView.BaseSeatPosition + new Vector2(0f, -0.15f);
                float seatRadius = maxCharacterRadius + r + 0.55f;
                Vector2 diff = pos - seatCenter;
                float dist = diff.magnitude;

                if (dist < seatRadius)
                {
                    Vector2 pushDir = dist > 0.001f ? diff / dist : Random.insideUnitCircle.normalized;
                    desiredVel += pushDir * ((seatRadius - dist) * 4f + 0.5f);
                }
            }

            state.Velocity = Vector2.Lerp(state.Velocity, desiredVel, 1f - Mathf.Exp(-dt * 3.5f));

            Vector2 totalVel = state.Velocity + state.ImpulseVelocity;
            Vector2 nextPos = pos + totalVel * dt;
            nextPos.x = Mathf.Clamp(nextPos.x, -halfW + r + 0.05f, halfW - r - 0.05f);
            nextPos.y = Mathf.Clamp(nextPos.y, -halfH + r + 0.05f, halfH - r - 0.05f);

            body.LastPos = body.CurrentPos;
            body.CurrentPos = new Vector3(nextPos.x, nextPos.y, 0f);
        }
    }

    private void StepActiveMerges(float dt)
    {
        Vector3 forward = Vector3.forward;

        for (int i = 0; i < _merges.Count; i++)
        {
            ActiveMerge m = _merges[i];
            ColorBody a = m.A;
            ColorBody b = m.B;

            if (a == null || b == null)
                continue;

            m.PhaseTime += dt;

            if (m.IsPlayerJoinMerge)
            {
                Vector3 midpoint = (a.CurrentPos + b.CurrentPos) * 0.5f;
                Vector3 toSeat = m.TargetPosition - midpoint;
                float seatDist = toSeat.magnitude;

                if (seatDist > 0.0001f)
                {
                    float seatStep = Mathf.Min(seatDist, characterMergeSpeed * 1.35f * dt);
                    Vector3 shift = toSeat / seatDist * seatStep;
                    a.CurrentPos += shift;
                    a.LastPos += shift;
                    b.CurrentPos += shift;
                    b.LastPos += shift;
                }
            }

            Vector3 delta = b.CurrentPos - a.CurrentPos;
            float distance = delta.magnitude;
            float speed = m.IsPlayerJoinMerge ? characterMergeSpeed : ambientMergeSpeed;

            if (distance > 0.0001f)
            {
                float maxPull = speed * dt;
                float minTargetDist = m.Phase == MergePhase.Overlap
                    ? 0f
                    : a.Radius * (1f - m.SquashA) + b.Radius * (1f - m.SquashB);

                float pullAmount = Mathf.Min(maxPull, Mathf.Max(0f, distance - minTargetDist));
                Vector3 pull = delta / distance * (pullAmount * 0.5f);

                a.CurrentPos += pull;
                a.LastPos += pull;
                b.CurrentPos -= pull;
                b.LastPos -= pull;
            }

            Vector3 axis = b.CurrentPos - a.CurrentPos;
            axis.z = 0f;
            float axisLen = axis.magnitude;
            bool aligned = true;

            if (axisLen > 0.0001f)
            {
                axis /= axisLen;
                bool alignedA = RotateToFace(a, axis, forward, dt);
                bool alignedB = RotateToFace(b, axis, forward, dt);
                aligned = alignedA && alignedB;
            }

            switch (m.Phase)
            {
                case MergePhase.Approach:
                    float contact = a.Radius + b.Radius + ContactTolerance;
                    bool atSeat = !m.IsPlayerJoinMerge ||
                                  Vector3.Distance((a.CurrentPos + b.CurrentPos) * 0.5f, m.TargetPosition) <= 0.35f;

                    if ((aligned && axisLen <= contact && atSeat) || m.PhaseTime >= 1.35f)
                    {
                        m.Phase = MergePhase.Press;
                        m.PhaseTime = 0f;
                    }
                    break;

                case MergePhase.Press:
                    m.PressProgress = Mathf.Min(1f, m.PressProgress + dt / mergePressDuration);
                    ApplyPressSquash(m);

                    if (m.PressProgress >= 1f)
                    {
                        m.Phase = MergePhase.Overlap;
                        m.PhaseTime = 0f;
                        a.Unsquash(mergeUnsquashDuration);
                        b.Unsquash(mergeUnsquashDuration);
                    }
                    break;

                case MergePhase.Overlap:
                    break;
            }
        }
    }

    private void ApplyPressSquash(ActiveMerge m)
    {
        float t = m.PressProgress;
        float eased = 1f - (1f - t) * (1f - t);

        float areaA = m.A.Radius * m.A.Radius;
        float areaB = m.B.Radius * m.B.Radius;
        float totalArea = Mathf.Max(0.00001f, areaA + areaB);

        m.SquashA = Mathf.Min(MaxBodySquash, mergeMaxSquash * 2f * (areaA / totalArea) * eased);
        m.SquashB = Mathf.Min(MaxBodySquash, mergeMaxSquash * 2f * (areaB / totalArea) * eased);

        m.A.SetSquash(m.SquashA, mergeMaxStretch);
        m.B.SetSquash(m.SquashB, mergeMaxStretch);
    }

    private bool RotateToFace(ColorBody body, Vector3 axis, Vector3 forward, float dt)
    {
        Quaternion current = body.transform.rotation;
        Quaternion facing = Quaternion.LookRotation(forward, axis);
        Quaternion flipped = Quaternion.LookRotation(forward, -axis);

        Quaternion target =
            Quaternion.Angle(current, facing) <= Quaternion.Angle(current, flipped)
                ? facing
                : flipped;

        body.transform.rotation = Quaternion.RotateTowards(
            current,
            target,
            mergeAlignSpeed * dt
        );

        return Quaternion.Angle(body.transform.rotation, target) <= AlignedAngle;
    }

    private void SolveCollisions()
    {
        GatherAllBodies();
        int count = _allBodies.Count;

        for (int iter = 0; iter < collisionIterations; iter++)
        {
            for (int i = 0; i < count - 1; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    ColorBody a = _allBodies[i];
                    ColorBody b = _allBodies[j];

                    if (!TryGetMinDistance(a, b, out float minDistance))
                        continue;

                    Vector3 delta = b.CurrentPos - a.CurrentPos;
                    delta.z = 0f;
                    float dist = delta.magnitude;

                    if (dist >= minDistance)
                        continue;

                    Vector3 dir = dist > 0.0001f
                        ? delta / dist
                        : new Vector3(Mathf.Cos(i + j), Mathf.Sin(i - j), 0f).normalized;

                    float overlap = minDistance - dist;
                    GetCollisionShares(a, b, out float shareA, out float shareB);

                    Vector3 corrA = dir * (overlap * shareA);
                    Vector3 corrB = dir * (overlap * shareB);

                    a.CurrentPos -= corrA;
                    a.LastPos -= corrA;
                    b.CurrentPos += corrB;
                    b.LastPos += corrB;
                }
            }
        }
    }

    private bool TryGetMinDistance(ColorBody a, ColorBody b, out float minDistance)
    {
        if (_mergeLookup.TryGetValue(a, out ActiveMerge m) && (m.A == b || m.B == b))
        {
            if (m.Phase == MergePhase.Overlap)
            {
                minDistance = 0f;
                return false;
            }

            float squashA = m.A == a ? m.SquashA : m.SquashB;
            float squashB = m.A == b ? m.SquashA : m.SquashB;
            minDistance = Mathf.Max(0.001f, a.Radius * (1f - squashA) + b.Radius * (1f - squashB));
            return true;
        }

        minDistance = a.Radius + b.Radius + colorBodyGap;
        return true;
    }

    private void GetCollisionShares(ColorBody a, ColorBody b, out float shareA, out float shareB)
    {
        bool aFree = _freeLookup.ContainsKey(a);
        bool bFree = _freeLookup.ContainsKey(b);

        if (aFree == bFree)
        {
            shareA = 0.5f;
            shareB = 0.5f;
            return;
        }

        shareA = aFree ? 1f : 0f;
        shareB = 1f - shareA;
    }

    private void RelaxRotations(float dt)
    {
        float maxStep = rotationReturnSpeed * dt;

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            ColorBody body = _freeCubes[i].Body;
            if (body != null)
                body.transform.rotation = Quaternion.RotateTowards(body.transform.rotation, Quaternion.identity, maxStep);
        }

        foreach (PlayerCharacterView view in _players.Values)
        {
            if (view.CharacterBody != null)
                view.CharacterBody.transform.rotation = Quaternion.RotateTowards(view.CharacterBody.transform.rotation, Quaternion.identity, maxStep);
        }
    }

    private void ApplyTransforms()
    {
        GatherAllBodies();
        for (int i = 0; i < _allBodies.Count; i++)
        {
            ColorBody b = _allBodies[i];
            if (b != null)
                b.transform.position = b.CurrentPos;
        }
    }

    private void ResolveFinishedMerges()
    {
        for (int i = _merges.Count - 1; i >= 0; i--)
        {
            ActiveMerge m = _merges[i];
            if (m.Phase != MergePhase.Overlap)
                continue;

            float dist = Vector3.Distance(m.A.CurrentPos, m.B.CurrentPos);
            float fuseDist = Mathf.Abs(m.A.Radius - m.B.Radius) + 0.08f;

            if (dist <= fuseDist || m.PhaseTime >= 0.35f)
            {
                FuseMerge(i);
            }
        }
    }

    private void FuseMerge(int mergeIndex)
    {
        ActiveMerge m = _merges[mergeIndex];
        _merges.RemoveAt(mergeIndex);

        ColorBody a = m.A;
        ColorBody b = m.B;
        _mergeLookup.Remove(a);
        _mergeLookup.Remove(b);

        float areaA = a.Radius * a.Radius;
        float areaB = b.Radius * b.Radius;
        float combinedRadius = Mathf.Sqrt(areaA + areaB);

        float greyA = a.Color.r;
        float greyB = b.Color.r;
        float blendedGrey = (greyA * areaA + greyB * areaB) / Mathf.Max(0.0001f, areaA + areaB);

        if (m.IsPlayerJoinMerge && _players.TryGetValue(m.PlayerId, out PlayerCharacterView view))
        {
            float charRadius = Mathf.Clamp(combinedRadius, minCharacterRadius, maxCharacterRadius)
                               * CharacterScaleFor(view.SeatIndex);

            Destroy(a.gameObject);
            Destroy(b.gameObject);

            // Spawn the player's coloured character cube and shoot out very small cubes!
            SpawnFinishedPlayerCharacter(
                view,
                m.TargetPosition,
                charRadius,
                view.PlayerColor,
                startRadius: Mathf.Max(a.Radius, b.Radius)
            );
            return;
        }

        // Ambient free merge
        Vector3 pos = (a.CurrentPos + b.CurrentPos) * 0.5f;
        Quaternion rot = a.transform.rotation;
        Color color = new(blendedGrey, blendedGrey, blendedGrey, 1f);
        float startRad = Mathf.Max(a.Radius, b.Radius);

        Destroy(a.gameObject);
        Destroy(b.gameObject);

        ColorBody merged = Instantiate(
            colorBodyPrefab,
            pos,
            rot,
            transform
        );
        merged.Color = color;
        merged.CurrentPos = pos;
        merged.LastPos = pos;

        // If the merged cube is too large, it POPS: plays the same effect as a merge except it despawns!
        if (combinedRadius >= popRadiusThreshold)
        {
            merged.transform.localScale = Vector3.one * (combinedRadius * 2f);
            PopAndDespawnBody(merged, playSfx: true);
            return;
        }

        merged.ScaleRadius(
            start: startRad,
            end: combinedRadius,
            duration: mergeSpawnDuration,
            startSquash: mergeSpawnSquash,
            overshoot: mergeSpawnOvershoot,
            maxStretch: mergeMaxStretch
        );
        merged.PlayMergeEffect();

        FreeCubeState state = new()
        {
            Body = merged,
            Velocity = Random.insideUnitCircle * (meanderSpeed * 0.4f),
            NoiseSeed = new Vector2(Random.Range(0f, 100f), Random.Range(0f, 100f)),
            SpeedMultiplier = 0.75f
        };

        _freeCubes.Add(state);
        _freeLookup[merged] = state;

        PlayMergeSfx();
    }

    private void SpawnFinishedPlayerCharacter(
        PlayerCharacterView view,
        Vector3 position,
        float radius,
        Color color,
        float startRadius = 0.25f)
    {
        if (colorBodyPrefab == null)
            return;

        ColorBody character = Instantiate(
            colorBodyPrefab,
            position,
            Quaternion.identity,
            transform
        );

        character.ScaleRadius(
            start: startRadius,
            end: radius,
            duration: mergeSpawnDuration,
            startSquash: mergeSpawnSquash,
            overshoot: mergeSpawnOvershoot,
            maxStretch: mergeMaxStretch
        );
        character.Color = color;
        character.PlayMergeEffect();
        character.CurrentPos = position;
        character.LastPos = position;

        view.CharacterBody = character;
        view.IsForming = false;

        AttachPfpRendererIfNeeded(view);
        PlayMergeSfx();

        // Spawn very small cubes and apply force so they fly out from the newly joined player!
        SpawnJoinBurstMiniCubes(position, radius);
    }

    private void PlayMergeSfx()
    {
        if (mergeSound != null && AudioManager.Instance != null)
        {
            AudioManager.Instance.NewAudio(mergeSound)
                .RandomizePitch()
                .Create()
                .Play();
        }
    }

    private void GatherAllBodies()
    {
        _allBodies.Clear();

        for (int i = 0; i < _freeCubes.Count; i++)
        {
            if (_freeCubes[i].Body != null)
                _allBodies.Add(_freeCubes[i].Body);
        }

        for (int i = 0; i < _merges.Count; i++)
        {
            if (_merges[i].A != null) _allBodies.Add(_merges[i].A);
            if (_merges[i].B != null) _allBodies.Add(_merges[i].B);
        }

        foreach (PlayerCharacterView view in _players.Values)
        {
            if (view.CharacterBody != null)
                _allBodies.Add(view.CharacterBody);
        }
    }

    #endregion

    #region Overlap Blend Shader Update

    private void UpdateOverlapShaderBlocks()
    {
        if (overlapMaterial == null)
            return;

        _propertyBlock ??= new MaterialPropertyBlock();
        _shapes.Clear();

        GatherAllBodies();

        for (int i = 0; i < _allBodies.Count; i++)
        {
            ColorBody body = _allBodies[i];
            if (body == null)
                continue;

            SpriteRenderer sr = body.Renderer;
            if (sr == null || sr.sprite == null || !sr.enabled)
                continue;

            if (sr.sharedMaterial != overlapMaterial)
                sr.sharedMaterial = overlapMaterial;

            Transform t = body.transform;
            Bounds bounds = sr.sprite.bounds;
            Vector3 scale = t.lossyScale;

            Vector2 extents = new(
                Mathf.Abs(bounds.extents.x * scale.x),
                Mathf.Abs(bounds.extents.y * scale.y)
            );
            float radius = body.Radius;

            _shapes.Add(new ShaderShape
            {
                Renderer = sr,
                Color = sr.color,
                Center = t.TransformPoint(bounds.center),
                AxisX = ((Vector2)t.right).normalized,
                Extents = extents,
                BoundRadius = Mathf.Max(extents.x, extents.y),
                Weight = radius * radius
            });
        }

        for (int i = 0; i < _shapes.Count; i++)
        {
            _neighbors.Clear();
            ShaderShape self = _shapes[i];

            for (int j = 0; j < _shapes.Count; j++)
            {
                if (j == i)
                    continue;

                ShaderShape other = _shapes[j];
                float depth = self.BoundRadius + other.BoundRadius - Vector2.Distance(self.Center, other.Center);
                if (depth > 0f)
                    _neighbors.Add((j, depth));
            }

            if (_neighbors.Count > MaxShaderNeighbors)
                _neighbors.Sort((a, b) => b.depth.CompareTo(a.depth));

            int count = Mathf.Min(_neighbors.Count, MaxShaderNeighbors);
            for (int n = 0; n < MaxShaderNeighbors; n++)
            {
                if (n >= count)
                {
                    _shaderColors[n] = Vector4.zero;
                    _shaderFrames[n] = Vector4.zero;
                    _shaderExtents[n] = Vector4.one;
                    continue;
                }

                ShaderShape other = _shapes[_neighbors[n].index];
                _shaderColors[n] = other.Color;
                _shaderFrames[n] = new Vector4(other.Center.x, other.Center.y, other.AxisX.x, other.AxisX.y);
                _shaderExtents[n] = new Vector4(
                    Mathf.Max(other.Extents.x, 0.0001f),
                    Mathf.Max(other.Extents.y, 0.0001f),
                    other.Weight,
                    0f
                );
            }

            self.Renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetVector(SelfColorId, self.Color);
            _propertyBlock.SetFloat(SelfWeightId, self.Weight);
            _propertyBlock.SetVector(SelfFrameId, new Vector4(self.Center.x, self.Center.y, self.AxisX.x, self.AxisX.y));
            _propertyBlock.SetVector(SelfExtentsId, new Vector4(
                Mathf.Max(self.Extents.x, 0.0001f),
                Mathf.Max(self.Extents.y, 0.0001f),
                self.Weight,
                0f
            ));
            _propertyBlock.SetFloat(NeighborCountId, count);
            _propertyBlock.SetVectorArray(NeighborColorsId, _shaderColors);
            _propertyBlock.SetVectorArray(NeighborFramesId, _shaderFrames);
            _propertyBlock.SetVectorArray(NeighborExtentsId, _shaderExtents);
            self.Renderer.SetPropertyBlock(_propertyBlock);
        }
    }

    #endregion

    #region Bounds Utilities

    private void GetScreenWorldHalfExtents(out float halfW, out float halfH)
    {
        Camera cam = StageCamera;
        if (cam != null && cam.orthographic)
        {
            halfH = cam.orthographicSize;
            halfW = halfH * cam.aspect;
        }
        else
        {
            halfH = 5f;
            halfW = 5f * (16f / 9f);
        }
    }

    private bool IsInsideCenterExclusion(Vector2 point, float padding)
    {
        Vector2 delta = point - _centerExclusionCenter;
        return Mathf.Abs(delta.x) < _centerExclusionHalfExtents.x + padding &&
               Mathf.Abs(delta.y) < _centerExclusionHalfExtents.y + padding;
    }

    #endregion
}

using System;
using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Codejam.Networking;
using Core.Utility;
using UnityEngine;
using UnityEngine.UIElements;

public class GameManager : Singleton<GameManager>
{
    public enum GamePhase
    {
        None,
        Drawing,
        Guessing,
        MergeSequence,
        Results
    }

    [SerializeField, Required] private ColorPhysics colorPhysics;
    [SerializeField] private DrawingPhaseController drawingPhaseUI;

    [Title("Phase Flow")]
    [Tooltip("Starts the Drawing Phase immediately when GameScene begins.")]
    [SerializeField] private bool startDrawingOnStart = true;

    [Tooltip("Seconds players have to draw and submit (matches DrawingCanvasCard default of 60s).")]
    [SerializeField, Min(5f)] private float drawingDurationSeconds = 60f;

    [Tooltip("Short beat after all players submit before advancing out of the Drawing Phase.")]
    [SerializeField, Min(0f)] private float allSubmittedAdvanceDelay = 1.5f;

    [Tooltip("When true, automatically runs the ColorPhysics spawn-and-merge sequence after Drawing ends.")]
    [SerializeField] private bool autoStartMergeAfterDrawing = true;

    [Title("Colors")]
    // Number of distinct colours (one per person, also used for sample players when testing GameScene directly)
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

    [Title("Randomness")]
    // Use a fixed seed to get the same colours and pieces every time
    [SerializeField] private bool useSeed;
    [SerializeField, ShowIf(nameof(useSeed))] private int seed;

    public event Action<GamePhase> PhaseChanged;
    public event Action<GameSessionData.SubmittedDrawing> DrawingReceived;
    public event Action AllDrawingsSubmitted;

    private Coroutine _sequenceRoutine;
    private Coroutine _drawingTimerRoutine;
    private readonly HashSet<string> _submittedPlayerIds = new();

    private GamePhase _currentPhase = GamePhase.None;
    private int _currentRound = 1;

    public GamePhase CurrentPhase => _currentPhase;
    public int CurrentRound => _currentRound;
    public bool IsRunning => _sequenceRoutine != null;

    private static readonly string[] FallbackPlayerNames =
    {
        "PRISM", "CHROMA", "SPECTRA", "NEON", "PIXEL", "COBALT",
        "VERMILION", "CYAN", "MAGENTA", "AMBER", "INDIGO", "IRIS"
    };

    private void OnEnable()
    {
        BindRelayEvents(subscribe: true);
    }

    private void OnDisable()
    {
        BindRelayEvents(subscribe: false);
        BindDrawingUIEvents(subscribe: false);
    }

    private void Start()
    {
        EnsureDrawingPhaseUI();
        BindDrawingUIEvents(subscribe: true);
        BindRelayEvents(subscribe: true);

        if (startDrawingOnStart)
        {
            StartDrawingPhase();
        }
        else if (drawingPhaseUI != null)
        {
            drawingPhaseUI.SetVisible(false);
        }
    }

    #region Phase Control

    /// <summary>
    /// Starts the Drawing Phase: hides physics visuals, shows the rainbow-shifting Drawing UI,
    /// assigns each player a secret target colour, and notifies the relay so all players draw.
    /// </summary>
    [Title("Phase Controls (Play Mode)")]
    [Button]
    public void StartDrawingPhase()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("GameManager.StartDrawingPhase only works in Play mode.");
            return;
        }

        EnsureDrawingPhaseUI();
        BindDrawingUIEvents(subscribe: true);

        if (_sequenceRoutine != null)
        {
            StopCoroutine(_sequenceRoutine);
            _sequenceRoutine = null;
        }

        if (_drawingTimerRoutine != null)
        {
            StopCoroutine(_drawingTimerRoutine);
            _drawingTimerRoutine = null;
        }

        EnsurePlayersExistForTesting();
        AssignSecretTargetColorsToPlayers();

        GameSessionData.ClearDrawings();
        _submittedPlayerIds.Clear();

        SetPhase(GamePhase.Drawing);

        if (drawingPhaseUI != null)
            drawingPhaseUI.BeginDrawingRound(GameSessionData.Players.Count, drawingDurationSeconds);

        // Notify connected phones via relay to start drawing their secret target colour
        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastDrawingPhase(
                _currentRound,
                Mathf.RoundToInt(drawingDurationSeconds * 1000f),
                GameSessionData.Players
            );
        }

        _drawingTimerRoutine = StartCoroutine(DrawingTimerRoutine());
    }

    /// <summary>
    /// Simulates the next unsubmitted player submitting their drawing (for testing in Play Mode).
    /// </summary>
    [Button]
    public void SimulateNextPlayerSubmit()
    {
        if (!Application.isPlaying || _currentPhase != GamePhase.Drawing)
            return;

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (!_submittedPlayerIds.Contains(p.playerId))
            {
                OnPlayerDrawingSubmitted(
                    p.playerId,
                    $"Clue from {p.name}",
                    drawingTexture: null,
                    imageBase64: "",
                    mime: "image/png"
                );
                return;
            }
        }
    }

    /// <summary>Ends the Drawing Phase, hides the Drawing UI, and transitions to the next phase.</summary>
    [Button]
    public void CompleteDrawingPhase()
    {
        if (_currentPhase != GamePhase.Drawing)
            return;

        if (_drawingTimerRoutine != null)
        {
            StopCoroutine(_drawingTimerRoutine);
            _drawingTimerRoutine = null;
        }

        AllDrawingsSubmitted?.Invoke();

        if (autoStartMergeAfterDrawing)
        {
            SetPhase(GamePhase.Guessing);
            GenerateAndSpawn();
        }
        else
        {
            SetPhase(GamePhase.Results);
        }
    }

    private void SetPhase(GamePhase nextPhase)
    {
        _currentPhase = nextPhase;

        bool showDrawing = nextPhase == GamePhase.Drawing;

        if (drawingPhaseUI != null)
            drawingPhaseUI.SetVisible(showDrawing);

        if (colorPhysics != null)
            colorPhysics.gameObject.SetActive(!showDrawing);

        PhaseChanged?.Invoke(_currentPhase);
    }

    private IEnumerator DrawingTimerRoutine()
    {
        float remaining = drawingDurationSeconds;

        while (remaining > 0f && _currentPhase == GamePhase.Drawing)
        {
            if (drawingPhaseUI != null)
                drawingPhaseUI.SetRemainingSeconds(remaining);

            if (_submittedPlayerIds.Count >= GameSessionData.Players.Count && GameSessionData.Players.Count > 0)
            {
                if (allSubmittedAdvanceDelay > 0f)
                    yield return new WaitForSeconds(allSubmittedAdvanceDelay);

                CompleteDrawingPhase();
                yield break;
            }

            remaining -= Time.deltaTime;
            yield return null;
        }

        if (_currentPhase == GamePhase.Drawing)
        {
            if (drawingPhaseUI != null)
                drawingPhaseUI.SetRemainingSeconds(0f);

            CompleteDrawingPhase();
        }
    }

    private void OnRelayDrawingSubmitted(string playerId, DrawingSubmitPayload payload, Texture2D drawingTexture)
    {
        if (_currentPhase != GamePhase.Drawing)
            return;

        OnPlayerDrawingSubmitted(
            playerId,
            payload?.prompt ?? "",
            drawingTexture,
            payload?.image ?? "",
            payload?.mime ?? "image/png"
        );
    }

    /// <summary>
    /// Records a player's submitted drawing and pops up their icon on the Drawing Phase UI.
    /// </summary>
    public void OnPlayerDrawingSubmitted(
        string playerId,
        string prompt,
        Texture2D drawingTexture,
        string imageBase64,
        string mime)
    {
        if (string.IsNullOrEmpty(playerId) || _submittedPlayerIds.Contains(playerId))
            return;

        GameSessionData.SessionPlayer player = GameSessionData.FindPlayer(playerId);
        if (player == null)
        {
            // Player submitted via relay even if not pre-seeded locally
            player = new GameSessionData.SessionPlayer
            {
                playerId = playerId,
                name = $"PLAYER {GameSessionData.Players.Count + 1}",
                color = Color.HSVToRGB(UnityEngine.Random.value, 0.88f, 0.98f),
                secretTargetColor = Color.HSVToRGB(UnityEngine.Random.value, saturation, value)
            };
            GameSessionData.Players.Add(player);
        }

        _submittedPlayerIds.Add(playerId);

        GameSessionData.SubmittedDrawing record = new()
        {
            playerId = player.playerId,
            playerName = player.name,
            playerColor = player.color,
            targetColor = player.secretTargetColor,
            prompt = prompt ?? "",
            imageBase64 = imageBase64 ?? "",
            mime = mime ?? "image/png",
            texture = drawingTexture
        };

        GameSessionData.Drawings.Add(record);
        DrawingReceived?.Invoke(record);

        if (drawingPhaseUI != null)
            drawingPhaseUI.MarkPlayerSubmitted(player);
    }

    private void EnsurePlayersExistForTesting()
    {
        if (GameSessionData.Players.Count > 0)
            return;

        int count = Mathf.Clamp(colorCount, 1, 12);
        for (int i = 0; i < count; i++)
        {
            float hue = Mathf.Repeat(i * 0.61803398875f, 1f);
            GameSessionData.Players.Add(new GameSessionData.SessionPlayer
            {
                playerId = $"p_test_{i + 1}",
                name = FallbackPlayerNames[i % FallbackPlayerNames.Length],
                color = Color.HSVToRGB(hue, 0.88f, 0.98f)
            });
        }
    }

    private void AssignSecretTargetColorsToPlayers()
    {
        System.Random random = useSeed ? new System.Random(seed) : new System.Random();
        List<Color> targets = GenerateDistinctColors(Mathf.Max(1, GameSessionData.Players.Count), random);

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.Players[i].secretTargetColor = targets[i];
        }
    }

    private void EnsureDrawingPhaseUI()
    {
        if (drawingPhaseUI == null)
            drawingPhaseUI = FindAnyObjectByType<DrawingPhaseController>(FindObjectsInactive.Include);

#if UNITY_EDITOR
        if (drawingPhaseUI == null && Application.isPlaying)
        {
            GameObject uiObj = new("Drawing Phase UI");
            UIDocument doc = uiObj.AddComponent<UIDocument>();
            doc.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/MenuPanelSettings.asset");
            doc.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Drawing/DrawingPhase.uxml");
            drawingPhaseUI = uiObj.AddComponent<DrawingPhaseController>();
        }
#endif
    }

    private void BindDrawingUIEvents(bool subscribe)
    {
        if (drawingPhaseUI == null)
            return;

        drawingPhaseUI.SimulateSubmitRequested -= SimulateNextPlayerSubmit;
        drawingPhaseUI.ContinueRequested -= CompleteDrawingPhase;

        if (subscribe)
        {
            drawingPhaseUI.SimulateSubmitRequested += SimulateNextPlayerSubmit;
            drawingPhaseUI.ContinueRequested += CompleteDrawingPhase;
        }
    }

    private void BindRelayEvents(bool subscribe)
    {
        if (HostRelayClient.Instance == null)
            return;

        HostRelayClient.Instance.DrawingSubmitted -= OnRelayDrawingSubmitted;

        if (subscribe)
            HostRelayClient.Instance.DrawingSubmitted += OnRelayDrawingSubmitted;
    }

    #endregion

    #region ColorPhysics Spawn & Merge Sequence

    /// <summary>
    /// Generates colorCount distinct colours, then runs the sequence:
    /// spawn every partitioned piece, wait mergeDelay, merge them all.
    /// </summary>
    [Button]
    public void GenerateAndSpawn()
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

        if (_currentPhase == GamePhase.Drawing)
            SetPhase(GamePhase.MergeSequence);

        System.Random random = useSeed
            ? new System.Random(seed)
            : new System.Random();

        int count = GameSessionData.Players.Count > 0 ? GameSessionData.Players.Count : colorCount;
        List<Color> colors = GenerateDistinctColors(count, random);

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

        _sequenceRoutine = StartCoroutine(SpawnThenMergeRoutine(spawnQueue));
    }

    private IEnumerator SpawnThenMergeRoutine(
        Queue<(Color color, float radius)> spawnQueue)
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

    #endregion
}

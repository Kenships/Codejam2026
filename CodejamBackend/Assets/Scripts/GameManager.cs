using System;
using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Codejam.Networking;
using Core.Utility;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

public class GameManager : Singleton<GameManager>
{
    public enum GamePhase
    {
        None,
        Drawing,
        ArtistReveal,
        Guessing,
        MergeReveal,
        Finished
    }

    [SerializeField, Required] private ColorPhysics colorPhysics;
    [SerializeField] private RevealController revealController;
    [SerializeField] private DrawingPhaseController drawingPhaseUI;
    [SerializeField] private GuessingPhaseController guessingPhaseUI;

    public RevealController Reveal => revealController;

    [Title("Phase Flow")]
    [Tooltip("Starts the Drawing Phase immediately when GameScene begins.")]
    [SerializeField] private bool startDrawingOnStart = true;

    [Tooltip("Seconds players have to draw and submit (matches DrawingCanvasCard default of 60s).")]
    [SerializeField, Min(5f)] private float drawingDurationSeconds = 60f;

    [Tooltip("Seconds players have to guess each drawing's hue.")]
    [SerializeField, Min(5f)] private float guessingDurationSeconds = 30f;

    [Tooltip("Short beat after all players submit drawings before merging icons into the first artist.")]
    [SerializeField, Min(0f)] private float allSubmittedAdvanceDelay = 1.0f;

    [Tooltip("Seconds to hold the True Hue reveal before automatically advancing to the next drawing.")]
    [SerializeField, Min(1f)] private float revealDurationSeconds = 6f;

    [SerializeField] private string menuSceneName = "Menu";

    [Title("Colors")]
    // Number of distinct colours (one per person, also used for sample players when testing GameScene directly)
    [SerializeField, Min(1)] private int colorCount = 4;

    [SerializeField, Range(0f, 1f)] private float saturation = 1f;
    [SerializeField, Range(0f, 1f)] private float value = 1f;

    [Title("Area")]
    // Total circle area shared evenly between all guessed colours in a round
    [SerializeField, Min(0.01f)] private float totalArea = 30f;

    // Bounds on the area of each spawned piece
    [SerializeField, Min(0.001f)] private float minUnitArea = 0.6f;
    [SerializeField, Min(0.001f)] private float maxUnitArea = 1.5f;

    [Title("Sequence")]
    // Pause after the last body spawns before everything starts merging
    [SerializeField, Min(0f)] private float mergeDelay = 0.8f;

    [Title("Randomness")]
    // Use a fixed seed to get the same colours and pieces every time
    [SerializeField] private bool useSeed;
    [SerializeField, ShowIf(nameof(useSeed))] private int seed;

    public event Action<GamePhase> PhaseChanged;
    public event Action<GameSessionData.SubmittedDrawing> DrawingReceived;
    public event Action AllDrawingsSubmitted;

    private Coroutine _sequenceRoutine;
    private Coroutine _phaseRoutine;
    private RevealUI _revealUI;
    private readonly HashSet<string> _submittedDrawingPlayerIds = new();
    private readonly Dictionary<string, Color> _currentRoundGuesses = new();

    private GamePhase _currentPhase = GamePhase.None;
    private int _currentRound = 1;
    private int _currentDrawingIndex;
    private bool _advanceRevealRequested;

    public GamePhase CurrentPhase => _currentPhase;
    public int CurrentRound => _currentRound;
    public bool IsRunning => _sequenceRoutine != null;

    private static readonly string[] FallbackPlayerNames =
    {
        "PRISM", "CHROMA", "SPECTRA", "NEON", "PIXEL", "COBALT",
        "VERMILION", "CYAN", "MAGENTA", "AMBER", "INDIGO", "IRIS"
    };

    private static readonly string[] SamplePrompts =
    {
        "Golden hour sunset",
        "Midnight mountain peaks",
        "Fresh spring blossom",
        "Cozy autumn cabin",
        "Electric ocean wave",
        "Neon city glow"
    };

    private void OnEnable()
    {
        BindRelayEvents(subscribe: true);
    }

    private void OnDisable()
    {
        BindRelayEvents(subscribe: false);
        BindUIEvents(subscribe: false);
    }

    private void Start()
    {
        EnsurePhaseUIs();
        BindUIEvents(subscribe: true);
        BindRelayEvents(subscribe: true);

        if (startDrawingOnStart)
        {
            StartDrawingPhase();
        }
        else
        {
            drawingPhaseUI?.SetVisible(false);
            guessingPhaseUI?.SetVisible(false);
        }
    }

    #region Phase 1: Drawing

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

        EnsurePhaseUIs();
        BindUIEvents(subscribe: true);
        StopActiveRoutines();

        revealController?.Clear();
        colorPhysics?.ClearAllBodies();

        EnsurePlayersExistForTesting();
        AssignSecretTargetColorsToPlayers();

        GameSessionData.ClearDrawings();
        _submittedDrawingPlayerIds.Clear();
        _currentRoundGuesses.Clear();
        _currentDrawingIndex = 0;

        SetPhase(GamePhase.Drawing);

        if (drawingPhaseUI != null)
            drawingPhaseUI.BeginDrawingRound(GameSessionData.Players.Count, drawingDurationSeconds);

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastDrawingPhase(
                _currentRound,
                Mathf.RoundToInt(drawingDurationSeconds * 1000f),
                GameSessionData.Players
            );
        }

        _phaseRoutine = StartCoroutine(DrawingTimerRoutine());
    }

    /// <summary>Simulates the next unsubmitted player submitting their drawing.</summary>
    [Button]
    public void SimulateNextPlayerSubmit()
    {
        if (!Application.isPlaying || _currentPhase != GamePhase.Drawing)
            return;

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (!_submittedDrawingPlayerIds.Contains(p.playerId))
            {
                OnPlayerDrawingSubmitted(
                    p.playerId,
                    SamplePrompts[i % SamplePrompts.Length],
                    drawingTexture: null,
                    imageBase64: "",
                    mime: "image/png"
                );
                return;
            }
        }
    }

    /// <summary>Ends the Drawing Phase and begins the guessing loop across all submitted drawings.</summary>
    [Button]
    public void CompleteDrawingPhase()
    {
        if (_currentPhase != GamePhase.Drawing)
            return;

        StopActiveRoutines();

        // Ensure every player has a drawing entry (using stand-in drawing if timer expired before submit)
        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (!_submittedDrawingPlayerIds.Contains(p.playerId))
            {
                _submittedDrawingPlayerIds.Add(p.playerId);
                GameSessionData.Drawings.Add(new GameSessionData.SubmittedDrawing
                {
                    playerId = p.playerId,
                    playerName = p.name,
                    playerColor = p.color,
                    targetColor = p.secretTargetColor,
                    prompt = SamplePrompts[i % SamplePrompts.Length],
                    imageBase64 = "",
                    mime = "image/png",
                    texture = null
                });
            }
        }

        AllDrawingsSubmitted?.Invoke();

        _currentDrawingIndex = 0;
        BeginArtistRevealForCurrentDrawing();
    }

    private IEnumerator DrawingTimerRoutine()
    {
        float remaining = drawingDurationSeconds;

        while (remaining > 0f && _currentPhase == GamePhase.Drawing)
        {
            drawingPhaseUI?.SetRemainingSeconds(remaining);

            if (_submittedDrawingPlayerIds.Count >= GameSessionData.Players.Count && GameSessionData.Players.Count > 0)
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
            drawingPhaseUI?.SetRemainingSeconds(0f);
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

    public void OnPlayerDrawingSubmitted(
        string playerId,
        string prompt,
        Texture2D drawingTexture,
        string imageBase64,
        string mime)
    {
        if (string.IsNullOrEmpty(playerId) || _submittedDrawingPlayerIds.Contains(playerId))
            return;

        GameSessionData.SessionPlayer player = GameSessionData.FindPlayer(playerId);
        if (player == null)
        {
            player = new GameSessionData.SessionPlayer
            {
                playerId = playerId,
                name = $"PLAYER {GameSessionData.Players.Count + 1}",
                color = Color.HSVToRGB(UnityEngine.Random.value, 0.88f, 0.98f),
                secretTargetColor = Color.HSVToRGB(UnityEngine.Random.value, saturation, value)
            };
            GameSessionData.Players.Add(player);
        }

        _submittedDrawingPlayerIds.Add(playerId);

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

        drawingPhaseUI?.MarkPlayerSubmitted(player);
    }

    #endregion

    #region Phase 2 & 3: Artist Merge Transition -> Guessing (30s)

    private void BeginArtistRevealForCurrentDrawing()
    {
        if (_currentDrawingIndex >= GameSessionData.Drawings.Count)
        {
            EnterFinishedPhase();
            return;
        }

        StopActiveRoutines();
        revealController?.Clear();
        colorPhysics?.ClearAllBodies();

        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];
        GameSessionData.SessionPlayer artist = GameSessionData.FindPlayer(currentDrawing.playerId)
            ?? new GameSessionData.SessionPlayer
            {
                playerId = currentDrawing.playerId,
                name = currentDrawing.playerName,
                color = currentDrawing.playerColor,
                secretTargetColor = currentDrawing.targetColor
            };

        SetPhase(GamePhase.ArtistReveal);

        if (drawingPhaseUI != null)
        {
            drawingPhaseUI.PlayArtistMergeTransition(
                GameSessionData.Players,
                artist,
                _currentDrawingIndex + 1,
                GameSessionData.Drawings.Count,
                BeginGuessingForCurrentDrawing
            );
        }
        else
        {
            BeginGuessingForCurrentDrawing();
        }
    }

    private void BeginGuessingForCurrentDrawing()
    {
        if (_currentDrawingIndex >= GameSessionData.Drawings.Count)
        {
            EnterFinishedPhase();
            return;
        }

        StopActiveRoutines();
        _currentRoundGuesses.Clear();
        colorPhysics?.ClearAllBodies();

        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];
        GameSessionData.SessionPlayer artist = GameSessionData.FindPlayer(currentDrawing.playerId);

        int expectedGuessers = GetExpectedGuesserCount(currentDrawing.playerId);

        SetPhase(GamePhase.Guessing);

        if (guessingPhaseUI != null)
        {
            guessingPhaseUI.BeginGuessingRound(
                artist,
                currentDrawing,
                _currentDrawingIndex,
                GameSessionData.Drawings.Count,
                expectedGuessers,
                guessingDurationSeconds
            );
        }

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastRoundDrawing(
                currentDrawing,
                _currentDrawingIndex + 1,
                Mathf.RoundToInt(guessingDurationSeconds * 1000f)
            );
        }

        _phaseRoutine = StartCoroutine(GuessingTimerRoutine(currentDrawing.playerId, expectedGuessers));
    }

    private IEnumerator GuessingTimerRoutine(string artistId, int expectedGuessers)
    {
        float remaining = guessingDurationSeconds;

        while (remaining > 0f && _currentPhase == GamePhase.Guessing)
        {
            guessingPhaseUI?.SetRemainingSeconds(remaining);

            if (_currentRoundGuesses.Count >= expectedGuessers && expectedGuessers > 0)
            {
                BeginMergeAndRevealForCurrentDrawing();
                yield break;
            }

            remaining -= Time.deltaTime;
            yield return null;
        }

        if (_currentPhase == GamePhase.Guessing)
        {
            guessingPhaseUI?.SetRemainingSeconds(0f);
            BeginMergeAndRevealForCurrentDrawing();
        }
    }

    private void OnRelayGuessSubmitted(string playerId, string hex)
    {
        if (_currentPhase != GamePhase.Guessing)
            return;

        if (!ColorUtility.TryParseHtmlString(hex, out Color guessedColor))
            return;

        OnPlayerGuessSubmitted(playerId, guessedColor);
    }

    /// <summary>
    /// Simulates the next non-artist player submitting a guessed colour near the target hue.
    /// </summary>
    [Button]
    public void SimulateNextPlayerGuess()
    {
        if (!Application.isPlaying || _currentPhase != GamePhase.Guessing)
            return;

        if (_currentDrawingIndex >= GameSessionData.Drawings.Count)
            return;

        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (p.playerId == currentDrawing.playerId || _currentRoundGuesses.ContainsKey(p.playerId))
                continue;

            // Pick a simulated guessed hue near the artist's target colour with realistic spread
            Color.RGBToHSV(currentDrawing.targetColor, out float targetH, out float targetS, out float targetV);
            float guessedH = Mathf.Repeat(targetH + UnityEngine.Random.Range(-0.14f, 0.14f), 1f);
            Color guessedColor = Color.HSVToRGB(guessedH, Mathf.Clamp01(targetS), Mathf.Clamp01(targetV));

            OnPlayerGuessSubmitted(p.playerId, guessedColor);
            return;
        }
    }

    /// <summary>
    /// Records a player's colour guess and immediately spawns their guessed colour as cubes in ColorPhysics.
    /// </summary>
    public void OnPlayerGuessSubmitted(string playerId, Color guessedColor)
    {
        if (_currentPhase != GamePhase.Guessing || string.IsNullOrEmpty(playerId))
            return;

        if (_currentDrawingIndex >= GameSessionData.Drawings.Count)
            return;

        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];
        if (playerId == currentDrawing.playerId || _currentRoundGuesses.ContainsKey(playerId))
            return;

        _currentRoundGuesses[playerId] = guessedColor;

        int expectedGuessers = GetExpectedGuesserCount(currentDrawing.playerId);
        SpawnGuessedColorCubes(guessedColor, expectedGuessers);

        guessingPhaseUI?.SetGuessProgress(_currentRoundGuesses.Count, expectedGuessers);
    }

    private void SpawnGuessedColorCubes(Color guessedColor, int expectedGuessers)
    {
        if (colorPhysics == null)
            return;

        float shareArea = totalArea / Mathf.Max(1, expectedGuessers);
        ColorPartitionService partitionService = new();
        Queue<(Color color, float radius)> pieces = partitionService.CreateSpawnQueue(
            new List<Color> { guessedColor },
            shareArea,
            minUnitArea,
            maxUnitArea
        );

        colorPhysics.SpawnQueue(pieces);
    }

    private int GetExpectedGuesserCount(string artistId)
    {
        int count = 0;
        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            if (GameSessionData.Players[i].playerId != artistId)
                count++;
        }
        return Mathf.Max(1, count);
    }

    #endregion

    #region Phase 4: Merge Into Singular Colour & Reveal -> Loop

    /// <summary>
    /// Merges all guessed colour cubes into a singular colour using ColorPhysics.SquashAll()
    /// and reveals the merged colour alongside the artist's True Hue.
    /// </summary>
    [Button]
    public void BeginMergeAndRevealForCurrentDrawing()
    {
        if (_currentPhase != GamePhase.Guessing)
            return;

        StopActiveRoutines();
        SetPhase(GamePhase.MergeReveal);
        guessingPhaseUI?.ShowMergingState();

        _sequenceRoutine = StartCoroutine(MergeAndRevealRoutine());
    }

    private IEnumerator MergeAndRevealRoutine()
    {
        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];

        // If nobody guessed before time ran out, spawn a neutral fallback cube so merge still resolves
        if (colorPhysics != null && colorPhysics.Bodies.Count == 0 && !colorPhysics.IsSpawning)
        {
            colorPhysics.SpawnBody(new Color(0.5f, 0.5f, 0.5f, 1f), 0.7f);
        }

        // Wait for any remaining queued guess cubes to finish spawning
        while (colorPhysics != null && colorPhysics.IsSpawning)
            yield return null;

        if (mergeDelay > 0f)
            yield return new WaitForSeconds(mergeDelay);

        // Merge all guessed cubes into a single colour using ColorPhysics.SquashAll()
        if (colorPhysics != null && colorPhysics.Bodies.Count > 1)
        {
            colorPhysics.SquashAll();

            while (colorPhysics.IsSquashing)
                yield return null;
        }

        List<Color> guessedColors = new(_currentRoundGuesses.Values);
        if (guessedColors.Count == 0)
            guessedColors.Add(new Color(0.5f, 0.5f, 0.5f, 1f));

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastPhaseState("results", _currentDrawingIndex + 1, Mathf.RoundToInt(revealDurationSeconds * 1000f));
        }

        _advanceRevealRequested = false;

        if (revealController != null)
        {
            // Let RevealController + RevealUI take over the full screen for the side-by-side True Hue reveal
            drawingPhaseUI?.SetVisible(false);
            guessingPhaseUI?.SetVisible(false);

            bool hasMoreDrawings = _currentDrawingIndex + 1 < GameSessionData.Drawings.Count;
            _revealUI?.SetActionButtonText(hasMoreDrawings ? "NEXT DRAWING" : "FINISH GAME");

            yield return revealController.RevealLast(guessedColors, currentDrawing.targetColor);

            float elapsed = 0f;
            while (elapsed < revealDurationSeconds && !_advanceRevealRequested)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            revealController.Clear();
        }
        else
        {
            // Fallback if no RevealController is in the scene
            Color finalMergedColor = new(0.5f, 0.5f, 0.5f, 1f);
            if (colorPhysics != null)
            {
                for (int i = 0; i < colorPhysics.Bodies.Count; i++)
                {
                    if (colorPhysics.Bodies[i] != null)
                    {
                        finalMergedColor = colorPhysics.Bodies[i].Color;
                        break;
                    }
                }
            }

            bool hasMoreDrawings = _currentDrawingIndex + 1 < GameSessionData.Drawings.Count;
            guessingPhaseUI?.ShowReveal(finalMergedColor, currentDrawing.targetColor, hasMoreDrawings);

            float elapsed = 0f;
            while (elapsed < revealDurationSeconds && !_advanceRevealRequested)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        _sequenceRoutine = null;
        AdvanceToNextDrawing();
    }

    private void RequestNextDrawing()
    {
        if (_currentPhase == GamePhase.MergeReveal)
            _advanceRevealRequested = true;
    }

    private void AdvanceToNextDrawing()
    {
        _currentDrawingIndex++;

        if (_currentDrawingIndex < GameSessionData.Drawings.Count)
        {
            BeginArtistRevealForCurrentDrawing();
        }
        else
        {
            EnterFinishedPhase();
        }
    }

    private void EnterFinishedPhase()
    {
        StopActiveRoutines();
        revealController?.Clear();
        colorPhysics?.ClearAllBodies();

        SetPhase(GamePhase.Finished);
        guessingPhaseUI?.ShowGameFinished(GameSessionData.Drawings.Count);

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastPhaseState("finished", _currentRound, 0);
        }
    }

    private void HandlePlayAgain()
    {
        _currentRound++;
        StartDrawingPhase();
    }

    private void HandleBackToMenu()
    {
        StopActiveRoutines();
        revealController?.Clear();
        colorPhysics?.ClearAllBodies();

        if (!string.IsNullOrEmpty(menuSceneName))
            SceneManager.LoadScene(menuSceneName);
    }

    #endregion

    #region Phase Visibility & Setup

    private void SetPhase(GamePhase nextPhase)
    {
        _currentPhase = nextPhase;

        bool showDrawingUI = nextPhase is GamePhase.Drawing or GamePhase.ArtistReveal;
        bool showGuessingUI = nextPhase is GamePhase.Guessing or GamePhase.MergeReveal or GamePhase.Finished;
        bool showPhysics = nextPhase is GamePhase.Guessing or GamePhase.MergeReveal;

        drawingPhaseUI?.SetVisible(showDrawingUI);
        guessingPhaseUI?.SetVisible(showGuessingUI);

        if (colorPhysics != null)
            colorPhysics.gameObject.SetActive(showPhysics);

        PhaseChanged?.Invoke(_currentPhase);
    }

    private void StopActiveRoutines()
    {
        if (_phaseRoutine != null)
        {
            StopCoroutine(_phaseRoutine);
            _phaseRoutine = null;
        }

        if (_sequenceRoutine != null)
        {
            StopCoroutine(_sequenceRoutine);
            _sequenceRoutine = null;
        }
    }

    private void EnsurePlayersExistForTesting()
    {
        if (GameSessionData.Players.Count > 0)
            return;

        int count = Mathf.Clamp(colorCount, 2, 12);
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

    private void EnsurePhaseUIs()
    {
        if (revealController == null)
            revealController = GetComponent<RevealController>() ?? FindFirstObjectByType<RevealController>();

        if (_revealUI == null)
            _revealUI = FindFirstObjectByType<RevealUI>();

        if (drawingPhaseUI == null)
            drawingPhaseUI = FindAnyObjectByType<DrawingPhaseController>(FindObjectsInactive.Include);

        if (guessingPhaseUI == null)
            guessingPhaseUI = FindAnyObjectByType<GuessingPhaseController>(FindObjectsInactive.Include);

#if UNITY_EDITOR
        if (drawingPhaseUI == null && Application.isPlaying)
        {
            GameObject uiObj = new("Drawing Phase UI");
            UIDocument doc = uiObj.AddComponent<UIDocument>();
            doc.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/MenuPanelSettings.asset");
            doc.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Drawing/DrawingPhase.uxml");
            doc.sortingOrder = 10;
            drawingPhaseUI = uiObj.AddComponent<DrawingPhaseController>();
        }

        if (guessingPhaseUI == null && Application.isPlaying)
        {
            GameObject uiObj = new("Guessing Phase UI");
            UIDocument doc = uiObj.AddComponent<UIDocument>();
            doc.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/MenuPanelSettings.asset");
            doc.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/Guessing/GuessingPhase.uxml");
            doc.sortingOrder = 12;
            guessingPhaseUI = uiObj.AddComponent<GuessingPhaseController>();
        }
#endif
    }

    private void BindUIEvents(bool subscribe)
    {
        if (drawingPhaseUI != null)
        {
            drawingPhaseUI.SimulateSubmitRequested -= SimulateNextPlayerSubmit;
            drawingPhaseUI.ContinueRequested -= CompleteDrawingPhase;

            if (subscribe)
            {
                drawingPhaseUI.SimulateSubmitRequested += SimulateNextPlayerSubmit;
                drawingPhaseUI.ContinueRequested += CompleteDrawingPhase;
            }
        }

        if (_revealUI != null)
        {
            _revealUI.PlayAgainRequested -= RequestNextDrawing;

            if (subscribe)
                _revealUI.PlayAgainRequested += RequestNextDrawing;
        }

        if (guessingPhaseUI != null)
        {
            guessingPhaseUI.SimulateGuessRequested -= SimulateNextPlayerGuess;
            guessingPhaseUI.MergeNowRequested -= BeginMergeAndRevealForCurrentDrawing;
            guessingPhaseUI.NextDrawingRequested -= RequestNextDrawing;
            guessingPhaseUI.PlayAgainRequested -= HandlePlayAgain;
            guessingPhaseUI.BackToMenuRequested -= HandleBackToMenu;

            if (subscribe)
            {
                guessingPhaseUI.SimulateGuessRequested += SimulateNextPlayerGuess;
                guessingPhaseUI.MergeNowRequested += BeginMergeAndRevealForCurrentDrawing;
                guessingPhaseUI.NextDrawingRequested += RequestNextDrawing;
                guessingPhaseUI.PlayAgainRequested += HandlePlayAgain;
                guessingPhaseUI.BackToMenuRequested += HandleBackToMenu;
            }
        }
    }

    private void BindRelayEvents(bool subscribe)
    {
        if (HostRelayClient.Instance == null)
            return;

        HostRelayClient.Instance.DrawingSubmitted -= OnRelayDrawingSubmitted;
        HostRelayClient.Instance.GuessSubmitted -= OnRelayGuessSubmitted;

        if (subscribe)
        {
            HostRelayClient.Instance.DrawingSubmitted += OnRelayDrawingSubmitted;
            HostRelayClient.Instance.GuessSubmitted += OnRelayGuessSubmitted;
        }
    }

    #endregion

    #region Standalone ColorPhysics Spawn & Merge Sequence

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
        colorPhysics.SpawnQueue(spawnQueue);

        while (colorPhysics.IsSpawning)
            yield return null;

        if (mergeDelay > 0f)
            yield return new WaitForSeconds(mergeDelay);

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

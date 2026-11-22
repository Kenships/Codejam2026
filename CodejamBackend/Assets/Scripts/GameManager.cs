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
    [SerializeField] private LeaderboardUI leaderboardUI;

    public RevealController Reveal => revealController;

    [Title("Phase Flow")]
    [Tooltip("Starts the Drawing Phase immediately when GameScene begins.")]
    [SerializeField] private bool startDrawingOnStart = true;

    [Tooltip("Seconds players have to draw and submit. Replaced by the Create Game screen's setting " +
             "when the match was started from the menu.")]
    [SerializeField, Min(5f)] private float drawingDurationSeconds = 60f;

    [Tooltip("Extra seconds the drawing phase stays open after the timer, for phones still uploading. " +
             "Each phone runs its own timer that starts when the drawing phase reaches it, so its " +
             "auto-submit lands a little after ours. Ends early once everyone has submitted.")]
    [SerializeField, Min(0f)] private float lateSubmitGraceSeconds = 5f;

    [Tooltip("Seconds players have to guess each drawing's hue. Replaced by the Create Game screen's " +
             "setting when the match was started from the menu.")]
    [SerializeField, Min(5f)] private float guessingDurationSeconds = 30f;

    [Tooltip("Phases stop waiting for a phone this many seconds after it disconnects (it can still " +
             "submit if it comes back in time). Short, so a brief network blip doesn't cost a turn.")]
    [SerializeField, Min(0f)] private float disconnectedWaitSeconds = 3f;

    [Tooltip("Short beat after all players submit drawings before merging icons into the first artist.")]
    [SerializeField, Min(0f)] private float allSubmittedAdvanceDelay = 1.0f;

    [Tooltip("Seconds to hold the True Hue reveal before automatically advancing to the next drawing.")]
    [SerializeField, Min(1f)] private float revealDurationSeconds = 6f;

    [Tooltip("Seconds the standings stay up after each drawing before the next one (the host can skip).")]
    [SerializeField, Min(1f)] private float leaderboardDurationSeconds = 8f;

    [Tooltip("Also show the standings after the last drawing, before the winners podium.")]
    [SerializeField] private bool leaderboardAfterLastDrawing;

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
    // Points each player earned on the current drawing (guessers, and the artist at reveal)
    private readonly Dictionary<string, int> _currentRoundPoints = new();
    // Everyone's totalPoints before the current drawing was scored (for leaderboard movement)
    private readonly Dictionary<string, int> _totalsBeforeDrawing = new();
    // Players whose phone is disconnected, and since when (Time.time). Phases don't wait for them.
    // NegativeInfinity = gone for good (seat released: timed out, kicked or left).
    private readonly Dictionary<string, float> _disconnectedSince = new();
    private float _guessingStartedAt;
    // When the current drawing/guessing countdown ends (Time.time), for resyncing phones
    private float _phaseEndsAt;

    private GamePhase _currentPhase = GamePhase.None;
    private int _currentRound = 1;
    private int _currentDrawingIndex;
    private bool _advanceRevealRequested;

    public GamePhase CurrentPhase => _currentPhase;
    public int CurrentRound => _currentRound;
    public bool IsRunning => _sequenceRoutine != null;


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
        // Timers picked on the Create Game screen
        if (GameSessionData.HasMatchSettings)
        {
            drawingDurationSeconds = GameSessionData.DrawingSeconds;
            guessingDurationSeconds = GameSessionData.GuessingSeconds;
        }

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

        // No game with fewer than 2 players (e.g. GameScene played on its own, or Play Again
        // after everyone else left): back to the menu's lobby instead.
        SyncConnectionStatesFromRelay();
        int connectedPlayers = CountConnectedPlayers();
        if (connectedPlayers < GameSessionData.MinPlayersToStart)
        {
            Debug.LogWarning($"[GameManager] Need at least {GameSessionData.MinPlayersToStart} connected players " +
                             $"to start a game (have {connectedPlayers}). Returning to the menu.");
            HandleBackToMenu();
            return;
        }

        AssignSecretTargetColorsToPlayers();
        DealCategoriesToPlayers();

        GameSessionData.ClearDrawings();
        _submittedDrawingPlayerIds.Clear();
        _currentRoundGuesses.Clear();
        _currentDrawingIndex = 0;

        SetPhase(GamePhase.Drawing);
        _phaseEndsAt = Time.time + drawingDurationSeconds;

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

    /// <summary>Ends the Drawing Phase and begins the guessing loop across all submitted drawings.</summary>
    [Button]
    public void CompleteDrawingPhase()
    {
        if (_currentPhase != GamePhase.Drawing)
            return;

        StopActiveRoutines();

        // Phones that didn't submit (left, or never pressed submit) are skipped:
        // a blank drawing would be impossible to guess.
        AllDrawingsSubmitted?.Invoke();

        _currentDrawingIndex = 0;
        BeginArtistRevealForCurrentDrawing();
    }

    /// <summary>
    /// Host pressed CONTINUE on the Drawing UI. Ignored until at least one drawing is in,
    /// so the round can't be skipped while players are still preparing their drawings.
    /// (The timer still ends the phase on its own, even with no submissions.)
    /// </summary>
    private void HandleDrawingContinueRequested()
    {
        if (GameSessionData.Drawings.Count == 0)
            return;

        CompleteDrawingPhase();
    }

    private IEnumerator DrawingTimerRoutine()
    {
        float remaining = drawingDurationSeconds;

        while (remaining > 0f && _currentPhase == GamePhase.Drawing)
        {
            drawingPhaseUI?.SetRemainingSeconds(remaining);

            if (HaveAllDrawings())
            {
                if (allSubmittedAdvanceDelay > 0f)
                    yield return new WaitForSeconds(allSubmittedAdvanceDelay);

                CompleteDrawingPhase();
                yield break;
            }

            remaining -= Time.deltaTime;
            yield return null;
        }

        // Grace period: keep accepting drawings from phones whose own timer ended a moment
        // after ours (it starts when the drawing phase reaches the phone) or that are still
        // uploading. Ends as soon as everyone is in.
        drawingPhaseUI?.SetRemainingSeconds(0f);
        float grace = lateSubmitGraceSeconds;
        while (grace > 0f && _currentPhase == GamePhase.Drawing && !HaveAllDrawings())
        {
            grace -= Time.deltaTime;
            yield return null;
        }

        if (_currentPhase == GamePhase.Drawing)
            CompleteDrawingPhase();
    }

    /// <summary>
    /// True once every connected player has submitted a drawing. Disconnected players aren't
    /// waited for; if nobody is connected, the timer runs out instead (they may come back).
    /// </summary>
    private bool HaveAllDrawings()
    {
        bool anyoneToWaitFor = false;
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (!IsWaitingFor(p.playerId))
                continue;

            anyoneToWaitFor = true;
            if (!_submittedDrawingPlayerIds.Contains(p.playerId))
                return false;
        }
        return anyoneToWaitFor;
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
            prompt = ResolveDrawingPrompt(player, prompt),
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
        _currentRoundPoints.Clear();
        colorPhysics?.ClearAllBodies();

        GameSessionData.SubmittedDrawing currentDrawing = GameSessionData.Drawings[_currentDrawingIndex];
        GameSessionData.SessionPlayer artist = GameSessionData.FindPlayer(currentDrawing.playerId);
        _guessingStartedAt = Time.time;
        _phaseEndsAt = Time.time + guessingDurationSeconds;

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
            // Everyone except the artist (who'd be guessing their own colour)
            HostRelayClient.Instance.BroadcastRoundDrawing(
                currentDrawing,
                _currentDrawingIndex + 1,
                Mathf.RoundToInt(guessingDurationSeconds * 1000f),
                GameSessionData.Players
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

            if (HaveAllGuesses(artistId))
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

        // Only players in this game can guess (not phones that joined after Start Match)
        GameSessionData.SessionPlayer guesser = GameSessionData.FindPlayer(playerId);
        if (guesser == null)
            return;

        _currentRoundGuesses[playerId] = guessedColor;

        double deltaE = HueScoring.DeltaE2000(guessedColor, currentDrawing.targetColor);
        guesser.streak = HueScoring.UpdateStreak(guesser.streak, deltaE);
        _currentRoundPoints[playerId] = HueScoring.GuessPoints(
            deltaE, Time.time - _guessingStartedAt, guessingDurationSeconds, guesser.streak);

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

    /// <summary>
    /// True once every connected guesser (everyone but the artist) has guessed. Disconnected
    /// players aren't waited for; if nobody is connected, the timer runs out instead.
    /// </summary>
    private bool HaveAllGuesses(string artistId)
    {
        bool anyoneToWaitFor = false;
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (p.playerId == artistId || !IsWaitingFor(p.playerId))
                continue;

            anyoneToWaitFor = true;
            if (!_currentRoundGuesses.ContainsKey(p.playerId))
                return false;
        }
        return anyoneToWaitFor;
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

        _totalsBeforeDrawing.Clear();
        for (int i = 0; i < GameSessionData.Players.Count; i++)
            _totalsBeforeDrawing[GameSessionData.Players[i].playerId] = GameSessionData.Players[i].totalPoints;

        ScoreCurrentDrawing(currentDrawing);

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            // Each phone's own results first (its results screen needs them), then the phase
            SendResultsToPlayers(currentDrawing);
            HostRelayClient.Instance.BroadcastPhaseState("results", _currentDrawingIndex + 1, Mathf.RoundToInt(revealDurationSeconds * 1000f));
        }

        _advanceRevealRequested = false;

        bool moreDrawings = _currentDrawingIndex + 1 < GameSessionData.Drawings.Count;
        bool showStandings = leaderboardUI != null && (moreDrawings || leaderboardAfterLastDrawing);

        if (revealController != null)
        {
            // Let RevealController + RevealUI take over the full screen for the side-by-side True Hue reveal
            drawingPhaseUI?.SetVisible(false);
            guessingPhaseUI?.SetVisible(false);

            bool hasMoreDrawings = moreDrawings;
            _revealUI?.SetActionButtonText(
                showStandings ? "LEADERBOARD"
                : hasMoreDrawings ? "NEXT DRAWING"
                : leaderboardUI != null ? "SEE WINNERS" : "FINISH GAME");

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

        if (showStandings)
            yield return ShowStandingsRoutine(currentDrawing, moreDrawings);

        _sequenceRoutine = null;
        AdvanceToNextDrawing();
    }

    /// <summary>The leaderboard between drawings; the host's button (or the timer) moves on.</summary>
    private IEnumerator ShowStandingsRoutine(GameSessionData.SubmittedDrawing drawing, bool moreDrawings)
    {
        _advanceRevealRequested = false;
        guessingPhaseUI?.SetVisible(false);

        leaderboardUI.ShowRound(
            BuildStandings(drawing.playerId, withHistory: _currentDrawingIndex > 0),
            _currentDrawingIndex + 1,
            GameSessionData.Drawings.Count,
            drawing.targetColor,
            moreDrawings ? "NEXT DRAWING" : "SEE WINNERS",
            leaderboardDurationSeconds);

        float elapsed = 0f;
        while (elapsed < leaderboardDurationSeconds && !_advanceRevealRequested)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        leaderboardUI.Hide();
    }

    /// <summary>Everyone's standing by totalPoints (ties share a rank), for LeaderboardUI.</summary>
    private List<LeaderboardUI.Entry> BuildStandings(string artistId, bool withHistory)
    {
        List<LeaderboardUI.Entry> entries = new();

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            int previousTotal = _totalsBeforeDrawing.TryGetValue(p.playerId, out int before) ? before : p.totalPoints;

            int previousRank = 0;
            if (withHistory)
            {
                previousRank = 1;
                for (int j = 0; j < GameSessionData.Players.Count; j++)
                {
                    string otherId = GameSessionData.Players[j].playerId;
                    int otherBefore = _totalsBeforeDrawing.TryGetValue(otherId, out int ob)
                        ? ob
                        : GameSessionData.Players[j].totalPoints;
                    if (otherBefore > previousTotal)
                        previousRank++;
                }
            }

            entries.Add(new LeaderboardUI.Entry
            {
                PlayerId = p.playerId,
                Name = string.IsNullOrEmpty(p.name) ? "PLAYER" : p.name,
                Color = p.color,
                Avatar = p.avatar,
                Total = p.totalPoints,
                PreviousTotal = previousTotal,
                RoundPoints = _currentRoundPoints.TryGetValue(p.playerId, out int pts) ? pts : 0,
                Rank = RankBy(p.totalPoints, other => other.totalPoints),
                PreviousRank = previousRank,
                IsArtist = p.playerId == artistId
            });
        }

        return entries;
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

        if (leaderboardUI != null)
        {
            // Crown the winners on the podium (replaces the plain Game Complete card)
            guessingPhaseUI?.SetVisible(false);
            _totalsBeforeDrawing.Clear();
            leaderboardUI.ShowWinners(BuildStandings(null, withHistory: false));
        }
        else
        {
            guessingPhaseUI?.ShowGameFinished(GameSessionData.Drawings.Count);
        }

        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastPhaseState("finished", _currentRound, 0);
        }
    }

    private void HandlePlayAgain()
    {
        // A new game: the podium has crowned this one's winners, so scores start over
        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.Players[i].totalPoints = 0;
            GameSessionData.Players[i].streak = 0;
        }

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

        if (nextPhase != GamePhase.Finished)
            leaderboardUI?.Hide();

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

    private void AssignSecretTargetColorsToPlayers()
    {
        System.Random random = useSeed ? new System.Random(seed) : new System.Random();
        List<Color> targets = GenerateDistinctColors(Mathf.Max(1, GameSessionData.Players.Count), random);

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.Players[i].secretTargetColor = targets[i];
        }
    }

    /// <summary>Gives every player fresh categories to pick from and clears last game's pick.</summary>
    private void DealCategoriesToPlayers()
    {
        System.Random random = useSeed ? new System.Random(seed + 1) : new System.Random();
        List<string[]> hands = CategoryWordBank.Deal(GameSessionData.Players.Count, random);

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.Players[i].categoryOptions = hands[i];
            GameSessionData.Players[i].category = null;
        }
    }

    /// <summary>
    /// A phone picked its category. Only during drawing, only one of the categories that player
    /// was offered, and only their first pick counts.
    /// </summary>
    private void OnRelayCategoryPicked(string playerId, string category)
    {
        if (_currentPhase != GamePhase.Drawing)
            return;

        GameSessionData.SessionPlayer player = GameSessionData.FindPlayer(playerId);
        if (player == null || player.categoryOptions == null || !string.IsNullOrEmpty(player.category))
            return;

        if (Array.IndexOf(player.categoryOptions, category) < 0)
        {
            Debug.LogWarning($"[GameManager] {player.name} picked \"{category}\", which they weren't offered. Ignored.");
            return;
        }

        player.category = category;
    }

    /// <summary>
    /// The prompt a drawing is shown with: the player's picked category; if they were offered
    /// categories but never picked, one of those at random (recorded as their pick); otherwise
    /// (no categories, e.g. the menu's simulated players) whatever the phone sent.
    /// </summary>
    private static string ResolveDrawingPrompt(GameSessionData.SessionPlayer player, string submittedPrompt)
    {
        if (!string.IsNullOrEmpty(player.category))
            return player.category;

        if (player.categoryOptions != null && player.categoryOptions.Length > 0)
        {
            player.category = player.categoryOptions[UnityEngine.Random.Range(0, player.categoryOptions.Length)];
            return player.category;
        }

        return submittedPrompt ?? "";
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

        if (leaderboardUI == null)
            leaderboardUI = FindAnyObjectByType<LeaderboardUI>(FindObjectsInactive.Include);

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
            drawingPhaseUI.ContinueRequested -= HandleDrawingContinueRequested;

            if (subscribe)
                drawingPhaseUI.ContinueRequested += HandleDrawingContinueRequested;
        }

        if (_revealUI != null)
        {
            _revealUI.PlayAgainRequested -= RequestNextDrawing;

            if (subscribe)
                _revealUI.PlayAgainRequested += RequestNextDrawing;
        }

        if (leaderboardUI != null)
        {
            leaderboardUI.ContinueRequested -= RequestNextDrawing;
            leaderboardUI.PlayAgainRequested -= HandlePlayAgain;
            leaderboardUI.BackToMenuRequested -= HandleBackToMenu;

            if (subscribe)
            {
                leaderboardUI.ContinueRequested += RequestNextDrawing;
                leaderboardUI.PlayAgainRequested += HandlePlayAgain;
                leaderboardUI.BackToMenuRequested += HandleBackToMenu;
            }
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
        HostRelayClient.Instance.CategoryPicked -= OnRelayCategoryPicked;
        HostRelayClient.Instance.PlayerRejoined -= OnRelayPlayerRejoined;
        HostRelayClient.Instance.PlayerDisconnected -= OnRelayPlayerDisconnected;
        HostRelayClient.Instance.PlayerLeft -= OnRelayPlayerLeft;
        HostRelayClient.Instance.RoomCreated -= OnRelayRoomReady;

        if (subscribe)
        {
            HostRelayClient.Instance.DrawingSubmitted += OnRelayDrawingSubmitted;
            HostRelayClient.Instance.GuessSubmitted += OnRelayGuessSubmitted;
            HostRelayClient.Instance.CategoryPicked += OnRelayCategoryPicked;
            HostRelayClient.Instance.PlayerRejoined += OnRelayPlayerRejoined;
            HostRelayClient.Instance.PlayerDisconnected += OnRelayPlayerDisconnected;
            HostRelayClient.Instance.PlayerLeft += OnRelayPlayerLeft;
            HostRelayClient.Instance.RoomCreated += OnRelayRoomReady;
        }
    }

    #endregion

    #region Scoring & Results

    /// <summary>
    /// Finishes the current drawing's scores: guessers who didn't guess get 0 (and lose their
    /// streak), the artist gets the average of the guessers' points, and everyone's
    /// totalPoints is updated.
    /// </summary>
    private void ScoreCurrentDrawing(GameSessionData.SubmittedDrawing drawing)
    {
        string artistId = drawing.playerId;
        int sum = 0;
        int guessers = 0;

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (p.playerId == artistId)
                continue;

            if (_currentRoundPoints.TryGetValue(p.playerId, out int points))
            {
                sum += points;
                guessers++;
            }
            else
            {
                p.streak = 0;
                _currentRoundPoints[p.playerId] = 0;
            }
        }

        // The artist earns the average of the players who actually guessed their drawing,
        // so a clear drawing pays off and a troll drawing doesn't
        _currentRoundPoints[artistId] = guessers > 0 ? Mathf.RoundToInt((float)sum / guessers) : 0;

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            if (_currentRoundPoints.TryGetValue(p.playerId, out int points))
                p.totalPoints += points;
        }

        Debug.Log($"[GameManager] Scores for {drawing.playerName}'s drawing: {DescribeScores()}");
    }

    /// <summary>Sends every phone in the game its own results screen for the current drawing.</summary>
    private void SendResultsToPlayers(GameSessionData.SubmittedDrawing drawing)
    {
        HostRelayClient relay = HostRelayClient.Instance;
        int count = GameSessionData.Players.Count;

        for (int i = 0; i < count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            int roundPoints = _currentRoundPoints.TryGetValue(p.playerId, out int pts) ? pts : 0;

            relay.SendResults(
                p.playerId,
                drawing.targetColor,
                roundPoints,
                p.totalPoints,
                RankBy(roundPoints, other => _currentRoundPoints.TryGetValue(other.playerId, out int o) ? o : 0),
                RankBy(p.totalPoints, other => other.totalPoints),
                count);
        }
    }

    /// <summary>1 + how many players scored strictly more (ties share a rank).</summary>
    private static int RankBy(int score, Func<GameSessionData.SessionPlayer, int> scoreOf)
    {
        int rank = 1;
        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            if (scoreOf(GameSessionData.Players[i]) > score)
                rank++;
        }
        return rank;
    }

    private string DescribeScores()
    {
        List<string> parts = new();
        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            int round = _currentRoundPoints.TryGetValue(p.playerId, out int pts) ? pts : 0;
            parts.Add($"{p.name} +{round} (total {p.totalPoints})");
        }
        return string.Join(", ", parts);
    }

    #endregion

    #region Reconnect Resync

    /// <summary>
    /// A phone reconnected mid-game. Its screen may be stale (it missed messages while it
    /// was away), so send it what it needs for the current phase.
    /// </summary>
    private void OnRelayPlayerRejoined(string playerId, string name)
    {
        _disconnectedSince.Remove(playerId);

        HostRelayClient relay = HostRelayClient.Instance;
        GameSessionData.SessionPlayer player = GameSessionData.FindPlayer(playerId);
        if (relay == null || !relay.IsInRoom || player == null)
            return;

        int remainingMs = Mathf.Max(0, Mathf.RoundToInt((_phaseEndsAt - Time.time) * 1000f));

        switch (_currentPhase)
        {
            case GamePhase.Drawing:
                if (!_submittedDrawingPlayerIds.Contains(playerId))
                    relay.SendDrawingPhaseTo(player, _currentRound, remainingMs);
                break;

            case GamePhase.Guessing:
                if (_currentDrawingIndex < GameSessionData.Drawings.Count)
                {
                    GameSessionData.SubmittedDrawing drawing = GameSessionData.Drawings[_currentDrawingIndex];
                    if (drawing.playerId != playerId && !_currentRoundGuesses.ContainsKey(playerId))
                        relay.SendRoundDrawingTo(playerId, drawing, _currentDrawingIndex + 1, remainingMs);
                }
                break;

            case GamePhase.Finished:
                relay.SendPhaseStateTo(playerId, "finished", _currentRound, 0);
                break;
        }
    }

    #endregion

    #region Disconnected Players

    /// <summary>
    /// Whether the current phase should wait for this player. A disconnected phone stops
    /// counting after disconnectedWaitSeconds.
    /// </summary>
    private bool IsWaitingFor(string playerId)
    {
        if (!_disconnectedSince.TryGetValue(playerId, out float since))
            return true;

        return Time.time - since < disconnectedWaitSeconds;
    }

    private void OnRelayPlayerDisconnected(string playerId)
    {
        if (GameSessionData.FindPlayer(playerId) == null || _disconnectedSince.ContainsKey(playerId))
            return;

        _disconnectedSince[playerId] = Time.time;
        Debug.Log($"[GameManager] {playerId} disconnected; not waiting for them after {disconnectedWaitSeconds:0.#} s.");
    }

    private void OnRelayPlayerLeft(string playerId, string reason)
    {
        if (GameSessionData.FindPlayer(playerId) == null)
            return;

        // The seat is gone, so this id can't come back: stop waiting right away
        _disconnectedSince[playerId] = float.NegativeInfinity;
        Debug.Log($"[GameManager] {playerId} left the room ({reason}); no longer waiting for them.");
    }

    /// <summary>Players in this game whose phone is connected right now.</summary>
    private int CountConnectedPlayers()
    {
        int count = 0;
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (!_disconnectedSince.ContainsKey(p.playerId))
                count++;
        }
        return count;
    }

    /// <summary>The host's room came back (resumed): players may have come or gone meanwhile.</summary>
    private void OnRelayRoomReady(string room, string hostKey) => SyncConnectionStatesFromRelay();

    /// <summary>
    /// Rebuilds who's disconnected from the relay's player list. Needed at the start of a game
    /// and after the host's own connection resumes, since neither comes with per-player events.
    /// </summary>
    private void SyncConnectionStatesFromRelay()
    {
        HostRelayClient host = HostRelayClient.Instance;
        RelayClient relay = host != null ? host.Relay : null;
        if (relay == null || !host.IsInRoom)
            return;

        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (!relay.Players.TryGetValue(p.playerId, out RelayPlayer seat))
                _disconnectedSince[p.playerId] = float.NegativeInfinity;
            else if (seat.Connected)
                _disconnectedSince.Remove(p.playerId);
            else if (!_disconnectedSince.ContainsKey(p.playerId))
                _disconnectedSince[p.playerId] = Time.time;
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

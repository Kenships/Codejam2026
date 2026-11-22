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

    [Tooltip("How many players draw each game, picked at random from the connected players when " +
             "the drawing phase starts; everyone else only guesses. Replaced by the Create Game " +
             "screen's setting when the match was started from the menu.")]
    [SerializeField, Min(1)] private int artistCount = 4;

    [Tooltip("Phases stop waiting for a phone this many seconds after it disconnects (it can still " +
             "submit if it comes back in time). Short, so a brief network blip doesn't cost a turn.")]
    [SerializeField, Min(0f)] private float disconnectedWaitSeconds = 3f;

    [Tooltip("Short beat after all players submit drawings before merging icons into the first artist.")]
    [SerializeField, Min(0f)] private float allSubmittedAdvanceDelay = 1.0f;

    [Tooltip("Short beat after the last guess lands, so that guesser's icon pops in around the card " +
             "before every guesser bursts into their colour.")]
    [SerializeField, Min(0f)] private float allGuessedAdvanceDelay = 1.0f;

    [Tooltip("Seconds to hold the True Hue reveal before automatically advancing to the next drawing.")]
    [SerializeField, Min(1f)] private float revealDurationSeconds = 6f;

    [Tooltip("Seconds the \"no guesses\" message stays up before a drawing nobody guessed a colour " +
             "for is skipped.")]
    [SerializeField, Min(0f)] private float skippedDrawingHoldSeconds = 2.5f;

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

    // Piece sizes are derived from these so the screen never gets cluttered: few
    // guessers -> a few big pieces each, many guessers -> smaller pieces, total capped.
    [Tooltip("Most cubes spawned for one merge. Only exceeded when needed to give every " +
             "guessed colour Min Cubes Per Color.")]
    [SerializeField, Min(1)] private int maxCubesOnScreen = 32;

    [Tooltip("Every guessed colour gets at least this many cubes, however many players there are.")]
    [SerializeField, Min(1)] private int minCubesPerColor = 2;

    [Tooltip("Most cubes one colour can get (with few players). Lower = bigger cubes.")]
    [SerializeField, Min(1)] private int maxCubesPerColor = 6;

    [Title("Sequence")]
    [Tooltip("Gap between guessers bursting into their guessed colour (in seat order, clockwise " +
             "from the top-left) when guessing ends. 0 = everyone at once.")]
    [SerializeField, Min(0f)] private float burstStaggerSeconds = 0.06f;

    [Tooltip("How fast (units/s) a guesser's cubes fly out of them when they burst.")]
    [SerializeField, Min(0f)] private float burstSpeed = 12f;

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
    // Players drawing this game (picked in StartDrawingPhase); everyone else only guesses
    private readonly HashSet<string> _artistIds = new();
    private readonly Dictionary<string, Color> _currentRoundGuesses = new();
    // Points each player earned on the current drawing (guessers, and the artist at reveal)
    private readonly Dictionary<string, int> _currentRoundPoints = new();
    // Everyone's totalPoints before the current drawing was scored (for leaderboard movement)
    private readonly Dictionary<string, int> _totalsBeforeDrawing = new();
    // Order guesses arrived in for the current drawing (0 = first), for breaking ties
    private readonly Dictionary<string, int> _guessOrder = new();
    // Standings (player ids, best first, no shared places) after the last scored drawing, and before it
    private readonly List<string> _standingsOrder = new();
    private readonly List<string> _previousStandingsOrder = new();
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
            artistCount = GameSessionData.ArtistCount;
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

        PickArtists();
        List<GameSessionData.SessionPlayer> artists = GetArtists();
        AssignSecretTargetColors(artists);
        DealCategories(artists);

        GameSessionData.ClearDrawings();
        _submittedDrawingPlayerIds.Clear();
        _currentRoundGuesses.Clear();
        _guessOrder.Clear();
        _standingsOrder.Clear();
        _previousStandingsOrder.Clear();
        _currentDrawingIndex = 0;

        SetPhase(GamePhase.Drawing);
        _phaseEndsAt = Time.time + drawingDurationSeconds;

        if (drawingPhaseUI != null)
            drawingPhaseUI.BeginDrawingRound(artists.Count, drawingDurationSeconds, GameSessionData.Players.Count);

        // Only artists get a target; the drawing state reaches everyone, and a phone with no
        // target shows "artists are drawing" until guessing starts
        if (HostRelayClient.Instance != null && HostRelayClient.Instance.IsInRoom)
        {
            HostRelayClient.Instance.BroadcastDrawingPhase(
                _currentRound,
                Mathf.RoundToInt(drawingDurationSeconds * 1000f),
                artists
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
    /// Picks artistCount artists at random from the connected players (all of them when
    /// artistCount is at least the player count).
    /// </summary>
    private void PickArtists()
    {
        _artistIds.Clear();

        List<GameSessionData.SessionPlayer> candidates = GameSessionData.Players.FindAll(
            p => !_disconnectedSince.ContainsKey(p.playerId));

        System.Random random = useSeed ? new System.Random(seed) : new System.Random();
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        int count = Mathf.Min(Mathf.Max(1, artistCount), candidates.Count);
        for (int i = 0; i < count; i++)
            _artistIds.Add(candidates[i].playerId);
    }

    /// <summary>This game's artists, in player order.</summary>
    private List<GameSessionData.SessionPlayer> GetArtists() =>
        GameSessionData.Players.FindAll(p => _artistIds.Contains(p.playerId));

    public bool IsArtist(string playerId) => _artistIds.Contains(playerId);

    /// <summary>
    /// True once every connected artist has submitted a drawing. Disconnected artists aren't
    /// waited for; if none is connected, the timer runs out instead (they may come back).
    /// </summary>
    private bool HaveAllDrawings()
    {
        bool anyoneToWaitFor = false;
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (!_artistIds.Contains(p.playerId) || !IsWaitingFor(p.playerId))
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

        // Only this game's artists draw (a guesser's phone never gets a target, so this is a stray)
        if (_artistIds.Count > 0 && !_artistIds.Contains(playerId))
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
        _guessOrder.Clear();
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
                // Let the last guesser's icon pop in and be seen before everyone bursts
                if (allGuessedAdvanceDelay > 0f)
                    yield return new WaitForSeconds(allGuessedAdvanceDelay);

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
    /// Records a player's colour guess. Its cubes are spawned with everyone else's when the round ends.
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
        _guessOrder[playerId] = _guessOrder.Count;

        // White is what a phone sends without a hue picked: they've had their turn (and take a
        // seat), but it isn't a colour, so it neither scores nor goes into the mix. Scoring then
        // treats them like someone who didn't guess.
        if (!IsWhiteGuess(guessedColor))
        {
            double deltaE = HueScoring.DeltaE2000(guessedColor, currentDrawing.targetColor);
            guesser.streak = HueScoring.UpdateStreak(guesser.streak, deltaE);
            _currentRoundPoints[playerId] = HueScoring.GuessPoints(
                deltaE, Time.time - _guessingStartedAt, guessingDurationSeconds, guesser.streak);
        }

        // They take a seat around the card in their player colour; the guess stays secret until
        // the round ends and it bursts out of them (see BurstGuessesRoutine)
        int expectedGuessers = GetExpectedGuesserCount(currentDrawing.playerId);
        guessingPhaseUI?.MarkPlayerGuessed(guesser);
        guessingPhaseUI?.SetGuessProgress(_currentRoundGuesses.Count, expectedGuessers);
    }

    /// <summary>
    /// Splits totalArea evenly between the colours, partitions each share into pieces
    /// sized so the cube count stays bounded, and spawns them all in one shuffled queue.
    /// </summary>
    private void SpawnPartitionedCubes(List<Color> colors, ColorPartitionService partitionService)
    {
        if (colorPhysics == null || colors.Count == 0)
            return;

        (float minArea, float maxArea) = ColorPartitionService.GetUnitAreaRange(
            colors.Count, totalArea, maxCubesOnScreen, minCubesPerColor, maxCubesPerColor);

        colorPhysics.SpawnQueue(partitionService.CreateSpawnQueue(colors, totalArea, minArea, maxArea));
    }

    /// <summary>
    /// White (or near enough: the phone's default before a hue is picked, and the white end of
    /// its hue bar). Such a guess counts as having guessed but adds no colour and no points.
    /// </summary>
    public static bool IsWhiteGuess(Color color)
    {
        Color.RGBToHSV(color, out _, out float saturation, out float value);
        return saturation <= WhiteGuessMaxSaturation && value >= WhiteGuessMinValue;
    }

    private const float WhiteGuessMaxSaturation = 0.05f;
    private const float WhiteGuessMinValue = 0.95f;

    /// <summary>
    /// Bursts each guesser, in the order they took their seats, into cubes of their guessed
    /// colour from where they sit around the card. Every colour guess gets the same share of
    /// totalArea, split into pieces sized like SpawnPartitionedCubes' so the cube count stays
    /// bounded. A white guess just pops its icon: it adds no colour.
    /// </summary>
    private IEnumerator BurstGuessesRoutine(
        IReadOnlyList<(string playerId, Color guess)> seatOrder,
        ColorPartitionService partitionService)
    {
        if (colorPhysics == null || seatOrder.Count == 0)
            yield break;

        // An icon added this frame (a guess just before the timer ran out) has no layout yet;
        // give UI Toolkit a frame to place it so the burst starts from its seat
        yield return null;

        int count = seatOrder.Count;
        int colorCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (!IsWhiteGuess(seatOrder[i].guess))
                colorCount++;
        }

        (float minArea, float maxArea) = ColorPartitionService.GetUnitAreaRange(
            Mathf.Max(1, colorCount), totalArea, maxCubesOnScreen, minCubesPerColor, maxCubesPerColor);
        float areaPerGuess = totalArea / Mathf.Max(1, colorCount);

        Camera cam = revealController != null ? revealController.RevealCamera : Camera.main;
        float physicsZ = colorPhysics.transform.position.z;
        WaitForSeconds stagger = burstStaggerSeconds > 0f ? new WaitForSeconds(burstStaggerSeconds) : null;

        for (int i = 0; i < count; i++)
        {
            (string playerId, Color guess) = seatOrder[i];

            if (IsWhiteGuess(guess))
            {
                guessingPhaseUI?.BurstGuessedPlayer(playerId);
                if (stagger != null && i < count - 1)
                    yield return stagger;
                continue;
            }

            // No seat on screen (e.g. no guessing UI): burst from the edge of the stage instead
            if (guessingPhaseUI == null ||
                !guessingPhaseUI.TryGetGuessedPlayerWorldPosition(playerId, cam, physicsZ, out Vector3 origin))
            {
                origin = colorPhysics.transform.position + (Vector3)(UnityEngine.Random.insideUnitCircle.normalized * 5f);
            }

            List<(Color color, float radius)> pieces = new();
            foreach (float area in partitionService.PartitionArea(areaPerGuess, minArea, maxArea))
                pieces.Add((guess, ColorPartitionService.AreaToRadius(area)));

            colorPhysics.SpawnBurst(origin, pieces, burstSpeed);
            guessingPhaseUI?.BurstGuessedPlayer(playerId);

            if (stagger != null && i < count - 1)
                yield return stagger;
        }
    }

    /// <summary>This round's guesses in the order their players took their seats.</summary>
    private List<(string playerId, Color guess)> GuessesInSeatOrder()
    {
        List<(string playerId, Color guess)> guesses = new(_currentRoundGuesses.Count);
        foreach (KeyValuePair<string, Color> pair in _currentRoundGuesses)
            guesses.Add((pair.Key, pair.Value));

        guesses.Sort((a, b) => _guessOrder[a.playerId].CompareTo(_guessOrder[b.playerId]));
        return guesses;
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

        // Only real colours go into the mix (white guesses don't)
        List<Color> guessedColors = new();
        foreach (Color guess in _currentRoundGuesses.Values)
        {
            if (!IsWhiteGuess(guess))
                guessedColors.Add(guess);
        }

        // Nobody guessed a colour: skip this drawing (no merge, reveal or scores)
        if (guessedColors.Count == 0)
        {
            yield return SkipDrawingRoutine();
            yield break;
        }

        // Everyone who guessed bursts into cubes of their guessed colour and the artist's card
        // goes away
        guessingPhaseUI?.HideCard();
        yield return BurstGuessesRoutine(GuessesInSeatOrder(), new ColorPartitionService());

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

    /// <summary>
    /// No colour guesses for this drawing: says so on the card (anyone who sent white pops
    /// empty), holds for skippedDrawingHoldSeconds, then moves on. Nobody scores, the artist
    /// included, and no streaks change. The phones move on with the next drawing (or the end).
    /// </summary>
    private IEnumerator SkipDrawingRoutine()
    {
        Debug.Log($"[GameManager] No colour guesses for drawing {_currentDrawingIndex + 1}; skipping it.");

        guessingPhaseUI?.ShowSkipped();

        List<(string playerId, Color guess)> seated = GuessesInSeatOrder();
        WaitForSeconds stagger = burstStaggerSeconds > 0f ? new WaitForSeconds(burstStaggerSeconds) : null;
        for (int i = 0; i < seated.Count; i++)
        {
            guessingPhaseUI?.BurstGuessedPlayer(seated[i].playerId);
            if (stagger != null && i < seated.Count - 1)
                yield return stagger;
        }

        if (skippedDrawingHoldSeconds > 0f)
            yield return new WaitForSeconds(skippedDrawingHoldSeconds);

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

    /// <summary>
    /// Everyone's standing by totalPoints for LeaderboardUI, one place each (ties are split as
    /// in RankPlayers, fixed when the drawing was scored).
    /// </summary>
    private List<LeaderboardUI.Entry> BuildStandings(string artistId, bool withHistory)
    {
        List<LeaderboardUI.Entry> entries = new();

        // Matches _standingsOrder; anyone who joined since lands by score
        List<string> order = Ids(RankPlayers(p => p.totalPoints, null, _standingsOrder));
        // What the last standings showed
        List<string> previousOrder = withHistory
            ? Ids(RankPlayers(PreviousTotalOf, null, _previousStandingsOrder))
            : null;

        for (int i = 0; i < GameSessionData.Players.Count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];
            int previousTotal = PreviousTotalOf(p);
            int previousRank = previousOrder != null ? previousOrder.IndexOf(p.playerId) + 1 : 0;

            entries.Add(new LeaderboardUI.Entry
            {
                PlayerId = p.playerId,
                Name = string.IsNullOrEmpty(p.name) ? "PLAYER" : p.name,
                Color = p.color,
                Avatar = p.avatar,
                Total = p.totalPoints,
                PreviousTotal = previousTotal,
                RoundPoints = _currentRoundPoints.TryGetValue(p.playerId, out int pts) ? pts : 0,
                Rank = order.IndexOf(p.playerId) + 1,
                PreviousRank = previousRank,
                IsArtist = p.playerId == artistId
            });
        }

        return entries;
    }

    private int PreviousTotalOf(GameSessionData.SessionPlayer p) =>
        _totalsBeforeDrawing.TryGetValue(p.playerId, out int before) ? before : p.totalPoints;

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

    /// <summary>Evenly spaced hues across the artists, so no two drawings share a target.</summary>
    private void AssignSecretTargetColors(List<GameSessionData.SessionPlayer> artists)
    {
        System.Random random = useSeed ? new System.Random(seed) : new System.Random();
        List<Color> targets = GenerateDistinctColors(Mathf.Max(1, artists.Count), random);

        for (int i = 0; i < artists.Count; i++)
            artists[i].secretTargetColor = targets[i];
    }

    /// <summary>
    /// Gives this game's artists fresh categories to pick from, including players' lobby
    /// suggestions. Everyone's options and pick from last game are cleared first, so non-artists
    /// have none (and their picks are ignored).
    /// </summary>
    private void DealCategories(List<GameSessionData.SessionPlayer> artists)
    {
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            p.categoryOptions = null;
            p.category = null;
        }

        // Suggestions from players in this game (each goes to exactly one other artist)
        List<CategoryWordBank.Suggestion> suggestions = new();
        foreach (GameSessionData.SessionPlayer p in GameSessionData.Players)
        {
            if (GameSessionData.PromptSuggestions.TryGetValue(p.playerId, out string text))
                suggestions.Add(new CategoryWordBank.Suggestion(p.playerId, text));
        }

        List<string> artistIds = artists.ConvertAll(a => a.playerId);
        System.Random random = useSeed ? new System.Random(seed + 1) : new System.Random();
        List<string[]> hands = CategoryWordBank.Deal(artistIds, suggestions, random, out List<CategoryWordBank.Suggestion> undelivered);

        for (int i = 0; i < artists.Count; i++)
            artists[i].categoryOptions = hands[i];

        foreach (CategoryWordBank.Suggestion s in undelivered)
        {
            GameSessionData.SessionPlayer from = GameSessionData.FindPlayer(s.FromPlayerId);
            Debug.Log($"[GameManager] Couldn't deal {from?.name ?? s.FromPlayerId}'s suggestion \"{s.Text}\": " +
                      "no other artist had room for it this game.");
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
            guessingPhaseUI.NextDrawingRequested -= RequestNextDrawing;
            guessingPhaseUI.PlayAgainRequested -= HandlePlayAgain;
            guessingPhaseUI.BackToMenuRequested -= HandleBackToMenu;

            if (subscribe)
            {
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

        // Fix the new standings while this drawing's artist and guess order can still break ties
        _previousStandingsOrder.Clear();
        _previousStandingsOrder.AddRange(_standingsOrder);
        _standingsOrder.Clear();
        foreach (GameSessionData.SessionPlayer p in RankPlayers(
                     other => other.totalPoints, artistId, _previousStandingsOrder))
            _standingsOrder.Add(p.playerId);

        Debug.Log($"[GameManager] Scores for {drawing.playerName}'s drawing: {DescribeScores()}");
    }

    /// <summary>Sends every phone in the game its own results screen for the current drawing.</summary>
    private void SendResultsToPlayers(GameSessionData.SubmittedDrawing drawing)
    {
        HostRelayClient relay = HostRelayClient.Instance;
        int count = GameSessionData.Players.Count;

        List<string> roundOrder = Ids(RankPlayers(RoundPointsOf, drawing.playerId, null));
        List<string> overallOrder = Ids(RankPlayers(p => p.totalPoints, null, _standingsOrder));

        for (int i = 0; i < count; i++)
        {
            GameSessionData.SessionPlayer p = GameSessionData.Players[i];

            relay.SendResults(
                p.playerId,
                drawing.targetColor,
                RoundPointsOf(p),
                p.totalPoints,
                roundOrder.IndexOf(p.playerId) + 1,
                overallOrder.IndexOf(p.playerId) + 1,
                count);
        }
    }

    private int RoundPointsOf(GameSessionData.SessionPlayer p) =>
        _currentRoundPoints.TryGetValue(p.playerId, out int points) ? points : 0;

    /// <summary>
    /// Every player, best first, with no shared places. Equal scores are split by the current
    /// drawing when <paramref name="artistId"/> is given: its artist first, then guessers in the
    /// order they guessed, then whoever didn't guess. Anyone still level keeps their place in
    /// <paramref name="previousOrder"/> (e.g. the last standings), then player order.
    /// </summary>
    private List<GameSessionData.SessionPlayer> RankPlayers(
        Func<GameSessionData.SessionPlayer, int> scoreOf,
        string artistId,
        IReadOnlyList<string> previousOrder)
    {
        List<GameSessionData.SessionPlayer> ranked = new(GameSessionData.Players);
        Dictionary<string, int> joinOrder = new();
        for (int i = 0; i < GameSessionData.Players.Count; i++)
            joinOrder[GameSessionData.Players[i].playerId] = i;

        int ThisDrawing(GameSessionData.SessionPlayer p)
        {
            if (artistId == null)
                return 0;
            if (p.playerId == artistId)
                return -1;
            return _guessOrder.TryGetValue(p.playerId, out int order) ? order : int.MaxValue;
        }

        int Previously(GameSessionData.SessionPlayer p)
        {
            if (previousOrder == null)
                return 0;
            int index = -1;
            for (int i = 0; i < previousOrder.Count; i++)
            {
                if (previousOrder[i] == p.playerId)
                {
                    index = i;
                    break;
                }
            }
            return index >= 0 ? index : int.MaxValue;
        }

        ranked.Sort((a, b) =>
        {
            int c = scoreOf(b).CompareTo(scoreOf(a));
            if (c == 0) c = ThisDrawing(a).CompareTo(ThisDrawing(b));
            if (c == 0) c = Previously(a).CompareTo(Previously(b));
            if (c == 0) c = joinOrder[a.playerId].CompareTo(joinOrder[b.playerId]);
            return c;
        });

        return ranked;
    }

    private static List<string> Ids(List<GameSessionData.SessionPlayer> players) =>
        players.ConvertAll(p => p.playerId);

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
                if (!_artistIds.Contains(playerId))
                    relay.SendPhaseStateTo(playerId, "drawing", _currentRound, remainingMs); // guesser: wait
                else if (!_submittedDrawingPlayerIds.Contains(playerId))
                    relay.SendDrawingPhaseTo(player, _currentRound, remainingMs);
                break;

            case GamePhase.Guessing:
                if (_currentDrawingIndex < GameSessionData.Drawings.Count)
                {
                    GameSessionData.SubmittedDrawing drawing = GameSessionData.Drawings[_currentDrawingIndex];
                    if (drawing.playerId == playerId)
                        relay.SendPhaseStateTo(playerId, "playing", _currentDrawingIndex + 1, remainingMs);
                    else if (!_currentRoundGuesses.ContainsKey(playerId))
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
    public void GenerateAndSpawn() =>
        GenerateAndSpawn(GameSessionData.Players.Count > 0 ? GameSessionData.Players.Count : colorCount);

    /// <summary>
    /// Same as the end of a guessing round with <paramref name="count"/> guesses: partitions,
    /// spawns (within ColorPhysics' spawn window) and merges. Used by the layout test scene.
    /// </summary>
    public void GenerateAndSpawn(int count)
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

        StopActiveRoutines();
        colorPhysics.gameObject.SetActive(true);
        colorPhysics.ClearAllBodies();

        List<Color> colors = GenerateDistinctColors(Mathf.Max(1, count), random);

        ColorPartitionService partitionService = new(random.Next());

        if (_sequenceRoutine != null)
            StopCoroutine(_sequenceRoutine);

        _sequenceRoutine = StartCoroutine(SpawnThenMergeRoutine(colors, partitionService));
    }

    /// <summary>
    /// Same as the end of a guessing round: hides the artist's card, bursts each seated
    /// guesser (in the given seat order) into their guessed colour, and merges. Used by the
    /// layout test scene after seating fake guessers with GuessingPhaseController.MarkPlayerGuessed.
    /// </summary>
    public void BurstAndMerge(IReadOnlyList<(string playerId, Color guess)> seatOrder)
    {
        if (!Application.isPlaying || colorPhysics == null || seatOrder == null)
            return;

        StopActiveRoutines();
        colorPhysics.gameObject.SetActive(true);
        colorPhysics.ClearAllBodies();
        guessingPhaseUI?.HideCard();

        ColorPartitionService partitionService = useSeed ? new ColorPartitionService(seed) : new ColorPartitionService();
        _sequenceRoutine = StartCoroutine(BurstThenMergeRoutine(seatOrder, partitionService));
    }

    private IEnumerator BurstThenMergeRoutine(
        IReadOnlyList<(string playerId, Color guess)> seatOrder,
        ColorPartitionService partitionService)
    {
        yield return BurstGuessesRoutine(seatOrder, partitionService);
        yield return MergeRoutine();
    }

    private IEnumerator SpawnThenMergeRoutine(List<Color> colors, ColorPartitionService partitionService)
    {
        SpawnPartitionedCubes(colors, partitionService);
        yield return MergeRoutine();
    }

    private IEnumerator MergeRoutine()
    {
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

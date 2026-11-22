using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Codejam.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Layout test bench (Assets/Scenes/LayoutTest.unity, built by TrueHue > Build Layout Test Scene).
/// Fills every screen with fake players so you can check how it looks at any player count
/// without phones or the relay: the lobby rings, the drawing-phase icon rings, the artist
/// transition, the guessing screen, the end-of-round cube spawn, the standings and the podium.
///
/// Use the panel in the Game view (or the buttons in this component's inspector). The harness
/// survives scene loads, so "Lobby" opens the real Menu scene and "Back to game screens"
/// returns here. Nothing is sent anywhere: the relay is switched off in the Menu scene.
/// </summary>
public class LayoutTestHarness : MonoBehaviour
{
    private static LayoutTestHarness _instance;

    [Title("Fake Players")]
    [SerializeField, Range(GameSessionData.MinPlayersToStart, GameSessionData.MaxPlayersLimit)]
    private int playerCount = GameSessionData.MaxPlayersLimit;

    [Tooltip("Players drawing this game (capped at the player count).")]
    [SerializeField, Range(1, GameSessionData.MaxPlayersLimit)]
    private int artistCount = 8;

    [Tooltip("Give everyone a scribbled profile picture (off = coloured cubes with initials).")]
    [SerializeField] private bool fakeAvatars = true;

    [Tooltip("Every Nth player gets a maximum-length (16 character) name, to catch overflow.")]
    [SerializeField, Min(1)] private int longNameEvery = 4;

    [Tooltip("Seconds between fake players joining / submitting, so the animations play.")]
    [SerializeField, Min(0f)] private float stagger = 0.08f;

    [Title("Panel")]
    [SerializeField] private bool showPanel = true;

    private const string MenuSceneName = "Menu";
    private const int NameMaxLength = 16;

    private static readonly string[] ShortNames =
    {
        "ALEX", "SAM", "PRISM", "JO", "RIVER", "KAI", "NOVA", "MAX", "IVY", "REN", "ZOE", "LEO",
        "MIA", "ODIN", "PIXEL", "QUINN", "ROSE", "SKY", "TESS", "UMA", "VIC", "WREN", "XAN", "YUKI"
    };

    private readonly List<GameSessionData.SessionPlayer> _players = new();
    private readonly List<Texture2D> _ownedTextures = new();
    private string _testScenePath;
    private Coroutine _routine;
    private bool _panelExpanded = true;
    private Vector2 _panelScroll;

    #region Lifecycle

    private void Awake()
    {
        // Returning to the test scene loads a second copy of this object
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        _testScenePath = gameObject.scene.path;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (_instance != this)
            return;

        _instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        foreach (Texture2D tex in _ownedTextures)
        {
            if (tex != null)
                Destroy(tex);
        }
    }

    // Runs after the scene's Awake/OnEnable but before Start, so the relay never connects
    // (connecting creates a real room, and the lobby would then drop the fake players)
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        foreach (RelayClient relay in FindObjectsByType<RelayClient>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            relay.gameObject.SetActive(false);
    }

    #endregion

    #region Actions

    [Button]
    public void ShowLobby()
    {
        StopRoutine();
        if (SceneManager.GetActiveScene().name != MenuSceneName)
        {
            SceneManager.LoadScene(MenuSceneName);
            _routine = StartCoroutine(AfterSceneLoad(FillLobby()));
        }
        else
        {
            _routine = StartCoroutine(FillLobby());
        }
    }

    [Button]
    public void BackToGameScreens()
    {
        StopRoutine();
        if (IsInTestScene())
            return;

#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            _testScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(_testScenePath);
#endif
    }

    [Button]
    public void ShowDrawingPhase() => RunInTestScene(DrawingPhase());

    [Button]
    public void PlayArtistTransition() => RunInTestScene(ArtistTransition());

    [Button]
    public void ShowGuessingAndCubes() => RunInTestScene(GuessingAndCubes());

    [Button]
    public void ShowStandings() => RunInTestScene(Standings());

    [Button]
    public void ShowWinners() => RunInTestScene(Winners());

    [Button]
    public void HideGameScreens()
    {
        StopRoutine();
        HideAllGameScreens();
    }

    #endregion

    #region Screens

    private IEnumerator FillLobby()
    {
        MenuController menu = FindAnyObjectByType<MenuController>();
        if (menu == null)
        {
            Debug.LogWarning("[LayoutTest] No MenuController in the Menu scene.");
            yield break;
        }

        BuildPlayers();
        GameSessionData.MaxPlayers = GameSessionData.MaxPlayersLimit;
        GameSessionData.ArtistCount = artistCount;

        // An empty list clears whoever was seated (without CloseLobby's events)
        menu.OpenLobby("TEST", new List<MenuController.LobbyPlayer>());

        foreach (GameSessionData.SessionPlayer p in _players)
        {
            menu.AddOrUpdatePlayer(p.playerId, p.name, p.color, connected: true, p.avatar);
            if (stagger > 0f)
                yield return new WaitForSeconds(stagger);
        }

        _routine = null;
    }

    private IEnumerator DrawingPhase()
    {
        DrawingPhaseController drawing = Find<DrawingPhaseController>();
        if (drawing == null)
            yield break;

        List<GameSessionData.SessionPlayer> artists = Artists();

        HideAllGameScreens();
        drawing.BeginDrawingRound(artists.Count, 60f, _players.Count);

        foreach (GameSessionData.SessionPlayer artist in artists)
        {
            if (stagger > 0f)
                yield return new WaitForSeconds(stagger);
            drawing.MarkPlayerSubmitted(artist);
        }

        _routine = null;
    }

    private IEnumerator ArtistTransition()
    {
        DrawingPhaseController drawing = Find<DrawingPhaseController>();
        if (drawing == null)
            yield break;

        // The icons merge from wherever they are, so lay out a full ring first
        yield return DrawingPhase();
        yield return new WaitForSeconds(0.6f);

        List<GameSessionData.SessionPlayer> artists = Artists();
        bool done = false;
        drawing.PlayArtistMergeTransition(_players, artists[0], 1, artists.Count, () => done = true);
        while (!done)
            yield return null;

        _routine = null;
    }

    private IEnumerator GuessingAndCubes()
    {
        GuessingPhaseController guessing = Find<GuessingPhaseController>();
        GameManager game = FindAnyObjectByType<GameManager>();
        if (guessing == null || game == null)
            yield break;

        List<GameSessionData.SessionPlayer> artists = Artists();
        GameSessionData.SessionPlayer artist = artists[0];
        int guessers = _players.Count - 1;

        HideAllGameScreens();

        GameSessionData.SubmittedDrawing drawingRecord = new()
        {
            playerId = artist.playerId,
            playerName = artist.name,
            playerColor = artist.color,
            targetColor = artist.secretTargetColor,
            prompt = "Food",
            texture = MakeScribble(512, artist.playerId.GetHashCode())
        };

        guessing.SetVisible(true);
        guessing.BeginGuessingRound(artist, drawingRecord, 0, artists.Count, guessers, 30f);

        // Guesses trickle in (each guesser takes a seat around the card), then the round ends:
        // every guesser bursts into cubes of their guess and they all merge
        List<(string playerId, Color guess)> guesses = new();
        foreach (GameSessionData.SessionPlayer p in _players)
        {
            if (p.playerId == artist.playerId)
                continue;

            guesses.Add((p.playerId, Color.HSVToRGB(Random.value, 1f, 1f)));
            guessing.MarkPlayerGuessed(p);
            guessing.SetGuessProgress(guesses.Count, guessers);
            if (stagger > 0f)
                yield return new WaitForSeconds(stagger);
        }

        guessing.ShowMergingState();
        game.BurstAndMerge(guesses);
        _routine = null;
    }

    private IEnumerator Standings()
    {
        LeaderboardUI board = Find<LeaderboardUI>();
        if (board == null)
            yield break;

        HideAllGameScreens();

        List<GameSessionData.SessionPlayer> artists = Artists();
        GameSessionData.SessionPlayer artist = artists[0];
        List<LeaderboardUI.Entry> entries = BuildEntries(withHistory: true, artist.playerId);

        board.ShowRound(entries, 2, artists.Count, artist.secretTargetColor, "NEXT DRAWING", 8f);
        _routine = null;
    }

    private IEnumerator Winners()
    {
        LeaderboardUI board = Find<LeaderboardUI>();
        if (board == null)
            yield break;

        HideAllGameScreens();
        board.ShowWinners(BuildEntries(withHistory: false, artistId: null));
        _routine = null;
    }

    #endregion

    #region Fake Data

    private void BuildPlayers()
    {
        // Every screen is rebuilt from scratch on each action, so last action's textures can go
        foreach (Texture2D tex in _ownedTextures)
        {
            if (tex != null)
                Destroy(tex);
        }
        _ownedTextures.Clear();
        _players.Clear();

        for (int i = 0; i < playerCount; i++)
        {
            string name = longNameEvery > 0 && i % longNameEvery == longNameEvery - 1
                ? $"LONGNAMEPLAYER{i:00}"
                : $"{ShortNames[i % ShortNames.Length]}{(i >= ShortNames.Length ? (i / ShortNames.Length + 1).ToString() : "")}";

            if (name.Length > NameMaxLength)
                name = name.Substring(0, NameMaxLength);

            _players.Add(new GameSessionData.SessionPlayer
            {
                playerId = $"test_{i:00}",
                name = name,
                color = Color.HSVToRGB(Mathf.Repeat(i * 0.61803398875f, 1f), 0.88f, 0.98f),
                secretTargetColor = Color.HSVToRGB(Mathf.Repeat(i * 0.37f + 0.1f, 1f), 1f, 1f),
                avatar = fakeAvatars ? MakeScribble(96, i * 7919) : null
            });
        }

        // GameScene screens read the session (e.g. the artist transition's icon ring)
        GameSessionData.Players.Clear();
        GameSessionData.Players.AddRange(_players);
    }

    private List<GameSessionData.SessionPlayer> Artists()
    {
        if (_players.Count != playerCount)
            BuildPlayers();

        int count = Mathf.Clamp(artistCount, 1, _players.Count);
        List<GameSessionData.SessionPlayer> artists = new();
        // Spread through the list so artists aren't just the first few
        for (int i = 0; i < count; i++)
            artists.Add(_players[i * _players.Count / count]);
        return artists;
    }

    /// <summary>Plausible scores with ties, so ranks, movement arrows and "+N MORE" all show.</summary>
    private List<LeaderboardUI.Entry> BuildEntries(bool withHistory, string artistId)
    {
        if (_players.Count != playerCount)
            BuildPlayers();

        System.Random random = new(1234 + _players.Count);
        List<(GameSessionData.SessionPlayer p, int before, int gain)> scores = new();
        foreach (GameSessionData.SessionPlayer p in _players)
        {
            int before = withHistory ? random.Next(0, 40) * 50 : 0;
            int gain = random.Next(0, 21) * 50;
            scores.Add((p, before, gain));
        }

        Dictionary<string, int> rankNow = Ranks(scores, s => s.before + s.gain);
        Dictionary<string, int> rankBefore = Ranks(scores, s => s.before);

        List<LeaderboardUI.Entry> entries = new();
        foreach ((GameSessionData.SessionPlayer p, int before, int gain) in scores)
        {
            entries.Add(new LeaderboardUI.Entry
            {
                PlayerId = p.playerId,
                Name = p.name,
                Color = p.color,
                Avatar = p.avatar,
                Total = before + gain,
                PreviousTotal = before,
                RoundPoints = gain,
                Rank = rankNow[p.playerId],
                PreviousRank = withHistory ? rankBefore[p.playerId] : 0,
                IsArtist = p.playerId == artistId
            });
        }

        return entries;
    }

    private static Dictionary<string, int> Ranks(
        List<(GameSessionData.SessionPlayer p, int before, int gain)> scores,
        System.Func<(GameSessionData.SessionPlayer p, int before, int gain), int> score)
    {
        // One place each, like GameManager: equal scores keep player order
        List<int> order = new();
        for (int i = 0; i < scores.Count; i++)
            order.Add(i);
        order.Sort((a, b) =>
        {
            int byScore = score(scores[b]).CompareTo(score(scores[a]));
            return byScore != 0 ? byScore : a.CompareTo(b);
        });

        Dictionary<string, int> ranks = new();
        for (int i = 0; i < order.Count; i++)
            ranks[scores[order[i]].p.playerId] = i + 1;
        return ranks;
    }

    /// <summary>White strokes on black, like a phone drawing or profile picture.</summary>
    private Texture2D MakeScribble(int size, int seed)
    {
        System.Random random = new(seed);
        Color32[] pixels = new Color32[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(0, 0, 0, 255);

        float brush = size * 0.035f;
        for (int stroke = 0; stroke < 4; stroke++)
        {
            Vector2 pos = new(random.Next(size / 5, size * 4 / 5), random.Next(size / 5, size * 4 / 5));
            for (int step = 0; step < 40; step++)
            {
                Vector2 next = pos + new Vector2(random.Next(-size / 12, size / 12 + 1), random.Next(-size / 12, size / 12 + 1));
                next = new Vector2(Mathf.Clamp(next.x, brush, size - brush), Mathf.Clamp(next.y, brush, size - brush));
                StampLine(pixels, size, pos, next, brush);
                pos = next;
            }
        }

        Texture2D tex = new(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        _ownedTextures.Add(tex);
        return tex;
    }

    private static void StampLine(Color32[] pixels, int size, Vector2 a, Vector2 b, float radius)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(1f, radius * 0.5f)));
        int r = Mathf.CeilToInt(radius);
        for (int s = 0; s <= steps; s++)
        {
            Vector2 c = Vector2.Lerp(a, b, s / (float)steps);
            for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
            {
                if (x * x + y * y > radius * radius)
                    continue;
                int px = Mathf.RoundToInt(c.x) + x, py = Mathf.RoundToInt(c.y) + y;
                if (px >= 0 && py >= 0 && px < size && py < size)
                    pixels[py * size + px] = new Color32(255, 255, 255, 255);
            }
        }
    }

    #endregion

    #region Helpers

    private bool IsInTestScene() => SceneManager.GetActiveScene().path == _testScenePath;

    private void RunInTestScene(IEnumerator screen)
    {
        StopRoutine();
        BuildPlayers(); // picks up any slider/toggle changes

        if (IsInTestScene())
        {
            _routine = StartCoroutine(screen);
            return;
        }

        BackToGameScreens();
        _routine = StartCoroutine(AfterSceneLoad(screen));
    }

    private IEnumerator AfterSceneLoad(IEnumerator then)
    {
        // Scene loads land next frame; give Start() a frame too
        yield return null;
        yield return null;
        yield return then;
    }

    private void HideAllGameScreens()
    {
        Find<DrawingPhaseController>(warn: false)?.SetVisible(false);
        Find<GuessingPhaseController>(warn: false)?.SetVisible(false);
        Find<LeaderboardUI>(warn: false)?.Hide();
        FindAnyObjectByType<RevealController>()?.Clear();

        // Only the guessing screen shows the cube stage (GameManager.GenerateAndSpawn turns it on)
        ColorPhysics physics = FindAnyObjectByType<ColorPhysics>(FindObjectsInactive.Include);
        if (physics != null)
        {
            physics.ClearAllBodies();
            physics.gameObject.SetActive(false);
        }
    }

    private static T Find<T>(bool warn = true) where T : Object
    {
        T found = FindAnyObjectByType<T>(FindObjectsInactive.Include);
        if (found == null && warn)
            Debug.LogWarning($"[LayoutTest] No {typeof(T).Name} in this scene. Use 'Back to game screens' first.");
        return found;
    }

    private void StopRoutine()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }
    }

    #endregion

    #region Panel

    private void OnGUI()
    {
        if (!showPanel)
            return;

        const float width = 230f;
        GUILayout.BeginArea(new Rect(10f, 10f, width, Screen.height - 20f));

        if (GUILayout.Button(_panelExpanded ? "Layout test  [-]" : "Layout test  [+]"))
            _panelExpanded = !_panelExpanded;

        if (_panelExpanded)
        {
            _panelScroll = GUILayout.BeginScrollView(_panelScroll, GUI.skin.box);

            GUILayout.Label($"Players: {playerCount}");
            playerCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                playerCount, GameSessionData.MinPlayersToStart, GameSessionData.MaxPlayersLimit));

            GUILayout.Label($"Artists: {Mathf.Min(artistCount, playerCount)}");
            artistCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(artistCount, 1, GameSessionData.MaxPlayersLimit));

            fakeAvatars = GUILayout.Toggle(fakeAvatars, " Fake profile pictures");
            GUILayout.Space(6f);

            if (GUILayout.Button("Lobby (Menu scene)")) ShowLobby();
            if (GUILayout.Button("Back to game screens")) BackToGameScreens();
            GUILayout.Space(6f);
            if (GUILayout.Button("Drawing phase")) ShowDrawingPhase();
            if (GUILayout.Button("Artist transition")) PlayArtistTransition();
            if (GUILayout.Button("Guessing + cubes")) ShowGuessingAndCubes();
            if (GUILayout.Button("Standings")) ShowStandings();
            if (GUILayout.Button("Winners")) ShowWinners();
            if (GUILayout.Button("Hide all")) HideGameScreens();

            GUILayout.EndScrollView();
        }

        GUILayout.EndArea();
    }

    #endregion
}

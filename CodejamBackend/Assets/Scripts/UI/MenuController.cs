using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using Codejam.Networking;
using Core.AudioSystem;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Drives the Console Host Menu & Lobby UI:
/// - Create Game screen: minimal console card to open a new game room.
/// - Host Lobby screen: compact center join card displaying the 4-letter Room Code,
///   while joined players populate the space around the join screen in a clockwise circle
///   from top-left as merged ColorBody character cubes in the colour they picked (shooting
///   out small burst cubes on creation, with their name underneath and optional PFP on the cube).
/// - All UI elements dynamically take the colour picked on the HueBar.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class MenuController : MonoBehaviour
{
    public const int NameMaxLength = 16;
    public const int RoomCodeLength = 4;
    /// <summary>Seats in this game, picked on the Create Game screen (up to 12).</summary>
    public static int MaxPlayers => GameSessionData.MaxPlayers;

    /// <summary>Drawing / guessing time step on the Create Game screen.</summary>
    private const int PhaseSecondsStep = 10;
    private const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ";

    // Tailwind red-400
    private static readonly Color ErrorColor = new(0.973f, 0.443f, 0.443f);

    [Serializable]
    public class LobbyPlayer
    {
        public string playerId;
        public string name;
        public Color color = Color.cyan;
        public bool connected = true;
        public Texture2D avatar;
        public bool ownsAvatarTexture;
    }

    [SerializeField, Required] private UIDocument document;
    [SerializeField] private MenuColorStage colorStage;

    [Title("Lobby Settings")]
    [Tooltip("When true, clicking Create Game immediately opens the Host Lobby with a generated room code if no network listener calls SetCreating(true) or OpenLobby().")]
    [SerializeField] private bool autoOpenLobbyOnCreate = true;

    [Tooltip("Automatically load GameScene when Start Match is pressed.")]
    [SerializeField] private bool loadGameSceneOnStart = true;

    [SerializeField] private string gameSceneName = "GameScene";

    [Title("Relay")]
    [Tooltip("Take the room code and players from the RelayClient (the persistent \"Relay\" object). Without one, Create Game falls back to a local code for offline testing.")]
    [SerializeField] private bool useRelay = true;

    [Title("Events")]
    [SerializeField] private UnityEvent onCreateGameRequested;
    [SerializeField] private UnityEvent<string> onStartMatchRequested;
    [SerializeField] private UnityEvent onCloseLobbyRequested;

    [HideInInspector, SerializeField] private UnityEvent<string, string> onJoinRequested;

    public event Action CreateGameRequested;
    public event Action<string> StartMatchRequested;
    public event Action CloseLobbyRequested;
    public event Action<string, string> JoinRequested;

    private VisualElement _root;
    private VisualElement _column;
    private VisualElement _characterLabelsLayer;
    private PixelBlastBackground _background;
    private TrueHueTitle _title;
    private HueBar _hueBar;

    // Create Game view elements
    private VisualElement _createCard;
    private Label _createError;
    private Button _createButton;

    // Create Game settings steppers
    private Button _maxPlayersDown;
    private Button _maxPlayersUp;
    private Label _maxPlayersValue;
    private Button _drawingTimeDown;
    private Button _drawingTimeUp;
    private Label _drawingTimeValue;
    private Button _guessingTimeDown;
    private Button _guessingTimeUp;
    private Label _guessingTimeValue;

    // Host Lobby view elements (lobby card + QR panel side by side, join site below)
    private VisualElement _lobbyView;
    private Label _roomCodeDisplay;
    private Label _lobbyTitle;
    private Label _lobbyDescription;
    private Label _playerCountLabel;
    private Label _lobbyError;
    private Button _backButton;
    private Button _startMatchButton;
    private VisualElement _roomQrBox;
    private VisualElement _roomQr;     // Rectangle 16 plate, tinted the picked hue
    private VisualElement _roomQrCode; // white QR mask on top, tinted black or white to read on the plate
    private VisualElement _roomJoinLink;
    private Label _roomJoinSite;

    // Join QR code, rebuilt only when the join link changes
    private Texture2D _roomQrTexture;
    private string _roomQrUrl;

    private readonly List<LobbyPlayer> _players = new();
    private readonly Dictionary<string, Label> _playerNameLabels = new();
    private readonly HashSet<Button> _hoveredButtons = new();
    private readonly HashSet<Button> _focusedButtons = new();

    private Color _hue = Color.white;

    // QR plate / ink colours as currently shown, eased toward the picked hue each frame.
    // Time constants: ~95% of the way there after 3x (so ~0.35s for the plate)
    private const float QrPlateFadeTime = 0.12f;
    private const float QrInkFadeTime = 0.05f;
    private Color _qrPlateShown = Color.white;
    private Color _qrInkShown = Color.black;
    private bool _qrColorsShown;

    private bool _creating;
    private bool _startingMatch;
    private bool _inLobby;
    private string _currentRoomCode = "";
    private int _nextColorIndex;

    private RelayClient _relay;
    private PlayerAvatars _avatars;

    /// <summary>The colour currently picked on the hue bar (white if none).</summary>
    public Color SelectedColor => _hue;

    public bool IsCreating => _creating;
    public bool IsJoining => _creating;
    public bool IsInLobby => _inLobby;
    public string CurrentRoomCode => _currentRoomCode;
    public IReadOnlyList<LobbyPlayer> Players => _players;

    private void Reset()
    {
        document = GetComponent<UIDocument>();
        colorStage = GetComponent<MenuColorStage>() ?? FindAnyObjectByType<MenuColorStage>();
    }

    private void OnEnable()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        if (colorStage == null)
            colorStage = GetComponent<MenuColorStage>() ?? FindAnyObjectByType<MenuColorStage>();

        if (colorStage == null && Application.isPlaying)
            colorStage = gameObject.AddComponent<MenuColorStage>();

        _root = document.rootVisualElement;

        if (_root == null)
            return;

        _column = _root.Q<VisualElement>("column");
        _characterLabelsLayer = _root.Q<VisualElement>("character-labels-layer");
        _background = _root.Q<PixelBlastBackground>();
        _title = _root.Q<TrueHueTitle>("title");
        _hueBar = _root.Q<HueBar>();

        _createCard = _root.Q<VisualElement>("card");
        _createError = _root.Q<Label>("error");
        _createButton = _root.Q<Button>("create-game") ?? _root.Q<Button>("join");

        _maxPlayersDown = _root.Q<Button>("max-players-down");
        _maxPlayersUp = _root.Q<Button>("max-players-up");
        _maxPlayersValue = _root.Q<Label>("max-players-value");
        _drawingTimeDown = _root.Q<Button>("drawing-time-down");
        _drawingTimeUp = _root.Q<Button>("drawing-time-up");
        _drawingTimeValue = _root.Q<Label>("drawing-time-value");
        _guessingTimeDown = _root.Q<Button>("guessing-time-down");
        _guessingTimeUp = _root.Q<Button>("guessing-time-up");
        _guessingTimeValue = _root.Q<Label>("guessing-time-value");

        _lobbyView = _root.Q<VisualElement>("lobby-view");
        _roomCodeDisplay = _root.Q<Label>("room-code-display");
        _lobbyTitle = _root.Q<Label>("lobby-title");
        _lobbyDescription = _root.Q<Label>("lobby-description");
        _playerCountLabel = _root.Q<Label>("player-count");
        _lobbyError = _root.Q<Label>("lobby-error");
        _backButton = _root.Q<Button>("back-button");
        _startMatchButton = _root.Q<Button>("start-match");
        _roomQrBox = _root.Q<VisualElement>("room-qr-box");
        _roomQr = _root.Q<VisualElement>("room-qr");
        _roomQrCode = _root.Q<VisualElement>("room-qr-code");
        _roomJoinLink = _root.Q<VisualElement>("room-join-link");
        _roomJoinSite = _root.Q<Label>("room-join-site");
        _roomQrUrl = null; // fresh elements: apply the QR again on the next refresh

        if (_hueBar != null)
            _hueBar.ValueChanged += OnHueChanged;

        RegisterButton(_createButton, CreateGame);
        RegisterButton(_backButton, CloseLobby);
        RegisterButton(_startMatchButton, StartMatch);
        RegisterSettingButtons(register: true);

        _root.RegisterCallback<KeyDownEvent>(OnRootKeyDown);

        _inLobby = false;
        _creating = false;
        _startingMatch = false;
        _currentRoomCode = "";
        _hoveredButtons.Clear();
        _focusedButtons.Clear();

        ClearPlayers();
        ShowError(null);
        SetCreating(false);
        SetView(inLobby: false);
        RefreshLobbyUI();
        ApplyHue(_hueBar != null ? _hueBar.Color : Color.white);

        BindRelay();
        RefreshSettingsUI();
    }

    // The Relay object may wake after this one, so try again once every Awake has run
    private void Start() => BindRelay();

    private void OnDisable()
    {
        UnbindRelay();

        if (_hueBar != null)
            _hueBar.ValueChanged -= OnHueChanged;

        UnregisterButton(_createButton, CreateGame);
        UnregisterButton(_backButton, CloseLobby);
        UnregisterButton(_startMatchButton, StartMatch);
        RegisterSettingButtons(register: false);

        _root?.UnregisterCallback<KeyDownEvent>(OnRootKeyDown);

        _hoveredButtons.Clear();
        _focusedButtons.Clear();
    }

    private void OnDestroy()
    {
        ReleaseOwnedAvatars();
        ReleaseJoinQr();
    }

    private void LateUpdate()
    {
        if (_root == null || _root.panel == null)
            return;

        SyncCenterCardWorldBoundsToStage();
        SyncCharacterLabelsToWorldPositions();
        UpdateQrColors();
    }

    /// <summary>
    /// Eases the QR plate toward the picked hue (around the colour wheel, so red > blue passes
    /// through magenta rather than grey), and the ink toward whichever of black/white reads on
    /// the plate as currently shown, so it flips exactly when the plate crosses over.
    /// </summary>
    private void UpdateQrColors()
    {
        if (_roomQr == null && _roomQrCode == null)
            return;

        if (!_qrColorsShown)
        {
            // First frame: start on the right colours instead of fading in from white
            _qrPlateShown = _hue;
            _qrInkShown = QrCode.InkFor(_hue);
            _qrColorsShown = true;
        }
        else
        {
            float dt = Time.unscaledDeltaTime;
            _qrPlateShown = LerpAroundHue(_qrPlateShown, _hue, 1f - Mathf.Exp(-dt / QrPlateFadeTime));
            _qrInkShown = Color.Lerp(_qrInkShown, QrCode.InkFor(_qrPlateShown), 1f - Mathf.Exp(-dt / QrInkFadeTime));
        }

        if (_roomQr != null)
            _roomQr.style.unityBackgroundImageTintColor = _qrPlateShown;
        if (_roomQrCode != null)
            _roomQrCode.style.unityBackgroundImageTintColor = _qrInkShown;
    }

    /// <summary>Lerps in HSV, taking the short way round the hue wheel.</summary>
    private static Color LerpAroundHue(Color from, Color to, float t)
    {
        Color.RGBToHSV(from, out float h0, out float s0, out float v0);
        Color.RGBToHSV(to, out float h1, out float s1, out float v1);

        // White/greys have no real hue: borrow the other end's so they fade in place
        // instead of sweeping in from red
        if (s0 < 0.01f)
            h0 = h1;
        if (s1 < 0.01f)
            h1 = h0;

        float h = h0 + Mathf.DeltaAngle(h0 * 360f, h1 * 360f) / 360f * t;
        return Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Lerp(s0, s1, t), Mathf.Lerp(v0, v1, t));
    }

    #region Create Game & Lobby Flow

    /// <summary>Requests room creation (and opens the Host Lobby if autoOpenLobbyOnCreate is enabled).</summary>
    public void CreateGame()
    {
        if (_creating || _inLobby)
            return;

        ShowError(null);
        PulseBackgroundRipple(_createButton);

        bool handledExternally = false;
        SetCreating(true);

        CreateGameRequested?.Invoke();
        onCreateGameRequested?.Invoke();

        if (_inLobby || !_creating)
            handledExternally = true;

        if (handledExternally)
            return;

        if (_relay != null)
        {
            // The relay connects on its own at startup; if the room isn't ready yet,
            // stay on "CREATING ROOM..." until RoomReady opens the lobby
            if (_relay.Status == RelayStatus.InRoom && !string.IsNullOrEmpty(_relay.RoomCode))
                OpenRelayLobby();
            else if (_relay.Status == RelayStatus.Disconnected)
                _relay.Connect();
        }
        else if (autoOpenLobbyOnCreate)
        {
            OpenLobby(GenerateRoomCode());
        }
    }

    /// <summary>Backwards-compatible alias for CreateGame.</summary>
    public void Submit() => CreateGame();

    /// <summary>Disables the Create Game button and shows "CREATING ROOM..." while true.</summary>
    public void SetCreating(bool creating)
    {
        _creating = creating;

        if (_createButton != null)
        {
            _createButton.SetEnabled(!creating);
            _createButton.text = creating ? "CREATING ROOM..." : "CREATE GAME";
            _createButton.style.opacity = creating ? 0.6f : 1f;
        }

        RefreshAllButtons();
    }

    public void SetJoining(bool joining) => SetCreating(joining);

    /// <summary>Transitions to the Host Lobby screen for the given 4-letter room code.</summary>
    public void OpenLobby(string roomCode, IEnumerable<LobbyPlayer> initialPlayers = null)
    {
        _creating = false;
        _startingMatch = false;
        _currentRoomCode = string.IsNullOrWhiteSpace(roomCode)
            ? GenerateRoomCode()
            : roomCode.Trim().ToUpperInvariant();

        SetCreating(false);
        ShowError(null);
        SetView(inLobby: true);

        if (initialPlayers != null)
        {
            ClearPlayers();
            foreach (LobbyPlayer player in initialPlayers)
            {
                if (player != null && _players.Count < MaxPlayers)
                {
                    AddOrUpdatePlayer(
                        player.playerId,
                        player.name,
                        player.color,
                        player.connected,
                        player.avatar,
                        player.ownsAvatarTexture
                    );
                }
            }
        }

        RefreshLobbyUI();
        ApplyHue(_hue);
    }

    /// <summary>
    /// Closes the current room lobby, despawns player character cubes, and returns to the Create Game card.
    /// With the relay, the room and its players stay; Create Game reopens the same lobby.
    /// </summary>
    public void CloseLobby()
    {
        if (!_inLobby)
            return;

        PulseBackgroundRipple(_backButton);

        _inLobby = false;
        _startingMatch = false;
        _currentRoomCode = "";
        ClearPlayers();
        ShowError(null);

        SetView(inLobby: false);
        ApplyHue(_hue);

        CloseLobbyRequested?.Invoke();
        onCloseLobbyRequested?.Invoke();
    }

    /// <summary>Validates player count and triggers StartMatchRequested for the active room.</summary>
    public void StartMatch()
    {
        if (!_inLobby || _startingMatch)
            return;

        int connectedCount = GetConnectedPlayerCount();
        if (connectedCount < GameSessionData.MinPlayersToStart)
        {
            ShowError($"Need at least {GameSessionData.MinPlayersToStart} players to start the match.");
            return;
        }

        ShowError(null);
        PulseBackgroundRipple(_startMatchButton);

        // Hand off joined players (names, picked colours, optional PFPs) to GameScene
        GameSessionData.SetLobbySession(_currentRoomCode, _players);

        StartMatchRequested?.Invoke(_currentRoomCode);
        onStartMatchRequested?.Invoke(_currentRoomCode);
        JoinRequested?.Invoke("HOST", _currentRoomCode);
        onJoinRequested?.Invoke("HOST", _currentRoomCode);

        if (loadGameSceneOnStart && !string.IsNullOrEmpty(gameSceneName))
        {
            SceneManager.LoadScene(gameSceneName);
        }
    }

    #endregion

    #region Match Settings (Create Game screen)

    private void RegisterSettingButtons(bool register)
    {
        (Button button, Action onClick)[] steppers =
        {
            (_maxPlayersDown, DecreaseMaxPlayers),
            (_maxPlayersUp, IncreaseMaxPlayers),
            (_drawingTimeDown, DecreaseDrawingTime),
            (_drawingTimeUp, IncreaseDrawingTime),
            (_guessingTimeDown, DecreaseGuessingTime),
            (_guessingTimeUp, IncreaseGuessingTime),
        };

        foreach ((Button button, Action onClick) in steppers)
        {
            if (register)
                RegisterButton(button, onClick);
            else
                UnregisterButton(button, onClick);
        }
    }

    private void DecreaseMaxPlayers() => SetMaxPlayers(GameSessionData.MaxPlayers - 1);
    private void IncreaseMaxPlayers() => SetMaxPlayers(GameSessionData.MaxPlayers + 1);

    private void DecreaseDrawingTime() =>
        SetDrawingSeconds(GameSessionData.DrawingSeconds - PhaseSecondsStep);

    private void IncreaseDrawingTime() =>
        SetDrawingSeconds(GameSessionData.DrawingSeconds + PhaseSecondsStep);

    private void DecreaseGuessingTime() =>
        SetGuessingSeconds(GameSessionData.GuessingSeconds - PhaseSecondsStep);

    private void IncreaseGuessingTime() =>
        SetGuessingSeconds(GameSessionData.GuessingSeconds + PhaseSecondsStep);

    /// <summary>
    /// Sets how many players can join (never below the players already in the relay room,
    /// since lowering the limit doesn't remove anyone) and tells the relay.
    /// </summary>
    public void SetMaxPlayers(int maxPlayers)
    {
        GameSessionData.MaxPlayers = Mathf.Max(maxPlayers, MinSelectableMaxPlayers());
        ApplyMaxPlayersToRelay();
        RefreshSettingsUI();
        RefreshLobbyUI();
    }

    public void SetDrawingSeconds(int seconds)
    {
        GameSessionData.DrawingSeconds = seconds;
        RefreshSettingsUI();
    }

    public void SetGuessingSeconds(int seconds)
    {
        GameSessionData.GuessingSeconds = seconds;
        RefreshSettingsUI();
    }

    /// <summary>The limit can't go below the minimum to start, or below who's already seated.</summary>
    private int MinSelectableMaxPlayers()
    {
        int seated = _relay != null ? _relay.Players.Count : _players.Count;
        return Mathf.Clamp(seated, GameSessionData.MinPlayersToStart, GameSessionData.MaxPlayersLimit);
    }

    private void ApplyMaxPlayersToRelay()
    {
        if (_relay != null)
            _relay.MaxPlayers = GameSessionData.MaxPlayers;
    }

    private void RefreshSettingsUI()
    {
        // Players may have joined the (kept) relay room since the limit was picked
        if (GameSessionData.MaxPlayers < MinSelectableMaxPlayers())
        {
            GameSessionData.MaxPlayers = MinSelectableMaxPlayers();
            ApplyMaxPlayersToRelay();
        }

        int maxPlayers = GameSessionData.MaxPlayers;
        int drawing = GameSessionData.DrawingSeconds;
        int guessing = GameSessionData.GuessingSeconds;

        if (_maxPlayersValue != null)
            _maxPlayersValue.text = maxPlayers.ToString();
        if (_drawingTimeValue != null)
            _drawingTimeValue.text = $"{drawing}s";
        if (_guessingTimeValue != null)
            _guessingTimeValue.text = $"{guessing}s";

        SetStepperEnabled(_maxPlayersDown, maxPlayers > MinSelectableMaxPlayers());
        SetStepperEnabled(_maxPlayersUp, maxPlayers < GameSessionData.MaxPlayersLimit);
        SetStepperEnabled(_drawingTimeDown, drawing > GameSessionData.MinPhaseSeconds);
        SetStepperEnabled(_drawingTimeUp, drawing < GameSessionData.MaxPhaseSeconds);
        SetStepperEnabled(_guessingTimeDown, guessing > GameSessionData.MinPhaseSeconds);
        SetStepperEnabled(_guessingTimeUp, guessing < GameSessionData.MaxPhaseSeconds);
    }

    private void SetStepperEnabled(Button button, bool enabled)
    {
        if (button == null)
            return;

        button.SetEnabled(enabled);
        button.style.opacity = enabled ? 1f : 0.35f;
        RefreshButton(button);
    }

    #endregion

    #region View

    private void SetView(bool inLobby)
    {
        _inLobby = inLobby;

        _column?.EnableInClassList("menu-column--lobby", inLobby);
        _createCard?.EnableInClassList("is-hidden", inLobby);
        _lobbyView?.EnableInClassList("is-hidden", !inLobby);

        if (_title != null)
            _title.OverlapAnimate = inLobby;

        // Back on the Create Game card: the room may have gained players meanwhile
        if (!inLobby)
            RefreshSettingsUI();
    }

    #endregion

    #region Relay

    private void BindRelay()
    {
        if (!useRelay || _relay != null || !Application.isPlaying)
            return;

        _relay = RelayClient.Instance;

        if (_relay == null)
            return;

        ApplyMaxPlayersToRelay();

        // Prefer the PlayerAvatars next to the live RelayClient: when the menu reloads,
        // the scene's own copy of the Relay object is a duplicate being destroyed
        _avatars = _relay.GetComponent<PlayerAvatars>();
        if (_avatars == null)
            _avatars = FindAnyObjectByType<PlayerAvatars>();

        _relay.StatusChanged += OnRelayStatusChanged;
        _relay.RoomReady += OnRelayRoomReady;
        _relay.RoomLost += OnRelayRoomLost;
        _relay.PlayerJoined += OnRelayPlayerJoined;
        _relay.PlayerRejoined += OnRelayPlayerRejoined;
        _relay.PlayerDisconnected += OnRelayPlayerDisconnected;
        _relay.PlayerLeft += OnRelayPlayerLeft;

        if (_avatars != null)
            _avatars.AvatarChanged += OnRelayAvatarChanged;
    }

    private void UnbindRelay()
    {
        if (_relay != null)
        {
            _relay.StatusChanged -= OnRelayStatusChanged;
            _relay.RoomReady -= OnRelayRoomReady;
            _relay.RoomLost -= OnRelayRoomLost;
            _relay.PlayerJoined -= OnRelayPlayerJoined;
            _relay.PlayerRejoined -= OnRelayPlayerRejoined;
            _relay.PlayerDisconnected -= OnRelayPlayerDisconnected;
            _relay.PlayerLeft -= OnRelayPlayerLeft;
        }

        if (_avatars != null)
            _avatars.AvatarChanged -= OnRelayAvatarChanged;

        _relay = null;
        _avatars = null;
    }

    /// <summary>Opens the lobby on the relay's room with everyone already seated in it.</summary>
    private void OpenRelayLobby()
    {
        OpenLobby(_relay.RoomCode);
        SyncPlayersFromRelay();
    }

    /// <summary>
    /// Makes the lobby match the relay's player list: drops anyone it no longer has,
    /// adds or updates the rest (also needed after a resume, which brings players back
    /// without PlayerJoined events).
    /// </summary>
    private void SyncPlayersFromRelay()
    {
        if (_relay == null)
            return;

        for (int i = _players.Count - 1; i >= 0; i--)
        {
            string id = _players[i].playerId;
            if (!_relay.Players.ContainsKey(id))
                RemovePlayer(id);
        }

        foreach (RelayPlayer player in _relay.Players.Values)
            AddRelayPlayer(player);
    }

    private void AddRelayPlayer(RelayPlayer player)
    {
        // The relay refuses joins over the limit itself; this only matters if it's an older
        // relay that doesn't know set_max_players
        bool isNew = _players.FindIndex(p => p.playerId == player.Id) < 0;
        if (isNew && _players.Count >= MaxPlayers)
        {
            Debug.LogWarning($"[Menu] Lobby full ({MaxPlayers}); removing {player.Name}.");
            _relay?.Kick(player.Id);
            return;
        }

        Texture2D avatar = null;
        if (_avatars != null && _avatars.TryGetAvatar(player.Id, out Sprite sprite) && sprite != null)
            avatar = sprite.texture;

        // PlayerAvatars owns the texture, so the lobby mustn't destroy it
        AddOrUpdatePlayer(player.Id, player.Name, connected: player.Connected, avatar: avatar, ownsAvatarTexture: false);
    }

    private void OnRelayStatusChanged(RelayStatus status)
    {
        if (!_inLobby)
            return;

        if (status == RelayStatus.Reconnecting)
            ShowError("Connection lost. Reconnecting...");
    }

    private void OnRelayRoomReady(string code)
    {
        if (_inLobby)
        {
            // Resumed (same code) or replaced (new code) while the lobby was open
            _currentRoomCode = code;
            ShowError(null);
            SyncPlayersFromRelay();
            RefreshLobbyUI();
        }
        else if (_creating)
        {
            OpenRelayLobby();
        }
    }

    private void OnRelayRoomLost()
    {
        if (!_inLobby)
            return;

        // The relay makes a new room right away; RoomReady brings the new code
        _currentRoomCode = "";
        SyncPlayersFromRelay();
        ShowError("The room closed. Making a new one: players need to join again.");
        RefreshLobbyUI();
    }

    private void OnRelayPlayerJoined(RelayPlayer player)
    {
        if (_inLobby)
            AddRelayPlayer(player);
    }

    private void OnRelayPlayerRejoined(RelayPlayer player)
    {
        if (_inLobby)
            AddRelayPlayer(player);
    }

    private void OnRelayPlayerDisconnected(RelayPlayer player)
    {
        if (_inLobby)
            SetPlayerConnected(player.Id, false);
    }

    private void OnRelayPlayerLeft(RelayPlayer player, string reason)
    {
        if (_inLobby)
            RemovePlayer(player.Id);
    }

    private void OnRelayAvatarChanged(string playerId, Sprite sprite)
    {
        // A null sprite means the player left; RemovePlayer handles that
        if (!_inLobby || sprite == null || _relay == null)
            return;

        if (_relay.Players.TryGetValue(playerId, out RelayPlayer player))
            AddRelayPlayer(player);
    }

    /// <summary>
    /// Shows the relay room's join link as a QR code in its own panel beside the lobby card,
    /// with the site's address in large text under both panels. Hidden when there's no real
    /// room (no relay, or between rooms), leaving the lobby card alone in a narrower column.
    /// Returns true if the QR code is showing.
    /// </summary>
    private bool RefreshJoinQr()
    {
        string url = _relay != null
            && !string.IsNullOrEmpty(_currentRoomCode)
            && _relay.RoomCode == _currentRoomCode
                ? _relay.JoinUrl
                : null;

        if (url != _roomQrUrl)
        {
            _roomQrUrl = url;
            ReleaseJoinQr();

            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    _roomQrTexture = QrCode.CreateMaskTexture(url);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Menu] Couldn't make the join QR code for {url}: {e.Message}");
                }
            }

            if (_roomQrCode != null)
            {
                _roomQrCode.style.backgroundImage = _roomQrTexture != null
                    ? new StyleBackground(_roomQrTexture)
                    : new StyleBackground(StyleKeyword.None);
            }

            if (_roomJoinSite != null)
            {
                _roomJoinSite.text = !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                    ? uri.Host
                    : "";
            }
        }

        bool shown = _roomQrTexture != null;
        _roomQrBox?.EnableInClassList("is-hidden", !shown);
        _roomJoinLink?.EnableInClassList("is-hidden",
            !shown || _roomJoinSite == null || string.IsNullOrEmpty(_roomJoinSite.text));

        // Widen the lobby column to fit the QR panel; the cube ring is placed around the column's bounds
        _column?.EnableInClassList("menu-column--lobby-qr", shown);
        return shown;
    }

    private void ReleaseJoinQr()
    {
        if (_roomQrTexture == null)
            return;

        if (Application.isPlaying)
            Destroy(_roomQrTexture);
        else
            DestroyImmediate(_roomQrTexture);
        _roomQrTexture = null;
    }

    #endregion

    #region Players & Character Cubes

    /// <summary>
    /// Adds a new player (merging the two closest greyscale cubes at the next clockwise seat
    /// around the join screen into their coloured character cube) or updates an existing player.
    /// </summary>
    public void AddOrUpdatePlayer(
        string playerId,
        string playerName,
        Color? pickedColor = null,
        bool connected = true,
        Texture2D avatar = null,
        bool ownsAvatarTexture = false)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            playerId = Guid.NewGuid().ToString("N");

        string cleanName = string.IsNullOrWhiteSpace(playerName)
            ? "PLAYER"
            : playerName.Trim();

        if (cleanName.Length > NameMaxLength)
            cleanName = cleanName.Substring(0, NameMaxLength);

        Color chosenColor;

        int existingIndex = _players.FindIndex(p => p.playerId == playerId);
        if (existingIndex >= 0)
        {
            LobbyPlayer existing = _players[existingIndex];
            existing.name = cleanName;
            existing.connected = connected;
            if (pickedColor.HasValue)
                existing.color = pickedColor.Value;

            chosenColor = existing.color;

            if (avatar != null)
            {
                if (existing.ownsAvatarTexture && existing.avatar != null && existing.avatar != avatar)
                    Destroy(existing.avatar);

                existing.avatar = avatar;
                existing.ownsAvatarTexture = ownsAvatarTexture;
            }
        }
        else
        {
            if (_players.Count >= MaxPlayers)
                return;

            chosenColor = pickedColor ?? NextDistinctPlayerColor();

            _players.Add(new LobbyPlayer
            {
                playerId = playerId,
                name = cleanName,
                color = chosenColor,
                connected = connected,
                avatar = avatar,
                ownsAvatarTexture = ownsAvatarTexture
            });

            GameAudioController.EnsureExists().PlayPlayerJoin();
        }

        if (colorStage != null)
            colorStage.AddOrUpdatePlayerCharacter(playerId, cleanName, chosenColor, connected, avatar);

        EnsureCharacterNameLabel(playerId, cleanName, chosenColor, connected);

        ShowError(null);
        RefreshLobbyUI();
        ApplyHue(_hue);
    }

    /// <summary>Updates a player's picked colour on their character cube and name tag.</summary>
    public void SetPlayerColor(string playerId, Color pickedColor)
    {
        int index = _players.FindIndex(p => p.playerId == playerId);
        if (index < 0)
            return;

        LobbyPlayer player = _players[index];
        player.color = pickedColor;

        if (colorStage != null)
            colorStage.AddOrUpdatePlayerCharacter(playerId, player.name, pickedColor, player.connected, player.avatar);

        EnsureCharacterNameLabel(playerId, player.name, pickedColor, player.connected);
    }

    /// <summary>
    /// Decodes a GZIP+Base64 white-on-black PFP drawing (d.t == "pfp_submit") and displays it
    /// on the player's character cube.
    /// </summary>
    public void SetPlayerAvatar(string playerId, string base64GzipImage)
    {
        int index = _players.FindIndex(p => p.playerId == playerId);
        if (index < 0)
            return;

        Texture2D tex = DrawingImageCodec.DecompressToTexture2D(base64GzipImage);
        if (tex == null)
            return;

        LobbyPlayer player = _players[index];
        if (player.ownsAvatarTexture && player.avatar != null)
            Destroy(player.avatar);

        player.avatar = tex;
        player.ownsAvatarTexture = true;

        if (colorStage != null)
            colorStage.SetPlayerCharacterAvatar(playerId, tex);
    }

    /// <summary>Updates a player's connection state.</summary>
    public void SetPlayerConnected(string playerId, bool connected)
    {
        int index = _players.FindIndex(p => p.playerId == playerId);
        if (index < 0)
            return;

        LobbyPlayer player = _players[index];
        player.connected = connected;

        if (colorStage != null)
            colorStage.SetPlayerCharacterConnected(playerId, connected);

        EnsureCharacterNameLabel(playerId, player.name, player.color, connected);
        RefreshLobbyUI();
        ApplyHue(_hue);
    }

    /// <summary>
    /// Removes a player from the lobby, despawning their character cube and spawning
    /// replacement greyscale cubes.
    /// </summary>
    public void RemovePlayer(string playerId)
    {
        int index = _players.FindIndex(p => p.playerId == playerId);
        if (index < 0)
            return;

        LobbyPlayer player = _players[index];
        if (player.ownsAvatarTexture && player.avatar != null)
            Destroy(player.avatar);

        _players.RemoveAt(index);

        if (colorStage != null)
            colorStage.RemovePlayerCharacter(playerId);

        RemoveCharacterNameLabel(playerId);
        RefreshLobbyUI();
        ApplyHue(_hue);
    }

    /// <summary>Clears all players from the lobby and despawns their character cubes.</summary>
    public void ClearPlayers()
    {
        ReleaseOwnedAvatars();
        _players.Clear();

        if (colorStage != null)
            colorStage.ClearAllPlayerCharacters();

        foreach (Label label in _playerNameLabels.Values)
            label?.RemoveFromHierarchy();

        _playerNameLabels.Clear();

        RefreshLobbyUI();
        ApplyHue(_hue);
    }

    private Color NextDistinctPlayerColor()
    {
        // Golden-ratio hue step produces vivid, maximally distinct colours for every joining player
        float hue = Mathf.Repeat(_nextColorIndex * 0.61803398875f + UnityEngine.Random.Range(0f, 0.08f), 1f);
        _nextColorIndex++;
        return Color.HSVToRGB(hue, 0.88f, 0.98f);
    }

    private void EnsureCharacterNameLabel(string playerId, string playerName, Color playerColor, bool connected)
    {
        if (_characterLabelsLayer == null)
            return;

        if (!_playerNameLabels.TryGetValue(playerId, out Label label) || label == null)
        {
            label = new Label
            {
                pickingMode = PickingMode.Ignore
            };
            label.AddToClassList("character-name-label");
            _characterLabelsLayer.Add(label);
            _playerNameLabels[playerId] = label;
        }

        label.text = playerName.ToUpperInvariant();
        label.style.color = playerColor;
        SetBorderColor(label, new Color(playerColor.r, playerColor.g, playerColor.b, 0.7f));
        label.style.opacity = connected ? 1f : 0.6f;
    }

    private void RemoveCharacterNameLabel(string playerId)
    {
        if (_playerNameLabels.TryGetValue(playerId, out Label label))
        {
            label?.RemoveFromHierarchy();
            _playerNameLabels.Remove(playerId);
        }
    }

    private void SyncCenterCardWorldBoundsToStage()
    {
        if (colorStage == null || _column == null)
            return;

        Camera cam = colorStage.StageCamera;
        if (cam == null || !cam.orthographic)
            return;

        Rect panelRect = _root.worldBound;

        // The column wraps the whole lobby view (lobby card + QR panel + join site below),
        // widened by menu-column--lobby-qr, so its bounds keep the cube ring clear of all of it.
        // A long join site can overflow the column sideways (no wrapping), so include it too.
        Rect colRect = _column.worldBound;
        if (_inLobby && _roomJoinLink != null && _roomJoinLink.resolvedStyle.display == DisplayStyle.Flex)
            colRect = Union(colRect, _roomJoinLink.worldBound);

        if (panelRect.width < 1f || panelRect.height < 1f || colRect.width < 1f || colRect.height < 1f)
            return;

        float worldH = cam.orthographicSize * 2f;
        float worldW = worldH * cam.aspect;

        float normCenterX = (colRect.center.x - panelRect.xMin) / panelRect.width - 0.5f;
        float normCenterY = 0.5f - (colRect.center.y - panelRect.yMin) / panelRect.height;

        Vector2 worldCenter = new(
            cam.transform.position.x + normCenterX * worldW,
            cam.transform.position.y + normCenterY * worldH
        );

        Vector2 worldHalfExtents = new(
            (colRect.width / panelRect.width) * worldW * 0.5f,
            (colRect.height / panelRect.height) * worldH * 0.5f
        );

        colorStage.SetCenterCardWorldBounds(worldCenter, worldHalfExtents);
    }

    private static Rect Union(Rect a, Rect b)
    {
        if (b.width < 1f || b.height < 1f || float.IsNaN(b.width) || float.IsNaN(b.height))
            return a;

        return Rect.MinMaxRect(
            Mathf.Min(a.xMin, b.xMin),
            Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax),
            Mathf.Max(a.yMax, b.yMax)
        );
    }

    private void SyncCharacterLabelsToWorldPositions()
    {
        if (colorStage == null || _characterLabelsLayer == null || _root.panel == null)
            return;

        Camera cam = colorStage.StageCamera;
        if (cam == null)
            return;

        for (int i = 0; i < _players.Count; i++)
        {
            LobbyPlayer player = _players[i];
            if (!_playerNameLabels.TryGetValue(player.playerId, out Label label) || label == null)
                continue;

            if (!colorStage.TryGetPlayerCharacterAnchor(player.playerId, out Vector3 worldCenter, out float radius))
            {
                label.style.display = DisplayStyle.None;
                continue;
            }

            label.style.display = DisplayStyle.Flex;

            Vector3 anchorWorld = worldCenter - new Vector3(0f, radius + 0.34f, 0f);
            Vector2 panelPos = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, anchorWorld, cam);

            label.style.left = panelPos.x;
            label.style.top = panelPos.y;
        }
    }

    private void ReleaseOwnedAvatars()
    {
        for (int i = 0; i < _players.Count; i++)
        {
            if (_players[i].ownsAvatarTexture && _players[i].avatar != null)
                Destroy(_players[i].avatar);
        }
    }

    private int GetConnectedPlayerCount()
    {
        int count = 0;
        for (int i = 0; i < _players.Count; i++)
        {
            if (_players[i].connected)
                count++;
        }
        return count;
    }

    private void RefreshLobbyUI()
    {
        if (_roomCodeDisplay != null)
            _roomCodeDisplay.text = string.IsNullOrEmpty(_currentRoomCode) ? "----" : _currentRoomCode;

        bool hasJoinQr = RefreshJoinQr();
        int totalJoined = _players.Count;
        int connectedCount = GetConnectedPlayerCount();

        if (_playerCountLabel != null)
            _playerCountLabel.text = $"{totalJoined} / {MaxPlayers}";

        if (_lobbyTitle != null)
        {
            _lobbyTitle.text = totalJoined == 0
                ? "WAITING FOR PLAYERS..."
                : totalJoined >= MaxPlayers
                    ? "ROOM FULL — READY"
                    : "PLAYERS JOINED";
        }

        if (_lobbyDescription != null)
        {
            _lobbyDescription.text = totalJoined > 0
                ? "Press START MATCH when everyone is ready"
                : hasJoinQr
                    ? "Scan the QR code with your phone, or enter this code at the site below"
                    : "Enter the 4-letter code on your device to join";
        }

        if (_startMatchButton != null)
        {
            bool canStart = !_startingMatch && connectedCount >= GameSessionData.MinPlayersToStart;
            _startMatchButton.SetEnabled(canStart);
            _startMatchButton.style.opacity = canStart ? 1f : 0.5f;
        }
    }

    #endregion

    #region Error & Helpers

    /// <summary>Shows an error on the currently active card (null or empty hides it).</summary>
    public void ShowError(string message)
    {
        bool show = !string.IsNullOrEmpty(message);

        if (_createError != null)
        {
            _createError.text = show && !_inLobby ? message : "";
            _createError.style.display = show && !_inLobby ? DisplayStyle.Flex : DisplayStyle.None;
        }

        if (_lobbyError != null)
        {
            _lobbyError.text = show && _inLobby ? message : "";
            _lobbyError.style.display = show && _inLobby ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>Generates a random 4-letter room code using the relay protocol alphabet.</summary>
    public static string GenerateRoomCode()
    {
        char[] chars = new char[RoomCodeLength];
        for (int i = 0; i < RoomCodeLength; i++)
        {
            chars[i] = RoomCodeAlphabet[UnityEngine.Random.Range(0, RoomCodeAlphabet.Length)];
        }
        return new string(chars);
    }

    #endregion

    #region Buttons & Console Input

    private void RegisterButton(Button button, Action onClick)
    {
        if (button == null)
            return;

        button.clicked += onClick;
        button.clicked += PlayButtonClickSfx;
        button.RegisterCallback<PointerEnterEvent, Button>(OnButtonPointerEnter, button);
        button.RegisterCallback<PointerLeaveEvent, Button>(OnButtonPointerLeave, button);
        button.RegisterCallback<FocusInEvent, Button>(OnButtonFocusIn, button);
        button.RegisterCallback<FocusOutEvent, Button>(OnButtonFocusOut, button);
    }

    private void UnregisterButton(Button button, Action onClick)
    {
        if (button == null)
            return;

        button.clicked -= onClick;
        button.clicked -= PlayButtonClickSfx;
        button.UnregisterCallback<PointerEnterEvent, Button>(OnButtonPointerEnter);
        button.UnregisterCallback<PointerLeaveEvent, Button>(OnButtonPointerLeave);
        button.UnregisterCallback<FocusInEvent, Button>(OnButtonFocusIn);
        button.UnregisterCallback<FocusOutEvent, Button>(OnButtonFocusOut);
    }

    private static void PlayButtonClickSfx() =>
        GameAudioController.EnsureExists().PlayButtonClick();

    private void OnButtonPointerEnter(PointerEnterEvent evt, Button button)
    {
        _hoveredButtons.Add(button);
        RefreshButton(button);
    }

    private void OnButtonPointerLeave(PointerLeaveEvent evt, Button button)
    {
        _hoveredButtons.Remove(button);
        RefreshButton(button);
    }

    private void OnButtonFocusIn(FocusInEvent evt, Button button)
    {
        _focusedButtons.Add(button);
        RefreshButton(button);
    }

    private void OnButtonFocusOut(FocusOutEvent evt, Button button)
    {
        _focusedButtons.Remove(button);
        RefreshButton(button);
    }

    private void OnRootKeyDown(KeyDownEvent evt)
    {
        if (_hueBar != null && _hueBar.IsOpen)
            return;

        if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
        {
            if (!_inLobby)
            {
                CreateGame();
                evt.StopPropagation();
            }
            else
            {
                StartMatch();
                evt.StopPropagation();
            }
        }
        else if (evt.keyCode == KeyCode.Escape && _inLobby)
        {
            CloseLobby();
            evt.StopPropagation();
        }
    }

    private void PulseBackgroundRipple(VisualElement target)
    {
        if (target == null)
            return;

        Vector2 worldCenter = target.worldBound.center;

        if (_background != null)
        {
            Vector2 localPos = _background.WorldToLocal(worldCenter);
            _background.AddRipple(localPos);
        }
        else if (PersistentPixelBlastBackground.Instance != null)
        {
            PersistentPixelBlastBackground.Instance.AddRipple(worldCenter);
        }
    }

    #endregion

    #region Hue Colouring

    private void OnHueChanged(float? value) =>
        ApplyHue(value.HasValue ? HueBar.ColorAt(value.Value) : Color.white);

    /// <summary>
    /// Colours the entire console UI with the picked hue (matching the frontend's --hue var).
    /// Done inline since USS variables can't be mutated at runtime.
    /// </summary>
    private void ApplyHue(Color hue)
    {
        _hue = hue;

        if (_root == null)
            return;

        Color dimHue = new(hue.r, hue.g, hue.b, 0.78f);
        Color faintHue = new(hue.r, hue.g, hue.b, 0.35f);
        Color dimBorder = new(hue.r, hue.g, hue.b, 0.45f);
        Color dimBg = new(hue.r, hue.g, hue.b, 0.22f);

        _root.Query(className: "hue-text").ForEach(e => e.style.color = hue);
        _root.Query(className: "hue-dim-text").ForEach(e => e.style.color = dimHue);
        _root.Query(className: "hue-faint-text").ForEach(e => e.style.color = faintHue);
        _root.Query(className: "hue-border").ForEach(e => SetBorderColor(e, hue));
        _root.Query(className: "hue-dim-border").ForEach(e => SetBorderColor(e, dimBorder));
        _root.Query(className: "hue-dim-bg").ForEach(e => e.style.backgroundColor = dimBg);

        if (_background != null)
            _background.accentColor = hue;

        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(hue);

        if (_title != null)
            _title.BaseColor = hue;

        // The QR plate and ink fade to the new hue in UpdateQrColors (black ink on warm/bright
        // hues, white on deep ones like blue and violet)

        if (_createError != null)
            _createError.style.color = ErrorColor;

        if (_lobbyError != null)
            _lobbyError.style.color = ErrorColor;

        RefreshAllButtons();
    }

    private void RefreshAllButtons()
    {
        RefreshButton(_createButton);
        RefreshButton(_backButton);
        RefreshButton(_startMatchButton);
        RefreshButton(_maxPlayersDown);
        RefreshButton(_maxPlayersUp);
        RefreshButton(_drawingTimeDown);
        RefreshButton(_drawingTimeUp);
        RefreshButton(_guessingTimeDown);
        RefreshButton(_guessingTimeUp);
    }

    private void RefreshButton(Button button)
    {
        if (button == null)
            return;

        bool active = (_hoveredButtons.Contains(button) || _focusedButtons.Contains(button)) &&
                      button.enabledSelf;

        SetBorderColor(button, _hue);
        button.style.backgroundColor = active ? _hue : new Color(0f, 0f, 0f, 0.45f);
        button.style.color = active ? Color.black : _hue;
    }

    private static void SetBorderColor(VisualElement element, Color color)
    {
        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    #endregion

    #region Simulation / Testing Actions

    [Title("Debug / Preview (Play Mode)")]
    [Button]
    public void SimulateOpenLobby()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("MenuController preview buttons work in Play Mode.");
            return;
        }

        OpenLobby(GenerateRoomCode());
    }

    [Button]
    public void SimulateBackToCreate()
    {
        if (!Application.isPlaying)
            return;

        CloseLobby();
    }

    #endregion
}

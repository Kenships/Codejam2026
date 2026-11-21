using System;
using System.Text.RegularExpressions;
using Alchemy.Inspector;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UIElements;

/// <summary>
/// Drives the Menu UI (port of the frontend's LoginPage / LoginCard): the
/// whole UI takes the colour picked on the hue bar, and the join form
/// validates the username and room code the same way the frontend does.
/// The actual join is left to whoever listens to JoinRequested, which should
/// call SetJoining(false) / ShowError(...) when it's done.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class MenuController : MonoBehaviour
{
    // Matches frontend/src/lib/protocol.ts
    public const int NameMaxLength = 16;
    public const int RoomCodeLength = 4;
    private const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ";

    private static readonly Regex RoomCodeRegex =
        new($"^[{RoomCodeAlphabet}]{{{RoomCodeLength}}}$");

    private const string UsernamePrefKey = "menu.username";
    private const string RoomPrefKey = "menu.room";

    // Tailwind red-400
    private static readonly Color ErrorColor = new(0.973f, 0.443f, 0.443f);

    [SerializeField, Required] private UIDocument document;

    [Title("Join")]
    // Invoked with (username, roomCode) once both validate
    [SerializeField] private UnityEvent<string, string> onJoinRequested;

    public event Action<string, string> JoinRequested;

    private VisualElement _root;
    private PixelBlastBackground _background;
    private HueBar _hueBar;
    private TextField _username;
    private TextField _roomCode;
    private Label _error;
    private Button _joinButton;

    private Color _hue = Color.white;
    private bool _joining;
    private bool _buttonHovered;

    /// <summary>The colour currently picked on the hue bar (white if none).</summary>
    public Color SelectedColor => _hue;

    public bool IsJoining => _joining;

    private void Reset() => document = GetComponent<UIDocument>();

    private void OnEnable()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        _root = document.rootVisualElement;

        if (_root == null)
            return;

        _background = _root.Q<PixelBlastBackground>();
        _hueBar = _root.Q<HueBar>();
        _username = _root.Q<TextField>("username");
        _roomCode = _root.Q<TextField>("room-code");
        _error = _root.Q<Label>("error");
        _joinButton = _root.Q<Button>("join");

        SetupInput(_username, "Username", NameMaxLength);
        SetupInput(_roomCode, "Room Code", RoomCodeLength);

        _username?.SetValueWithoutNotify(PlayerPrefs.GetString(UsernamePrefKey, ""));
        _roomCode?.SetValueWithoutNotify(PlayerPrefs.GetString(RoomPrefKey, ""));

        _username?.RegisterValueChangedCallback(OnUsernameChanged);
        _roomCode?.RegisterValueChangedCallback(OnRoomChanged);

        if (_hueBar != null)
            _hueBar.ValueChanged += OnHueChanged;

        if (_joinButton != null)
        {
            _joinButton.clicked += Submit;
            _joinButton.RegisterCallback<PointerEnterEvent>(OnButtonEnter);
            _joinButton.RegisterCallback<PointerLeaveEvent>(OnButtonLeave);
        }

        ShowError(null);
        SetJoining(false);
        ApplyHue(_hueBar != null ? _hueBar.Color : Color.white);
    }

    private void OnDisable()
    {
        _username?.UnregisterValueChangedCallback(OnUsernameChanged);
        _roomCode?.UnregisterValueChangedCallback(OnRoomChanged);

        if (_hueBar != null)
            _hueBar.ValueChanged -= OnHueChanged;

        if (_joinButton != null)
        {
            _joinButton.clicked -= Submit;
            _joinButton.UnregisterCallback<PointerEnterEvent>(OnButtonEnter);
            _joinButton.UnregisterCallback<PointerLeaveEvent>(OnButtonLeave);
        }
    }

    #region Join

    /// <summary>Validates the form and raises JoinRequested.</summary>
    public void Submit()
    {
        if (_joining)
            return;

        if (!ValidateName(_username?.value ?? "", out string name, out string error) ||
            !ValidateRoomCode(_roomCode?.value ?? "", out string room, out error))
        {
            ShowError(error);
            return;
        }

        ShowError(null);
        SetJoining(true);

        JoinRequested?.Invoke(name, room);
        onJoinRequested?.Invoke(name, room);
    }

    /// <summary>Disables the form and shows "Joining..." while true.</summary>
    public void SetJoining(bool joining)
    {
        _joining = joining;

        _username?.SetEnabled(!joining);
        _roomCode?.SetEnabled(!joining);
        _joinButton?.SetEnabled(!joining);

        if (_joinButton != null)
            _joinButton.text = joining ? "JOINING..." : "JOIN GAME";

        float opacity = joining ? 0.6f : 1f;

        foreach (VisualElement element in new VisualElement[] { _username, _roomCode, _joinButton })
            if (element != null)
                element.style.opacity = opacity;

        RefreshButton();
    }

    /// <summary>Shows an error under the inputs (null or empty hides it).</summary>
    public void ShowError(string message)
    {
        if (_error == null)
            return;

        bool show = !string.IsNullOrEmpty(message);

        _error.text = show ? message : "";
        _error.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public static bool ValidateName(string input, out string name, out string error)
    {
        name = input.Trim();
        error = null;

        if (name.Length == 0)
            error = "Enter a username.";
        else if (name.Length > NameMaxLength)
            error = $"Username must be {NameMaxLength} characters or fewer.";

        return error == null;
    }

    public static bool ValidateRoomCode(string input, out string room, out string error)
    {
        room = input.Trim().ToUpperInvariant();
        error = null;

        if (room.Length != RoomCodeLength)
            error = $"Room code must be {RoomCodeLength} letters.";
        else if (!RoomCodeRegex.IsMatch(room))
            error = "Room code must use 4 letters (no I, L, or O).";

        return error == null;
    }

    #endregion

    #region Inputs

    private void SetupInput(TextField field, string placeholder, int maxLength)
    {
        if (field == null)
            return;

        field.maxLength = maxLength;
        field.textEdition.placeholder = placeholder;
        field.textEdition.hidePlaceholderOnFocus = false;

        // Enter submits, like the frontend's <form>
        field.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                Submit();
                evt.StopPropagation();
            }
        }, TrickleDown.TrickleDown);
    }

    private void OnUsernameChanged(ChangeEvent<string> evt)
    {
        PlayerPrefs.SetString(UsernamePrefKey, evt.newValue);
        ShowError(null);
        StyleInput(_username, false);
    }

    private void OnRoomChanged(ChangeEvent<string> evt)
    {
        string upper = evt.newValue.ToUpperInvariant();

        if (upper != evt.newValue)
            _roomCode.SetValueWithoutNotify(upper);

        PlayerPrefs.SetString(RoomPrefKey, upper);
        ShowError(null);
        StyleInput(_roomCode, true);
    }

    #endregion

    #region Colour

    private void OnHueChanged(float? value) =>
        ApplyHue(value.HasValue ? HueBar.ColorAt(value.Value) : Color.white);

    /// <summary>
    /// Colours everything with the picked hue (the frontend's --hue var).
    /// Done inline since USS variables can't be set at runtime, which also
    /// overrides the default theme's hover/focus colours.
    /// </summary>
    private void ApplyHue(Color hue)
    {
        _hue = hue;

        if (_root == null)
            return;

        _root.Query(className: "hue-text").ForEach(e => e.style.color = hue);
        _root.Query(className: "hue-border").ForEach(e => SetBorderColor(e, hue));

        if (_background != null)
            _background.accentColor = hue;

        if (_error != null)
            _error.style.color = ErrorColor;

        StyleInput(_username, false);
        StyleInput(_roomCode, true);
        RefreshButton();
    }

    private void StyleInput(TextField field, bool trackWhenFilled)
    {
        if (field == null)
            return;

        bool empty = string.IsNullOrEmpty(field.value);

        // Placeholder at 60% like placeholder:text-(--hue)/60
        Color textColor = empty ? new Color(_hue.r, _hue.g, _hue.b, 0.6f) : _hue;

        VisualElement input = field.Q(className: TextField.inputUssClassName) ?? field;

        SetBorderColor(input, _hue);
        input.style.backgroundColor = new Color(0f, 0f, 0f, 0.5f);
        input.style.color = textColor;
        input.Query<TextElement>().ForEach(t => t.style.color = textColor);

        // Room code: wide tracking once there's text, normal for the placeholder
        if (trackWhenFilled)
            input.style.letterSpacing = empty ? 0f : 1.6f;
    }

    private void OnButtonEnter(PointerEnterEvent evt)
    {
        _buttonHovered = true;
        RefreshButton();
    }

    private void OnButtonLeave(PointerLeaveEvent evt)
    {
        _buttonHovered = false;
        RefreshButton();
    }

    // hover:bg-(--hue) hover:text-black
    private void RefreshButton()
    {
        if (_joinButton == null)
            return;

        bool hovered = _buttonHovered && !_joining;

        SetBorderColor(_joinButton, _hue);
        _joinButton.style.backgroundColor = hovered ? _hue : Color.clear;
        _joinButton.style.color = hovered ? Color.black : _hue;
    }

    private static void SetBorderColor(VisualElement element, Color color)
    {
        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    #endregion
}

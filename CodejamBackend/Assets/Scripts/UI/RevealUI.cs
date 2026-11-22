using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UIElements;

/// <summary>
/// Overlay for the end-of-round reveal, driven by RevealController's
/// events. Hidden until a reveal starts, then:
/// - header as the merged body slides over,
/// - a tag under the merged body with its hex once it's in place,
/// - a tag under the true colour body whose hex scrambles through the
///   blend and settles on the true colour,
/// - a result card with the match % counting up, a verdict, the colours
///   that went into the mix, and PLAY AGAIN.
/// Everything hides again when the reveal is cleared.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class RevealUI : MonoBehaviour
{
    private const string ShownClass = "is-shown";
    private const string HiddenClass = "is-hidden";
    private const string SwatchClass = "reveal-swatch";

    private static readonly Color ButtonBackground = new(0f, 0f, 0f, 0.45f);

    // Lowest match (0-1) for each verdict, best first
    private static readonly (float min, string text)[] Verdicts =
    {
        (0.95f, "PERFECT MATCH!"),
        (0.85f, "SO CLOSE!"),
        (0.70f, "NOT BAD"),
        (0.50f, "IN THE NEIGHBOURHOOD"),
        (0.00f, "WAY OFF..."),
    };

    [SerializeField, Required] private UIDocument document;

    // Falls back to GameManager's, then any in the scene
    [SerializeField] private RevealController revealController;

    [Title("Timing")]
    // Pause after the true colour lands before the result card comes up
    [SerializeField, Min(0f)] private float resultDelay = 0.4f;

    [SerializeField, Min(0.01f)] private float countUpDuration = 1.2f;
    [SerializeField] private Ease countUpEase = Ease.OutCubic;

    [Title("Play Again")]
    [SerializeField] private bool showPlayAgain = true;

    // If nothing listens here or to PlayAgainRequested, PLAY AGAIN
    // restarts the round through GameManager
    [SerializeField] private UnityEvent onPlayAgain;

    public event Action PlayAgainRequested;

    /// <summary>Updates the bottom action button label (e.g. "NEXT DRAWING" or "FINISH GAME").</summary>
    public void SetActionButtonText(string text)
    {
        if (_playAgain != null && !string.IsNullOrEmpty(text))
            _playAgain.text = text;
    }

    private VisualElement _root;
    private VisualElement _header;
    private Label _title;
    private VisualElement _mixedTag;
    private Label _mixedHex;
    private VisualElement _trueTag;
    private Label _trueHex;
    private VisualElement _result;
    private VisualElement _card;
    private VisualElement _matchBox;
    private Label _matchValue;
    private Label _verdict;
    private VisualElement _guesses;
    private Button _playAgain;

    private RevealController _bound;
    private Sequence _resultSequence;
    private Color _accent = Color.white;
    private bool _trueColorLanded;
    private bool _buttonActive;

    private void Reset()
    {
        document = GetComponent<UIDocument>();
        revealController = FindFirstObjectByType<RevealController>();
    }

    private void OnEnable()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        _root = document.rootVisualElement;

        if (_root == null)
            return;

        QueryElements();
        HideAll(instant: true);

        if (_playAgain != null)
        {
            _playAgain.clicked += OnPlayAgainClicked;
            _playAgain.RegisterCallback<PointerEnterEvent>(OnButtonEnter);
            _playAgain.RegisterCallback<PointerLeaveEvent>(OnButtonLeave);
            _playAgain.RegisterCallback<FocusInEvent>(OnButtonFocusIn);
            _playAgain.RegisterCallback<FocusOutEvent>(OnButtonFocusOut);
            _playAgain.EnableInClassList(HiddenClass, !showPlayAgain);
        }

        Bind();
    }

    // GameManager may only add its RevealController in its Awake
    private void Start() => Bind();

    private void OnDisable()
    {
        Unbind();
        StopResultSequence();

        if (_playAgain != null)
        {
            _playAgain.clicked -= OnPlayAgainClicked;
            _playAgain.UnregisterCallback<PointerEnterEvent>(OnButtonEnter);
            _playAgain.UnregisterCallback<PointerLeaveEvent>(OnButtonLeave);
            _playAgain.UnregisterCallback<FocusInEvent>(OnButtonFocusIn);
            _playAgain.UnregisterCallback<FocusOutEvent>(OnButtonFocusOut);
        }
    }

    private void LateUpdate()
    {
        if (_bound == null || _root?.panel == null)
            return;

        ColorBody mixed = _bound.MergedBody;
        ColorBody truth = _bound.TrueBody;

        if (mixed == null)
            return;

        Camera cam = _bound.RevealCamera;

        // Both tags sit at the merged body's size, so the true colour tag
        // doesn't bounce around while its body grows
        float radius = mixed.Radius;

        PlaceUnder(_mixedTag, mixed.transform.position, radius, cam);

        if (truth == null)
            return;

        PlaceUnder(_trueTag, truth.transform.position, radius, cam);

        // Scramble through the blend until the true colour lands
        if (!_trueColorLanded)
            SetTag(_trueTag, _trueHex, truth.Color);
    }

    #region Binding

    private void Bind()
    {
        if (_bound != null)
            return;

        if (revealController == null && GameManager.Instance != null)
            revealController = GameManager.Instance.Reveal;

        if (revealController == null)
            revealController = FindFirstObjectByType<RevealController>();

        if (revealController == null)
            return;

        _bound = revealController;
        _bound.Started += OnStarted;
        _bound.MergedPlaced += OnMergedPlaced;
        _bound.TrueBodyCreated += OnTrueBodyCreated;
        _bound.Revealed += OnRevealed;
        _bound.Cleared += OnCleared;

        FitBands();
    }

    private void Unbind()
    {
        if (_bound == null)
            return;

        _bound.Started -= OnStarted;
        _bound.MergedPlaced -= OnMergedPlaced;
        _bound.TrueBodyCreated -= OnTrueBodyCreated;
        _bound.Revealed -= OnRevealed;
        _bound.Cleared -= OnCleared;
        _bound = null;
    }

    private void QueryElements()
    {
        _header = _root.Q<VisualElement>("reveal-header");
        _title = _root.Q<Label>("reveal-title");
        _mixedTag = _root.Q<VisualElement>("mixed-tag");
        _mixedHex = _root.Q<Label>("mixed-tag-hex");
        _trueTag = _root.Q<VisualElement>("true-tag");
        _trueHex = _root.Q<Label>("true-tag-hex");
        _result = _root.Q<VisualElement>("reveal-result");
        _card = _root.Q<VisualElement>("reveal-card");
        _matchBox = _root.Q<VisualElement>("match-box");
        _matchValue = _root.Q<Label>("match-value");
        _verdict = _root.Q<Label>("verdict");
        _guesses = _root.Q<VisualElement>("guesses");
        _playAgain = _root.Q<Button>("play-again");
    }

    /// <summary>Sizes the header and result bands to the space the camera leaves.</summary>
    private void FitBands()
    {
        if (_bound == null)
            return;

        if (_header != null)
            _header.style.height = Length.Percent(_bound.ReservedTop * 100f);

        if (_result != null)
            _result.style.height = Length.Percent(_bound.ReservedBottom * 100f);
    }

    #endregion

    #region Reveal Events

    private void OnStarted(ColorBody merged)
    {
        HideAll(instant: true);
        FitBands();
        ApplyAccent(Color.white);
        Show(_header);
    }

    private void OnMergedPlaced(ColorBody merged)
    {
        SetTag(_mixedTag, _mixedHex, merged.Color);
        Show(_mixedTag);
    }

    private void OnTrueBodyCreated(ColorBody truth)
    {
        _trueColorLanded = false;
        SetTag(_trueTag, _trueHex, truth.Color);
        Show(_trueTag);
    }

    private void OnRevealed(RevealResult result)
    {
        _trueColorLanded = true;
        SetTag(_trueTag, _trueHex, result.TrueColor);

        ApplyAccent(result.TrueColor);
        FillResult(result);

        StopResultSequence();

        float percent = result.Match * 100f;

        _resultSequence = Sequence.Create()
            .ChainDelay(resultDelay)
            .ChainCallback(() =>
            {
                Show(_result);
                GameAudioController.EnsureExists().PlayRevealOutcome(result.Match);
            })
            .Chain(Tween.Custom(
                0f,
                percent,
                new TweenSettings(countUpDuration, countUpEase),
                SetMatchText
            ));
    }

    private void OnCleared() => HideAll(instant: false);

    #endregion

    #region Display

    private void FillResult(RevealResult result)
    {
        SetMatchText(0f);

        if (_verdict != null)
            _verdict.text = VerdictFor(result.Match);

        if (_guesses == null)
            return;

        _guesses.Clear();

        foreach (Color color in result.GuessedColors)
        {
            VisualElement swatch = new();
            swatch.AddToClassList(SwatchClass);
            swatch.style.backgroundColor = color;
            _guesses.Add(swatch);
        }
    }

    private void SetMatchText(float percent)
    {
        if (_matchValue != null)
            _matchValue.text = $"{Mathf.RoundToInt(percent)}%";
    }

    private static string VerdictFor(float match)
    {
        foreach ((float min, string text) in Verdicts)
        {
            if (match >= min)
                return text;
        }

        return Verdicts[^1].text;
    }

    /// <summary>Colours the title, card, match value and button with the true colour.</summary>
    private void ApplyAccent(Color accent)
    {
        _accent = accent;

        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(accent);

        if (_title != null)
            _title.style.color = accent;

        if (_matchValue != null)
            _matchValue.style.color = accent;

        if (_verdict != null)
            _verdict.style.color = accent;

        SetBorderColor(_card, accent);

        if (_matchBox != null)
            _matchBox.style.borderRightColor = new Color(accent.r, accent.g, accent.b, 0.35f);

        RefreshButton();
    }

    private static void SetTag(VisualElement tag, Label hex, Color color)
    {
        if (hex != null)
        {
            hex.text = "#" + ColorUtility.ToHtmlStringRGB(color);
            hex.style.color = color;
        }

        SetBorderColor(tag, color);
    }

    /// <summary>Puts a tag's top centre just under a body.</summary>
    private void PlaceUnder(VisualElement tag, Vector3 center, float radius, Camera cam)
    {
        if (tag == null || cam == null)
            return;

        Vector3 bottom = center - Vector3.up * radius;
        Vector2 point = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, bottom, cam);

        tag.style.left = point.x;
        tag.style.top = point.y;
    }

    private void Show(VisualElement element) => element?.AddToClassList(ShownClass);

    private void HideAll(bool instant)
    {
        StopResultSequence();

        foreach (VisualElement element in new[] { _header, _mixedTag, _trueTag, _result })
        {
            if (element == null)
                continue;

            // Skip the fade-out when resetting, so nothing from the last
            // round flashes up
            if (instant)
                element.style.transitionDuration = new List<TimeValue> { new(0f) };

            element.RemoveFromClassList(ShownClass);

            if (instant)
                element.schedule.Execute(() => element.style.transitionDuration = StyleKeyword.Null);
        }

        _trueColorLanded = false;
    }

    private void StopResultSequence()
    {
        if (_resultSequence.isAlive)
            _resultSequence.Stop();
    }

    private static void SetBorderColor(VisualElement element, Color color)
    {
        if (element == null)
            return;

        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    #endregion

    #region Play Again

    private void OnPlayAgainClicked()
    {
        GameAudioController.EnsureExists().PlayButtonClick();

        bool handled = PlayAgainRequested != null || onPlayAgain.GetPersistentEventCount() > 0;

        PlayAgainRequested?.Invoke();
        onPlayAgain?.Invoke();

        if (!handled && GameManager.Instance != null)
            GameManager.Instance.GenerateAndSpawn();
    }

    private void OnButtonEnter(PointerEnterEvent _) => SetButtonActive(true);
    private void OnButtonLeave(PointerLeaveEvent _) => SetButtonActive(_playAgain.focusController?.focusedElement == _playAgain);
    private void OnButtonFocusIn(FocusInEvent _) => SetButtonActive(true);
    private void OnButtonFocusOut(FocusOutEvent _) => SetButtonActive(false);

    private void SetButtonActive(bool active)
    {
        _buttonActive = active;
        RefreshButton();
    }

    // Filled with the accent when hovered or focused, like the menu buttons
    private void RefreshButton()
    {
        if (_playAgain == null)
            return;

        SetBorderColor(_playAgain, _accent);
        _playAgain.style.backgroundColor = _buttonActive ? _accent : ButtonBackground;
        _playAgain.style.color = _buttonActive ? Color.black : _accent;
    }

    #endregion
}

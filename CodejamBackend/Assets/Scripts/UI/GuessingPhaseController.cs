using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the Guessing, Merge Reveal, and Game Finished overlays in GameScene:
/// - Guessing: card showing the artist's icon, their black-and-white drawing (or stand-in B&W
///   drawing icon) aspect-fitted as large as the card allows, clue prompt, 30s timer, and guess
///   progress. The card spans the screen until the first guess, then docks left so ColorPhysics
///   can render all guessed colour cubes on the open stage (the camera's AreaCameraZoom is
///   pointed at that stage).
/// - Reveal: tints the drawing with the True Hue and shows the bottom Reveal Banner comparing
///   the room's merged colour vs the artist's True Hue.
/// - Finished: full-screen rainbow-shifting completion screen once all drawings are done.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class GuessingPhaseController : MonoBehaviour
{
    [SerializeField, Required] private UIDocument document;
    [SerializeField, Min(1f)] private float finishedRainbowDegPerSec = 24f;

    [Title("Guess Stage")]
    // Camera zoom that frames the guessed cubes (defaults to the one in the scene)
    [SerializeField] private AreaCameraZoom stageZoom;

    // Sidebar width once guesses arrive, as a % of the layout's width (before that it spans it all)
    [SerializeField, Range(20f, 100f)] private float dockedSidebarPercent = 44f;

    // Gap (panel px) kept between the docked sidebar / screen edges and the guessed cubes
    [SerializeField, Min(0f)] private float stagePadding = 40f;

    public event Action NextDrawingRequested;
    public event Action PlayAgainRequested;
    public event Action BackToMenuRequested;

    private VisualElement _root;
    private VisualElement _layout;
    private VisualElement _sidebar;
    private Label _roundLabel;
    private Label _timerLabel;
    private VisualElement _artistCube;
    private VisualElement _artistPfp;
    private Label _artistName;
    private VisualElement _drawingArea;
    private VisualElement _drawingViewport;
    private VisualElement _drawingImage;
    private Label _promptLabel;
    private Label _guessCountLabel;

    // Reveal banner
    private VisualElement _revealBanner;
    private VisualElement _mergedSwatch;
    private Label _mergedHex;
    private VisualElement _targetSwatch;
    private Label _targetHex;
    private Label _matchPercentLabel;
    private Label _revealSubtitle;
    private Button _nextDrawingButton;

    // Finished overlay
    private VisualElement _finishedOverlay;
    private PixelBlastBackground _finishedBackground;
    private TrueHueTitle _finishedTitle;
    private Label _finishedSummary;
    private Button _playAgainButton;
    private Button _backToMenuButton;

    private readonly HashSet<Button> _hoveredButtons = new();
    private readonly HashSet<Button> _focusedButtons = new();
    private readonly List<Texture2D> _generatedStandInTextures = new();

    private Color _accentColor = Color.white;
    private float _finishedHueDeg;
    private float _drawingAspect = 1f;
    private bool _isVisible;
    private bool _isFinishedVisible;
    private bool _isSidebarDocked;

    private void Reset() => document = GetComponent<UIDocument>();

    private void Awake()
    {
        if (document == null)
            document = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        _root = document.rootVisualElement;
        if (_root == null)
            return;

        _layout = _root.Q<VisualElement>("guessing-layout");
        _sidebar = _root.Q<VisualElement>("guessing-sidebar");
        _roundLabel = _root.Q<Label>("round-label");
        _timerLabel = _root.Q<Label>("guess-timer");
        _artistCube = _root.Q<VisualElement>("artist-cube");
        _artistPfp = _root.Q<VisualElement>("artist-pfp");
        _artistName = _root.Q<Label>("artist-name");
        _drawingArea = _root.Q<VisualElement>("drawing-area");
        _drawingViewport = _root.Q<VisualElement>("drawing-viewport");
        _drawingImage = _root.Q<VisualElement>("drawing-image");
        _promptLabel = _root.Q<Label>("drawing-prompt");
        _guessCountLabel = _root.Q<Label>("guess-count");

        _revealBanner = _root.Q<VisualElement>("reveal-banner");
        _mergedSwatch = _root.Q<VisualElement>("merged-swatch");
        _mergedHex = _root.Q<Label>("merged-hex");
        _targetSwatch = _root.Q<VisualElement>("target-swatch");
        _targetHex = _root.Q<Label>("target-hex");
        _matchPercentLabel = _root.Q<Label>("match-percent");
        _revealSubtitle = _root.Q<Label>("reveal-subtitle");
        _nextDrawingButton = _root.Q<Button>("next-drawing");

        _finishedOverlay = _root.Q<VisualElement>("finished-overlay");
        _finishedBackground = _root.Q<PixelBlastBackground>("finished-background");
        _finishedTitle = _root.Q<TrueHueTitle>("finished-title");
        _finishedSummary = _root.Q<Label>("finished-summary");
        _playAgainButton = _root.Q<Button>("play-again");
        _backToMenuButton = _root.Q<Button>("back-to-menu");

        RegisterButton(_nextDrawingButton, () => NextDrawingRequested?.Invoke());
        RegisterButton(_playAgainButton, () => PlayAgainRequested?.Invoke());
        RegisterButton(_backToMenuButton, () => BackToMenuRequested?.Invoke());

        // Refit the drawing whenever its free space changes (sidebar docking, screen resize),
        // and re-aim the camera at the open stage when the layout itself changes size
        _drawingArea?.RegisterCallback<GeometryChangedEvent>(OnDrawingAreaGeometryChanged);
        _layout?.RegisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);

        SetSidebarDocked(_isSidebarDocked);
        SetVisible(_isVisible);
    }

    private void OnDisable()
    {
        _drawingArea?.UnregisterCallback<GeometryChangedEvent>(OnDrawingAreaGeometryChanged);
        _layout?.UnregisterCallback<GeometryChangedEvent>(OnLayoutGeometryChanged);
        if (stageZoom != null)
            stageZoom.ClearFocusRect();

        UnregisterAllButtons();
        _hoveredButtons.Clear();
        _focusedButtons.Clear();
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _generatedStandInTextures.Count; i++)
        {
            if (_generatedStandInTextures[i] != null)
                Destroy(_generatedStandInTextures[i]);
        }
        _generatedStandInTextures.Clear();
    }

    private void Update()
    {
        if (!_isVisible || !_isFinishedVisible || _root == null)
            return;

        _finishedHueDeg = (_finishedHueDeg + Time.deltaTime * finishedRainbowDegPerSec) % 360f;
        Color rainbow = Color.HSVToRGB(_finishedHueDeg / 360f, 1f, 1f);
        ApplyFinishedRainbow(rainbow);
    }

    public void SetVisible(bool visible)
    {
        _isVisible = visible;

        if (_root == null && document != null)
            _root = document.rootVisualElement;

        if (_root != null)
        {
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _root.visible = visible;
        }

        if (!visible)
        {
            _isFinishedVisible = false;
            if (_finishedOverlay != null)
            {
                _finishedOverlay.AddToClassList("is-hidden");
                _finishedOverlay.style.display = DisplayStyle.None;
            }
        }

        UpdateStageFocus();
    }

    /// <summary>
    /// Shows the Guessing Phase HUD with the artist's icon and their black-and-white drawing.
    /// </summary>
    public void BeginGuessingRound(
        GameSessionData.SessionPlayer artist,
        GameSessionData.SubmittedDrawing drawing,
        int drawingIndex,
        int totalDrawings,
        int totalGuessers,
        float durationSeconds)
    {
        _isFinishedVisible = false;
        _accentColor = Color.white;
        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(Color.white);

        SetVisible(true);
        _sidebar?.RemoveFromClassList("is-hidden");
        _revealBanner?.AddToClassList("is-hidden");
        if (_finishedOverlay != null)
        {
            _finishedOverlay.AddToClassList("is-hidden");
            _finishedOverlay.style.display = DisplayStyle.None;
        }

        if (_roundLabel != null)
            _roundLabel.text = $"DRAWING {drawingIndex + 1} / {totalDrawings}";

        SetRemainingSeconds(durationSeconds);
        SetGuessProgress(0, totalGuessers);

        Color artistColor = artist != null ? artist.color : Color.white;
        if (_artistCube != null)
        {
            _artistCube.style.backgroundColor = artistColor;
            SetBorderColor(_artistCube, Color.Lerp(artistColor, Color.white, 0.35f));
        }

        if (_artistPfp != null)
        {
            bool hasPfp = artist != null && artist.avatar != null;
            _artistPfp.style.display = hasPfp ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasPfp)
            {
                _artistPfp.style.backgroundImage = new StyleBackground(artist.avatar);
                _artistPfp.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
            }
        }

        if (_artistName != null)
        {
            _artistName.text = (artist != null ? artist.name : drawing?.playerName ?? "ARTIST").ToUpperInvariant();
            _artistName.style.color = artistColor;
        }

        // Show the drawing strictly in black and white (white strokes on black background)
        Texture2D tex = drawing != null && drawing.texture != null
            ? drawing.texture
            : GetOrCreateStandInDrawing(drawingIndex);

        if (_drawingImage != null && tex != null)
        {
            // Scale-to-fit comes from the USS; the viewport itself is aspect-fitted in FitDrawingToArea
            _drawingImage.style.backgroundImage = new StyleBackground(tex);
            _drawingImage.style.unityBackgroundImageTintColor = Color.white; // Pure B&W during guessing!
        }

        _drawingAspect = tex != null && tex.height > 0 ? (float)tex.width / tex.height : 1f;
        FitDrawingToArea();

        if (_drawingViewport != null)
            SetBorderColor(_drawingViewport, Color.white);

        if (_promptLabel != null)
        {
            string prompt = drawing != null && !string.IsNullOrWhiteSpace(drawing.prompt)
                ? $"\"{drawing.prompt.Trim()}\""
                : "Guess the True Hue!";
            _promptLabel.text = prompt;
        }

        RefreshAllButtons();
    }

    public void SetRemainingSeconds(float remainingSeconds)
    {
        if (_timerLabel != null)
            _timerLabel.text = $"{Mathf.Max(0, Mathf.CeilToInt(remainingSeconds))}s";
    }

    public void SetGuessProgress(int guessedCount, int totalGuessers)
    {
        // First guess in: dock the card left so the guessed cubes get the open stage
        SetSidebarDocked(guessedCount > 0);

        if (_guessCountLabel != null)
            _guessCountLabel.text = $"{guessedCount} / {Mathf.Max(1, totalGuessers)} GUESSED";
    }

    /// <summary>Updates the sidebar while ColorPhysics is actively merging all guessed cubes.</summary>
    public void ShowMergingState()
    {
        // Even with no guesses a fallback cube is spawned, so make room for it
        SetSidebarDocked(true);

        if (_guessCountLabel != null)
            _guessCountLabel.text = "MERGING GUESSES...";
    }

    /// <summary>
    /// Reveals the final merged colour vs the artist's True Hue and tints the B&W drawing
    /// with the revealed True Hue.
    /// </summary>
    public void ShowReveal(Color mergedColor, Color targetColor, bool hasMoreDrawings)
    {
        _accentColor = targetColor;

        // Tint the black-and-white drawing with the True Hue on reveal!
        if (_drawingImage != null)
            _drawingImage.style.unityBackgroundImageTintColor = targetColor;

        if (_drawingViewport != null)
            SetBorderColor(_drawingViewport, targetColor);

        if (_guessCountLabel != null)
            _guessCountLabel.text = "TRUE HUE REVEALED!";

        _revealBanner?.RemoveFromClassList("is-hidden");

        if (_revealBanner != null)
        {
            SetBorderColor(_revealBanner, targetColor);
            _revealBanner.style.scale = new Scale(new Vector3(0.85f, 0.85f, 1f));
            Tween.Custom(
                target: _revealBanner,
                startValue: 0.85f,
                endValue: 1f,
                duration: 0.35f,
                onValueChange: (el, s) => el.style.scale = new Scale(new Vector3(s, s, 1f)),
                ease: Easing.Overshoot(1.15f)
            );
        }

        if (_mergedSwatch != null)
            _mergedSwatch.style.backgroundColor = mergedColor;

        if (_mergedHex != null)
        {
            _mergedHex.text = "#" + ColorUtility.ToHtmlStringRGB(mergedColor);
            _mergedHex.style.color = mergedColor;
        }

        if (_targetSwatch != null)
            _targetSwatch.style.backgroundColor = targetColor;

        if (_targetHex != null)
        {
            _targetHex.text = "#" + ColorUtility.ToHtmlStringRGB(targetColor);
            _targetHex.style.color = targetColor;
        }

        int matchPct = ComputeHueMatchPercent(mergedColor, targetColor);
        GameAudioController.EnsureExists().PlayRevealOutcome(matchPct / 100f);
        if (_matchPercentLabel != null)
        {
            _matchPercentLabel.text = $"{matchPct}% MATCH";
            _matchPercentLabel.style.color = targetColor;
        }

        if (_revealSubtitle != null)
        {
            _revealSubtitle.text = matchPct >= 85
                ? "Spot on! Incredible room vibe!"
                : matchPct >= 65
                    ? "Close match!"
                    : "The True Hue is revealed!";
        }

        if (_nextDrawingButton != null)
            _nextDrawingButton.text = hasMoreDrawings ? "NEXT DRAWING" : "FINISH GAME";

        RefreshAllButtons();
    }

    /// <summary>Shows the rainbow-shifting Game Complete overlay once all drawings are done.</summary>
    public void ShowGameFinished(int totalDrawings)
    {
        _isFinishedVisible = true;
        SetVisible(true);

        _sidebar?.AddToClassList("is-hidden");
        _revealBanner?.AddToClassList("is-hidden");
        if (_finishedOverlay != null)
        {
            _finishedOverlay.RemoveFromClassList("is-hidden");
            _finishedOverlay.style.display = DisplayStyle.Flex;
        }

        if (_finishedTitle != null)
            _finishedTitle.OverlapAnimate = true;

        if (_finishedSummary != null)
        {
            string plural = totalDrawings == 1 ? "drawing has" : "drawings have";
            _finishedSummary.text = $"All {totalDrawings} {plural} been guessed and revealed!";
        }

        ApplyFinishedRainbow(Color.HSVToRGB(_finishedHueDeg / 360f, 1f, 1f));
    }

    private void ApplyFinishedRainbow(Color rainbow)
    {
        _accentColor = rainbow;
        Color dim = new(rainbow.r, rainbow.g, rainbow.b, 0.78f);

        _finishedOverlay?.Query(className: "rainbow-text").ForEach(e => e.style.color = rainbow);
        _finishedOverlay?.Query(className: "rainbow-dim-text").ForEach(e => e.style.color = dim);
        _finishedOverlay?.Query(className: "rainbow-border").ForEach(e => SetBorderColor(e, rainbow));

        if (_finishedBackground != null)
            _finishedBackground.accentColor = rainbow;

        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(rainbow);

        if (_finishedTitle != null)
            _finishedTitle.BaseColor = rainbow;

        RefreshButton(_playAgainButton);
        RefreshButton(_backToMenuButton);
    }

    #region Drawing & Stage Layout

    /// <summary>
    /// Docks the card to the left (guesses are in) or lets it span the screen again. The USS
    /// width transition animates it, and the drawing refits every frame via GeometryChangedEvent.
    /// </summary>
    private void SetSidebarDocked(bool docked)
    {
        _isSidebarDocked = docked;

        if (_sidebar != null)
        {
            _sidebar.style.width = docked
                ? new StyleLength(Length.Percent(dockedSidebarPercent))
                : new StyleLength(StyleKeyword.Null);
        }

        UpdateStageFocus();
    }

    private void OnDrawingAreaGeometryChanged(GeometryChangedEvent evt) => FitDrawingToArea();

    private void OnLayoutGeometryChanged(GeometryChangedEvent evt) => UpdateStageFocus();

    /// <summary>
    /// Sizes the bordered drawing viewport to the largest rect with the drawing's aspect ratio
    /// that fits the drawing area, centred in it (no stretching or cropping).
    /// </summary>
    private void FitDrawingToArea()
    {
        if (_drawingArea == null || _drawingViewport == null)
            return;

        Rect area = _drawingArea.contentRect;
        if (!IsUsableRect(area))
            return;

        IResolvedStyle viewportStyle = _drawingViewport.resolvedStyle;
        float borderX = viewportStyle.borderLeftWidth + viewportStyle.borderRightWidth;
        float borderY = viewportStyle.borderTopWidth + viewportStyle.borderBottomWidth;

        float aspect = _drawingAspect > 0f ? _drawingAspect : 1f;
        float innerWidth = Mathf.Max(0f, area.width - borderX);
        float innerHeight = Mathf.Max(0f, area.height - borderY);
        float fitWidth = Mathf.Min(innerWidth, innerHeight * aspect);
        float fitHeight = fitWidth / aspect;

        float width = Mathf.Floor(fitWidth + borderX);
        float height = Mathf.Floor(fitHeight + borderY);

        _drawingViewport.style.width = width;
        _drawingViewport.style.height = height;
        _drawingViewport.style.left = area.x + Mathf.Round((area.width - width) * 0.5f);
        _drawingViewport.style.top = area.y + Mathf.Round((area.height - height) * 0.5f);
    }

    private AreaCameraZoom GetStageZoom()
    {
        if (stageZoom == null)
            stageZoom = FindAnyObjectByType<AreaCameraZoom>();

        return stageZoom;
    }

    /// <summary>
    /// Points the camera's AreaCameraZoom at the open stage right of the docked card, so the
    /// guessed cubes are framed in the space the drawing leaves free instead of behind it.
    /// </summary>
    private void UpdateStageFocus()
    {
        AreaCameraZoom zoom = GetStageZoom();
        if (zoom == null)
            return;

        bool framesStage = _isVisible && !_isFinishedVisible && _isSidebarDocked;
        if (framesStage && TryGetStageViewportRect(out Rect viewportRect))
            zoom.SetFocusRect(viewportRect);
        else
            zoom.ClearFocusRect();
    }

    /// <summary>
    /// The open stage right of the docked card, in camera viewport coordinates (0-1, y up).
    /// Uses the docked width rather than the card's current one, so the camera heads straight
    /// for its final framing while the card's width transition is still running.
    /// </summary>
    private bool TryGetStageViewportRect(out Rect viewportRect)
    {
        viewportRect = default;
        if (_layout == null || _root?.panel == null)
            return false;

        Rect screen = _root.panel.visualTree.worldBound;
        Rect layout = _layout.worldBound;
        if (!IsUsableRect(screen) || !IsUsableRect(layout))
            return false;

        // The card's % width is relative to the layout's content box
        IResolvedStyle layoutStyle = _layout.resolvedStyle;
        float contentLeft = layout.xMin + layoutStyle.borderLeftWidth + layoutStyle.paddingLeft;
        float contentWidth = layout.width
            - layoutStyle.borderLeftWidth - layoutStyle.paddingLeft
            - layoutStyle.paddingRight - layoutStyle.borderRightWidth;
        float sidebarRight = contentLeft + contentWidth * dockedSidebarPercent / 100f;

        float xMin = sidebarRight + stagePadding;
        float xMax = screen.xMax - stagePadding;
        float yMin = screen.yMin + stagePadding;
        float yMax = screen.yMax - stagePadding;
        if (xMax - xMin < 1f || yMax - yMin < 1f)
            return false;

        // Panel space is y-down; viewport space has y up
        viewportRect = Rect.MinMaxRect(
            (xMin - screen.xMin) / screen.width,
            (screen.yMax - yMax) / screen.height,
            (xMax - screen.xMin) / screen.width,
            (screen.yMax - yMin) / screen.height
        );
        return true;
    }

    private static bool IsUsableRect(Rect rect) =>
        !float.IsNaN(rect.width) && !float.IsNaN(rect.height) && rect.width > 0f && rect.height > 0f;

    #endregion

    private static int ComputeHueMatchPercent(Color a, Color b)
    {
        Color.RGBToHSV(a, out float ha, out float sa, out float va);
        Color.RGBToHSV(b, out float hb, out float sb, out float vb);

        float hueDiff = Mathf.Abs(Mathf.DeltaAngle(ha * 360f, hb * 360f)) / 180f;
        float satDiff = Mathf.Abs(sa - sb);
        float valDiff = Mathf.Abs(va - vb);

        float error = hueDiff * 0.7f + satDiff * 0.15f + valDiff * 0.15f;
        return Mathf.Clamp(Mathf.RoundToInt((1f - error) * 100f), 0, 100);
    }

    /// <summary>
    /// Generates a crisp 128x128 black-and-white stand-in drawing icon (sun, mountain, flower, house)
    /// when testing without a real phone drawing payload.
    /// </summary>
    private Texture2D GetOrCreateStandInDrawing(int variantIndex)
    {
        const int size = 128;
        Texture2D tex = new(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        Color32 bg = new(0, 0, 0, 255);
        Color32 white = new(255, 255, 255, 255);
        Color32[] pixels = new Color32[size * size];

        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = bg;

        void Dot(int cx, int cy, int r)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r)
                        continue;

                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                        pixels[y * size + x] = white;
                }
            }
        }

        void Line(int x0, int y0, int x1, int y1, int r)
        {
            int steps = Mathf.Max(1, Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)));
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                Dot(Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), r);
            }
        }

        void Circle(int cx, int cy, int radius, int thickness)
        {
            for (int deg = 0; deg < 360; deg += 3)
            {
                float rad = deg * Mathf.Deg2Rad;
                Dot(cx + Mathf.RoundToInt(Mathf.Cos(rad) * radius), cy + Mathf.RoundToInt(Mathf.Sin(rad) * radius), thickness);
            }
        }

        switch (variantIndex % 4)
        {
            case 0: // Sun & horizon waves
                Circle(64, 78, 22, 3);
                for (int i = 0; i < 8; i++)
                {
                    float rad = i * 45f * Mathf.Deg2Rad;
                    Line(
                        64 + Mathf.RoundToInt(Mathf.Cos(rad) * 30),
                        78 + Mathf.RoundToInt(Mathf.Sin(rad) * 30),
                        64 + Mathf.RoundToInt(Mathf.Cos(rad) * 42),
                        78 + Mathf.RoundToInt(Mathf.Sin(rad) * 42),
                        2
                    );
                }
                Line(16, 36, 112, 36, 3);
                Line(26, 22, 102, 22, 2);
                break;

            case 1: // Mountain peaks & moon
                Line(14, 24, 52, 86, 3);
                Line(52, 86, 84, 34, 3);
                Line(64, 62, 92, 98, 3);
                Line(92, 98, 116, 24, 3);
                Line(14, 24, 116, 24, 3);
                Circle(30, 98, 12, 2);
                break;

            case 2: // Flower
                Circle(64, 80, 14, 3);
                for (int i = 0; i < 6; i++)
                {
                    float rad = i * 60f * Mathf.Deg2Rad;
                    Circle(64 + Mathf.RoundToInt(Mathf.Cos(rad) * 24), 80 + Mathf.RoundToInt(Mathf.Sin(rad) * 24), 10, 2);
                }
                Line(64, 64, 64, 18, 3);
                Line(64, 38, 86, 50, 2);
                Line(64, 32, 42, 44, 2);
                break;

            default: // Little house
                Line(28, 24, 28, 72, 3);
                Line(100, 24, 100, 72, 3);
                Line(28, 24, 100, 24, 3);
                Line(20, 70, 64, 106, 3);
                Line(64, 106, 108, 70, 3);
                Line(54, 24, 54, 52, 2);
                Line(74, 24, 74, 52, 2);
                Line(54, 52, 74, 52, 2);
                break;
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        _generatedStandInTextures.Add(tex);
        return tex;
    }

    #region Buttons

    private readonly List<(Button btn, Action cb)> _registeredButtons = new();

    private void RegisterButton(Button button, Action onClick)
    {
        if (button == null)
            return;

        button.clicked += onClick;
        button.clicked += PlayButtonClickSfx;
        button.RegisterCallback<PointerEnterEvent, Button>(OnButtonEnter, button);
        button.RegisterCallback<PointerLeaveEvent, Button>(OnButtonLeave, button);
        button.RegisterCallback<FocusInEvent, Button>(OnButtonFocusIn, button);
        button.RegisterCallback<FocusOutEvent, Button>(OnButtonFocusOut, button);
        _registeredButtons.Add((button, onClick));
    }

    private void UnregisterAllButtons()
    {
        for (int i = 0; i < _registeredButtons.Count; i++)
        {
            (Button btn, Action cb) = _registeredButtons[i];
            if (btn == null)
                continue;

            btn.clicked -= cb;
            btn.clicked -= PlayButtonClickSfx;
            btn.UnregisterCallback<PointerEnterEvent, Button>(OnButtonEnter);
            btn.UnregisterCallback<PointerLeaveEvent, Button>(OnButtonLeave);
            btn.UnregisterCallback<FocusInEvent, Button>(OnButtonFocusIn);
            btn.UnregisterCallback<FocusOutEvent, Button>(OnButtonFocusOut);
        }
        _registeredButtons.Clear();
    }

    private static void PlayButtonClickSfx() =>
        GameAudioController.EnsureExists().PlayButtonClick();

    private void OnButtonEnter(PointerEnterEvent evt, Button button)
    {
        _hoveredButtons.Add(button);
        RefreshButton(button);
    }

    private void OnButtonLeave(PointerLeaveEvent evt, Button button)
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

    private void RefreshAllButtons()
    {
        RefreshButton(_nextDrawingButton);
        RefreshButton(_playAgainButton);
        RefreshButton(_backToMenuButton);
    }

    private void RefreshButton(Button button)
    {
        if (button == null)
            return;

        bool active = (_hoveredButtons.Contains(button) || _focusedButtons.Contains(button)) &&
                      button.enabledSelf;

        SetBorderColor(button, _accentColor);
        button.style.backgroundColor = active ? _accentColor : new Color(0f, 0f, 0f, 0.55f);
        button.style.color = active ? Color.black : _accentColor;
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

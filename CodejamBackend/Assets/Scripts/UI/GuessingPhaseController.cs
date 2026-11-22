using System;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the Guessing, Merge Reveal, and Game Finished overlays in GameScene:
/// - Guessing: centred card showing the artist's icon, their black-and-white drawing (or stand-in
///   B&W drawing icon) aspect-fitted as large as the card allows, clue prompt, 30s timer, and guess
///   progress. Each player who guesses pops in around the card, clockwise from the top-left like
///   the join screen (PlayerRingLayout). When guessing ends, GameManager bursts each of them into
///   cubes of their guessed colour (TryGetGuessedPlayerWorldPosition / BurstGuessedPlayer) and
///   the card is hidden (HideCard) while the cubes merge.
/// - Reveal: tints the drawing with the True Hue and shows the bottom Reveal Banner comparing
///   the room's merged colour vs the artist's True Hue.
/// - Finished: full-screen rainbow-shifting completion screen once all drawings are done.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class GuessingPhaseController : MonoBehaviour
{
    [SerializeField, Required] private UIDocument document;
    [SerializeField, Min(1f)] private float finishedRainbowDegPerSec = 24f;

    [Title("Card Hide")]
    // How long the card takes to fade and shrink away once the guessed players burst
    [SerializeField, Min(0.01f)] private float cardHideDuration = 0.3f;

    [Title("Guesser Pop")]
    // How long a guesser's icon takes to swell and fade as their colour bursts out of it
    [SerializeField, Min(0.01f)] private float iconPopDuration = 0.15f;

    // How big the icon swells to (1 = no swell) as it pops
    [SerializeField, Min(1f)] private float iconPopScale = 1.5f;

    public event Action NextDrawingRequested;
    public event Action PlayAgainRequested;
    public event Action BackToMenuRequested;

    private VisualElement _root;
    private VisualElement _card;
    private VisualElement _iconsLayer;
    private Label _roundLabel;
    private Label _timerLabel;
    private UiFx.TimerWarning _timerWarning; // red + pop + tick in the last seconds
    private int _shownGuessCount = -1;
    private VisualElement _artistCube;
    private VisualElement _artistPfp;
    private Label _artistName;
    private VisualElement _drawingArea;
    private VisualElement _drawingViewport;
    private VisualElement _drawingImage;
    private Label _promptLabel;
    private WaveText _guessCountLabel; // "MERGING GUESSES..." dots hop

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

    // Guessed players' icons by player id, and in seat order (seat = order they guessed in)
    private readonly Dictionary<string, VisualElement> _guessedIcons = new();
    private readonly List<VisualElement> _orderedIcons = new();
    private Tween _cardHideTween;

    private Color _accentColor = Color.white;
    private float _finishedHueDeg;
    private float _drawingAspect = 1f;
    private bool _isVisible;
    private bool _isFinishedVisible;

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

        _card = _root.Q<VisualElement>("guessing-card");
        _iconsLayer = _root.Q<VisualElement>("guessed-icons-layer");
        _roundLabel = _root.Q<Label>("round-label");
        _timerLabel = _root.Q<Label>("guess-timer");
        _timerWarning = new UiFx.TimerWarning(_timerLabel);
        _artistCube = _root.Q<VisualElement>("artist-cube");
        _artistPfp = _root.Q<VisualElement>("artist-pfp");
        _artistName = _root.Q<Label>("artist-name");
        _drawingArea = _root.Q<VisualElement>("drawing-area");
        _drawingViewport = _root.Q<VisualElement>("drawing-viewport");
        _drawingImage = _root.Q<VisualElement>("drawing-image");
        _promptLabel = _root.Q<Label>("drawing-prompt");
        _guessCountLabel = _root.Q<WaveText>("guess-count");

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

        // Refit the drawing whenever its free space changes (screen resize), and keep the
        // guessed players on their seats as the screen changes size
        _drawingArea?.RegisterCallback<GeometryChangedEvent>(OnDrawingAreaGeometryChanged);
        _iconsLayer?.RegisterCallback<GeometryChangedEvent>(OnIconsLayerGeometryChanged);

        SetVisible(_isVisible);
    }

    private void OnDisable()
    {
        _drawingArea?.UnregisterCallback<GeometryChangedEvent>(OnDrawingAreaGeometryChanged);
        _iconsLayer?.UnregisterCallback<GeometryChangedEvent>(OnIconsLayerGeometryChanged);
        StopCardHideTween();

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
        ShowCard();
        ClearGuessedPlayers();
        _revealBanner?.AddToClassList("is-hidden");
        if (_finishedOverlay != null)
        {
            _finishedOverlay.AddToClassList("is-hidden");
            _finishedOverlay.style.display = DisplayStyle.None;
        }

        if (_roundLabel != null)
            _roundLabel.text = $"DRAWING {drawingIndex + 1} / {totalDrawings}";

        _timerWarning?.Reset();
        _shownGuessCount = -1;
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

        _timerWarning?.Update(remainingSeconds);
    }

    public void SetGuessProgress(int guessedCount, int totalGuessers)
    {
        if (_guessCountLabel != null)
            _guessCountLabel.Text = $"{guessedCount} / {Mathf.Max(1, totalGuessers)} GUESSED";

        // Pop when a new guess lands (not when the round resets to 0)
        if (_shownGuessCount >= 0 && guessedCount > _shownGuessCount)
            UiFx.Pop(_guessCountLabel);
        _shownGuessCount = guessedCount;
    }

    /// <summary>Says on the card that nobody guessed a colour, so this drawing is skipped.</summary>
    public void ShowSkipped()
    {
        ShowCard();

        if (_guessCountLabel != null)
        {
            _guessCountLabel.Text = "NO GUESSES - SKIPPING...";
            UiFx.Pop(_guessCountLabel);
        }
    }

    /// <summary>Updates the card's footer once guessing is over and the guesses start merging.</summary>
    public void ShowMergingState()
    {
        if (_guessCountLabel != null)
            _guessCountLabel.Text = "MERGING GUESSES...";
    }

    /// <summary>
    /// Reveals the final merged colour vs the artist's True Hue and tints the B&W drawing
    /// with the revealed True Hue.
    /// </summary>
    public void ShowReveal(Color mergedColor, Color targetColor, bool hasMoreDrawings)
    {
        _accentColor = targetColor;

        // HideCard put it away for the merge; this reveal shows the tinted drawing on it
        ShowCard();

        // Tint the black-and-white drawing with the True Hue on reveal!
        if (_drawingImage != null)
            _drawingImage.style.unityBackgroundImageTintColor = targetColor;

        if (_drawingViewport != null)
            SetBorderColor(_drawingViewport, targetColor);

        if (_guessCountLabel != null)
            _guessCountLabel.Text = "TRUE HUE REVEALED!";

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

        StopCardHideTween();
        _card?.AddToClassList("is-hidden");
        ClearGuessedPlayers();
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

    #region Guessed Players

    /// <summary>
    /// Pops in a player who has guessed at the next seat around the card (clockwise from the
    /// top-left, like the join screen), in their player colour so the guess itself stays secret.
    /// </summary>
    public void MarkPlayerGuessed(GameSessionData.SessionPlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.playerId) || _iconsLayer == null)
            return;

        if (_guessedIcons.ContainsKey(player.playerId))
            return;

        VisualElement icon = CreatePlayerIconElement(player);
        _iconsLayer.Add(icon);
        _guessedIcons[player.playerId] = icon;
        _orderedIcons.Add(icon);

        LayoutGuessedPlayers();
        UiFx.Pop(icon, from: 0.15f);
        GameAudioController.EnsureExists().PlayPlayerJoin();

        if (PersistentPixelBlastBackground.Instance != null)
            PersistentPixelBlastBackground.Instance.AddRipple(SeatPanelPosition(_orderedIcons.Count - 1));
    }

    /// <summary>
    /// Where a guessed player's cube sits in the world, on the z = <paramref name="worldZ"/>
    /// plane, so their guess can burst out of it. False if they have no icon on screen.
    /// </summary>
    public bool TryGetGuessedPlayerWorldPosition(string playerId, Camera cam, float worldZ, out Vector3 worldPosition)
    {
        worldPosition = default;
        if (cam == null || _root?.panel == null || string.IsNullOrEmpty(playerId))
            return false;

        if (!_guessedIcons.TryGetValue(playerId, out VisualElement icon) || icon.childCount == 0)
            return false;

        Rect screen = _root.panel.visualTree.worldBound;
        Rect cube = icon[0].worldBound; // The coloured cube (the name label sits below it)
        if (!IsUsableRect(screen) || !IsUsableRect(cube))
            return false;

        // Panel space is y-down; viewport space has y up
        Vector3 viewport = new(
            (cube.center.x - screen.xMin) / screen.width,
            1f - (cube.center.y - screen.yMin) / screen.height,
            worldZ - cam.transform.position.z
        );

        worldPosition = cam.ViewportToWorldPoint(viewport);
        worldPosition.z = worldZ;
        return true;
    }

    /// <summary>
    /// Pops a guessed player's icon as their guess bursts out of it: a quick swell while it fades,
    /// uncovering the cubes growing underneath.
    /// </summary>
    public void BurstGuessedPlayer(string playerId)
    {
        if (string.IsNullOrEmpty(playerId) || !_guessedIcons.TryGetValue(playerId, out VisualElement icon))
            return;

        // Ends hidden rather than removed, so the others keep their seats mid-burst
        Tween.StopAll(icon); // e.g. a pop-in still finishing
        Tween.Custom(
            target: icon,
            startValue: 0f,
            endValue: 1f,
            duration: iconPopDuration,
            onValueChange: (el, t) =>
            {
                float s = Mathf.Lerp(1f, iconPopScale, t);
                el.style.scale = new Scale(new Vector3(s, s, 1f));
                el.style.opacity = 1f - t;
            },
            ease: Ease.OutQuad
        ).OnComplete(icon, el => el.style.visibility = Visibility.Hidden);

        if (PersistentPixelBlastBackground.Instance != null)
            PersistentPixelBlastBackground.Instance.AddRipple(SeatPanelPosition(_orderedIcons.IndexOf(icon)));
    }

    private void ClearGuessedPlayers()
    {
        foreach (VisualElement icon in _orderedIcons)
            icon?.RemoveFromHierarchy();

        _guessedIcons.Clear();
        _orderedIcons.Clear();
        _iconsLayer?.RemoveFromClassList(PlayerRingLayout.CrowdedClass);
    }

    private void OnIconsLayerGeometryChanged(GeometryChangedEvent evt) => LayoutGuessedPlayers();

    private void LayoutGuessedPlayers()
    {
        if (_iconsLayer == null || _orderedIcons.Count == 0)
            return;

        _iconsLayer.EnableInClassList(PlayerRingLayout.CrowdedClass, PlayerRingLayout.IsCrowded(_orderedIcons.Count));

        for (int i = 0; i < _orderedIcons.Count; i++)
        {
            Vector2 pos = SeatPanelPosition(i);
            _orderedIcons[i].style.left = pos.x;
            _orderedIcons[i].style.top = pos.y;
        }
    }

    private Vector2 SeatPanelPosition(int seatIndex) =>
        PlayerRingLayout.PanelPosition(_iconsLayer.contentRect, seatIndex, _orderedIcons.Count);

    /// <summary>Coloured cube + optional PFP + badge, name underneath (as in the drawing phase).</summary>
    private static VisualElement CreatePlayerIconElement(GameSessionData.SessionPlayer player)
    {
        VisualElement container = new() { pickingMode = PickingMode.Ignore };
        container.AddToClassList("guessed-player-icon");

        VisualElement cube = new() { pickingMode = PickingMode.Ignore };
        cube.AddToClassList("guessed-player-icon__cube");
        cube.style.backgroundColor = player.color;
        SetBorderColor(cube, Color.Lerp(player.color, Color.white, 0.35f));

        if (player.avatar != null)
        {
            VisualElement pfp = new() { pickingMode = PickingMode.Ignore };
            pfp.AddToClassList("guessed-player-icon__pfp");
            pfp.style.backgroundImage = new StyleBackground(player.avatar);
            pfp.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
            cube.Add(pfp);
        }

        Label check = new("GUESSED") { pickingMode = PickingMode.Ignore };
        check.AddToClassList("guessed-player-icon__check");
        check.style.color = player.color;
        SetBorderColor(check, player.color);
        cube.Add(check);

        Label nameLabel = new((player.name ?? "PLAYER").ToUpperInvariant()) { pickingMode = PickingMode.Ignore };
        nameLabel.AddToClassList("guessed-player-icon__name");
        nameLabel.style.color = player.color;
        SetBorderColor(nameLabel, new Color(player.color.r, player.color.g, player.color.b, 0.75f));

        container.Add(cube);
        container.Add(nameLabel);
        return container;
    }

    #endregion

    #region Card

    /// <summary>Fades and shrinks the artist's card away (the guesses are about to merge).</summary>
    public void HideCard()
    {
        if (_card == null || _card.ClassListContains("is-hidden"))
            return;

        StopCardHideTween();
        _cardHideTween = Tween.Custom(
            target: _card,
            startValue: 1f,
            endValue: 0f,
            duration: cardHideDuration,
            onValueChange: (el, t) =>
            {
                el.style.opacity = t;
                float s = Mathf.Lerp(0.85f, 1f, t);
                el.style.scale = new Scale(new Vector3(s, s, 1f));
            },
            ease: Ease.InQuad
        ).OnComplete(_card, el => el.AddToClassList("is-hidden"));
    }

    private void ShowCard()
    {
        StopCardHideTween();
        if (_card == null)
            return;

        _card.RemoveFromClassList("is-hidden");
        _card.style.opacity = StyleKeyword.Null;
        _card.style.scale = StyleKeyword.Null;
    }

    private void StopCardHideTween()
    {
        if (_cardHideTween.isAlive)
            _cardHideTween.Stop();
    }

    #endregion

    #region Drawing Layout

    private void OnDrawingAreaGeometryChanged(GeometryChangedEvent evt) => FitDrawingToArea();

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

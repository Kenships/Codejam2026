using System;
using System.Collections;
using System.Collections.Generic;
using Alchemy.Inspector;
using Core.AudioSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the Drawing Phase console UI in GameScene:
/// - Runs the frontend's loading rainbow colour shift (24 deg/s HSV cycle) across TrueHueTitle,
///   PixelBlastBackground, card borders, and timer.
/// - Displays the drawing countdown timer and submission count.
/// - Whenever a player submits their drawing (via relay), pops in that player's
///   icon (coloured cube + optional PFP + name underneath) around the center card in the same
///   top-left clockwise circle as the join screen.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class DrawingPhaseController : MonoBehaviour
{
    // Icons fill an inner ring of 12, then an outer ring for the rest (up to the 32-player limit).
    // While the outer ring is in use, both rings spread out and icons shrink (is-crowded).
    private const int InnerRingSeats = 12;
    private const int OuterRingSeats = GameSessionData.MaxPlayersLimit - InnerRingSeats;
    private const string CrowdedClass = "is-crowded";

    [SerializeField, Required] private UIDocument document;
    [SerializeField, AssetsOnly] private AudioClip submitSound;

    [Title("Rainbow Loading Shift")]
    [Tooltip("Matches frontend PlayPage.tsx (24 deg/s -> ~15s per full rainbow cycle).")]
    [SerializeField, Min(1f)] private float rainbowDegreesPerSecond = 24f;

    private VisualElement _root;
    private VisualElement _drawingRoot;
    private VisualElement _drawingHeader;
    private PixelBlastBackground _background;
    private TrueHueTitle _title;
    private Label _subtitle;
    private VisualElement _iconsLayer;
    private VisualElement _drawingCard;
    private Label _drawingTitle;
    private Label _submissionCount;
    private Label _timerValue;
    private Label _description;

    // Artist spotlight elements (shown after all player icons merge into the artist)
    private VisualElement _artistSpotlight;
    private Label _artistSpotlightKicker;
    private VisualElement _artistSpotlightCube;
    private VisualElement _artistSpotlightPfp;
    private Label _artistSpotlightName;

    private readonly Dictionary<string, VisualElement> _submittedIcons = new();
    private readonly List<VisualElement> _orderedIcons = new();

    private float _rainbowHueDeg;
    private Color _currentRainbowColor = Color.red;
    private int _expectedPlayerCount;
    private bool _isVisible;
    private bool _isTransitioning;
    private Coroutine _transitionRoutine;

    private void Reset()
    {
        document = GetComponent<UIDocument>();
        AutoAssignDefaultAssets();
    }

    private void Awake()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        AutoAssignDefaultAssets();
    }

    private void AutoAssignDefaultAssets()
    {
#if UNITY_EDITOR
        if (submitSound == null)
            submitSound = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Media/BubbleMerge.mp3");
#endif
    }

    private void OnEnable()
    {
        if (document == null)
            document = GetComponent<UIDocument>();

        _root = document.rootVisualElement;
        if (_root == null)
            return;

        _drawingRoot = _root.Q<VisualElement>("drawing-root");
        _drawingHeader = _root.Q<VisualElement>("drawing-header");
        _background = _root.Q<PixelBlastBackground>();
        _title = _root.Q<TrueHueTitle>("title");
        _subtitle = _root.Q<Label>("subtitle");
        _iconsLayer = _root.Q<VisualElement>("submitted-icons-layer");
        _drawingCard = _root.Q<VisualElement>("drawing-card");
        _drawingTitle = _root.Q<Label>("drawing-title");
        _submissionCount = _root.Q<Label>("submission-count");
        _timerValue = _root.Q<Label>("drawing-timer");
        _description = _root.Q<Label>("drawing-description");

        _artistSpotlight = _root.Q<VisualElement>("artist-spotlight");
        _artistSpotlightKicker = _root.Q<Label>("artist-spotlight-kicker");
        _artistSpotlightCube = _root.Q<VisualElement>("artist-spotlight-cube");
        _artistSpotlightPfp = _root.Q<VisualElement>("artist-spotlight-pfp");
        _artistSpotlightName = _root.Q<Label>("artist-spotlight-name");

        if (_title != null)
            _title.OverlapAnimate = true;

        ApplyRainbowColor(Color.HSVToRGB(0f, 1f, 1f));
        SetVisible(_isVisible);
    }

    private void Update()
    {
        if (!_isVisible || _root == null)
            return;

        // Smooth rainbow colour shift matching PlayPage.tsx (24 deg/s)
        _rainbowHueDeg = (_rainbowHueDeg + Time.deltaTime * rainbowDegreesPerSecond) % 360f;
        Color rainbow = Color.HSVToRGB(_rainbowHueDeg / 360f, 1f, 1f);
        ApplyRainbowColor(rainbow);

        if (!_isTransitioning)
            LayoutSubmittedIcons();
    }

    /// <summary>Shows or hides the entire Drawing Phase UI overlay.</summary>
    public void SetVisible(bool visible)
    {
        _isVisible = visible;

        if (!visible && _transitionRoutine != null)
        {
            StopCoroutine(_transitionRoutine);
            _transitionRoutine = null;
            _isTransitioning = false;
        }

        if (_root == null && document != null)
            _root = document.rootVisualElement;

        if (_root != null)
        {
            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _root.visible = visible;
        }

        if (_drawingRoot != null)
        {
            _drawingRoot.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            _drawingRoot.visible = visible;
        }

        if (!visible && _title != null)
            _title.style.display = DisplayStyle.None;
        else if (visible && _title != null && !_isTransitioning)
            _title.style.display = DisplayStyle.Flex;
    }

    /// <summary>Resets the Drawing Phase UI for a fresh drawing round.</summary>
    public void BeginDrawingRound(int expectedPlayers, float durationSeconds)
    {
        if (_transitionRoutine != null)
        {
            StopCoroutine(_transitionRoutine);
            _transitionRoutine = null;
        }

        _isTransitioning = false;
        _expectedPlayerCount = Mathf.Max(1, expectedPlayers);

        if (_drawingHeader != null)
            _drawingHeader.style.display = DisplayStyle.Flex;

        if (_title != null)
            _title.style.display = DisplayStyle.Flex;

        if (_drawingCard != null)
            _drawingCard.style.display = DisplayStyle.Flex;

        if (_subtitle != null)
            _subtitle.text = "Everyone is drawing their secret hue";

        _artistSpotlight?.AddToClassList("is-hidden");

        ClearSubmittedIcons();
        SetRemainingSeconds(durationSeconds);
        RefreshProgressLabels();
        SetVisible(true);
    }

    /// <summary>
    /// Merges all player icons into the center of the screen and transforms them into
    /// the spotlight icon of the artist whose drawing's hue is about to be guessed.
    /// </summary>
    public void PlayArtistMergeTransition(
        IReadOnlyList<GameSessionData.SessionPlayer> allPlayers,
        GameSessionData.SessionPlayer targetArtist,
        int drawingNumber,
        int totalDrawings,
        Action onComplete)
    {
        if (_transitionRoutine != null)
            StopCoroutine(_transitionRoutine);

        _transitionRoutine = StartCoroutine(
            ArtistMergeTransitionRoutine(allPlayers, targetArtist, drawingNumber, totalDrawings, onComplete)
        );
    }

    private IEnumerator ArtistMergeTransitionRoutine(
        IReadOnlyList<GameSessionData.SessionPlayer> allPlayers,
        GameSessionData.SessionPlayer targetArtist,
        int drawingNumber,
        int totalDrawings,
        Action onComplete)
    {
        _isTransitioning = true;
        SetVisible(true);

        // Hide the TrueHue header & drawing card while player icons merge into the artist icon
        if (_drawingHeader != null)
            _drawingHeader.style.display = DisplayStyle.None;

        if (_title != null)
            _title.style.display = DisplayStyle.None;

        if (_drawingCard != null)
            _drawingCard.style.display = DisplayStyle.None;

        if (_subtitle != null)
            _subtitle.text = $"DRAWING {drawingNumber} OF {totalDrawings}";

        _artistSpotlight?.AddToClassList("is-hidden");

        // Ensure all player icons are placed around the clockwise ring before merging
        if (_orderedIcons.Count == 0 && allPlayers != null && _iconsLayer != null)
        {
            for (int i = 0; i < allPlayers.Count; i++)
            {
                GameSessionData.SessionPlayer p = allPlayers[i];
                if (p == null)
                    continue;

                VisualElement iconRoot = CreatePlayerIconElement(p);
                _iconsLayer.Add(iconRoot);
                _submittedIcons[p.playerId] = iconRoot;
                _orderedIcons.Add(iconRoot);
            }
            LayoutSubmittedIcons();
        }

        yield return new WaitForSeconds(0.2f);

        Rect rect = _iconsLayer != null ? _iconsLayer.contentRect : new Rect(0, 0, 1920, 1080);
        float cx = (rect.width > 1f ? rect.width : 1920f) * 0.5f;
        float cy = (rect.height > 1f ? rect.height : 1080f) * 0.54f;

        // Capture starting positions of all icons
        Vector2[] startPositions = new Vector2[_orderedIcons.Count];
        for (int i = 0; i < _orderedIcons.Count; i++)
            startPositions[i] = ComputeClockwisePanelPosition(i);

        // Pull all player icons smoothly into the center to merge
        const float mergeDuration = 0.65f;
        float elapsed = 0f;
        while (elapsed < mergeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / mergeDuration);
            // Ease.InBack-style anticipation then crisp pull to center
            float eased = t * t * (2.2f * t - 1.2f);
            float clampedEased = Mathf.Clamp(eased, -0.15f, 1f);
            float scale = Mathf.Lerp(1f, 0.35f, t * t);

            for (int i = 0; i < _orderedIcons.Count; i++)
            {
                VisualElement icon = _orderedIcons[i];
                if (icon == null)
                    continue;

                Vector2 pos = Vector2.LerpUnclamped(startPositions[i], new Vector2(cx, cy), clampedEased);
                icon.style.left = pos.x;
                icon.style.top = pos.y;
                icon.style.scale = new Scale(new Vector3(scale, scale, 1f));
                icon.style.opacity = Mathf.Lerp(1f, 0.2f, t * t);
            }

            yield return null;
        }

        // Clear converging icons and burst into the artist's spotlight icon!
        ClearSubmittedIcons();

        if (_background != null)
            _background.AddRipple(new Vector2(cx, cy));
        else if (PersistentPixelBlastBackground.Instance != null)
            PersistentPixelBlastBackground.Instance.AddRipple(new Vector2(cx, cy));

        if (submitSound != null && AudioManager.Instance != null)
        {
            AudioManager.Instance.NewAudio(submitSound)
                .RandomizePitch()
                .Create()
                .Play();
        }

        ShowArtistSpotlight(targetArtist, drawingNumber, totalDrawings);

        yield return new WaitForSeconds(1.35f);

        _artistSpotlight?.AddToClassList("is-hidden");
        _isTransitioning = false;
        _transitionRoutine = null;

        onComplete?.Invoke();
    }

    private void ShowArtistSpotlight(GameSessionData.SessionPlayer artist, int drawingNumber, int totalDrawings)
    {
        if (_artistSpotlight == null || artist == null)
            return;

        _artistSpotlight.RemoveFromClassList("is-hidden");

        if (_artistSpotlightKicker != null)
        {
            _artistSpotlightKicker.text = $"NOW GUESSING  ({drawingNumber}/{totalDrawings})";
            _artistSpotlightKicker.style.color = artist.color;
        }

        if (_artistSpotlightCube != null)
        {
            _artistSpotlightCube.style.backgroundColor = artist.color;
            SetBorderColor(_artistSpotlightCube, Color.Lerp(artist.color, Color.white, 0.35f));
        }

        if (_artistSpotlightPfp != null)
        {
            bool hasPfp = artist.avatar != null;
            _artistSpotlightPfp.style.display = hasPfp ? DisplayStyle.Flex : DisplayStyle.None;
            if (hasPfp)
            {
                _artistSpotlightPfp.style.backgroundImage = new StyleBackground(artist.avatar);
                _artistSpotlightPfp.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
            }
        }

        if (_artistSpotlightName != null)
        {
            _artistSpotlightName.text = (artist.name ?? "ARTIST").ToUpperInvariant();
            _artistSpotlightName.style.color = artist.color;
            SetBorderColor(_artistSpotlightName, artist.color);
        }

        AnimateIconPopIn(_artistSpotlight);
    }

    /// <summary>Updates the countdown timer display.</summary>
    public void SetRemainingSeconds(float remainingSeconds)
    {
        if (_timerValue != null)
            _timerValue.text = $"{Mathf.Max(0, Mathf.CeilToInt(remainingSeconds))}";
    }

    /// <summary>
    /// Displays the submitted player's icon (coloured cube + optional PFP + name underneath)
    /// at the next top-left clockwise position around the center drawing card.
    /// </summary>
    public void MarkPlayerSubmitted(GameSessionData.SessionPlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.playerId))
            return;

        if (_submittedIcons.ContainsKey(player.playerId))
            return;

        if (_iconsLayer != null)
        {
            VisualElement iconRoot = CreatePlayerIconElement(player);
            _iconsLayer.Add(iconRoot);
            _submittedIcons[player.playerId] = iconRoot;
            _orderedIcons.Add(iconRoot);

            LayoutSubmittedIcons();
            AnimateIconPopIn(iconRoot);

            Vector2 pos = ComputeClockwisePanelPosition(_orderedIcons.Count - 1);
            if (_background != null)
                _background.AddRipple(pos);
            else if (PersistentPixelBlastBackground.Instance != null)
                PersistentPixelBlastBackground.Instance.AddRipple(pos);
        }

        if (submitSound != null && AudioManager.Instance != null)
        {
            AudioManager.Instance.NewAudio(submitSound)
                .RandomizePitch()
                .Create()
                .Play();
        }

        RefreshProgressLabels();
    }

    public void ClearSubmittedIcons()
    {
        foreach (VisualElement icon in _orderedIcons)
            icon?.RemoveFromHierarchy();

        _submittedIcons.Clear();
        _orderedIcons.Clear();
    }

    private VisualElement CreatePlayerIconElement(GameSessionData.SessionPlayer player)
    {
        VisualElement container = new()
        {
            pickingMode = PickingMode.Ignore
        };
        container.AddToClassList("submitted-player-icon");

        VisualElement cube = new()
        {
            pickingMode = PickingMode.Ignore
        };
        cube.AddToClassList("submitted-player-icon__cube");
        cube.style.backgroundColor = player.color;
        SetBorderColor(cube, Color.Lerp(player.color, Color.white, 0.35f));

        if (player.avatar != null)
        {
            VisualElement pfp = new()
            {
                pickingMode = PickingMode.Ignore
            };
            pfp.AddToClassList("submitted-player-icon__pfp");
            pfp.style.backgroundImage = new StyleBackground(player.avatar);
            pfp.style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
            cube.Add(pfp);
        }

        Label check = new("READY")
        {
            pickingMode = PickingMode.Ignore
        };
        check.AddToClassList("submitted-player-icon__check");
        check.style.color = player.color;
        SetBorderColor(check, player.color);
        cube.Add(check);

        Label nameLabel = new((player.name ?? "PLAYER").ToUpperInvariant())
        {
            pickingMode = PickingMode.Ignore
        };
        nameLabel.AddToClassList("submitted-player-icon__name");
        nameLabel.style.color = player.color;
        SetBorderColor(nameLabel, new Color(player.color.r, player.color.g, player.color.b, 0.75f));

        container.Add(cube);
        container.Add(nameLabel);

        return container;
    }

    private void AnimateIconPopIn(VisualElement icon)
    {
        if (icon == null)
            return;

        icon.style.scale = new Scale(new Vector3(0.15f, 0.15f, 1f));

        Tween.Custom(
            target: icon,
            startValue: 0.15f,
            endValue: 1f,
            duration: 0.35f,
            onValueChange: (el, s) => el.style.scale = new Scale(new Vector3(s, s, 1f)),
            ease: Easing.Overshoot(1.25f)
        );
    }

    private void LayoutSubmittedIcons()
    {
        if (_iconsLayer == null || _orderedIcons.Count == 0)
            return;

        _iconsLayer.EnableInClassList(CrowdedClass, _orderedIcons.Count > InnerRingSeats);

        for (int i = 0; i < _orderedIcons.Count; i++)
        {
            Vector2 pos = ComputeClockwisePanelPosition(i);
            VisualElement icon = _orderedIcons[i];
            icon.style.left = pos.x;
            icon.style.top = pos.y;
        }
    }

    /// <summary>
    /// Top-left clockwise rings around the center card, matching the join screen layout:
    /// seats 0-11 on the inner ring (seat 0 = 135 deg, -30 deg per seat), seats 12+ on an outer
    /// ring whose seats sit between the inner ones.
    /// </summary>
    private Vector2 ComputeClockwisePanelPosition(int seatIndex)
    {
        Rect rect = _iconsLayer.contentRect;
        float w = rect.width > 1f ? rect.width : 1920f;
        float h = rect.height > 1f ? rect.height : 1080f;

        float cx = w * 0.5f;
        float cy = h * 0.5f;

        bool crowded = _orderedIcons.Count > InnerRingSeats;
        bool outer = seatIndex >= InnerRingSeats;

        float rx, ry, angleDeg;
        if (!outer)
        {
            rx = w * (crowded ? 0.335f : 0.36f);
            ry = h * (crowded ? 0.33f : 0.35f);
            angleDeg = 135f - seatIndex * (360f / InnerRingSeats);
        }
        else
        {
            rx = w * 0.455f;
            ry = h * 0.43f;
            int outerIndex = (seatIndex - InnerRingSeats) % OuterRingSeats;
            angleDeg = 135f - (outerIndex + 0.5f) * (360f / OuterRingSeats);
        }

        // Panel Y grows downward, so negate Sin for top-left = 135 deg going clockwise
        float rad = angleDeg * Mathf.Deg2Rad;

        float x = cx + Mathf.Cos(rad) * rx;
        float y = cy - Mathf.Sin(rad) * ry;

        return new Vector2(x, y);
    }

    private void RefreshProgressLabels()
    {
        int submitted = _submittedIcons.Count;
        bool allDone = submitted >= _expectedPlayerCount;

        if (_submissionCount != null)
            _submissionCount.text = $"{submitted} / {_expectedPlayerCount} SUBMITTED";

        if (_drawingTitle != null)
            _drawingTitle.text = allDone ? "ALL DRAWINGS SUBMITTED!" : "DRAWING IN PROGRESS...";

        if (_description != null)
        {
            _description.text = allDone
                ? "Everyone is ready — moving to the next phase!"
                : "Draw a clue on your phone and submit before time runs out";
        }
    }

    private void ApplyRainbowColor(Color rainbow)
    {
        _currentRainbowColor = rainbow;
        Color dim = new(rainbow.r, rainbow.g, rainbow.b, 0.78f);

        _root.Query(className: "rainbow-text").ForEach(e => e.style.color = rainbow);
        _root.Query(className: "rainbow-dim-text").ForEach(e => e.style.color = dim);
        _root.Query(className: "rainbow-border").ForEach(e => SetBorderColor(e, rainbow));

        if (_background != null)
            _background.accentColor = rainbow;

        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(rainbow);

        if (_title != null)
            _title.BaseColor = rainbow;
    }

    #region Helpers

    private static void SetBorderColor(VisualElement element, Color color)
    {
        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Standings between drawings, and the winners podium at the end of the game.
/// Driven by GameManager; looks are in UI/Leaderboard (a USS translation of the
/// frontend's leaderboard-rankings / leaderboard-podium components).
///
/// Round view: rows start in the previous order, then slide to their new places while
/// totals count up, with each player's points for the drawing and how many places they
/// moved. Winners view: podium blocks rise (3rd, 2nd, 1st), then a crown drops onto
/// each winner; everyone else is listed beside the podium.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class LeaderboardUI : MonoBehaviour
{
    /// <summary>One player's standing. Rank 1 = best; every player has their own rank (no ties).</summary>
    public struct Entry
    {
        public string PlayerId;
        public string Name;
        public Color Color;
        public Texture2D Avatar;
        public int Total;
        public int PreviousTotal;
        public int RoundPoints;
        public int Rank;
        /// <summary>Rank before this drawing; 0 when there is none (first drawing, winners screen).</summary>
        public int PreviousRank;
        public bool IsArtist;
    }

    // --color-rank-1/2/3 from the frontend
    private static readonly Color[] RankColors =
    {
        new(250f / 255f, 204f / 255f, 21f / 255f),
        new(203f / 255f, 213f / 255f, 225f / 255f),
        new(217f / 255f, 119f / 255f, 6f / 255f)
    };

    private static readonly Color UpColor = new(34f / 255f, 197f / 255f, 94f / 255f);
    private static readonly Color DownColor = new(220f / 255f, 38f / 255f, 38f / 255f);

    private const string HiddenClass = "is-hidden";
    private const string ShownClass = "is-shown";

    [SerializeField] private UIDocument document;

    [Header("Rows")]
    [Tooltip("Most players listed: the top N in the standings, and podium + ranks 4..N on the " +
             "winners screen. 10 fills the 1080p card; the rest are summed up as \"+N MORE\".")]
    [SerializeField, Min(3)] private int maxShownPlayers = 10;
    [SerializeField, Min(30f)] private float rowHeight = 64f;
    [SerializeField, Min(30f)] private float othersRowHeight = 52f;
    [SerializeField, Min(0f)] private float rowStagger = 0.06f;

    [Header("Round Standings")]
    [Tooltip("Seconds the rows sit in the previous order before sliding to the new one.")]
    [SerializeField, Min(0f)] private float reorderDelay = 1.0f;
    [SerializeField, Min(0.05f)] private float countUpDuration = 1.1f;

    [Header("Winners")]
    [SerializeField, Min(0f)] private float crownBobPixels = 5f;
    [SerializeField, Min(1f)] private float winnersRainbowDegPerSec = 24f;

    public event Action ContinueRequested;
    public event Action PlayAgainRequested;
    public event Action BackToMenuRequested;

    private enum Mode
    {
        Hidden,
        Round,
        Winners
    }

    private sealed class RowRefs
    {
        public VisualElement Row;
        public Label Rank;
        public LeaderboardGlyph Crown;
        public Label Total;
    }

    private VisualElement _root;
    private VisualElement _roundView;
    private VisualElement _winnersView;
    private VisualElement _roundList;
    private VisualElement _podium;
    private VisualElement _othersCard;
    private VisualElement _othersList;
    private Label _roundKicker;
    private Label _roundTitle;
    private Label _roundCountdown;
    private Label _winnersKicker;
    private Label _winnersTitle;
    private Button _continue;
    private Button _playAgain;
    private Button _backToMenu;

    private Mode _mode = Mode.Hidden;
    private int _showId;
    private float _hueDeg;

    private float _countdownEndsAt;
    private string _countdownPrefix = "";

    private readonly List<(Label label, int from, int to)> _countUps = new();
    private float _countUpStartsAt = float.MaxValue;

    private readonly List<VisualElement> _bobbingCrowns = new();

    public bool IsShowing => _mode != Mode.Hidden;

    #region Lifecycle

    private void OnEnable()
    {
        Bind();
        Hide();
    }

    private void OnDisable()
    {
        if (_continue != null)
            _continue.clicked -= OnContinueClicked;
        if (_playAgain != null)
            _playAgain.clicked -= OnPlayAgainClicked;
        if (_backToMenu != null)
            _backToMenu.clicked -= OnBackToMenuClicked;

        _root = null;
    }

    private bool Bind()
    {
        if (_root != null)
            return true;

        if (document == null)
            document = GetComponent<UIDocument>();

        VisualElement docRoot = document != null ? document.rootVisualElement : null;
        if (docRoot == null)
            return false;

        _root = docRoot.Q<VisualElement>("lb-root");
        if (_root == null)
            return false;

        _roundView = _root.Q<VisualElement>("round-view");
        _winnersView = _root.Q<VisualElement>("winners-view");
        _roundList = _root.Q<VisualElement>("round-list");
        _podium = _root.Q<VisualElement>("podium");
        _othersCard = _root.Q<VisualElement>("others-card");
        _othersList = _root.Q<VisualElement>("others-list");
        _roundKicker = _root.Q<Label>("round-kicker");
        _roundTitle = _root.Q<Label>("round-title");
        _roundCountdown = _root.Q<Label>("round-countdown");
        _winnersKicker = _root.Q<Label>("winners-kicker");
        _winnersTitle = _root.Q<Label>("winners-title");
        _continue = _root.Q<Button>("continue");
        _playAgain = _root.Q<Button>("play-again");
        _backToMenu = _root.Q<Button>("back-to-menu");

        // Created up front so ApplyAccent tints them along with the rest
        SetMoreLabel(_roundList, 0);
        SetMoreLabel(_othersList, 0);

        if (_continue != null)
            _continue.clicked += OnContinueClicked;
        if (_playAgain != null)
            _playAgain.clicked += OnPlayAgainClicked;
        if (_backToMenu != null)
            _backToMenu.clicked += OnBackToMenuClicked;

        return true;
    }

    private void OnContinueClicked() => ContinueRequested?.Invoke();
    private void OnPlayAgainClicked() => PlayAgainRequested?.Invoke();
    private void OnBackToMenuClicked() => BackToMenuRequested?.Invoke();

    private void Update()
    {
        if (_mode == Mode.Hidden || _root == null)
            return;

        UpdateCountUps();

        if (_mode == Mode.Round && _roundCountdown != null)
        {
            int remaining = Mathf.CeilToInt(_countdownEndsAt - Time.time);
            _roundCountdown.text = remaining > 0 ? $"{_countdownPrefix} IN {remaining}" : "";
        }

        if (_mode == Mode.Winners)
        {
            _hueDeg = (_hueDeg + Time.deltaTime * winnersRainbowDegPerSec) % 360f;
            ApplyAccent(Color.HSVToRGB(_hueDeg / 360f, 0.75f, 1f));

            float bob = Mathf.Sin(Time.time * 2.4f) * crownBobPixels;
            foreach (VisualElement crown in _bobbingCrowns)
                crown.style.translate = new Translate(0f, bob);
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Standings after a drawing. <paramref name="accent"/> tints the headings (e.g. the true
    /// hue just revealed); <paramref name="secondsUntilNext"/> drives the on-screen countdown
    /// (GameManager does the actual waiting).
    /// </summary>
    public void ShowRound(
        IReadOnlyList<Entry> entries,
        int drawingNumber,
        int drawingCount,
        Color accent,
        string continueText,
        float secondsUntilNext)
    {
        if (!Bind() || entries == null)
            return;

        int id = BeginShow(Mode.Round);

        ApplyAccent(Readable(accent));
        PersistentPixelBlastBackground.EnsureExists().SetAccentColor(Readable(accent));

        if (_roundKicker != null)
            _roundKicker.text = $"AFTER DRAWING {drawingNumber} / {drawingCount}";
        if (_roundTitle != null)
            _roundTitle.text = "LEADERBOARD";
        if (_continue != null)
            _continue.text = continueText;

        _countdownPrefix = continueText;
        _countdownEndsAt = Time.time + secondsUntilNext;

        List<Entry> now = SortedBy(entries, previous: false);

        // No history on the first drawing: nothing to reorder from
        bool hasHistory = false;
        foreach (Entry e in entries)
            hasHistory |= e.PreviousRank > 0;

        List<Entry> before = hasHistory ? SortedBy(entries, previous: true) : now;

        // Only the top maxShownPlayers fit. Rows for anyone in the top before OR after this
        // drawing are built; the list clips at the cutoff, so a row parked just below it
        // slides up into view (climbed in) or down out of it (dropped out).
        int shown = Mathf.Min(maxShownPlayers, now.Count);
        var inTop = new HashSet<string>();
        for (int i = 0; i < shown; i++)
        {
            inTop.Add(now[i].PlayerId);
            inTop.Add(before[i].PlayerId);
        }

        _roundList.Clear();
        _roundList.style.height = shown * rowHeight;
        _roundList.style.overflow = Overflow.Hidden;
        SetMoreLabel(_roundList, now.Count - shown);

        var rows = new Dictionary<string, RowRefs>();
        for (int i = 0; i < before.Count; i++)
        {
            Entry e = before[i];
            if (!inTop.Contains(e.PlayerId))
                continue;

            RowRefs refs = BuildRow(e, rowHeight, showGain: true, shownRank: hasHistory ? e.PreviousRank : e.Rank);
            refs.Row.style.top = Mathf.Min(i, shown) * rowHeight;
            _roundList.Add(refs.Row);
            rows[e.PlayerId] = refs;

            refs.Total.text = Format(hasHistory ? e.PreviousTotal : e.Total);
            ShowLater(refs.Row, Mathf.Min(i, shown) * rowStagger, id);
        }

        // Slide into the new order, update ranks, count the totals up
        float reorderAt = reorderDelay + shown * rowStagger;
        After(reorderAt, id, () =>
        {
            for (int i = 0; i < now.Count; i++)
            {
                Entry e = now[i];
                if (!rows.TryGetValue(e.PlayerId, out RowRefs refs))
                    continue;

                refs.Row.style.top = Mathf.Min(i, shown) * rowHeight;
                SetRank(refs, e.Rank);

                if (hasHistory && e.Total != e.PreviousTotal)
                    _countUps.Add((refs.Total, e.PreviousTotal, e.Total));
            }

            _countUpStartsAt = Time.time;
        });
    }

    /// <summary>End of game: crown the winner(s) on a podium, list everyone else.</summary>
    public void ShowWinners(IReadOnlyList<Entry> entries)
    {
        if (!Bind() || entries == null)
            return;

        int id = BeginShow(Mode.Winners);
        List<Entry> sorted = SortedBy(entries, previous: false);

        List<Entry> winners = sorted.FindAll(e => e.Rank == 1);
        if (_winnersTitle != null)
        {
            _winnersTitle.text = winners.Count == 1
                ? $"{winners[0].Name.ToUpperInvariant()} WINS!"
                : winners.Count > 1 ? "IT'S A TIE!" : "WINNERS";
        }
        if (_winnersKicker != null)
            _winnersKicker.text = "GAME COMPLETE";

        // Podium in display order 2nd, 1st, 3rd; rises 3rd -> 2nd -> 1st
        _podium.Clear();
        int[] displayOrder = { 1, 0, 2 };
        float lastRise = 0f;

        foreach (int index in displayOrder)
        {
            if (index >= sorted.Count)
                continue;

            Entry e = sorted[index];
            float riseAt = 0.3f + (2 - index) * 0.5f;
            lastRise = Mathf.Max(lastRise, riseAt);
            _podium.Add(BuildPodiumSlot(e, riseAt, id));
        }

        // Crowns drop once the tallest block is up
        float crownAt = lastRise + 0.9f;
        After(crownAt, id, () =>
        {
            _podium.Query(className: "lb-podium-crown-wrap").ForEach(c => c.AddToClassList(ShownClass));
        });

        // Everyone else, down to rank maxShownPlayers
        _othersList.Clear();
        int listedUpTo = Mathf.Min(sorted.Count, maxShownPlayers);
        int others = Mathf.Max(0, listedUpTo - 3);
        _othersCard.EnableInClassList(HiddenClass, others == 0);
        _othersList.style.height = others * othersRowHeight;
        SetMoreLabel(_othersList, sorted.Count - listedUpTo);

        for (int i = 3; i < listedUpTo; i++)
        {
            Entry e = sorted[i];
            RowRefs refs = BuildRow(e, othersRowHeight, showGain: false, shownRank: e.Rank);
            refs.Row.style.top = (i - 3) * othersRowHeight;
            refs.Total.text = Format(e.Total);
            _othersList.Add(refs.Row);
            ShowLater(refs.Row, crownAt + 0.3f + (i - 3) * rowStagger, id);
        }
    }

    public void Hide()
    {
        _showId++;
        _mode = Mode.Hidden;
        _countUps.Clear();
        _countUpStartsAt = float.MaxValue;
        _bobbingCrowns.Clear();

        _root?.AddToClassList(HiddenClass);
    }

    #endregion

    #region Building

    private int BeginShow(Mode mode)
    {
        int id = ++_showId;
        _mode = mode;
        _countUps.Clear();
        _countUpStartsAt = float.MaxValue;
        _bobbingCrowns.Clear();

        _root.RemoveFromClassList(HiddenClass);
        _roundView?.EnableInClassList(HiddenClass, mode != Mode.Round);
        _winnersView?.EnableInClassList(HiddenClass, mode != Mode.Winners);
        return id;
    }

    private RowRefs BuildRow(Entry e, float height, bool showGain, int shownRank)
    {
        var refs = new RowRefs();

        VisualElement row = new();
        row.AddToClassList("lb-row");
        row.style.height = height;
        refs.Row = row;

        // Rank number + crown
        VisualElement rankCell = new();
        rankCell.AddToClassList("lb-rank-cell");
        refs.Rank = new Label();
        refs.Rank.AddToClassList("lb-rank-number");
        refs.Crown = new LeaderboardGlyph(LeaderboardGlyph.Shape.Crown, Color.white);
        refs.Crown.AddToClassList("lb-crown");
        rankCell.Add(refs.Rank);
        rankCell.Add(refs.Crown);
        row.Add(rankCell);

        row.Add(BuildAvatar(e, "lb-avatar"));

        // Name + byline
        VisualElement nameCol = new();
        nameCol.AddToClassList("lb-name-col");
        Label name = new(e.Name);
        name.AddToClassList("lb-name");
        nameCol.Add(name);

        if (showGain && e.IsArtist)
        {
            Label byline = new("THE ARTIST");
            byline.AddToClassList("lb-byline");
            nameCol.Add(byline);
        }
        row.Add(nameCol);

        // Places moved since the last drawing
        VisualElement change = new();
        change.AddToClassList("lb-change");
        int moved = e.PreviousRank > 0 ? e.PreviousRank - e.Rank : 0;
        if (showGain && moved != 0)
        {
            bool up = moved > 0;
            change.AddToClassList(up ? "lb-change--up" : "lb-change--down");

            LeaderboardGlyph arrow = new(up ? LeaderboardGlyph.Shape.Up : LeaderboardGlyph.Shape.Down, up ? UpColor : DownColor);
            arrow.AddToClassList("lb-change-glyph");
            change.Add(arrow);

            Label amount = new(Mathf.Abs(moved).ToString(CultureInfo.InvariantCulture));
            amount.AddToClassList("lb-change-text");
            change.Add(amount);
        }
        if (showGain)
            row.Add(change);

        if (showGain)
        {
            Label gain = new($"+{Format(e.RoundPoints)}");
            gain.AddToClassList("lb-gain");
            gain.EnableInClassList("lb-gain--zero", e.RoundPoints <= 0);
            row.Add(gain);
        }

        refs.Total = new Label();
        refs.Total.AddToClassList("lb-total");
        row.Add(refs.Total);

        SetRank(refs, shownRank);
        return refs;
    }

    private VisualElement BuildPodiumSlot(Entry e, float riseAt, int id)
    {
        int place = Mathf.Clamp(e.Rank, 1, 3);
        Color rankColor = RankColors[place - 1];

        VisualElement slot = new();
        slot.AddToClassList("lb-podium-slot");
        slot.AddToClassList($"lb-podium-slot--{place}");

        // Winners get the big crown
        if (e.Rank == 1)
        {
            VisualElement crownWrap = new();
            crownWrap.AddToClassList("lb-podium-crown-wrap");
            LeaderboardGlyph crown = new(LeaderboardGlyph.Shape.Crown, rankColor);
            crown.AddToClassList("lb-podium-crown");
            crownWrap.Add(crown);
            slot.Add(crownWrap);
            _bobbingCrowns.Add(crown);
        }

        VisualElement person = new();
        person.AddToClassList("lb-podium-person");

        VisualElement avatarWrap = new();
        avatarWrap.AddToClassList("lb-podium-avatar-wrap");
        avatarWrap.Add(BuildAvatar(e, "lb-podium-avatar"));

        // Crown badge in the corner (leaderboard-podium's medal)
        LeaderboardGlyph badge = new(LeaderboardGlyph.Shape.Crown, rankColor);
        badge.AddToClassList("lb-podium-badge");
        avatarWrap.Add(badge);
        person.Add(avatarWrap);

        Label name = new(e.Name);
        name.AddToClassList("lb-podium-name");
        person.Add(name);

        Label points = new($"{Format(e.Total)} PTS");
        points.AddToClassList("lb-podium-points");
        person.Add(points);
        slot.Add(person);

        VisualElement block = new();
        block.AddToClassList("lb-podium-block");
        block.AddToClassList($"lb-podium-block--{place}");

        Label rank = new(e.Rank.ToString(CultureInfo.InvariantCulture));
        rank.AddToClassList("lb-podium-rank");
        rank.style.color = place == 1 ? Color.white : rankColor;
        block.Add(rank);
        slot.Add(block);

        ShowLater(block, riseAt, id);
        ShowLater(person, riseAt + 0.3f, id);
        return slot;
    }

    /// <summary>"+N MORE" right under a list (one label per list, reused between shows).</summary>
    private static void SetMoreLabel(VisualElement list, int hiddenCount)
    {
        VisualElement parent = list?.parent;
        if (parent == null)
            return;

        Label more = parent.Q<Label>(className: "lb-more");
        if (more == null)
        {
            more = new Label();
            more.AddToClassList("lb-more");
            more.AddToClassList("accent-dim-text");
            parent.Insert(parent.IndexOf(list) + 1, more);
        }

        more.text = hiddenCount == 1 ? "+1 MORE PLAYER" : $"+{hiddenCount} MORE PLAYERS";
        more.EnableInClassList(HiddenClass, hiddenCount <= 0);
    }

    private static VisualElement BuildAvatar(Entry e, string className)
    {
        VisualElement avatar = new();
        avatar.AddToClassList(className);
        SetBorderColor(avatar, e.Color);

        if (e.Avatar != null)
        {
            avatar.style.backgroundImage = new StyleBackground(e.Avatar);
            avatar.style.backgroundColor = Color.black;
        }
        else
        {
            // Initial on the player's colour (the frontend's no-avatar fallback)
            avatar.style.backgroundColor = e.Color;
            string initial = string.IsNullOrEmpty(e.Name) ? "?" : e.Name.Substring(0, 1).ToUpperInvariant();
            Label letter = new(initial);
            letter.AddToClassList("lb-avatar-initial");
            float luminance = 0.2126f * e.Color.r + 0.7152f * e.Color.g + 0.0722f * e.Color.b;
            letter.style.color = luminance > 0.55f ? Color.black : Color.white;
            avatar.Add(letter);
        }

        return avatar;
    }

    private static void SetRank(RowRefs refs, int rank)
    {
        refs.Rank.text = rank > 0 ? rank.ToString(CultureInfo.InvariantCulture) : "-";

        for (int place = 1; place <= 3; place++)
        {
            refs.Rank.EnableInClassList($"lb-rank-{place}", rank == place);
            refs.Row.EnableInClassList($"lb-row--rank-{place}", rank == place);
        }

        bool crowned = rank >= 1 && rank <= 3;
        refs.Crown.style.visibility = crowned ? Visibility.Visible : Visibility.Hidden;
        if (crowned)
            refs.Crown.Tint = RankColors[rank - 1];
    }

    #endregion

    #region Animation helpers

    private void ShowLater(VisualElement element, float delaySeconds, int id)
    {
        // At least a frame later, so the hidden state is laid out and the transition runs
        After(Mathf.Max(0.02f, delaySeconds), id, () => element.AddToClassList(ShownClass));
    }

    private void After(float delaySeconds, int id, Action action)
    {
        _root.schedule.Execute(() =>
        {
            if (id == _showId)
                action();
        }).StartingIn(Mathf.RoundToInt(delaySeconds * 1000f));
    }

    private void UpdateCountUps()
    {
        if (_countUps.Count == 0 || Time.time < _countUpStartsAt)
            return;

        float t = Mathf.Clamp01((Time.time - _countUpStartsAt) / countUpDuration);
        float eased = 1f - Mathf.Pow(1f - t, 3f);

        foreach ((Label label, int from, int to) in _countUps)
            label.text = Format(Mathf.RoundToInt(Mathf.Lerp(from, to, eased)));

        if (t >= 1f)
            _countUps.Clear();
    }

    private void ApplyAccent(Color accent)
    {
        Color dim = new(accent.r, accent.g, accent.b, 0.78f);

        _root.Query(className: "accent-text").ForEach(e => e.style.color = accent);
        _root.Query(className: "accent-dim-text").ForEach(e => e.style.color = dim);
        _root.Query(className: "accent-border").ForEach(e => SetBorderColor(e, accent));

        if (_mode == Mode.Winners)
            PersistentPixelBlastBackground.EnsureExists().SetAccentColor(accent);
    }

    #endregion

    #region Utility

    /// <summary>
    /// Sorted best first by the ranks GameManager gave (which already split ties); by score,
    /// then name, only where a rank is missing.
    /// </summary>
    private static List<Entry> SortedBy(IReadOnlyList<Entry> entries, bool previous)
    {
        List<Entry> list = new(entries);
        list.Sort((a, b) =>
        {
            int rankA = previous ? a.PreviousRank : a.Rank;
            int rankB = previous ? b.PreviousRank : b.Rank;
            if (rankA > 0 && rankB > 0 && rankA != rankB)
                return rankA.CompareTo(rankB);

            int scoreA = previous ? a.PreviousTotal : a.Total;
            int scoreB = previous ? b.PreviousTotal : b.Total;
            int byScore = scoreB.CompareTo(scoreA);
            return byScore != 0 ? byScore : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>Keeps a hue readable on the dark UI (dark target colours get brightened).</summary>
    private static Color Readable(Color color)
    {
        Color.RGBToHSV(color, out float h, out float s, out float v);
        return Color.HSVToRGB(h, Mathf.Min(s, 0.85f), Mathf.Max(v, 0.9f));
    }

    private static string Format(int value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    private static void SetBorderColor(VisualElement element, Color color)
    {
        element.style.borderTopColor = color;
        element.style.borderRightColor = color;
        element.style.borderBottomColor = color;
        element.style.borderLeftColor = color;
    }

    #endregion
}

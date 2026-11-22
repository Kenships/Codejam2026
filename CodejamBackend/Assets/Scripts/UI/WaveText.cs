using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Text whose characters hop up and down one after another, like the frontend's per-character
/// text templates (components/ui/text-3d-flip.tsx splits text into staggered character spans).
/// A Label can't move single characters, so each character gets its own Label in a row.
///
/// Every <see cref="Period"/> seconds a wave starts: character i hops <see cref="Stagger"/> * i
/// seconds later, for <see cref="HopDuration"/> seconds, <see cref="Amplitude"/> em high.
/// - Typing-indicator dots: short period, animated-characters=".", e.g. "WAITING FOR PLAYERS..."
/// - Periodic wave: long period, every character, e.g. "SCAN TO JOIN"
///
/// Font size, style and text shadow are inherited from this element's USS class, and the colour
/// set on this element (e.g. by the hue-text class in MenuController) is copied to every character.
/// </summary>
[UxmlElement]
public partial class WaveText : VisualElement
{
    private const string CharClass = "wave-text__char";
    private const string SpaceClass = "wave-text__space";

    private readonly List<VisualElement> _chars = new();
    // Hop order of each character (-1 = doesn't move)
    private readonly List<int> _waveIndex = new();

    private string _text = "";
    private string _animatedCharacters = "";
    private IVisualElementScheduledItem _tick;
    private Color _appliedColor = new(-1f, -1f, -1f, -1f);
    private float _startTime = -1f;

    [UxmlAttribute]
    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text)
                return;

            _text = value;
            Rebuild();
        }
    }

    /// <summary>Only these characters move (e.g. "."). Empty = every character.</summary>
    [UxmlAttribute]
    public string AnimatedCharacters
    {
        get => _animatedCharacters;
        set
        {
            _animatedCharacters = value ?? "";
            Rebuild();
        }
    }

    /// <summary>Seconds between the starts of two waves.</summary>
    [UxmlAttribute]
    public float Period { get; set; } = 1.2f;

    /// <summary>Delay between one character's hop and the next one's.</summary>
    [UxmlAttribute]
    public float Stagger { get; set; } = 0.15f;

    /// <summary>How long one character's hop lasts.</summary>
    [UxmlAttribute]
    public float HopDuration { get; set; } = 0.45f;

    /// <summary>Hop height as a fraction of the font size.</summary>
    [UxmlAttribute]
    public float Amplitude { get; set; } = 0.22f;

    /// <summary>Seconds before the first wave (to offset several WaveTexts from each other).</summary>
    [UxmlAttribute]
    public float StartDelay { get; set; }

    public WaveText()
    {
        AddToClassList("wave-text");
        pickingMode = PickingMode.Ignore;

        RegisterCallback<AttachToPanelEvent>(_ =>
        {
            _startTime = -1f;
            _tick ??= schedule.Execute(Tick).Every(16);
            _tick.Resume();
        });
        RegisterCallback<DetachFromPanelEvent>(_ => _tick?.Pause());
    }

    private void Rebuild()
    {
        Clear();
        _chars.Clear();
        _waveIndex.Clear();
        _appliedColor = new Color(-1f, -1f, -1f, -1f);

        int next = 0;
        foreach (char c in _text)
        {
            VisualElement element;
            if (char.IsWhiteSpace(c))
            {
                // A Label holding only a space can measure zero wide: use a sized gap instead
                element = new VisualElement();
                element.AddToClassList(SpaceClass);
            }
            else
            {
                element = new Label(c.ToString());
                element.AddToClassList(CharClass);
            }

            element.pickingMode = PickingMode.Ignore;
            Add(element);
            _chars.Add(element);

            bool animated = !char.IsWhiteSpace(c) &&
                            (string.IsNullOrEmpty(_animatedCharacters) || _animatedCharacters.IndexOf(c) >= 0);
            _waveIndex.Add(animated ? next++ : -1);
        }
    }

    private void Tick()
    {
        if (_chars.Count == 0 || resolvedStyle.display == DisplayStyle.None)
            return;

        float now = Time.realtimeSinceStartup;
        if (_startTime < 0f)
            _startTime = now;

        float fontSize = resolvedStyle.fontSize > 1f ? resolvedStyle.fontSize : 24f;
        float t = now - _startTime - StartDelay;
        float period = Mathf.Max(0.05f, Period);
        float hop = Mathf.Max(0.05f, HopDuration);

        Color color = resolvedStyle.color;
        bool recolor = color != _appliedColor;
        _appliedColor = color;

        for (int i = 0; i < _chars.Count; i++)
        {
            VisualElement element = _chars[i];

            if (element.ClassListContains(SpaceClass))
            {
                element.style.width = fontSize * 0.3f;
                continue;
            }

            if (recolor)
                element.style.color = color;

            int index = _waveIndex[i];
            float y = 0f;
            if (index >= 0 && t >= 0f)
            {
                // Time since this character's hop began in the current wave
                float local = Mathf.Repeat(t - index * Stagger, period);
                if (local < hop)
                    y = -Mathf.Sin(local / hop * Mathf.PI) * Amplitude * fontSize;
            }

            element.style.translate = new Translate(0f, y);
        }
    }
}

using System.Collections.Generic;
using Alchemy.Inspector;
using PrimeTween;
using UnityEngine;

public class ColorBody: MonoBehaviour
{
    [SerializeField, Required] private SpriteRenderer sr;
    [SerializeField] private ParticleSystem particleSystems;

    private Tween _scaleTween;
    private Tween _squashTween;

    // While squashed, the scale no longer reflects the physics size
    // (stretch is clamped, so area isn't preserved), so remember the
    // round radius from just before the squash began.
    private float _squash;
    private float _roundRadius;
    private float _maxStretch = float.MaxValue;

    /// <summary>
    /// Physics radius. While squashed this is the round radius from
    /// before the squash; otherwise the geometric mean of the x/y scale
    /// (so it follows the spawn scale animation, squashed start included).
    /// </summary>
    public float Radius
    {
        get
        {
            if (_squash > 0f)
                return _roundRadius;

            Vector3 scale = transform.localScale;
            return Mathf.Sqrt(Mathf.Abs(scale.x * scale.y)) * 0.5f;
        }
    }

    public SpriteRenderer Renderer => sr;

    public Sprite Sprite
    {
        get => sr.sprite;
        set => sr.sprite = value;
    }

    public Vector3 LastPos { get; set; }
    public Vector3 CurrentPos { get; set; }

    private Color _color;
    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            sr.color = _color;
            RecolorEffect(_color);
        }
    }

    #region Merge Effect

    // The root effect system plus all its child systems
    private ParticleSystem[] _effectSystems;
    private bool _effectPlayRequested;

    private void Awake()
    {
        // The effect prefab plays on awake; it should only play on merge,
        // so turn that off and clear anything it already started
        foreach (ParticleSystem system in GetEffectSystems())
        {
            ParticleSystem.MainModule main = system.main;
            main.playOnAwake = false;
        }

        StopEffect();
    }

    private void Start()
    {
        // Catch a play-on-awake that started after our Awake ran, unless
        // the merge effect was explicitly requested in the meantime
        if (!_effectPlayRequested)
            StopEffect();
    }

    /// <summary>
    /// Plays the merge particle effect (root and all child systems).
    /// </summary>
    public void PlayMergeEffect()
    {
        _effectPlayRequested = true;

        // The CFXR effect may have destroyed itself after finishing
        if (particleSystems == null)
            return;

        particleSystems.Clear(true);
        particleSystems.Play(true);
    }

    /// <summary>
    /// Plays the merge effect unparented from this body, so it keeps going
    /// after the body is destroyed (e.g. a burst). The effect cleans itself
    /// up once its particles have finished, and this body no longer owns it.
    /// </summary>
    public void PlayDetachedEffect()
    {
        _effectPlayRequested = true;

        if (particleSystems == null)
            return;

        ParticleSystem effect = particleSystems;
        float lifetime = 0f;

        foreach (ParticleSystem system in GetEffectSystems())
        {
            if (system == null)
                continue;

            ParticleSystem.MainModule main = system.main;
            lifetime = Mathf.Max(
                lifetime,
                main.startDelay.constantMax + main.duration + main.startLifetime.constantMax
            );
        }

        // Hand it over: later recolours/stops on this body leave it alone
        particleSystems = null;
        _effectSystems = null;

        effect.transform.SetParent(null, true);
        effect.Clear(true);
        effect.Play(true);

        // CFXR may destroy it first; destroying it again is harmless
        Destroy(effect.gameObject, lifetime + 0.5f);
    }

    private void StopEffect()
    {
        if (particleSystems != null)
            particleSystems.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private ParticleSystem[] GetEffectSystems()
    {
        if (_effectSystems == null && particleSystems != null)
            _effectSystems = particleSystems.GetComponentsInChildren<ParticleSystem>(true);

        return _effectSystems ?? System.Array.Empty<ParticleSystem>();
    }

    /// <summary>
    /// Sets every effect system's colour over lifetime to the body colour.
    /// Each system keeps its own alpha fade (alpha keys); only the hue is
    /// replaced. Systems without a gradient get colour -> alpha 0.
    /// </summary>
    private void RecolorEffect(Color color)
    {
        foreach (ParticleSystem system in GetEffectSystems())
        {
            if (system == null)
                continue;

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime =
                system.colorOverLifetime;
            ParticleSystem.MinMaxGradient current = colorOverLifetime.color;

            colorOverLifetime.color = current.mode switch
            {
                ParticleSystemGradientMode.Gradient =>
                    new ParticleSystem.MinMaxGradient(
                        RecolorGradient(current.gradient, color)
                    ),

                ParticleSystemGradientMode.TwoGradients =>
                    new ParticleSystem.MinMaxGradient(
                        RecolorGradient(current.gradientMin, color),
                        RecolorGradient(current.gradientMax, color)
                    ),

                _ => new ParticleSystem.MinMaxGradient(
                    RecolorGradient(null, color)
                )
            };

            colorOverLifetime.enabled = true;
        }
    }

    /// <summary>
    /// Solid colour with the source gradient's alpha keys, or a
    /// colour -> alpha 0 fade if there's no source. (Alpha keys are read
    /// back from the current gradient, so they're kept as-is rather than
    /// scaled, which would compound on every recolour.)
    /// </summary>
    private static Gradient RecolorGradient(Gradient source, Color color)
    {
        GradientAlphaKey[] alphaKeys =
            source != null && source.alphaKeys.Length > 0
                ? source.alphaKeys
                : new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                };

        Color opaque = color;
        opaque.a = 1f;

        Gradient gradient = new();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(opaque, 0f),
                new GradientColorKey(opaque, 1f)
            },
            alphaKeys
        );

        return gradient;
    }

    #endregion

    /// <summary>
    /// Squashes along local Y and stretches along local X. 0 = round.
    /// The stretch would keep area constant, but is capped at maxStretch
    /// (1.2 = at most 20% wider). Rotate the body so local Y faces the
    /// push direction.
    /// </summary>
    public void SetSquash(float squash, float maxStretch = float.MaxValue)
    {
        StopTween(ref _squashTween);

        _maxStretch = maxStretch;
        ApplySquash(squash);
    }

    /// <summary>
    /// Smoothly tweens the squash back to 0 (round) from wherever it is.
    /// </summary>
    public void Unsquash(float duration, Ease ease = Ease.OutQuad)
    {
        StopTween(ref _squashTween);

        if (_squash <= 0f)
            return;

        _squashTween = Tween.Custom(
            target: this,
            startValue: _squash,
            endValue: 0f,
            duration: duration,
            onValueChange: (body, squash) => body.ApplySquash(squash),
            ease: ease
        );
    }

    private void ApplySquash(float squash)
    {
        // Capture the round size on the first squash
        if (_squash <= 0f)
            _roundRadius = Radius;

        _squash = Mathf.Max(0f, squash);
        transform.localScale =
            SquashedScale(_roundRadius, _squash, _maxStretch);
    }

    /// <summary>
    /// Animates from a (optionally squashed) start radius to a round end
    /// radius, overshooting slightly before settling.
    /// </summary>
    public void ScaleRadius(
        float start,
        float end,
        float duration = 0.15f,
        float startSquash = 0f,
        float overshoot = 1f,
        float maxStretch = float.MaxValue)
    {
        StopTween(ref _scaleTween);
        StopTween(ref _squashTween);

        // The tween owns the scale from here, so Radius follows it
        _squash = 0f;

        Vector3 startScale = SquashedScale(start, startSquash, maxStretch);

        // Apply immediately so physics sees the right radius this frame
        transform.localScale = startScale;

        _scaleTween = Tween.Scale(
            target: transform,
            startValue: startScale,
            endValue: Vector3.one * (end * 2f),
            duration: duration,
            ease: Easing.Overshoot(overshoot)
        );
    }

    /// <summary>Jumps any running scale/squash animation to its end state.</summary>
    public void CompleteScaleTween()
    {
        if (_scaleTween.isAlive)
            _scaleTween.Complete();

        if (_squashTween.isAlive)
            _squashTween.Complete();
    }

    private void OnDestroy()
    {
        StopTween(ref _scaleTween);
        StopTween(ref _squashTween);
    }

    private static void StopTween(ref Tween tween)
    {
        if (tween.isAlive)
            tween.Stop();
    }

    private static Vector3 SquashedScale(
        float radius,
        float squash,
        float maxStretch)
    {
        float diameter = radius * 2f;
        float k = 1f - squash;

        // 1/k would preserve area; clamp how wide it's allowed to get
        float stretch = Mathf.Min(1f / k, Mathf.Max(1f, maxStretch));

        return new Vector3(diameter * stretch, diameter * k, diameter);
    }
}

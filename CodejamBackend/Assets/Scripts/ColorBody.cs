using Alchemy.Inspector;
using UnityEngine;

public class ColorBody: MonoBehaviour
{
    [SerializeField, Required] private SpriteRenderer sr;

    private float _radius;

    public float Radius => _radius == 0 ? transform.localScale.x * 0.5f : _radius;

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
        } 
    }
}

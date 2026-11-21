using UnityEngine;

namespace Core.AudioSystem
{
    [CreateAssetMenu(fileName = "AudioClipVariations", menuName = "Scriptable Objects/AudioClipVariationsSO")]
    public class AudioClipVariationsSO : ScriptableObject
    {
        public AudioClip[] Variations;
    }
}

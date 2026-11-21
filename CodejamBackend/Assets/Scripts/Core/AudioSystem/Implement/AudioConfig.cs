using Core.AudioSystem.Interface;
using UnityEngine;

namespace Core.AudioSystem.Implement
{
    
    public struct AudioConfig
    {
        public int SceneBuildIndex { get; set; }
        public AudioClip Clip { get; set; }
        public AudioType AudioType { get; set; }
        public int Priority { get; set; }
        public Vector3 Position { get; set; }
        public Transform TransformParent { get; set; }
        public float Volume { get; set; }
        public float Pitch { get; set; }
        public float SpatialBlend { get; set; }
        public int LoopCount { get; set; }
        public float MinDistance { get; set; }
        public float MaxDistance { get; set; }
        public bool IsBypassReverbZones { get; set; }

        public AudioConfig(AudioClip audioClip)
        {
            Clip = audioClip;
            
            SceneBuildIndex = 0;
            AudioType = AudioType.Sfx;
            Priority = 1;
            Position = Vector3.zero;
            TransformParent = null;
            Pitch = 1f;
            Volume = 1f;
            LoopCount = 0;
            SpatialBlend = 0f;
            MinDistance = 0f;
            MaxDistance = 500f;
            IsBypassReverbZones = false;
        }

        public AudioConfig(AudioClip audioClip, AudioConfig audioConfig)
        {
            Clip = audioClip;
            
            SceneBuildIndex = audioConfig.SceneBuildIndex;
            AudioType = audioConfig.AudioType;
            Priority = audioConfig.Priority;
            Position = audioConfig.Position;
            TransformParent = audioConfig.TransformParent;
            Pitch = audioConfig.Pitch;
            Volume = audioConfig.Volume;
            LoopCount = audioConfig.LoopCount;
            SpatialBlend = audioConfig.SpatialBlend;
            MinDistance = audioConfig.MinDistance;
            MaxDistance = audioConfig.MaxDistance;
            IsBypassReverbZones = audioConfig.IsBypassReverbZones;
        }
        
        public AudioConfig AddToScene(int sceneBuildIndex)
        {
            SceneBuildIndex = sceneBuildIndex;
            return this;
        }

        public AudioConfig OnChannel(AudioType audioType)
        {
            AudioType = audioType;
            return this;
        }

        public AudioConfig SetVolume(float volume)
        {
            Volume = volume;
            return this;
        }

        public AudioConfig SetPitch(float pitch)
        {
            Pitch = pitch;
            return this;
        }

        public AudioConfig RandomizePitch(float min = -0.05f, float max = 0.05f)
        {
            return SetPitch(1f + Random.Range(min, max));
        }

        public AudioConfig Loop(int loopCount = -1)
        {
            LoopCount = loopCount;
            return this;
        }

        public AudioConfig AddPriority(int priority)
        {
            Priority = priority;
            return this;
        }

        public AudioConfig MarkFrequent()
        {
            Priority = 0;
            return this;
        }
        
        public AudioConfig AtPosition(Vector3 position)
        {
            SpatialBlend = 1f;
            Position = position;
            return this;
        }

        public AudioConfig SetParent(Transform parent)
        {
            SpatialBlend = 1f;
            TransformParent = parent;
            return this;
        }

        public AudioConfig WithMinDistance(float minDistance)
        {
            SpatialBlend = 1f;
            MinDistance = minDistance;
            return this;
        }

        public AudioConfig WithMaxDistance(float maxDistance)
        {
            SpatialBlend = 1f;
            MaxDistance = maxDistance;
            return this;
        }

        public AudioConfig BypassReverbZones()
        {
            SpatialBlend = 1f;
            IsBypassReverbZones = true;
            return this;
        }
        
        public IAudioPlayer Create()
        {
            return AudioManager.Instance.Create(this);
        }
    }
}

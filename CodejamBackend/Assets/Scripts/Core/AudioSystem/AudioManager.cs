using System;
using System.Collections.Generic;
using System.Linq;
using AYellowpaper.SerializedCollections;
using Core.AudioSystem.Implement;
using Core.AudioSystem.Interface;
using UnityEngine;
using UnityEngine.Audio;
using Alchemy.Inspector;
using Random = UnityEngine.Random;

namespace Core.AudioSystem
{
    public enum AudioOverridePolicy
    {
        DontPlayOnFull,
        OverrideFirst
    }
    
    public enum AudioType
    {
        Sfx,
        UI,
        Music
    }
    
    public partial class AudioManager : Utility.Singleton<AudioManager>
    {
        [SerializeField] private bool createBuffer;
        [SerializeField, ShowIf(nameof(createBuffer))] private int bufferSize;

        [SerializeField] private AudioOverridePolicy audioOverridePolicy;

        [SerializeField] private SerializedDictionary<AudioType, int> maxAudioSources;
        [SerializeField] private SerializedDictionary<AudioType, AudioMixerGroup> audioMixerGroups;
        
        
        [Space]
        [Header("Active Sources")]
        [SerializeField] private SerializedDictionary<AudioType, List<PooledAudioSource>> activeSourcesByAudioType;
        [SerializeField] private SerializedDictionary<int, List<PooledAudioSource>> activeSourcesBySceneIndex;
        
        [Header("Debug Values")]
        [SerializeField, ReadOnly] private int numberOfActiveSources;
        [SerializeField, ReadOnly] private int numberOfInactiveSources;
        
        
        private readonly Stack<PooledAudioSource> _inactiveSources = new();

        private readonly HashSet<AudioType> _audioTypes = new();

        private void OnValidate()
        {
#if UNITY_EDITOR
            //Add new keys
            _audioTypes.Clear();
            _audioTypes.UnionWith((AudioType[])Enum.GetValues(typeof(AudioType)));
            foreach (AudioType audioType in _audioTypes)
            {
                maxAudioSources.TryAdd(audioType, 0);
                audioMixerGroups.TryAdd(audioType, null);
            }

            //Remove deleted Keys
            var keysToRemoveAudioSourceMax = new List<AudioType>();
            var keysToRemoveAudioMixerGroups = new List<AudioType>();

            foreach (var keyValue in maxAudioSources)
            {
                if (!_audioTypes.Contains(keyValue.Key))
                {
                    keysToRemoveAudioSourceMax.Add(keyValue.Key);
                }

                if (!audioMixerGroups.ContainsKey(keyValue.Key))
                {
                    keysToRemoveAudioMixerGroups.Add(keyValue.Key);
                }
            }

            foreach (var key in keysToRemoveAudioSourceMax)
            {
                maxAudioSources.Remove(key);
            }

            foreach (var key in keysToRemoveAudioMixerGroups)
            {
                audioMixerGroups.Remove(key);
            }
#endif
        }

        protected override void Awake()
        {
            base.Awake();

            if (!createBuffer)
            {
                return;
            }
            
            for (int i = 0; i < bufferSize; i++)
            {
                var audioSource = CreateAudioSource();
                audioSource.gameObject.SetActive(false);
                _inactiveSources.Push(audioSource);
            }
        }

        public AudioConfig NewAudio(AudioClip clip, AudioConfig? audioConfig = null)
        {
            return audioConfig == null ? new AudioConfig(clip) : new AudioConfig(clip, audioConfig.Value);
        }

        public AudioConfig NewAudio(AudioClipVariationsSO clips, AudioConfig? audioConfig = null)
        {
            if (clips.Variations.Length == 0)
            {
                return new AudioConfig(null);
            }
            
            int randomIndex = Random.Range(0, clips.Variations.Length);
            return NewAudio(clips.Variations[randomIndex], audioConfig);
        }

        public IAudioPlayer Create(AudioConfig audioConfig)
        {
            if (!audioConfig.Clip)
            {
                Debug.LogWarning("No clip provided");
                return new EmptyAudioPlayer();
            }

            // check for capacity availability
            if (!activeSourcesByAudioType.TryGetValue(audioConfig.AudioType, out List<PooledAudioSource> list) ||
                list.Count < maxAudioSources[audioConfig.AudioType])
            {
                return GetNextAudioSource(audioConfig);
            }

            PooledAudioSource audioSource = activeSourcesByAudioType[audioConfig.AudioType][0];
            int minPriority = Int32.MaxValue;
            foreach (PooledAudioSource activeAudio in activeSourcesByAudioType[audioConfig.AudioType])
            {
                if (activeAudio.AudioConfig.Priority >= minPriority)
                {
                    continue;
                }

                audioSource = activeAudio;
                minPriority = activeAudio.AudioConfig.Priority;
            }

            if (minPriority < audioConfig.Priority)
            {
                Debug.LogWarning($"AudioPooler of AudioType: {audioConfig.AudioType} is Full. " +
                                   $"Replaced playing {audioSource.AudioConfig.Clip.name} with " +
                                   $"{audioConfig.Clip.name}");
                audioSource.StopAndRelease();

                return GetNextAudioSource(audioConfig);
            }

            switch (audioOverridePolicy)
            {
                case AudioOverridePolicy.DontPlayOnFull:
                    Debug.LogWarning($"AudioPooler of AudioType: {audioConfig.AudioType} is Full. " +
                                       $"Skip playing {audioConfig.Clip.name}");
                    return new EmptyAudioPlayer();

                case AudioOverridePolicy.OverrideFirst:
                    Debug.LogWarning($"AudioPooler of AudioType: {audioConfig.AudioType} is Full. " +
                                       $"Replaced playing {audioSource.AudioConfig.Clip.name} with " +
                                       $"{audioConfig.Clip.name}");
                    audioSource.StopAndRelease();
                    break;
            }

            return GetNextAudioSource(audioConfig);
        }

        public AudioMixerGroup GetMixerFor(AudioType audioType)
        {
            return audioMixerGroups[audioType];
        }

        private IAudioPlayer GetNextAudioSource(AudioConfig audioConfig)
        {
            PooledAudioSource audioSource = _inactiveSources.Count == 0 ? CreateAudioSource() : _inactiveSources.Pop();
            
            audioSource.Initialize(audioConfig, ReturnToPool);
            audioSource.gameObject.SetActive(true);
            
            if (activeSourcesByAudioType.ContainsKey(audioConfig.AudioType))
            {
                activeSourcesByAudioType[audioConfig.AudioType].Add(audioSource);
            }
            else
            {
                activeSourcesByAudioType.TryAdd(audioConfig.AudioType, new List<PooledAudioSource> { audioSource });
            }

            if (activeSourcesBySceneIndex.ContainsKey(audioConfig.SceneBuildIndex))
            {
                activeSourcesBySceneIndex[audioConfig.SceneBuildIndex].Add(audioSource);
            }
            else
            {
                activeSourcesBySceneIndex.TryAdd(audioConfig.SceneBuildIndex,
                    new List<PooledAudioSource> { audioSource });
            }

            numberOfInactiveSources = _inactiveSources.Count;
            numberOfActiveSources = activeSourcesByAudioType.Sum(val => val.Value.Count);
            
            return new AudioPlayer(audioSource);
        }

        private void ReturnToPool(PooledAudioSource pooledAudioSource)
        {
            if (!pooledAudioSource.gameObject.activeSelf)
                return;

            if (activeSourcesByAudioType.TryGetValue(pooledAudioSource.AudioConfig.AudioType, out var typeList))
            {
                typeList.Remove(pooledAudioSource);
                if (typeList.Count == 0)
                    activeSourcesByAudioType.Remove(pooledAudioSource.AudioConfig.AudioType);
            }

            if (activeSourcesBySceneIndex.TryGetValue(pooledAudioSource.AudioConfig.SceneBuildIndex, out var sceneList))
            {
                sceneList.Remove(pooledAudioSource);
                if (sceneList.Count == 0)
                    activeSourcesBySceneIndex.Remove(pooledAudioSource.AudioConfig.SceneBuildIndex);
            }

            pooledAudioSource.transform.SetParent(transform);
            pooledAudioSource.gameObject.SetActive(false);
            _inactiveSources.Push(pooledAudioSource);

            numberOfActiveSources = activeSourcesByAudioType.Sum(val => val.Value.Count);
            numberOfInactiveSources = _inactiveSources.Count;
        }

        private PooledAudioSource CreateAudioSource()
        {
            GameObject audioObject = new GameObject("AudioSource");
            audioObject.transform.SetParent(transform);
            PooledAudioSource audioComponent = audioObject.AddComponent<PooledAudioSource>();
            return audioComponent;
        }
    }
}

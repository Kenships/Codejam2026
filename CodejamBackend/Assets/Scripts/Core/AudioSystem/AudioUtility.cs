using System;
using System.Collections.Generic;
using System.Linq;
using Core.AudioSystem.Implement;
using Core.AudioSystem.Interface;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace Core.AudioSystem
{
    public partial class AudioManager
    {
        public const float DefaultAudioFadeDuration = 0.1f;

        #region Global AudioSource Control

        public static void StopAndReleaseAll(int buildIndex = -1, params AudioType[] audioType)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to ReleaseAll.");
                return;
            }

            foreach (var audioSource in GetFilteredAudioSources(buildIndex, audioType))
            {
                audioSource.FadeVolume(0f, DefaultAudioFadeDuration, () => audioSource.StopAndRelease());
            }
        }

        public static void PauseAll(int buildIndex = -1, params AudioType[] audioType)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to PauseAll.");
                return;
            }

            foreach (var audioSource in GetFilteredAudioSources(buildIndex, audioType))
            {
                audioSource.FadeVolume(0f, DefaultAudioFadeDuration, () => audioSource.Pause());
            }
        }

        public static void ResumeAll(int buildIndex = -1, params AudioType[] audioType)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to ResumeAll.");
                return;
            }

            foreach (var audioSource in GetFilteredAudioSources(buildIndex, audioType))
            {
                audioSource.Resume();
                audioSource.FadeVolume(audioSource.AudioConfig.Volume, DefaultAudioFadeDuration);
            }
        }

        #endregion

        #region Audio Transitions

        public static IAudioPlayer CrossFade(IAudioPlayer initialPlayer, AudioClipVariationsSO target, float fadeDuration)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to Crossfade.");
                return null;
            }

            if (target.Variations.Length == 0)
            {
                Debug.LogWarning("AudioClipVariationsSO is empty. Tried to Crossfade.");
                return initialPlayer;
            }

            int random = Random.Range(0, target.Variations.Length);

            return CrossFade(initialPlayer, target.Variations[random], fadeDuration);
        }

        public static IAudioPlayer CrossFade(IAudioPlayer initialPlayer, AudioClip targetAudio, float fadeDuration)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to Crossfade.");
                return null;
            }

            IAudioPlayer player = Instance.NewAudio(targetAudio, initialPlayer.AudioConfig).Create();
            player.SetVolume(0f);
            CrossFade(initialPlayer, player, fadeDuration);

            return player;
        }

        public static void CrossFade(IAudioPlayer initialPlayer, IAudioPlayer targetPlayer, float fadeDuration)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to Crossfade.");
                return;
            }

            initialPlayer.FadeVolume(0f, fadeDuration);
            targetPlayer.FadeVolume(1f, fadeDuration);
        }

        #endregion

        #region Audio Creation ShortHand

        public static void PlayOneShot(AudioClip audioClip)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to PlayOneShot.");
                return;
            }

            Instance.NewAudio(audioClip).Create().Play();
        }

        public static void PlayOneShot(AudioClipVariationsSO audioClip)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to PlayOneShot.");
                return;
            }

            Instance.NewAudio(audioClip).Create().Play();
        }

        public static void PlayOneShot(AudioClip audioClip, Vector3 position)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to PlayOneShot.");
                return;
            }

            Instance.NewAudio(audioClip).AtPosition(position).Create().Play();
        }

        public static void PlayOneShot(AudioClipVariationsSO audioClip, Vector3 position)
        {
            if (Instance == null)
            {
                Debug.LogError("AudioManager is null. Tried to PlayOneShot.");
                return;
            }

            Instance.NewAudio(audioClip).AtPosition(position).Create().Play();
        }

        #endregion

        #region Utility

        private static List<PooledAudioSource> GetFilteredAudioSources(int buildIndex, params AudioType[] audioTypes)
        {
            List<PooledAudioSource> result = new();

            if (audioTypes.Length == 0)
            {
                foreach (var category in Instance.activeSourcesByAudioType.Values)
                {
                    result.AddRange(category);
                }
            }
            else
            {
                IEnumerable<AudioType> distinctAudioTypes = audioTypes.Distinct();

                List<PooledAudioSource> sources;

                foreach (AudioType type in distinctAudioTypes)
                {
                    if (!Instance.activeSourcesByAudioType.TryGetValue(type, out sources))
                    {
                        continue;
                    }

                    result.AddRange(sources);
                }
            }

            if (buildIndex < 0 || buildIndex >= SceneManager.sceneCountInBuildSettings)
            {
                return result;
            }

            if (!Instance.activeSourcesBySceneIndex.TryGetValue(buildIndex, out List<PooledAudioSource> sceneSources))
            {
                return new List<PooledAudioSource>();
            }

            return sceneSources.Intersect(result).ToList();
        }

        #endregion
    }
}

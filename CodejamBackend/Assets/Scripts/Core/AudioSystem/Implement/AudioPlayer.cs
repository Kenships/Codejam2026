using System;
using Core.AudioSystem.Interface;
using UnityEngine;

namespace Core.AudioSystem.Implement
{
    /// <summary>
    ///     Wrapper class for any IAudioPlayer
    /// </summary>
    public class AudioPlayer : IAudioPlayer
    {
        public AudioConfig AudioConfig => _audioSource.AudioConfig;
        
        public event Action AudioFinished;
        private IAudioPlayer _audioSource;
        private Coroutine _playingCoroutine;

        public AudioPlayer(IAudioPlayer audioSource)
        {
            _audioSource = audioSource;
            _audioSource.AudioFinished += () => AudioFinished?.Invoke();
        }

        public void Play()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to Play from an Expired Source");
                return;
            }

            _audioSource.Play();
        }

        public void Release()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to Release an Expired Source");
                return;
            }
            AudioFinished = null;
            _audioSource.Release();
            _audioSource = null;
        }

        public void StopAndRelease()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to StopAndRelease an Expired Source");
                return;
            }

            Stop();
            Release();
        }

        public void Stop()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to Stop an Expired Source");
                return;
            }

            _audioSource.Stop();
        }

        public void Pause()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to Pause an Expired Source");
                return;
            }

            _audioSource.Pause();
        }

        public void Resume()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to Resume an Expired Source");
                return;
            }

            _audioSource.Play();
        }
        
        public void FadeVolume(float targetVolume, float duration, Action onCompleteCallback = null)
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to FadeVolume on an Expired Source");
                return;
            }
            
            _audioSource.FadeVolume(targetVolume, duration, onCompleteCallback);
        }

        public void SetVolume(float volume)
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to SetVolume on an Expired Source");
                return;
            }
            
            _audioSource.SetVolume(volume);
        }

        public void LerpPitch(float targetPitch, float duration, Action onCompleteCallback = null)
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to LerpPitch on an Expired Source");
                return;
            }
            
            _audioSource.LerpPitch(targetPitch, duration, onCompleteCallback);
        }

        public void SetPitch(float pitch)
        {
            if (_audioSource == null)
            {
                Debug.LogWarning("AudioPlayer Trying to SetPitch on an Expired Source");
                return;
            }
            
            _audioSource.SetPitch(pitch);
        }
    }
}

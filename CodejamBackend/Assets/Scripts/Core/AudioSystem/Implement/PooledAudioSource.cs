using System;
using System.Collections;
using Core.AudioSystem.Interface;
using Core.Utility;
using PrimeTween;
using UnityEngine;

namespace Core.AudioSystem.Implement
{
    internal class PooledAudioSource : MonoBehaviour, IAudioPlayer
    {
        public event Action AudioFinished;
        public AudioConfig AudioConfig { get; private set; }
        private AudioSource _audioSource;
        private Action<PooledAudioSource> _returnToPool;

        private Coroutine _playingCoroutine;
        private int _loopCount;

        protected void Awake()
        {
            _audioSource = gameObject.GetOrAdd<AudioSource>();
            _audioSource.playOnAwake = false;
        }

        public void Initialize(AudioConfig audioConfig, Action<PooledAudioSource> returnToPool)
        {
            _returnToPool = returnToPool;

            AudioConfig = audioConfig;

            transform.position = audioConfig.Position;
            transform.SetParent(audioConfig.TransformParent ?? transform.parent);
            
            _audioSource.clip = audioConfig.Clip;
            _audioSource.outputAudioMixerGroup = AudioManager.Instance.GetMixerFor(audioConfig.AudioType);
            _audioSource.volume = audioConfig.Volume;
            _audioSource.pitch = audioConfig.Pitch;
            _audioSource.spatialBlend = audioConfig.SpatialBlend;
            
            _loopCount = audioConfig.LoopCount;
            _audioSource.loop = _loopCount < 0;
            
            _audioSource.minDistance = audioConfig.MinDistance;
            _audioSource.maxDistance = audioConfig.MaxDistance;
            _audioSource.bypassReverbZones = audioConfig.IsBypassReverbZones;
        }

        public void Play()
        {
            if (_playingCoroutine != null)
            {
                StopCoroutine(_playingCoroutine);
            }

            _audioSource.Play();
            _playingCoroutine = StartCoroutine(WaitForAudioCompletion());
        }

        private IEnumerator WaitForAudioCompletion()
        {
            yield return new WaitWhile(() => _audioSource.isPlaying);
            
            
            if (_loopCount > 0)
            {
                Play();
                _loopCount--;
                yield break;
            }
            
            AudioFinished?.Invoke();
            _returnToPool(this);
        }

        public void Release()
        {
            AudioFinished = null;
            _returnToPool(this);
        }

        public void StopAndRelease()
        {
            Stop();
            Release();
        }

        public void Stop()
        {
            if (_playingCoroutine != null)
            {
                StopCoroutine(_playingCoroutine);
                _playingCoroutine = null;
            }

            _audioSource.Stop();
            AudioFinished?.Invoke();
        }

        public void Pause()
        {
            if (_playingCoroutine != null)
            {
                StopCoroutine(_playingCoroutine);
                _playingCoroutine = null;
            }

            _audioSource.Pause();
        }

        public void Resume()
        {
            _audioSource.UnPause();
            if (_playingCoroutine == null)
            {
                _playingCoroutine = StartCoroutine(WaitForAudioCompletion());
            }
        }

        public void FadeVolume(float targetVolume, float duration, Action onCompleteCallback = null)
        {
            if (duration == 0f)
            {
                _audioSource.volume = targetVolume;
                onCompleteCallback?.Invoke();
                return;
            }

            Tween.AudioVolume(
                _audioSource,
                targetVolume,
                duration,
                useUnscaledTime: true
            ).OnComplete(() =>
            {
                onCompleteCallback?.Invoke();
            });
        }

        public void SetVolume(float volume)
        {
            _audioSource.volume = volume;
        }

        public void LerpPitch(float targetPitch, float duration, Action onCompleteCallback)
        {
            if (duration == 0f)
            {
                _audioSource.pitch = targetPitch;
                onCompleteCallback?.Invoke();
                return;
            }

            Tween.AudioPitch(
                target: _audioSource,
                endValue: targetPitch,
                duration,
                useUnscaledTime: true
            ).OnComplete(() => onCompleteCallback?.Invoke());
        }

        public void SetPitch(float pitch)
        {
            _audioSource.pitch = pitch;
        }
    }
}

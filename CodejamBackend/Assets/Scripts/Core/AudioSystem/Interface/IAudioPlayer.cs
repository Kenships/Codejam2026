using System;
using Core.AudioSystem.Implement;

namespace Core.AudioSystem.Interface
{
    public interface IAudioPlayer
    {
        public AudioConfig AudioConfig { get; }

        event Action AudioFinished;
        void Play();
        void Stop();
        void Release();
        void StopAndRelease();
        void Pause();
        void Resume();
        
        void FadeVolume(float targetVolume, float duration, Action onCompleteCallback = null);
        void SetVolume(float volume);
        void LerpPitch(float targetPitch, float duration, Action onCompleteCallback = null);
        void SetPitch(float pitch);
    }
}

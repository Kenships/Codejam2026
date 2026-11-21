using System;
using Core.AudioSystem.Interface;
using PrimeTween;

namespace Core.AudioSystem.Implement
{
    public sealed class EmptyAudioPlayer : IAudioPlayer
    {
        public AudioConfig AudioConfig => default;
        public event Action AudioFinished;

        public void StopAndRelease()
        {
            //noop
        }

        public void FadeVolume(float targetVolume, float duration = 0, Action onCompleteCallback = null)
        {
            //noop
        }

        public void SetVolume(float volume)
        {
            //noop
        }

        public void LerpPitch(float targetPitch, float duration, Action onCompleteCallback = null)
        {
            //noop
        }

        public void SetPitch(float pitch)
        {
            //noop
        }

        public void Play()
        {
            //noop
        }

        public void Stop()
        {
            //noop
        }

        public void Release()
        {
            //noop
        }

        public void Pause()
        {
            //noop
        }

        public void Resume()
        {
            //noop
        }
    }
}

using Alchemy.Inspector;
using Core.AudioSystem.Interface;
using Core.Utility;
using UnityEngine;

namespace Core.AudioSystem
{
    /// <summary>
    /// Centralized game audio helper built on top of Core.AudioSystem.AudioManager.
    /// Starts looping background music once and persists across scenes, and provides
    /// one-line helpers for UI clicks, player joins, cube merges, and reveal verdicts.
    /// </summary>
    public class GameAudioController : Singleton<GameAudioController>
    {
        [Title("Music")]
        [SerializeField, AssetsOnly] private AudioClip backgroundMusic;
        [SerializeField, Range(0f, 1f)] private float musicVolume = 0.5f;
        [SerializeField] private bool playMusicOnStart = true;

        [Title("UI & Gameplay SFX")]
        [SerializeField, AssetsOnly] private AudioClip clickButton;
        [SerializeField, AssetsOnly] private AudioClip joinSound;
        [SerializeField, AssetsOnly] private AudioClip bubbleMerge;

        [Title("Reveal Outcome SFX")]
        [SerializeField, AssetsOnly] private AudioClip goodGuess;
        [SerializeField, AssetsOnly] private AudioClip mediocreGuess;
        [SerializeField, AssetsOnly] private AudioClip loseSound;

        [SerializeField, Range(0f, 1f)] private float goodGuessThreshold = 0.85f;
        [SerializeField, Range(0f, 1f)] private float mediocreGuessThreshold = 0.50f;

        private IAudioPlayer _musicPlayer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BootstrapOnLoad()
        {
            EnsureExists();
        }

        public static GameAudioController EnsureExists()
        {
            if (Instance != null)
                return Instance;

            GameAudioController existing = FindFirstObjectByType<GameAudioController>();
            if (existing != null)
                return existing;

            GameObject go = new("GameAudioController");
            return go.AddComponent<GameAudioController>();
        }

        // Clips are assigned on the GameAudioController object in Menu and GameScene.
        // The first one loaded persists across scenes; the other destroys itself.

        private void Start()
        {
            if (Instance != this)
                return;

            if (playMusicOnStart)
                StartBackgroundMusic();
        }

        /// <summary>Starts persistent looping background music on AudioType.Music if not already playing.</summary>
        public void StartBackgroundMusic()
        {
            if (_musicPlayer != null || backgroundMusic == null || AudioManager.Instance == null)
                return;

            _musicPlayer = AudioManager.Instance
                .NewAudio(backgroundMusic)
                .OnChannel(AudioType.Music)
                .SetVolume(musicVolume)
                .Loop(-1)
                .Create();

            _musicPlayer.Play();
        }

        public void StopBackgroundMusic()
        {
            if (_musicPlayer == null)
                return;

            _musicPlayer.StopAndRelease();
            _musicPlayer = null;
        }

        public void PlayButtonClick()
        {
            if (clickButton == null || AudioManager.Instance == null)
                return;

            AudioManager.Instance
                .NewAudio(clickButton)
                .OnChannel(AudioType.UI)
                .RandomizePitch(-0.03f, 0.03f)
                .Create()
                .Play();
        }

        public void PlayPlayerJoin()
        {
            if (joinSound == null || AudioManager.Instance == null)
                return;

            AudioManager.Instance
                .NewAudio(joinSound)
                .OnChannel(AudioType.Sfx)
                .RandomizePitch(-0.04f, 0.04f)
                .Create()
                .Play();
        }

        public void PlayBubbleMerge()
        {
            if (bubbleMerge == null || AudioManager.Instance == null)
                return;

            AudioManager.Instance
                .NewAudio(bubbleMerge)
                .OnChannel(AudioType.Sfx)
                .RandomizePitch()
                .Create()
                .Play();
        }

        /// <summary>
        /// Plays GoodGuess (>= 85%), MediocreGuess (50%–84%), or LoseSound (< 50%)
        /// matching RevealUI's verdict tiers.
        /// </summary>
        public void PlayRevealOutcome(float matchScore01)
        {
            if (AudioManager.Instance == null)
                return;

            AudioClip clip = matchScore01 >= goodGuessThreshold
                ? goodGuess
                : matchScore01 >= mediocreGuessThreshold
                    ? mediocreGuess
                    : loseSound;

            if (clip == null)
                return;

            AudioManager.Instance
                .NewAudio(clip)
                .OnChannel(AudioType.Sfx)
                .Create()
                .Play();
        }
    }
}

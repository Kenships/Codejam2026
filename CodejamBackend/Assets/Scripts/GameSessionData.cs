using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime session state shared between Menu.unity and GameScene.unity:
/// stores the active room code, joined players (with picked colours and optional PFPs),
/// and drawings submitted during the Drawing Phase.
/// </summary>
public static class GameSessionData
{
    /// <summary>A game needs at least this many connected players (lobby Start Match and Play Again).</summary>
    public const int MinPlayersToStart = 2;

    /// <summary>Most players a room can hold (the relay's own hard limit).</summary>
    public const int MaxPlayersLimit = 32;

    /// <summary>Range for the drawing and guessing timers picked on the Create Game screen.</summary>
    public const int MinPhaseSeconds = 30;
    public const int MaxPhaseSeconds = 120;

    // ---- Match settings (picked on the Create Game screen; kept for Play Again and the next lobby) ----

    private static int _maxPlayers = 12;
    private static int _artistCount = 4;
    private static int _drawingSeconds = 60;
    private static int _guessingSeconds = 30;

    /// <summary>
    /// Seats the room hands out (MinPlayersToStart..MaxPlayersLimit). The artist count follows:
    /// "everyone" stays everyone, and a lower limit pulls it down.
    /// </summary>
    public static int MaxPlayers
    {
        get => _maxPlayers;
        set
        {
            bool everyoneDrew = EveryoneDraws;
            _maxPlayers = Mathf.Clamp(value, MinPlayersToStart, MaxPlayersLimit);
            _artistCount = everyoneDrew ? _maxPlayers : Mathf.Min(_artistCount, _maxPlayers);
        }
    }

    /// <summary>
    /// How many players draw each game (1..MaxPlayers); picked at random when the game
    /// starts. Everyone else only guesses. At or above the player count, everyone draws.
    /// </summary>
    public static int ArtistCount
    {
        get => _artistCount;
        set => _artistCount = Mathf.Clamp(value, 1, MaxPlayers);
    }

    /// <summary>True when ArtistCount means "everyone" for the current seat limit.</summary>
    public static bool EveryoneDraws => _artistCount >= _maxPlayers;

    /// <summary>Seconds players have to draw (MinPhaseSeconds..MaxPhaseSeconds).</summary>
    public static int DrawingSeconds
    {
        get => _drawingSeconds;
        set => _drawingSeconds = Mathf.Clamp(value, MinPhaseSeconds, MaxPhaseSeconds);
    }

    /// <summary>Seconds players have to guess each drawing (MinPhaseSeconds..MaxPhaseSeconds).</summary>
    public static int GuessingSeconds
    {
        get => _guessingSeconds;
        set => _guessingSeconds = Mathf.Clamp(value, MinPhaseSeconds, MaxPhaseSeconds);
    }

    /// <summary>
    /// True once a match was started from the menu. GameScene then uses the timers above
    /// instead of GameManager's inspector values (which still apply when it's played on its own).
    /// </summary>
    public static bool HasMatchSettings { get; private set; }

    [Serializable]
    public class SessionPlayer
    {
        public string playerId;
        public string name;
        public Color color = Color.cyan;
        public Texture2D avatar;
        public Color secretTargetColor;

        /// <summary>Points across every drawing so far this game (see HueScoring).</summary>
        public int totalPoints;

        /// <summary>Consecutive close guesses (ΔE ≤ 10), for the streak bonus.</summary>
        public int streak;

        /// <summary>The categories offered this drawing phase (see CategoryWordBank), or null.</summary>
        public string[] categoryOptions;

        /// <summary>The one they picked to draw (their drawing's prompt), or null until they pick.</summary>
        public string category;
    }

    [Serializable]
    public class SubmittedDrawing
    {
        public string playerId;
        public string playerName;
        public Color playerColor;
        public Color targetColor;
        public string prompt;
        public string imageBase64;
        public string mime;
        public Texture2D texture;
    }

    public static string RoomCode { get; set; } = "";
    public static string HostKey { get; set; } = "";

    public static List<SessionPlayer> Players { get; } = new();
    public static List<SubmittedDrawing> Drawings { get; } = new();

    public static void SetLobbySession(string roomCode, IReadOnlyList<MenuController.LobbyPlayer> lobbyPlayers)
    {
        RoomCode = roomCode ?? "";
        HasMatchSettings = true;
        Players.Clear();
        Drawings.Clear();

        if (lobbyPlayers == null)
            return;

        for (int i = 0; i < lobbyPlayers.Count; i++)
        {
            MenuController.LobbyPlayer lp = lobbyPlayers[i];
            if (lp == null)
                continue;

            Players.Add(new SessionPlayer
            {
                playerId = lp.playerId,
                name = lp.name,
                color = lp.color,
                avatar = CloneTextureIfReadable(lp.avatar)
            });
        }
    }

    public static SessionPlayer FindPlayer(string playerId)
    {
        for (int i = 0; i < Players.Count; i++)
        {
            if (Players[i].playerId == playerId)
                return Players[i];
        }
        return null;
    }

    public static void ClearDrawings()
    {
        for (int i = 0; i < Drawings.Count; i++)
        {
            if (Drawings[i].texture != null)
                UnityEngine.Object.Destroy(Drawings[i].texture);
        }
        Drawings.Clear();
    }

    private static Texture2D CloneTextureIfReadable(Texture2D source)
    {
        if (source == null || !source.isReadable)
            return source;

        Texture2D copy = new(source.width, source.height, TextureFormat.RGBA32, false)
        {
            filterMode = source.filterMode,
            wrapMode = source.wrapMode
        };
        copy.SetPixels32(source.GetPixels32());
        copy.Apply(false, false);
        UnityEngine.Object.DontDestroyOnLoad(copy);
        return copy;
    }
}

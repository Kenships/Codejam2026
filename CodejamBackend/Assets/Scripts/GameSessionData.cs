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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alchemy.Inspector;
using Core.Utility;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Codejam.Networking
{
    /// <summary>
    /// The GAME layer of the host's relay connection, used by GameManager.
    ///
    /// It owns no socket: <see cref="RelayClient"/> (the "Relay" object in the Menu scene,
    /// kept alive across scenes) owns the connection, room, reconnects, players and
    /// keepalive. This class turns game messages from phones into typed events
    /// (drawing_submit, pfp_submit, guess) and wraps the host -> phone game messages
    /// (your_target, state, round_drawing, results).
    ///
    /// If GameScene is played on its own, it creates a RelayClient, which resumes the
    /// menu's last room if that's still alive (PlayerPrefs), or else makes a new one.
    /// </summary>
    public class HostRelayClient : Singleton<HostRelayClient>
    {
        [Title("Relay")]
        [Tooltip("When no RelayClient exists (GameScene played without the menu), create one.")]
        [SerializeField] private bool createRelayIfMissing = true;

        [Tooltip("Log game messages in and out (stroke batches excluded).")]
        [SerializeField] private bool logTraffic = false;

        private RelayClient _relay;

        /// <summary>The connection layer this sits on (null until bound).</summary>
        public RelayClient Relay => _relay;

        /// <summary>True while the relay connection is up and the room is ours.</summary>
        public bool IsConnected => IsInRoom;

        public bool IsInRoom => _relay != null && _relay.Status == RelayStatus.InRoom;

        public string RoomCode => _relay != null ? _relay.RoomCode ?? "" : "";

        /// <summary>Secret for host_resume. Never send it to phones.</summary>
        public string HostKey => _relay != null ? _relay.HostKey ?? "" : "";

        /// <summary>A room became ready (new or resumed). Arguments: room code, host key.</summary>
        public event Action<string, string> RoomCreated;

        /// <summary>Arguments: player id, name.</summary>
        public event Action<string, string> PlayerJoined;

        /// <summary>Arguments: player id, name.</summary>
        public event Action<string, string> PlayerRejoined;

        /// <summary>Argument: player id. Their seat is held for 30 s.</summary>
        public event Action<string> PlayerDisconnected;

        /// <summary>Arguments: player id, reason ("timeout", "kicked", or "left" when the phone left on purpose).</summary>
        public event Action<string, string> PlayerLeft;

        /// <summary>
        /// Arguments: player id, the payload, and the decoded drawing (null if it couldn't
        /// be decoded). The receiver owns the texture.
        /// </summary>
        public event Action<string, DrawingSubmitPayload, Texture2D> DrawingSubmitted;

        /// <summary>Arguments: player id, gzip+base64 image. PlayerAvatars also turns these into sprites.</summary>
        public event Action<string, string> PfpSubmitted;

        /// <summary>Arguments: player id, guessed hex (e.g. "#FF5733").</summary>
        public event Action<string, string> GuessSubmitted;

        /// <summary>Every game message from a phone, after the typed events above.</summary>
        public event Action<string, JObject> GameMessageReceived;

        /// <summary>The relay rejected something. Arguments: error code, message.</summary>
        public event Action<string, string> RelayError;

        // Bound in Awake, not Start: GameManager checks IsInRoom in its own Start, and
        // Start order between objects isn't guaranteed. The menu's RelayClient already
        // exists by now (it persists across the scene load).
        protected override void Awake()
        {
            base.Awake();

            if (Instance == this)
                Bind();
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
                Unbind();

            base.OnDestroy();
        }

        #region Connection (RelayClient does the work)

        /// <summary>
        /// Makes sure the relay connection is running. RelayClient creates a room, or resumes
        /// its saved one, by itself, and RoomCreated fires once the room is ready.
        /// </summary>
        public Task ConnectAndCreateRoomAsync()
        {
            if (Bind())
                _relay.Connect();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Kept for compatibility. RelayClient resumes its own saved room (room + hostKey in
        /// PlayerPrefs) automatically, so the arguments are not used.
        /// </summary>
        public Task ConnectAndResumeRoomAsync(string room, string hostKey) => ConnectAndCreateRoomAsync();

        /// <summary>
        /// Kept for compatibility; does nothing. The connection is shared with the lobby and
        /// lives as long as the RelayClient, so the game layer never closes it.
        /// </summary>
        public Task DisconnectAsync() => Task.CompletedTask;

        private bool Bind()
        {
            if (_relay != null)
                return true;

            RelayClient relay = RelayClient.Instance;

            if (relay == null && createRelayIfMissing)
                relay = CreateRelay();

            if (relay == null)
            {
                Debug.LogWarning("[HostRelayClient] No RelayClient found; game messages won't reach phones.");
                return false;
            }

            _relay = relay;
            _relay.RoomReady += OnRoomReady;
            _relay.PlayerJoined += OnPlayerJoined;
            _relay.PlayerRejoined += OnPlayerRejoined;
            _relay.PlayerDisconnected += OnPlayerDisconnected;
            _relay.PlayerLeft += OnPlayerLeft;
            _relay.GameMessage += OnGameMessage;
            _relay.Error += OnRelayError;

            if (IsInRoom)
                SyncSession(_relay.RoomCode);

            return true;
        }

        private void Unbind()
        {
            if (_relay == null)
                return;

            _relay.RoomReady -= OnRoomReady;
            _relay.PlayerJoined -= OnPlayerJoined;
            _relay.PlayerRejoined -= OnPlayerRejoined;
            _relay.PlayerDisconnected -= OnPlayerDisconnected;
            _relay.PlayerLeft -= OnPlayerLeft;
            _relay.GameMessage -= OnGameMessage;
            _relay.Error -= OnRelayError;
            _relay = null;
        }

        /// <summary>
        /// Same setup as the Menu scene's "Relay" object, on its own GameObject so it
        /// persists independently of this one.
        /// </summary>
        private static RelayClient CreateRelay()
        {
            Debug.Log("[HostRelayClient] No RelayClient (scene played without the menu); creating one.");

            GameObject go = new("Relay");
            RelayClient relay = go.AddComponent<RelayClient>();
            go.AddComponent<PlayerAvatars>();
            return relay;
        }

        private static void SyncSession(string roomCode)
        {
            GameSessionData.RoomCode = roomCode ?? "";
            GameSessionData.HostKey = Instance != null ? Instance.HostKey : "";
        }

        #endregion

        #region Host -> Phones

        /// <summary>
        /// Sends each artist in <paramref name="players"/> their secret target hex, then tells every
        /// phone the drawing phase has started. Phones without a target (guessers this game, late
        /// joiners) show a waiting screen until guessing starts.
        /// </summary>
        public void BroadcastDrawingPhase(
            int round,
            int remainingMs,
            IReadOnlyList<GameSessionData.SessionPlayer> players)
        {
            if (!IsInRoom)
                return;

            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    GameSessionData.SessionPlayer p = players[i];
                    if (p == null || string.IsNullOrEmpty(p.playerId) || !IsRelayPlayer(p.playerId))
                        continue;

                    SendTargetTo(p);
                }
            }

            BroadcastPhaseState("drawing", round, remainingMs);
        }

        /// <summary>
        /// Puts one (re)connected player into the drawing phase: their secret target, then the
        /// drawing state. Used to resync a phone that reconnects mid-drawing.
        /// </summary>
        public void SendDrawingPhaseTo(GameSessionData.SessionPlayer player, int round, int remainingMs)
        {
            if (player == null || !IsRelayPlayer(player.playerId))
                return;

            SendTargetTo(player);
            SendPhaseStateTo(player.playerId, "drawing", round, remainingMs);
        }

        private void SendTargetTo(GameSessionData.SessionPlayer player)
        {
            SendToTarget(player.playerId, new JObject
            {
                ["t"] = "your_target",
                ["hex"] = "#" + ColorUtility.ToHtmlStringRGB(player.secretTargetColor)
            });
        }

        /// <summary>Broadcasts a phase/state update to all connected players.</summary>
        public void BroadcastPhaseState(string phase, int round, int remainingMs) =>
            SendToTarget("players", StateMessage(phase, round, remainingMs));

        /// <summary>A phase/state update for one player.</summary>
        public void SendPhaseStateTo(string playerId, string phase, int round, int remainingMs)
        {
            if (IsRelayPlayer(playerId))
                SendToTarget(playerId, StateMessage(phase, round, remainingMs));
        }

        private static JObject StateMessage(string phase, int round, int remainingMs) => new JObject
        {
            ["t"] = "state",
            ["phase"] = phase,
            ["round"] = round,
            ["remainingMs"] = remainingMs
        };

        /// <summary>
        /// Broadcasts the current artist's black-and-white drawing to players so they can guess its hue.
        /// </summary>
        public void BroadcastRoundDrawing(
            GameSessionData.SubmittedDrawing drawing,
            int round,
            int remainingMs)
        {
            if (!IsInRoom || drawing == null)
                return;

            SendToTarget("players", RoundDrawingMessage(drawing));
            BroadcastPhaseState("playing", round, remainingMs);
        }

        /// <summary>
        /// Sends the drawing to guess to every player in the game EXCEPT its artist, who would
        /// otherwise be asked to guess their own colour. The artist's phone stays on its
        /// current screen until the results.
        /// </summary>
        public void BroadcastRoundDrawing(
            GameSessionData.SubmittedDrawing drawing,
            int round,
            int remainingMs,
            IReadOnlyList<GameSessionData.SessionPlayer> players)
        {
            if (!IsInRoom || drawing == null)
                return;

            if (players == null)
            {
                BroadcastRoundDrawing(drawing, round, remainingMs);
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                GameSessionData.SessionPlayer p = players[i];
                if (p != null && p.playerId != drawing.playerId)
                    SendRoundDrawingTo(p.playerId, drawing, round, remainingMs);
            }
        }

        /// <summary>One player's guessing screen: the drawing, then the playing state.</summary>
        public void SendRoundDrawingTo(
            string playerId,
            GameSessionData.SubmittedDrawing drawing,
            int round,
            int remainingMs)
        {
            if (drawing == null || !IsRelayPlayer(playerId))
                return;

            SendToTarget(playerId, RoundDrawingMessage(drawing));
            SendPhaseStateTo(playerId, "playing", round, remainingMs);
        }

        // The phone recognises gzip by its header, so the submitted image goes back out as-is
        private static JObject RoundDrawingMessage(GameSessionData.SubmittedDrawing drawing) => new JObject
        {
            ["t"] = "round_drawing",
            ["image"] = drawing.imageBase64 ?? "",
            ["mime"] = string.IsNullOrEmpty(drawing.mime) ? "image/png" : drawing.mime,
            ["prompt"] = drawing.prompt ?? "",
            ["artistName"] = drawing.playerName ?? ""
        };

        /// <summary>
        /// Sends one player their results screen (the phone needs this; state "results"
        /// alone shows nothing).
        /// </summary>
        public void SendResults(
            string playerId,
            Color targetColor,
            int roundPoints,
            int totalPoints,
            int roundRank,
            int overallRank,
            int totalPlayers)
        {
            if (!IsRelayPlayer(playerId))
                return;

            SendToTarget(playerId, new JObject
            {
                ["t"] = "results",
                ["targetHex"] = "#" + ColorUtility.ToHtmlStringRGB(targetColor),
                ["roundPoints"] = roundPoints,
                ["totalPoints"] = totalPoints,
                ["roundRank"] = roundRank,
                ["overallRank"] = overallRank,
                ["totalPlayers"] = totalPlayers
            });
        }

        /// <summary>
        /// Sends a game payload. target: a player id, or "players" / "all" for every
        /// connected phone.
        /// </summary>
        public void SendToTarget(string target, JObject payload)
        {
            if (!IsInRoom || string.IsNullOrEmpty(target) || payload == null)
                return;

            if (logTraffic)
                Debug.Log($"[HostRelayClient] -> {target}: {payload.ToString(Newtonsoft.Json.Formatting.None)}");

            if (target == "players" || target == "all")
                _relay.SendToAllPlayers(payload);
            else
                _relay.SendToPlayer(target, payload);
        }

        /// <summary>
        /// True for real phones in the room. False for ids the relay doesn't know (e.g. the
        /// menu's simulated players), which the relay would reject with unknown_target.
        /// </summary>
        public bool IsRelayPlayer(string playerId) =>
            _relay != null && !string.IsNullOrEmpty(playerId) && _relay.Players.ContainsKey(playerId);

        #endregion

        #region Relay -> Game

        private void OnRoomReady(string code)
        {
            SyncSession(code);
            Raise(RoomCreated, code, HostKey);
        }

        private void OnPlayerJoined(RelayPlayer player) => Raise(PlayerJoined, player.Id, player.Name);

        private void OnPlayerRejoined(RelayPlayer player) => Raise(PlayerRejoined, player.Id, player.Name);

        private void OnPlayerDisconnected(RelayPlayer player) => Raise(PlayerDisconnected, player.Id);

        private void OnPlayerLeft(RelayPlayer player, string reason) => Raise(PlayerLeft, player.Id, reason);

        private void OnRelayError(string code, string message) => Raise(RelayError, code, message);

        private void OnGameMessage(string fromPlayerId, JObject d)
        {
            string gameType = d.Value<string>("t");

            if (logTraffic && gameType != "stroke_batch")
                Debug.Log($"[HostRelayClient] <- {fromPlayerId}: {gameType}");

            switch (gameType)
            {
                case "drawing_submit":
                    HandleDrawingSubmit(fromPlayerId, d);
                    break;

                case "pfp_submit":
                    string pfpBase64 = d.Value<string>("image");
                    if (!string.IsNullOrEmpty(pfpBase64))
                        Raise(PfpSubmitted, fromPlayerId, pfpBase64);
                    break;

                case "guess":
                    string hex = d.Value<string>("hex");
                    if (!string.IsNullOrEmpty(hex))
                        Raise(GuessSubmitted, fromPlayerId, hex);
                    break;
            }

            if (GameMessageReceived == null)
                return;

            try
            {
                GameMessageReceived.Invoke(fromPlayerId, d);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void HandleDrawingSubmit(string fromPlayerId, JObject d)
        {
            DrawingSubmitPayload payload;
            try
            {
                payload = new DrawingSubmitPayload
                {
                    t = "drawing_submit",
                    prompt = d.Value<string>("prompt") ?? "",
                    image = d.Value<string>("image") ?? "",
                    mime = d.Value<string>("mime") ?? "image/png",
                    compression = d.Value<string>("compression") ?? "gzip",
                    w = d.Value<int?>("w") ?? 0,
                    h = d.Value<int?>("h") ?? 0
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HostRelayClient] Malformed drawing_submit from {fromPlayerId}: {e.Message}");
                return;
            }

            // A bad image shouldn't lose the submission (or throw inside the relay's handler)
            Texture2D texture = null;
            try
            {
                texture = DrawingImageCodec.DecompressToTexture2D(payload.image);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HostRelayClient] Couldn't decode drawing from {fromPlayerId}: {e.Message}");
            }

            if (DrawingSubmitted == null)
            {
                if (texture != null)
                    Destroy(texture);
                return;
            }

            try
            {
                DrawingSubmitted.Invoke(fromPlayerId, payload, texture);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        #endregion

        // One broken subscriber must not stop the relay's other listeners.
        private static void Raise<T>(Action<T> handler, T arg)
        {
            try { handler?.Invoke(arg); } catch (Exception e) { Debug.LogException(e); }
        }

        private static void Raise<T1, T2>(Action<T1, T2> handler, T1 a, T2 b)
        {
            try { handler?.Invoke(a, b); } catch (Exception e) { Debug.LogException(e); }
        }
    }
}

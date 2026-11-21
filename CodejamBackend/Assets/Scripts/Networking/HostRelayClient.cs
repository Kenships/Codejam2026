using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Alchemy.Inspector;
using Core.Utility;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Codejam.Networking
{
    /// <summary>
    /// Unity Host WebSocket client for the True Hue relay (relay/PROTOCOL.md).
    /// Connects via NativeWebSocket, routes host -> player messages (your_target, state, etc.),
    /// and dispatches incoming player messages (drawing_submit, pfp_submit, guess) to listeners.
    /// </summary>
    public class HostRelayClient : Singleton<HostRelayClient>
    {
        [Title("Relay Connection")]
        [SerializeField] private string relayUrl = "ws://localhost:8080/";
        [SerializeField] private bool logTraffic = false;

        private WebSocket _socket;
        private bool _connecting;
        private bool _isHostInRoom;

        public bool IsConnected => _socket != null && _socket.State == WebSocketState.Open;
        public bool IsInRoom => IsConnected && _isHostInRoom;
        public string RoomCode { get; private set; } = "";
        public string HostKey { get; private set; } = "";

        public event Action<string, string> RoomCreated;
        public event Action<string, string> PlayerJoined;
        public event Action<string, string> PlayerRejoined;
        public event Action<string> PlayerDisconnected;
        public event Action<string, string> PlayerLeft;
        public event Action<string, DrawingSubmitPayload, Texture2D> DrawingSubmitted;
        public event Action<string, string> PfpSubmitted;
        public event Action<string, string> GuessSubmitted;
        public event Action<string, JObject> GameMessageReceived;
        public event Action<string, string> RelayError;

        private void Update()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            _socket?.DispatchMessageQueue();
#endif
        }

        private async void OnApplicationQuit()
        {
            await DisconnectAsync();
        }

        public async Task ConnectAndCreateRoomAsync()
        {
            await ConnectAsync(onOpen: () =>
            {
                SendJson(new JObject { ["t"] = "host_create" });
            });
        }

        public async Task ConnectAndResumeRoomAsync(string room, string hostKey)
        {
            await ConnectAsync(onOpen: () =>
            {
                SendJson(new JObject
                {
                    ["t"] = "host_resume",
                    ["room"] = room,
                    ["hostKey"] = hostKey
                });
            });
        }

        private async Task ConnectAsync(Action onOpen)
        {
            if (_connecting)
                return;

            await DisconnectAsync();

            _connecting = true;
            _isHostInRoom = false;

            try
            {
                _socket = new WebSocket(relayUrl);

                _socket.OnOpen += () =>
                {
                    _connecting = false;
                    if (logTraffic)
                        Debug.Log($"[HostRelayClient] Connected to {relayUrl}");

                    onOpen?.Invoke();
                };

                _socket.OnError += err =>
                {
                    _connecting = false;
                    Debug.LogWarning($"[HostRelayClient] WebSocket error: {err}");
                    RelayError?.Invoke("socket_error", err);
                };

                _socket.OnClose += code =>
                {
                    _connecting = false;
                    _isHostInRoom = false;
                    if (logTraffic)
                        Debug.Log($"[HostRelayClient] Closed ({code})");
                };

                _socket.OnMessage += bytes =>
                {
                    string json = Encoding.UTF8.GetString(bytes);
                    HandleRawMessage(json);
                };

                await _socket.Connect();
            }
            catch (Exception ex)
            {
                _connecting = false;
                Debug.LogWarning($"[HostRelayClient] Failed to connect: {ex.Message}");
                RelayError?.Invoke("connect_failed", ex.Message);
            }
        }

        public async Task DisconnectAsync()
        {
            _isHostInRoom = false;
            _connecting = false;

            if (_socket != null)
            {
                WebSocket old = _socket;
                _socket = null;

                if (old.State == WebSocketState.Open || old.State == WebSocketState.Connecting)
                {
                    try
                    {
                        await old.Close();
                    }
                    catch
                    {
                        // Ignore close exceptions during teardown
                    }
                }
            }
        }

        #region Host -> Relay Sends

        /// <summary>
        /// Notifies all players that the drawing phase has started and sends each player their secret target hex.
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
                    if (p == null || string.IsNullOrEmpty(p.playerId))
                        continue;

                    string hex = "#" + ColorUtility.ToHtmlStringRGB(p.secretTargetColor);
                    SendToTarget(p.playerId, new JObject
                    {
                        ["t"] = "your_target",
                        ["hex"] = hex
                    });
                }
            }

            SendToTarget("players", new JObject
            {
                ["t"] = "state",
                ["phase"] = "drawing",
                ["round"] = round,
                ["remainingMs"] = remainingMs
            });
        }

        /// <summary>Broadcasts a phase/state update to all connected players.</summary>
        public void BroadcastPhaseState(string phase, int round, int remainingMs)
        {
            if (!IsInRoom)
                return;

            SendToTarget("players", new JObject
            {
                ["t"] = "state",
                ["phase"] = phase,
                ["round"] = round,
                ["remainingMs"] = remainingMs
            });
        }

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

            SendToTarget("players", new JObject
            {
                ["t"] = "round_drawing",
                ["image"] = drawing.imageBase64 ?? "",
                ["mime"] = string.IsNullOrEmpty(drawing.mime) ? "image/png" : drawing.mime,
                ["prompt"] = drawing.prompt ?? "",
                ["artistName"] = drawing.playerName ?? ""
            });

            BroadcastPhaseState("playing", round, remainingMs);
        }

        public void SendToTarget(string target, JObject payload)
        {
            if (!IsInRoom || string.IsNullOrEmpty(target) || payload == null)
                return;

            SendJson(new JObject
            {
                ["t"] = "send",
                ["to"] = target,
                ["d"] = payload
            });
        }

        private void SendJson(JObject obj)
        {
            if (!IsConnected)
                return;

            string text = obj.ToString(Newtonsoft.Json.Formatting.None);
            if (logTraffic)
                Debug.Log($"[HostRelayClient] -> {text}");

            _ = _socket.SendText(text);
        }

        #endregion

        #region Relay -> Host Parsing

        private void HandleRawMessage(string json)
        {
            if (logTraffic)
                Debug.Log($"[HostRelayClient] <- {json}");

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HostRelayClient] Invalid JSON: {ex.Message}");
                return;
            }

            string type = root.Value<string>("t");
            if (string.IsNullOrEmpty(type))
                return;

            switch (type)
            {
                case "room_created":
                    RoomCode = root.Value<string>("room") ?? "";
                    HostKey = root.Value<string>("hostKey") ?? "";
                    GameSessionData.RoomCode = RoomCode;
                    GameSessionData.HostKey = HostKey;
                    _isHostInRoom = true;
                    RoomCreated?.Invoke(RoomCode, HostKey);
                    break;

                case "host_resumed":
                    RoomCode = root.Value<string>("room") ?? RoomCode;
                    _isHostInRoom = true;
                    break;

                case "player_joined":
                    PlayerJoined?.Invoke(root.Value<string>("playerId"), root.Value<string>("name"));
                    break;

                case "player_rejoined":
                    PlayerRejoined?.Invoke(root.Value<string>("playerId"), root.Value<string>("name"));
                    break;

                case "player_disconnected":
                    PlayerDisconnected?.Invoke(root.Value<string>("playerId"));
                    break;

                case "player_left":
                    PlayerLeft?.Invoke(root.Value<string>("playerId"), root.Value<string>("reason"));
                    break;

                case "msg":
                    string from = root.Value<string>("from");
                    JObject d = root["d"] as JObject;
                    if (!string.IsNullOrEmpty(from) && d != null)
                        HandleGamePayload(from, d);
                    break;

                case "error":
                    RelayError?.Invoke(root.Value<string>("code"), root.Value<string>("message"));
                    break;
            }
        }

        private void HandleGamePayload(string fromPlayerId, JObject d)
        {
            string gameType = d.Value<string>("t");

            switch (gameType)
            {
                case "drawing_submit":
                    DrawingSubmitPayload payload = new()
                    {
                        t = gameType,
                        prompt = d.Value<string>("prompt") ?? "",
                        image = d.Value<string>("image") ?? "",
                        mime = d.Value<string>("mime") ?? "image/png",
                        compression = d.Value<string>("compression") ?? "gzip",
                        w = d.Value<int?>("w") ?? 0,
                        h = d.Value<int?>("h") ?? 0
                    };
                    Texture2D drawingTex = DrawingImageCodec.DecompressToTexture2D(payload.image);
                    DrawingSubmitted?.Invoke(fromPlayerId, payload, drawingTex);
                    break;

                case "pfp_submit":
                    string pfpBase64 = d.Value<string>("image");
                    if (!string.IsNullOrEmpty(pfpBase64))
                        PfpSubmitted?.Invoke(fromPlayerId, pfpBase64);
                    break;

                case "guess":
                    string hex = d.Value<string>("hex");
                    if (!string.IsNullOrEmpty(hex))
                        GuessSubmitted?.Invoke(fromPlayerId, hex);
                    break;
            }

            GameMessageReceived?.Invoke(fromPlayerId, d);
        }

        #endregion
    }
}

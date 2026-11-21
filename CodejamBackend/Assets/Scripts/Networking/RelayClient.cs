using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Core.Utility;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Codejam.Networking
{
    public enum RelayStatus
    {
        /// <summary>Not connected and not trying (before Start, or after quitting).</summary>
        Disconnected,
        /// <summary>Opening the socket, or waiting for room_created / host_resumed.</summary>
        Connecting,
        /// <summary>Socket open and the room is ours. Players can join.</summary>
        InRoom,
        /// <summary>Lost the connection; retrying. Phones see "host lost" for up to 60 s.</summary>
        Reconnecting,
    }

    /// <summary>
    /// The Unity HOST's connection to the relay (see relay/PROTOCOL.md).
    ///
    /// - Creates a room on connect (host_create), or resumes the previous one (host_resume)
    ///   so a Unity crash / editor play-mode restart within 60 s keeps the same room and players.
    /// - Reconnects automatically with backoff.
    /// - Tracks players and raises C# events for everything that happens.
    /// - Sends a keepalive so the relay doesn't close the room as idle while waiting in the lobby.
    ///
    /// NativeWebSocket 2.x delivers events on Unity's main thread by itself
    /// (no DispatchMessageQueue needed), as long as the WebSocket is created on
    /// the main thread, which it always is here.
    /// </summary>
    public class RelayClient : Singleton<RelayClient>
    {
        [Header("Relay")]
        [Tooltip("wss:// for the deployed relay, ws://localhost:8080 for a local one.")]
        [SerializeField] private string relayUrl = "wss://truehue-production.up.railway.app";

        [Tooltip("The phones' website. Used to build the join link / QR code.")]
        [SerializeField] private string joinSiteUrl = "https://codejam2026.pages.dev";

        [SerializeField] private bool connectOnStart = true;

        [Tooltip("Reuse the last room (saved in PlayerPrefs) if it still exists on the relay.")]
        [SerializeField] private bool resumePreviousRoom = true;

        [Tooltip("Rooms with no messages for 10 min are closed by the relay. Send something this often.")]
        [SerializeField, Min(5f)] private float keepAliveSeconds = 30f;

        [Tooltip("Log every message in and out (very noisy during drawing).")]
        [SerializeField] private bool logTraffic = false;

        private const string RoomPrefKey = "relay.room";
        private const string HostKeyPrefKey = "relay.hostKey";
        private const float MaxReconnectDelay = 5f;
        private const float HostActiveRetryDelay = 3f;

        private readonly Dictionary<string, RelayPlayer> _players = new();
        private WebSocket _socket;
        private string _hostKey;
        private bool _quitting;
        private int _reconnectAttempt;
        private Coroutine _reconnectRoutine;
        private Coroutine _keepAliveRoutine;

        // ---- public state ----

        public RelayStatus Status { get; private set; } = RelayStatus.Disconnected;

        /// <summary>The 4-letter code players type, or null before a room exists.</summary>
        public string RoomCode { get; private set; }

        /// <summary>Link for the lobby screen / QR code, e.g. https://codejam2026.pages.dev/join?room=KXQM</summary>
        public string JoinUrl =>
            RoomCode == null ? null : $"{joinSiteUrl.TrimEnd('/')}/join?room={RoomCode}";

        public string RelayUrl => relayUrl;

        /// <summary>Secret for host_resume. Unity-side only: never send it to phones.</summary>
        internal string HostKey => _hostKey;

        /// <summary>Everyone with a seat, including players currently reconnecting.</summary>
        public IReadOnlyDictionary<string, RelayPlayer> Players => _players;

        // ---- events (all raised on the main thread) ----

        public event Action<RelayStatus> StatusChanged;

        /// <summary>A room is ready (new or resumed). Argument: the room code.</summary>
        public event Action<string> RoomReady;

        /// <summary>
        /// The previous room is gone (relay restarted, idle timeout, or resume failed).
        /// A new room with a NEW code is created automatically right after.
        /// </summary>
        public event Action RoomLost;

        public event Action<RelayPlayer> PlayerJoined;
        public event Action<RelayPlayer> PlayerRejoined;
        public event Action<RelayPlayer> PlayerDisconnected;

        /// <summary>Seat released for good. Reason: "timeout" or "kicked".</summary>
        public event Action<RelayPlayer, string> PlayerLeft;

        /// <summary>
        /// A game message from a phone. Arguments: the sender's player id (stamped by the
        /// relay, so it can be trusted) and the payload `d` (its "t" field is the type).
        /// </summary>
        public event Action<string, JObject> GameMessage;

        /// <summary>
        /// The relay rejected something we sent. Arguments: error code (see PROTOCOL.md) and
        /// its message, if any. Resume errors are handled here first (new room / retry).
        /// </summary>
        public event Action<string, string> Error;

        // ---- lifecycle ----

        protected override void Awake()
        {
            base.Awake();
            if (Instance != this) return;

            if (resumePreviousRoom)
            {
                string savedRoom = PlayerPrefs.GetString(RoomPrefKey, "");
                string savedKey = PlayerPrefs.GetString(HostKeyPrefKey, "");
                if (savedRoom != "" && savedKey != "")
                {
                    RoomCode = savedRoom;
                    _hostKey = savedKey;
                }
            }
        }

        private void Start()
        {
            if (Instance == this && connectOnStart) Connect();
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
            CloseSocket();
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
            {
                _quitting = true;
                CloseSocket();
            }
            base.OnDestroy();
        }

        /// <summary>Open the connection. Called automatically on Start unless connectOnStart is off.</summary>
        public void Connect()
        {
            if (_socket != null) return;
            _quitting = false;
            SetStatus(_reconnectAttempt > 0 ? RelayStatus.Reconnecting : RelayStatus.Connecting);

            // Created here, on the main thread, so NativeWebSocket captures Unity's
            // SynchronizationContext and raises its events on the main thread.
            WebSocket socket = new WebSocket(relayUrl);
            _socket = socket;

            socket.OnOpen += () =>
            {
                if (socket != _socket) return;
                Debug.Log($"[Relay] Connected to {relayUrl}");
                _reconnectAttempt = 0;

                if (RoomCode != null && _hostKey != null)
                    SendEnvelope(new JObject { ["t"] = "host_resume", ["room"] = RoomCode, ["hostKey"] = _hostKey });
                else
                    SendEnvelope(new JObject { ["t"] = "host_create" });
            };

            socket.OnMessage += bytes =>
            {
                if (socket != _socket) return;
                HandleMessage(bytes);
            };

            socket.OnError += error =>
            {
                if (socket != _socket) return;
                Debug.LogWarning($"[Relay] Socket error: {error}");
            };

            socket.OnClose += code =>
            {
                if (socket != _socket) return;
                _socket = null;
                StopKeepAlive();
                Debug.Log($"[Relay] Connection closed: {(int)code} {code}");

                if (_quitting)
                {
                    SetStatus(RelayStatus.Disconnected);
                    return;
                }
                ScheduleReconnect();
            };

            // Connect() runs the receive loop and only completes when the socket
            // closes, so it is deliberately not awaited here.
            Observe(socket.Connect(), "connect");
        }

        // ---- sending (game code calls these) ----

        /// <summary>Send a game payload to one player. `d` must serialize to a JSON object with a "t" field.</summary>
        public bool SendToPlayer(string playerId, object d) => SendGame(playerId, d);

        /// <summary>Send a game payload to every connected player.</summary>
        public bool SendToAllPlayers(object d) => SendGame("players", d);

        /// <summary>Remove a player. Their phone is disconnected with close code 4000.</summary>
        public bool Kick(string playerId) =>
            SendEnvelope(new JObject { ["t"] = "kick", ["playerId"] = playerId });

        private bool SendGame(string to, object d)
        {
            if (Status != RelayStatus.InRoom)
            {
                Debug.LogWarning($"[Relay] Not in a room yet; dropped message to {to}.");
                return false;
            }
            JObject payload = d as JObject ?? JObject.FromObject(d);
            return SendEnvelope(new JObject { ["t"] = "send", ["to"] = to, ["d"] = payload });
        }

        private bool SendEnvelope(JObject envelope)
        {
            if (_socket == null || _socket.State != WebSocketState.Open) return false;
            string json = envelope.ToString(Newtonsoft.Json.Formatting.None);
            if (logTraffic) Debug.Log($"[Relay] → {json}");
            Observe(_socket.SendText(json), "send");
            return true;
        }

        // ---- receiving ----

        private void HandleMessage(byte[] bytes)
        {
            string text = Encoding.UTF8.GetString(bytes);
            JObject message;
            try
            {
                message = JObject.Parse(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Relay] Unreadable message ({e.Message}): {text}");
                return;
            }

            string type = (string)message["t"];
            if (logTraffic && type != "msg") Debug.Log($"[Relay] ← {text}");

            switch (type)
            {
                case "room_created":
                    _hostKey = (string)message["hostKey"];
                    EnterRoom((string)message["room"]);
                    break;

                case "host_resumed":
                    _players.Clear();
                    if (message["players"] is JArray list)
                    {
                        foreach (JToken p in list)
                        {
                            string id = (string)p["playerId"];
                            if (id != null) _players[id] = new RelayPlayer(id, (string)p["name"], (bool?)p["connected"] ?? false);
                        }
                    }
                    Debug.Log($"[Relay] Resumed room {RoomCode} with {_players.Count} player(s).");
                    EnterRoom((string)message["room"]);
                    break;

                case "player_joined":
                {
                    RelayPlayer player = new RelayPlayer((string)message["playerId"], (string)message["name"], true);
                    _players[player.Id] = player;
                    Raise(PlayerJoined, player);
                    break;
                }

                case "player_rejoined":
                {
                    string id = (string)message["playerId"];
                    if (!_players.TryGetValue(id, out RelayPlayer player))
                    {
                        player = new RelayPlayer(id, (string)message["name"], true);
                        _players[id] = player;
                    }
                    player.Connected = true;
                    Raise(PlayerRejoined, player);
                    break;
                }

                case "player_disconnected":
                    if (_players.TryGetValue((string)message["playerId"], out RelayPlayer gone))
                    {
                        gone.Connected = false;
                        Raise(PlayerDisconnected, gone);
                    }
                    break;

                case "player_left":
                {
                    string id = (string)message["playerId"];
                    if (_players.TryGetValue(id, out RelayPlayer left))
                    {
                        _players.Remove(id);
                        Raise(PlayerLeft, left, (string)message["reason"] ?? "timeout");
                    }
                    break;
                }

                case "msg":
                {
                    string from = (string)message["from"];
                    if (message["d"] is JObject d)
                    {
                        if (logTraffic && (string)d["t"] != "stroke_batch") Debug.Log($"[Relay] ← {from}: {d.ToString(Newtonsoft.Json.Formatting.None)}");
                        Raise(GameMessage, from, d);
                    }
                    break;
                }

                case "room_closed":
                    Debug.LogWarning($"[Relay] Room {RoomCode} closed by the relay: {(string)message["reason"]}");
                    ForgetRoom();
                    // The relay closes the socket next; the reconnect creates a new room.
                    break;

                case "error":
                    HandleError((string)message["code"], (string)message["message"]);
                    break;

                default:
                    Debug.LogWarning($"[Relay] Unknown message type '{type}': {text}");
                    break;
            }
        }

        private void HandleError(string code, string text)
        {
            Raise(Error, code, text);

            switch (code)
            {
                case "room_not_found":
                case "bad_host_key":
                    // Our saved room is gone (relay restarted, or it timed out). Start fresh.
                    Debug.LogWarning($"[Relay] Couldn't resume room {RoomCode} ({code}); creating a new room.");
                    ForgetRoom();
                    SendEnvelope(new JObject { ["t"] = "host_create" });
                    break;

                case "host_active":
                    // The relay hasn't noticed our old connection is dead yet (its heartbeat
                    // catches that within ~25-50 s). Keep asking until it lets us back in.
                    Debug.Log("[Relay] Relay still sees the previous host connection; retrying resume shortly.");
                    StartCoroutine(RetryResume());
                    break;

                default:
                    Debug.LogWarning($"[Relay] Error from relay: {code}");
                    break;
            }
        }

        private IEnumerator RetryResume()
        {
            yield return new WaitForSecondsRealtime(HostActiveRetryDelay);
            if (Status != RelayStatus.InRoom && RoomCode != null && _hostKey != null)
                SendEnvelope(new JObject { ["t"] = "host_resume", ["room"] = RoomCode, ["hostKey"] = _hostKey });
        }

        // ---- room state ----

        private void EnterRoom(string code)
        {
            RoomCode = code;
            PlayerPrefs.SetString(RoomPrefKey, code);
            PlayerPrefs.SetString(HostKeyPrefKey, _hostKey ?? "");
            PlayerPrefs.Save();

            Debug.Log($"[Relay] Room {code} ready. Join link: {JoinUrl}");
            SetStatus(RelayStatus.InRoom);
            StartKeepAlive();
            Raise(RoomReady, code);
        }

        private void ForgetRoom()
        {
            bool hadRoom = RoomCode != null;
            RoomCode = null;
            _hostKey = null;
            _players.Clear();
            PlayerPrefs.DeleteKey(RoomPrefKey);
            PlayerPrefs.DeleteKey(HostKeyPrefKey);
            PlayerPrefs.Save();
            StopKeepAlive();
            if (Status == RelayStatus.InRoom) SetStatus(RelayStatus.Connecting);
            if (hadRoom) Raise(RoomLost);
        }

        // ---- reconnect / keepalive ----

        private void ScheduleReconnect()
        {
            SetStatus(RelayStatus.Reconnecting);
            if (_reconnectRoutine != null) StopCoroutine(_reconnectRoutine);
            _reconnectRoutine = StartCoroutine(ReconnectAfterDelay());
        }

        private IEnumerator ReconnectAfterDelay()
        {
            float delay = Mathf.Min(0.5f * Mathf.Pow(2f, _reconnectAttempt), MaxReconnectDelay);
            _reconnectAttempt++;
            yield return new WaitForSecondsRealtime(delay);
            _reconnectRoutine = null;
            if (!_quitting) Connect();
        }

        private void StartKeepAlive()
        {
            StopKeepAlive();
            _keepAliveRoutine = StartCoroutine(KeepAlive());
        }

        private void StopKeepAlive()
        {
            if (_keepAliveRoutine == null) return;
            StopCoroutine(_keepAliveRoutine);
            _keepAliveRoutine = null;
        }

        private IEnumerator KeepAlive()
        {
            WaitForSecondsRealtime wait = new WaitForSecondsRealtime(keepAliveSeconds);
            while (true)
            {
                yield return wait;
                // Any message counts as room activity. Phones ignore unknown types.
                SendGame("players", new JObject { ["t"] = "keepalive" });
            }
        }

        // ---- helpers ----

        private void CloseSocket()
        {
            StopKeepAlive();
            if (_reconnectRoutine != null)
            {
                StopCoroutine(_reconnectRoutine);
                _reconnectRoutine = null;
            }
            WebSocket socket = _socket;
            _socket = null;
            if (socket != null && (socket.State == WebSocketState.Open || socket.State == WebSocketState.Connecting))
                Observe(socket.Close(), "close");
            SetStatus(RelayStatus.Disconnected);
        }

        private void SetStatus(RelayStatus status)
        {
            if (Status == status) return;
            Status = status;
            Raise(StatusChanged, status);
        }

        /// <summary>Log failures of fire-and-forget socket tasks instead of losing them.</summary>
        private static async void Observe(Task task, string what)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Relay] {what} failed: {e.Message}");
            }
        }

        // One broken subscriber must not stop the others or the relay client.
        private static void Raise(Action handler)
        {
            try { handler?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
        }

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

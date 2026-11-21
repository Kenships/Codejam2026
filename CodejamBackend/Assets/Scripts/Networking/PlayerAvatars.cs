using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Codejam.Networking
{
    /// <summary>
    /// Turns each player's profile picture (the `pfp_submit` message) into a Sprite.
    ///
    /// The phone sends it right after joining (if one was drawn on the join page),
    /// again on every rejoin, and whenever it's edited in the lobby, so each new
    /// one simply REPLACES the previous one for that player.
    ///
    /// Put this next to <see cref="RelayClient"/> and read avatars with TryGetAvatar,
    /// or listen to AvatarChanged.
    /// </summary>
    public class PlayerAvatars : MonoBehaviour
    {
        [SerializeField] private RelayClient relay;
        [SerializeField] private FilterMode filterMode = FilterMode.Bilinear;
        [SerializeField, Min(1f)] private float pixelsPerUnit = 100f;

        /// <summary>Ignore payloads larger than this (the phone keeps them under ~45 KB).</summary>
        private const int MaxImageBase64Length = 60_000;

        private readonly Dictionary<string, Sprite> _avatars = new();
        private bool _subscribed;

        /// <summary>
        /// Arguments: player id, and the new sprite, or null when the player left
        /// (or the room was lost) and their avatar was removed.
        /// </summary>
        public event Action<string, Sprite> AvatarChanged;

        public bool TryGetAvatar(string playerId, out Sprite sprite) =>
            _avatars.TryGetValue(playerId, out sprite);

        private void Start()
        {
            if (relay == null) relay = RelayClient.Instance;
            if (relay == null)
            {
                Debug.LogError("[PlayerAvatars] No RelayClient in the scene.");
                return;
            }
            relay.GameMessage += OnGameMessage;
            relay.PlayerLeft += OnPlayerLeft;
            relay.RoomLost += OnRoomLost;
            _subscribed = true;
        }

        private void OnDestroy()
        {
            if (_subscribed && relay != null)
            {
                relay.GameMessage -= OnGameMessage;
                relay.PlayerLeft -= OnPlayerLeft;
                relay.RoomLost -= OnRoomLost;
            }
            foreach (Sprite sprite in _avatars.Values) DestroySprite(sprite);
            _avatars.Clear();
        }

        private void OnGameMessage(string playerId, JObject d)
        {
            if ((string)d["t"] != "pfp_submit") return;

            string image = (string)d["image"];
            if (string.IsNullOrEmpty(image) || image.Length > MaxImageBase64Length)
            {
                Debug.LogWarning($"[PlayerAvatars] Ignoring pfp from {playerId}: empty or too large.");
                return;
            }

            Texture2D texture;
            try
            {
                // Same gzip + base64 format as drawing_submit.
                texture = DrawingImageCodec.DecompressToTexture2D(image, filterMode);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerAvatars] Bad pfp from {playerId}: {e.Message}");
                return;
            }
            if (texture == null)
            {
                Debug.LogWarning($"[PlayerAvatars] Couldn't decode pfp from {playerId}.");
                return;
            }

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);

            if (_avatars.TryGetValue(playerId, out Sprite old)) DestroySprite(old);
            _avatars[playerId] = sprite;
            Raise(playerId, sprite);
        }

        private void OnPlayerLeft(RelayPlayer player, string reason)
        {
            if (!_avatars.TryGetValue(player.Id, out Sprite sprite)) return;
            _avatars.Remove(player.Id);
            DestroySprite(sprite);
            Raise(player.Id, null);
        }

        private void OnRoomLost()
        {
            List<string> ids = new List<string>(_avatars.Keys);
            foreach (Sprite sprite in _avatars.Values) DestroySprite(sprite);
            _avatars.Clear();
            foreach (string id in ids) Raise(id, null);
        }

        private void Raise(string playerId, Sprite sprite)
        {
            try { AvatarChanged?.Invoke(playerId, sprite); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // We created both the texture and the sprite, so we must free both.
        private static void DestroySprite(Sprite sprite)
        {
            if (sprite == null) return;
            Texture2D texture = sprite.texture;
            Destroy(sprite);
            if (texture != null) Destroy(texture);
        }
    }
}

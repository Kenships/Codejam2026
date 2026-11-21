using UnityEngine;

namespace Codejam.Networking
{
    /// <summary>
    /// Temporary on-screen overlay for testing the relay connection before the real
    /// lobby UI exists: status, room code, join link, and each player with their avatar.
    /// Remove it (or untick "show") once the real UI is in.
    /// </summary>
    public class RelayDebugHUD : MonoBehaviour
    {
        [SerializeField] private bool show = true;
        [SerializeField] private RelayClient relay;
        [SerializeField] private PlayerAvatars avatars;
        [SerializeField, Min(16f)] private float avatarSize = 48f;

        private GUIStyle _codeStyle;
        private GUIStyle _labelStyle;

        private void Start()
        {
            if (relay == null) relay = RelayClient.Instance;
            if (avatars == null) avatars = FindAnyObjectByType<PlayerAvatars>();
        }

        private void OnGUI()
        {
            if (!show || relay == null) return;

            _codeStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 48, fontStyle = FontStyle.Bold };
            _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };

            GUILayout.BeginArea(new Rect(16f, 16f, 460f, Screen.height - 32f), GUI.skin.box);
            GUILayout.Label($"Relay: {relay.Status}", _labelStyle);
            GUILayout.Label(relay.RelayUrl, _labelStyle);

            if (relay.RoomCode != null)
            {
                GUILayout.Label(relay.RoomCode, _codeStyle);
                GUILayout.Label(relay.JoinUrl, _labelStyle);
            }

            GUILayout.Space(8f);
            GUILayout.Label($"Players ({relay.Players.Count}):", _labelStyle);
            foreach (RelayPlayer player in relay.Players.Values)
            {
                GUILayout.BeginHorizontal();
                Rect slot = GUILayoutUtility.GetRect(avatarSize, avatarSize, GUILayout.Width(avatarSize), GUILayout.Height(avatarSize));
                if (avatars != null && avatars.TryGetAvatar(player.Id, out Sprite sprite) && sprite != null)
                    GUI.DrawTexture(slot, sprite.texture, ScaleMode.ScaleToFit);
                else
                    GUI.Box(slot, "?");
                GUILayout.Label($"{player.Name}{(player.Connected ? "" : "  (reconnecting…)")}", _labelStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }
    }
}

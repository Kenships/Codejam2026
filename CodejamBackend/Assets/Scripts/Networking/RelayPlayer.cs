namespace Codejam.Networking
{
    /// <summary>
    /// One player (phone) in the room, as tracked by <see cref="RelayClient"/>.
    /// The Id is assigned by the relay and is stable across reconnects.
    /// </summary>
    public class RelayPlayer
    {
        public string Id { get; }
        public string Name { get; }

        /// <summary>
        /// False while the phone is reconnecting. The relay keeps the seat for
        /// 30 s; if they don't return in time, <see cref="RelayClient.PlayerLeft"/> fires.
        /// </summary>
        public bool Connected { get; internal set; }

        public RelayPlayer(string id, string name, bool connected)
        {
            Id = id;
            Name = name;
            Connected = connected;
        }

        public override string ToString() => $"{Name} ({Id}{(Connected ? "" : ", disconnected")})";
    }
}

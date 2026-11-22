using System.Collections.Generic;

/// <summary>
/// Drawing prompts. At the start of each drawing phase every player is offered
/// <see cref="ChoicesPerPlayer"/> of these (via your_target.categories) and picks one to draw;
/// the pick becomes their drawing's prompt shown to the guessers. Players can also suggest a
/// category in the lobby (prompt_suggest); each suggestion is dealt to exactly one other artist.
/// </summary>
public static class CategoryWordBank
{
    public const int ChoicesPerPlayer = 4;

    public static readonly string[] Categories =
    {
        "Animals", "Food", "Drinks", "Fruits", "Vegetables", "Desserts", "Snacks", "Fast Food",
        "Restaurants", "Clothing", "Shoes", "Accessories", "Colours", "Shapes", "Numbers",
        "Body Parts", "Emotions", "Personality Traits", "Jobs", "Hobbies", "Sports", "Board Games",
        "Video Games", "Toys", "Musical Instruments", "Music Genres", "Movies", "TV Shows", "Books",
        "Cartoons", "Celebrities", "Superheroes", "Villains", "Fictional Characters",
        "Mythical Creatures", "Places", "Countries", "Cities", "Landmarks", "Tourist Attractions",
        "Buildings", "Rooms", "Furniture", "Household Items", "Kitchen Items", "Bathroom Items",
        "School Supplies", "Office Supplies", "Electronics", "Technology", "Websites",
        "Social Media", "Apps", "Vehicles", "Public Transportation", "Tools", "Machines", "Weapons",
        "Materials", "Metals", "Gems", "Plants", "Flowers", "Trees", "Insects", "Birds",
        "Sea Creatures", "Reptiles", "Dinosaurs", "Pets", "Weather", "Seasons", "Natural Disasters",
        "Landforms", "Bodies of Water", "Space", "Planets", "Science", "School Subjects",
        "Languages", "Holidays", "Celebrations", "Traditions", "Historical Events", "Time Periods",
        "Professions", "Businesses", "Stores", "Brands", "Products", "Currencies", "Activities",
        "Chores", "Outdoor Activities", "Indoor Activities", "Vacation Activities",
        "Things at a Party", "Things at the Beach", "Things at School", "Things in a Hospital",
        "Things in a Kitchen"
    };

    /// <summary>Longest suggestion accepted (matches the frontend's PROMPT_SUGGESTION_MAX_LENGTH).</summary>
    public const int SuggestionMaxLength = 24;

    /// <summary>A category a player suggested in the lobby.</summary>
    public readonly struct Suggestion
    {
        public readonly string FromPlayerId;
        public readonly string Text;

        public Suggestion(string fromPlayerId, string text)
        {
            FromPlayerId = fromPlayerId;
            Text = text;
        }
    }

    /// <summary>
    /// Trims, collapses whitespace, drops control characters and caps the length.
    /// Returns "" for nothing usable (which withdraws a suggestion).
    /// </summary>
    public static string CleanSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var sb = new System.Text.StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else if (!char.IsControl(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        string cleaned = sb.ToString().Trim();
        return cleaned.Length > SuggestionMaxLength ? cleaned.Substring(0, SuggestionMaxLength).TrimEnd() : cleaned;
    }

    /// <summary>
    /// Deals each artist <see cref="ChoicesPerPlayer"/> distinct categories (hands[i] belongs to
    /// artistIds[i]).
    /// Suggestions first: each one lands in exactly ONE hand, never its author's, never next to
    /// the same text, and no suggested text is dealt from the bank to anyone else. Suggestions
    /// spread out (the hand with the fewest so far takes the next). One that can't be placed
    /// (e.g. its author is the only artist, or every other hand is full) is returned in
    /// <paramref name="undelivered"/>.
    /// The rest of each hand comes from the bank; across hands nobody shares a bank category
    /// until the bank runs out. Each hand is shuffled, so suggestions don't always sit first.
    /// </summary>
    public static List<string[]> Deal(
        IReadOnlyList<string> artistIds,
        IReadOnlyList<Suggestion> suggestions,
        System.Random random,
        out List<Suggestion> undelivered)
    {
        int artistCount = artistIds.Count;
        var hands = new List<List<string>>(artistCount);
        for (int i = 0; i < artistCount; i++)
            hands.Add(new List<string>(ChoicesPerPlayer));

        undelivered = new List<Suggestion>();
        var suggestedTexts = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        // ---- suggestions ----
        var pending = new List<Suggestion>();
        if (suggestions != null)
        {
            foreach (Suggestion s in suggestions)
            {
                if (!string.IsNullOrEmpty(s.Text))
                    pending.Add(s);
            }
        }
        Shuffle(pending, random);

        // Artists' own suggestions have one fewer possible hand, so place them first
        pending.Sort((a, b) => IsArtist(artistIds, b.FromPlayerId).CompareTo(IsArtist(artistIds, a.FromPlayerId)));

        foreach (Suggestion s in pending)
        {
            int best = -1;
            int bestCount = int.MaxValue;
            int ties = 0;
            for (int i = 0; i < artistCount; i++)
            {
                List<string> hand = hands[i];
                if (artistIds[i] == s.FromPlayerId || hand.Count >= ChoicesPerPlayer || ContainsIgnoreCase(hand, s.Text))
                    continue;

                if (hand.Count < bestCount)
                {
                    best = i;
                    bestCount = hand.Count;
                    ties = 1;
                }
                else if (hand.Count == bestCount && random.Next(++ties) == 0)
                {
                    best = i; // random among equally empty hands
                }
            }

            if (best < 0)
            {
                undelivered.Add(s);
                continue;
            }

            hands[best].Add(s.Text);
            suggestedTexts.Add(s.Text);
        }

        // ---- bank fill ----
        var deck = new List<string>();
        foreach (List<string> hand in hands)
        {
            while (hand.Count < ChoicesPerPlayer)
            {
                // Refill and reshuffle when the deck runs out, skipping what's already in this
                // hand and anything a player suggested
                if (deck.Count == 0)
                {
                    foreach (string category in Categories)
                    {
                        if (!suggestedTexts.Contains(category) && !ContainsIgnoreCase(hand, category))
                            deck.Add(category);
                    }
                    Shuffle(deck, random);
                }

                string next = deck[deck.Count - 1];
                deck.RemoveAt(deck.Count - 1);
                if (!ContainsIgnoreCase(hand, next))
                    hand.Add(next);
            }
        }

        var result = new List<string[]>(artistCount);
        foreach (List<string> hand in hands)
        {
            Shuffle(hand, random);
            result.Add(hand.ToArray());
        }
        return result;
    }

    private static bool IsArtist(IReadOnlyList<string> artistIds, string playerId)
    {
        for (int i = 0; i < artistIds.Count; i++)
        {
            if (artistIds[i] == playerId)
                return true;
        }
        return false;
    }

    private static bool ContainsIgnoreCase(List<string> list, string value)
    {
        foreach (string item in list)
        {
            if (string.Equals(item, value, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void Shuffle<T>(List<T> list, System.Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

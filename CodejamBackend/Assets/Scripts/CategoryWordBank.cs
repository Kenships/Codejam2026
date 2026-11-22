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
        // Every entry should be something concrete you can sketch on a phone in ~30 seconds.
        // Keep entries <= SuggestionMaxLength so they fit the same UI as player suggestions.

        // Animals & nature
        "Animals", "Pets", "Farm Animals", "Jungle Animals", "Sea Creatures", "Birds", "Insects",
        "Reptiles", "Dinosaurs", "Mythical Creatures", "Plants", "Flowers", "Trees", "Fruits",
        "Vegetables", "Weather", "Seasons", "Natural Disasters", "Landforms", "Bodies of Water",
        "Space", "Planets", "Gems", "Things in the Sky", "Things in a Garden", "Things in a Forest",

        // Food & drink
        "Food", "Drinks", "Desserts", "Snacks", "Fast Food", "Breakfast Foods", "Pizza Toppings",
        "Sandwiches", "Candy", "Ice Cream Treats", "Baked Goods", "Soups", "Picnic Food",

        // People & characters
        "Body Parts", "Facial Expressions", "Jobs", "Superheroes", "Villains", "Monsters",
        "Robots", "Aliens", "Pirates", "Knights & Castles", "Fairy Tales", "Wizards & Magic",

        // Things you wear
        "Clothing", "Shoes", "Hats", "Accessories", "Costumes", "Jewelry",

        // Things around the house
        "Furniture", "Rooms", "Household Items", "Kitchen Items", "Kitchen Appliances",
        "Bathroom Items", "Things in a Bedroom", "Things in a Garage", "Garden Tools", "Tools",
        "Electronics", "School Supplies", "Office Supplies",

        // Getting around
        "Vehicles", "Things with Wheels", "Things That Fly", "Boats", "Public Transportation",

        // Places & buildings
        "Buildings", "Landmarks", "Things in a City", "Playground Equipment",

        // Fun & games
        "Sports", "Sports Equipment", "Hobbies", "Toys", "Board Games", "Musical Instruments",
        "Camping Gear", "Party Supplies", "Holidays", "Halloween", "Winter Things",
        "Summer Things", "Chores",

        // Things at / in a place
        "Things at a Party", "Things at the Beach", "Things at School", "Things in a Hospital",
        "Things at a Zoo", "Things at a Carnival", "Things at a Farm", "Things at a Circus",
        "Things in a Kitchen", "Things on a Pirate Ship", "Things Underwater",

        // Descriptive
        "Shapes", "Things That Are Round", "Things That Are Tiny", "Things That Are Huge",
        "Things That Glow", "Things That Are Hot", "Things That Are Cold", "Things That Are Sharp",
        "Things That Are Squishy", "Things That Bounce", "Things That Make Noise", "Things That Melt"
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

using System.Collections.Generic;

/// <summary>
/// Drawing prompts. At the start of each drawing phase every player is offered
/// <see cref="ChoicesPerPlayer"/> of these (via your_target.categories) and picks one to draw;
/// the pick becomes their drawing's prompt shown to the guessers.
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

    /// <summary>
    /// Deals each player <see cref="ChoicesPerPlayer"/> distinct categories. Across players,
    /// nobody shares a category until the bank runs out (100 categories covers 25 players).
    /// </summary>
    public static List<string[]> Deal(int playerCount, System.Random random)
    {
        var hands = new List<string[]>(playerCount);
        var deck = new List<string>();

        for (int p = 0; p < playerCount; p++)
        {
            var hand = new string[ChoicesPerPlayer];
            for (int c = 0; c < ChoicesPerPlayer; c++)
            {
                // Refill and reshuffle when the deck runs out, skipping this hand's picks
                if (deck.Count == 0)
                {
                    deck.AddRange(Categories);
                    deck.RemoveAll(category => System.Array.IndexOf(hand, category) >= 0);
                    Shuffle(deck, random);
                }

                hand[c] = deck[deck.Count - 1];
                deck.RemoveAt(deck.Count - 1);
            }
            hands.Add(hand);
        }

        return hands;
    }

    private static void Shuffle(List<string> list, System.Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

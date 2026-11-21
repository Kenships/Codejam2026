using System.Text.RegularExpressions;

namespace Core.Utility
{
    public static class StringExtensions
    {
        public static string AddSpaceToCamelCase(this string str)
        {
            return Regex.Replace(str, "([a-z])([A-Z])", "$1 $2");
        }
    }
}

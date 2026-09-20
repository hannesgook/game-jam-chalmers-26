using System.Globalization;
using UnityEngine;

namespace SparvagnRush.Map
{
    public sealed class CityPlace
    {
        public string name;
        public string kind;
        public Vector3 position;
        public int stationIndex = -1;
        public static bool Matches(string name, string query) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            name, query.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }
}

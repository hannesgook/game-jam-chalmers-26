using System.Collections.Generic;
using UnityEngine;

namespace TramRush.Map
{
    public sealed class TramStation
    {
        public string name;
        public Vector3 position;

        public static List<TramStation> Collect(Transform city, TramTrackNetwork network)
        {
            var stations = new List<TramStation>();
            var names = new HashSet<string>();
            foreach (Transform child in city.GetComponentsInChildren<Transform>())
            {
                if (!child.name.StartsWith("Tram Stop")) continue;
                TextMesh text = child.GetComponentInChildren<TextMesh>();
                string name = text != null ? text.text : child.name;
                if (!names.Add(name)) continue;
                stations.Add(new TramStation { name = name, position = network.FindClosestEdge(child.position).Point });
            }
            stations.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return stations;
        }
    }
}

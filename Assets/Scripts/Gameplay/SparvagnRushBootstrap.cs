using System;
using System.Collections;
using System.IO;
using SparvagnRush.Map;
using UnityEngine;

namespace SparvagnRush.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class SparvagnRushBootstrap : MonoBehaviour
    {
        [SerializeField] private TramTrackNetwork network;

        public void SetNetwork(TramTrackNetwork trackNetwork) => network = trackNetwork;

        // The layers a derailed tram has to collide with: the ground to land on, the
        // walls and roofs to hit on the way down.
        private static readonly string[] CollidableLayers = { "Ground", "Buildings", "BuildingRoofs" };

        // A map generated before these layers carried colliders leaves a derailed tram
        // falling through the city, so fit them at play time rather than making the map
        // have to be rebuilt first. Regenerating bakes them in and skips the cook cost.
        private void EnsureMapColliders()
        {
            foreach (string name in CollidableLayers) EnsureCollider(name);
        }

        private void EnsureCollider(string layerName)
        {
            Transform layer = transform.Find(layerName);
            if (layer == null || layer.GetComponent<MeshCollider>() != null) return;

            MeshFilter filter = layer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            if (!filter.sharedMesh.isReadable)
            {
                Debug.LogWarning($"{layerName} mesh is not readable, so no collider could be fitted. Run Tools > Göteborg > Generate Map.");
                return;
            }

            layer.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        }

        private void Start()
        {
            Application.runInBackground = true;
            if (network == null) network = GetComponentInChildren<TramTrackNetwork>();
            if (network == null || network.Paths.Count == 0)
            {
                Debug.LogError("Spårvagn Rush needs a generated tram network. Run Tools > Göteborg > Generate Map.");
                return;
            }

            EnsureMapColliders();
            EnsureGroundTexture();

            Vector3 requestedStart = network.Graph[0].position;
            GameObject tram_obj = Resources.Load<GameObject>("Prefabs/tram");
            GameObject tramObject = GameObject.Instantiate(tram_obj);
            tramObject.name = "PlayerTram";
           // tramObject.transform.localScale = new Vector3(3.2f, 2.4f, 9f);
            // The prefab's collider is left alone: it sits on a child object, and the
            // wreck rigidbody added on derail composes it.
//          tramObject.GetComponent<Renderer>().sharedMaterial = CreateRuntimeMaterial(new Color(0.1f, 0.55f, 0.95f));
            TramController tram = tramObject.AddComponent<TramController>();
            tram.Initialize(network, requestedStart);
            tramObject.AddComponent<TramAudio>().Initialize(tram);
            tramObject.AddComponent<TramImpactEffects>();

            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }
            camera.fieldOfView = 58f;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.48f, 0.67f, 0.82f);
            camera.allowHDR = true;
            camera.nearClipPlane = 0.15f;
            camera.farClipPlane = 2200f;
            camera.gameObject.AddComponent<TramFollowCamera>().SetTarget(tram.transform);

            ConfigureEnvironment();
            gameObject.AddComponent<CityPresentation>().Initialize(network, camera);

            TramGameManager manager = gameObject.AddComponent<TramGameManager>();
            manager.Initialize(network, tram);
            CityOverview overview = gameObject.AddComponent<CityOverview>();
            overview.Initialize(camera, manager, network, transform);
            gameObject.AddComponent<TramInterface>().Initialize(manager, overview);

            AmbientNpcManager pedestrians = gameObject.AddComponent<AmbientNpcManager>();
            pedestrians.Initialize(network, tram.transform);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "-captureSmoke"))
                StartCoroutine(CaptureSmokeFrame(camera));
#endif
        }

        public static Material CreateRuntimeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return new Material(shader) { color = color };
        }

        private static void ConfigureEnvironment()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.68f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.46f, 0.47f);
            RenderSettings.ambientGroundColor = new Color(0.19f, 0.20f, 0.18f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.57f, 0.65f, 0.70f);
            RenderSettings.fogDensity = 0.00045f;

            Light sun = FindFirstObjectByType<Light>();
            if (sun == null)
            {
                var sunObject = new GameObject("Göteborg Sun");
                sun = sunObject.AddComponent<Light>();
            }
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.86f, 0.69f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(32f, -38f, 0f);
        }

        // Older generated scenes have valid ground UVs but no image assigned. Give those
        // scenes the same detailed fallback immediately, without requiring a map rebuild.
        private void EnsureGroundTexture()
        {
            Transform ground = transform.Find("Ground");
            Renderer renderer = ground != null ? ground.GetComponent<Renderer>() : null;
            if (renderer == null || renderer.sharedMaterial == null || renderer.sharedMaterial.mainTexture != null) return;

            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                name = "Runtime Ground Detail",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            var pixels = new Color[size * size];
            Color moss = new(0.24f, 0.31f, 0.19f);
            Color grass = new(0.39f, 0.40f, 0.23f);
            Color soil = new(0.27f, 0.235f, 0.18f);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size * Mathf.PI * 2f;
                float v = y / (float)size * Mathf.PI * 2f;
                float pattern = Mathf.Sin(u * 2f + Mathf.Cos(v)) * 0.5f +
                                Mathf.Cos(v * 3f - Mathf.Sin(u * 2f)) * 0.3f +
                                Mathf.Sin((u + v) * 7f) * 0.2f;
                float grain = (((x * 73856093) ^ (y * 19349663)) & 255) / 255f;
                Color color = Color.Lerp(moss, grass, Mathf.InverseLerp(-0.75f, 0.75f, pattern));
                if (pattern < -0.48f) color = Color.Lerp(color, soil, (-0.48f - pattern) * 1.2f);
                pixels[y * size + x] = color * (0.91f + grain * 0.16f);
            }
            texture.SetPixels(pixels);
            texture.Apply(true, false);

            Material material = renderer.material;
            material.mainTexture = texture;
            material.mainTextureScale = new Vector2(90f, 120f);
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
                material.SetTextureScale("_BaseMap", new Vector2(90f, 120f));
            }
            material.color = Color.white;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static IEnumerator CaptureSmokeFrame(Camera camera)
        {
            yield return new WaitForSeconds(2f);
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "smoke.png"));
            var renderTexture = new RenderTexture(1280, 720, 24);
            var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            RenderTexture previousTarget = camera.targetTexture;
            camera.targetTexture = renderTexture;
            camera.Render();
            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            texture.Apply();
            File.WriteAllBytes(output, texture.EncodeToPNG());
            camera.targetTexture = previousTarget;
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(renderTexture);
            UnityEngine.Object.Destroy(texture);
            Debug.Log($"Smoke screenshot saved to {output}");
            Application.Quit();
        }
#endif
    }
}

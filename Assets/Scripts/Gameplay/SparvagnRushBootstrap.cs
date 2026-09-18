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

        private void Start()
        {
            Application.runInBackground = true;
            if (network == null) network = GetComponentInChildren<TramTrackNetwork>();
            if (network == null || network.Paths.Count == 0)
            {
                Debug.LogError("Spårvagn Rush needs a generated tram network. Run Tools > Göteborg > Generate Map.");
                return;
            }

            Vector3 requestedStart = network.Graph[0].position;
            GameObject tram_obj = Resources.Load<GameObject>("Prefabs/tram");
            GameObject tramObject = GameObject.Instantiate(tram_obj);
            tramObject.name = "PlayerTram";
           // tramObject.transform.localScale = new Vector3(3.2f, 2.4f, 9f);
            Destroy(tramObject.GetComponent<Collider>());
//          tramObject.GetComponent<Renderer>().sharedMaterial = CreateRuntimeMaterial(new Color(0.1f, 0.55f, 0.95f));
            TramController tram = tramObject.AddComponent<TramController>();
            tram.Initialize(network, requestedStart);
            tramObject.AddComponent<TramAudio>().Initialize(tram);

            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }
            camera.fieldOfView = 58f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.12f, 0.16f);
            camera.gameObject.AddComponent<TramFollowCamera>().SetTarget(tram.transform);

            TramGameManager manager = gameObject.AddComponent<TramGameManager>();
            manager.Initialize(network, tram);

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

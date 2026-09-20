using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace SparvagnRush.Editor
{
    /// <summary>
    /// Editor-side twin of Tools/FetchAerial.py, so a fresh clone can pull the
    /// orthophoto from the menu instead of needing Python on the machine. Both
    /// write the same file; whichever is more convenient wins.
    /// </summary>
    public static partial class GothenburgMapGenerator
    {
        private const string AerialEndpoint = "https://opengeodata.goteborg.se/services/ortofoto/wms/v1";
        private const string AerialLayer = "orto_2025";

        // The projected map is about 0.704 as wide as it is tall at this latitude.
        // 5760x8192 therefore preserves the real-world aspect ratio closely while
        // giving the ground about 20 cm per source pixel.
        private const int AerialWidth = 5760;
        private const int AerialHeight = 8192;

        // A WMS error comes back as a short XML ServiceException with a 200 status,
        // so a plausible size is part of deciding whether this is really a photo.
        private const int SmallestPlausibleAerial = 100_000;

        // The bounds are the generator's own consts, so the image and the terrain
        // UVs it is projected through cannot drift apart.
        private static string AerialUrl =>
            AerialEndpoint +
            "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
            // CRS:84 and image/jpeg are escaped so the query is byte-for-byte the
            // one Tools/FetchAerial.py sends, which this server is known to accept.
            $"&LAYERS={AerialLayer}&STYLES=raster&CRS=CRS%3A84" +
            $"&BBOX={Deg(West)},{Deg(South)},{Deg(East)},{Deg(North)}" +
            $"&WIDTH={AerialWidth}&HEIGHT={AerialHeight}" +
            "&FORMAT=image%2Fjpeg&TRANSPARENT=false";

        [MenuItem("Tools/Göteborg/Download Aerial Imagery")]
        public static void DownloadAerialMenu()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            string destination = Path.GetFullPath(Path.Combine(projectRoot, OptionalAerialSource));

            if (File.Exists(destination) && !EditorUtility.DisplayDialog(
                    "Download aerial imagery",
                    $"{OptionalAerialSource} already exists and will be overwritten with a fresh copy of the Göteborg 2025 orthophoto.\n\nContinue?",
                    "Download", "Cancel"))
                return;

            try
            {
                byte[] photo = RequestAerial();
                if (photo == null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, photo);
            }
            catch (Exception error)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"Aerial download failed: {error.Message}");
                EditorUtility.DisplayDialog("Download aerial imagery",
                    $"The download failed:\n\n{error.Message}\n\nThe Göteborg WMS is occasionally down for maintenance; retrying later usually clears it.",
                    "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.ImportAsset(OptionalAerialSource, ImportAssetOptions.ForceUpdate);
            ApplyAerialImportSettings();
            int relinked = RelinkAerialMaterials();

            Debug.Log($"Downloaded the Göteborg 2025 CC0 orthophoto to {OptionalAerialSource}." +
                      (relinked > 0
                          ? $" Repointed {relinked} generated material(s) at it, so the ground and roofs show it straight away."
                          : " Run Tools/Göteborg/Generate Map to put it on the ground."));
        }

        /// <summary>Blocks the editor behind a cancelable bar; the image is ~8 MB.</summary>
        private static byte[] RequestAerial()
        {
            using var request = UnityWebRequest.Get(AerialUrl);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("User-Agent", "SparvagnRush-gamejam/1.0 (CC0 orthophoto import)");
            request.timeout = 300;

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                // The server renders the whole tile before it sends anything, so the
                // bar waits at a third and tracks the body once it starts streaming.
                float progress = Mathf.Max(request.downloadProgress, 0f) * 0.6f + 0.3f;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Downloading aerial imagery", "Rendering the orthophoto…", progress))
                {
                    request.Abort();
                    return null;
                }
                Thread.Sleep(50);
            }

            if (request.result != UnityWebRequest.Result.Success)
                throw new IOException($"WMS request failed ({request.responseCode}): {request.error}");

            string contentType = request.GetResponseHeader("Content-Type") ?? "";
            byte[] payload = request.downloadHandler.data;
            if (!contentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
                payload == null || payload.Length < SmallestPlausibleAerial)
                throw new FormatException(
                    $"The WMS returned {contentType} ({payload?.Length ?? 0} bytes), not an orthophoto.");
            return payload;
        }

        /// <summary>
        /// A fresh import would cap the texture at Unity's default 2048 and throw away
        /// three quarters of the resolution the WMS was asked for, so the settings the
        /// ground needs are applied rather than left to the default importer.
        /// </summary>
        private static void ApplyAerialImportSettings()
        {
            if (AssetImporter.GetAtPath(OptionalAerialSource) is not TextureImporter importer) return;
            importer.textureType = TextureImporterType.Default;
            // Never below the longest edge the WMS was asked for.
            importer.maxTextureSize = Mathf.Max(AerialWidth, AerialHeight);
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;
            importer.sRGBTexture = true;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.wrapMode = TextureWrapMode.Clamp;
            // The ground samples it on the GPU only; keeping a CPU copy would cost
            // another 140 MB of managed memory for nothing.
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// The generated materials reference the photo by GUID, and a newly imported
        /// file gets a new one, so on a clone without the image they point at nothing.
        /// Repointing them here means fetching the photo is enough on its own; the map
        /// does not have to be regenerated just to hook the texture back up.
        /// </summary>
        private static int RelinkAerialMaterials()
        {
            var aerial = AssetDatabase.LoadAssetAtPath<Texture2D>(OptionalAerialSource);
            if (aerial == null) return 0;

            int changed = 0;
            foreach (string name in new[] { "Ground", "BuildingRoofs" })
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>($"{GeneratedAssetFolder}/{name}.mat");
                if (material == null || material.mainTexture == aerial) continue;
                material.mainTexture = aerial;
                material.mainTextureScale = Vector2.one;
                material.color = Color.white;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                if (material.HasProperty("_BaseMap"))
                {
                    material.SetTexture("_BaseMap", aerial);
                    material.SetTextureScale("_BaseMap", Vector2.one);
                }
                EditorUtility.SetDirty(material);
                changed++;
            }
            if (changed > 0) AssetDatabase.SaveAssets();
            return changed;
        }
    }
}

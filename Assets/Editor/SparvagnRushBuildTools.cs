using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SparvagnRush.Editor
{
    public static class SparvagnRushBuildTools
    {
        private const string OutputPath = "Builds/Windows/SparvagnRush.exe";

        [MenuItem("Tools/Göteborg/Build Windows")]
        public static void BuildWindows()
        {
            const string scenePath = "Assets/Scenes/SampleScene.unity";
            Scene buildScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (GameObject.Find("GeneratedCity") == null)
                GothenburgMapGenerator.GenerateFromDefaultFile();
            EditorSceneManager.SaveScene(buildScene);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException($"Windows build failed: {report.summary.result}");

            Debug.Log($"Windows build ready: {Path.GetFullPath(OutputPath)} ({report.summary.totalSize / (1024 * 1024)} MB)");
        }
    }
}

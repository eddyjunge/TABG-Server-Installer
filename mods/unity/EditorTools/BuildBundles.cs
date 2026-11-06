// Builds the asset bundles that client mods load (shaders, later models). Run headless by
// scripts/build-shaders.sh:
//   Unity -batchmode -quit -projectPath mods/unity -executeMethod TabgMods.BuildBundles.Build
//         -buildTarget Win64 -tabgOut <dir>
// Every folder directly under Assets/Bundles/ becomes one bundle named after the folder.
// TABG is a Windows game (also under Proton), so bundles are built for StandaloneWindows64,
// i.e. Direct3D 11 shaders.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TabgMods
{
    public static class BuildBundles
    {
        public static void Build()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-tabgOut");
            string outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "../../target/bundles";
            Directory.CreateDirectory(outDir);

            var builds = Directory.GetDirectories("Assets/Bundles")
                .Select(dir => new AssetBundleBuild
                {
                    assetBundleName = Path.GetFileName(dir).ToLowerInvariant(),
                    assetNames = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".meta"))
                        .Select(f => f.Replace('\\', '/'))
                        .ToArray(),
                })
                .Where(b => b.assetNames.Length > 0)
                .ToArray();
            if (builds.Length == 0) throw new Exception("nothing under Assets/Bundles/");

            var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
                BuildAssetBundleOptions.ChunkBasedCompression
                | BuildAssetBundleOptions.DeterministicAssetBundle
                | BuildAssetBundleOptions.StrictMode,
                BuildTarget.StandaloneWindows64);
            if (manifest == null) throw new Exception("asset bundle build failed");
            foreach (var b in builds)
                Debug.Log($"[tabg] built bundle {b.assetBundleName}: {string.Join(", ", b.assetNames)}");
        }

        // Diagnostics: the same bundles with different build options, into <out>/<variant>/.
        public static void BuildVariants()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-tabgOut");
            string outDir = args[i + 1];
            var builds = Directory.GetDirectories("Assets/Bundles").Select(dir => new AssetBundleBuild
            {
                assetBundleName = Path.GetFileName(dir).ToLowerInvariant(),
                assetNames = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")).Select(f => f.Replace('\\', '/')).ToArray(),
            }).ToArray();
            var variants = new (string, BuildAssetBundleOptions)[]
            {
                ("chunk-strict", BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle | BuildAssetBundleOptions.StrictMode),
                ("chunk", BuildAssetBundleOptions.ChunkBasedCompression),
                ("uncompressed", BuildAssetBundleOptions.UncompressedAssetBundle),
                ("lzma", BuildAssetBundleOptions.None),
            };
            foreach (var (name, opts) in variants)
            {
                string d = Path.Combine(outDir, name);
                Directory.CreateDirectory(d);
                var m = BuildPipeline.BuildAssetBundles(d, builds, opts | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
                Debug.Log($"[tabg] variant {name}: {(m == null ? "FAILED" : "ok")}");
            }
        }
    }
}

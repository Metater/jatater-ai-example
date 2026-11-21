using System.IO;
using UnityEditor.Build;
using UnityEditor.PackageManager;

namespace JataterAI.Editor
{
    public sealed class BuildNotices : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            const string notices = "Assets/JataterAI/Notices";
            if (Directory.Exists(notices))
                foreach (string file in Directory.GetFiles(notices, "*.md"))
                    context.AddAdditionalPathToStreamingAssets(file, "JataterAI-Notices/" + Path.GetFileName(file));
            foreach (var package in PackageInfo.GetAllRegisteredPackages())
            {
                if (package.name != "ai.jatater.pocket" && package.name != "com.rlabrecque.steamworks.net" &&
                    package.name != "com.github.asus4.onnxruntime" && package.name != "com.whisper.unity") continue;
                foreach (string file in Directory.GetFiles(package.resolvedPath))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".meta") || !(name.StartsWith("LICENSE", System.StringComparison.OrdinalIgnoreCase) || name == "ThirdPartyNotices.txt")) continue;
                    context.AddAdditionalPathToStreamingAssets(file, "JataterAI-Notices/" + package.name + "/" + name);
                }
            }
        }
    }
}

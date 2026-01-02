using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Solana.Unity.SDK.Editor
{
    public class AndroidGradleAutoConfig : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;
        private const string GradleTemplatePath = "Assets/Plugins/Android/mainTemplate.gradle";

        //Run on Editor Load to warn the user immediately if setup is missing
        [InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            EditorApplication.delayCall += CheckConfiguration;
        }

        //Menu Item for manual execution
        [MenuItem("Solana/Fix Android Dependencies")]
        public static void RunManualCheck()
        {
            CheckConfiguration();
        }

        //Run right before the build starts to ensure we don't fail midway
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            CheckConfiguration();
        }

        private static void CheckConfiguration()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return;

            //Check if the user has enabled the Custom Gradle Template
            if (!File.Exists(GradleTemplatePath))
            {
                //Inform the user to enable the Custom Gradle Template
                Debug.LogError("[Solana SDK] Android Build Setup Required!\n" +
                               "1. Go to: Edit > Project Settings > Player > Android (Robot Icon) > Publishing Settings\n" +
                               "2. Check the box: 'Custom Main Gradle Template'\n" +
                               "3. Then try Building again or click 'Solana > Fix Android Dependencies'.");
                return;
            }

            //If the file exists, ensure dependencies are correct
            PatchGradleFile();
        }

        private static void PatchGradleFile()
        {
            try
            {
                string content = File.ReadAllText(GradleTemplatePath);
                bool modified = false;

                //Add Missing Dependencies
                if (!content.Contains("androidx.browser:browser") || !content.Contains("listenablefuture"))
                {
                    string newDeps = @"
    // Solana SDK Dependencies
    implementation 'androidx.browser:browser:1.5.0'
    implementation 'androidx.versionedparcelable:versionedparcelable:1.1.1'
    implementation 'com.google.guava:guava:31.1-android'
    implementation 'com.google.guava:listenablefuture:9999.0-empty-to-avoid-conflict-with-guava'
";
                    var regex = new Regex(@"dependencies\s*\{");
                    if (regex.IsMatch(content))
                    {
                        content = regex.Replace(content, "dependencies {\n" + newDeps, 1);
                        modified = true;
                    }
                }

                //Add Conflict Resolution (Duplicate Class errors)
                if (!content.Contains("exclude group: 'com.google.guava'"))
                {
                    string resolutionBlock = @"
configurations.all {
    resolutionStrategy {
        exclude group: 'com.google.guava', module: 'listenablefuture'
        force 'androidx.core:core:1.9.0'
    }
}
";
                    //Append to the end of the file
                    content += "\n" + resolutionBlock;
                    modified = true;
                }

                if (modified)
                {
                    File.WriteAllText(GradleTemplatePath, content);
                    AssetDatabase.Refresh();
                    Debug.Log("[Solana SDK] Successfully patched 'mainTemplate.gradle' with dependency fixes.");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Solana SDK] Failed to patch mainTemplate.gradle: {e.Message}");
            }
        }
    }
}
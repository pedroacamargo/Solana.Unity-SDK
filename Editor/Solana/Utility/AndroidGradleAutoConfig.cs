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
        private const string DependencyMarker = "// [Solana.Unity-SDK] Dependencies";
        private const string ResolutionMarker = "// [Solana.Unity-SDK] Conflict Resolution";

        //Run on Editor Load to warn the user immediately if setup is missing
        [InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            EditorApplication.delayCall += () => CheckConfiguration(true);
        }

        //Menu Item for manual execution
        [MenuItem("Solana/Fix Android Dependencies")]
        public static void RunManualCheck()
        {
            CheckConfiguration(true);
        }

        //Run right before the build starts to ensure we don't fail midway
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            CheckConfiguration(false);
        }

        private static void CheckConfiguration(bool checkActiveTarget)
        {
            if (checkActiveTarget && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return;

            //Check if the user has enabled the Custom Gradle Template
            if (!File.Exists(GradleTemplatePath))
            {
                Debug.LogError("[Solana SDK] Android Build Setup Required!\n" +
                               "1. Go to: Edit -> Project Settings -> Player -> Android -> Publishing Settings\n" +
                               "2. Check the box: 'Custom Main Gradle Template'\n" +
                               "3. Then try Building again or click 'Solana -> Fix Android Dependencies'.");
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

                //--- ADAPTIVE VERSIONING ---
                //Unity 6+ gets modern stable versions (fixes Duplicate Class errors)
                //Unity 2022/2021 gets legacy versions (matches original SDK AARs, avoids Kotlin conflicts)
#if UNITY_6000_0_OR_NEWER
                string browserVersion = "1.9.0";
                string parcelableVersion = "1.2.1";
                string guavaVersion = "33.5.0-android";
                string coreVersion = "1.17.0";
#else
                //Legacy versions for Unity 2022/2021 to ensure stability with older Gradle/Kotlin plugins
                string browserVersion = "1.5.0";
                string parcelableVersion = "1.1.1";
                string guavaVersion = "31.1-android";
                string coreVersion = "1.8.0";
#endif

                //Dependency Injection
                if (!content.Contains(DependencyMarker))
                {
                    //Create a backup before modifying
                    CreateBackup();

                    string newDeps = $@"
    {DependencyMarker}
    implementation 'androidx.browser:browser:{browserVersion}'
    implementation 'androidx.versionedparcelable:versionedparcelable:{parcelableVersion}'
    implementation 'com.google.guava:guava:{guavaVersion}'
    implementation 'com.google.guava:listenablefuture:9999.0-empty-to-avoid-conflict-with-guava'
";
                    var regex = new Regex(@"dependencies\s*\{");
                    if (regex.IsMatch(content))
                    {
                        content = regex.Replace(content, "dependencies {\n" + newDeps, 1);
                        modified = true;
                    }
                    else
                    {
                        Debug.LogWarning("[Solana SDK] Could not find 'dependencies' block in mainTemplate.gradle. " +
                                         "Dependencies were not injected. Please add them manually.");
                    }
                }

                //Conflict Resolution (Duplicate Class errors)
                if (!content.Contains(ResolutionMarker))
                {
                    if (!modified) CreateBackup(); //Creating backup if we haven't yet

                    string resolutionBlock = $@"

{ResolutionMarker}
configurations.all {{
    resolutionStrategy {{
        exclude group: 'com.google.guava', module: 'listenablefuture'
        force 'androidx.core:core:{coreVersion}'
    }}
}}
";
                    //Append to the end of the file
                    content = content.TrimEnd() + resolutionBlock;
                    modified = true;
                }

                if (modified)
                {
                    File.WriteAllText(GradleTemplatePath, content);
                    AssetDatabase.Refresh();
                    Debug.Log($"[Solana SDK] Successfully patched 'mainTemplate.gradle' with dependency fixes (Core v{coreVersion}).");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Solana SDK] Failed to patch mainTemplate.gradle: {e.Message}");
            }
        }

        private static void CreateBackup()
        {
            try
            {
                if (File.Exists(GradleTemplatePath))
                {
                    string backupPath = GradleTemplatePath + ".bak";
                    //Preserve original backup if it already exists
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(GradleTemplatePath, backupPath, false);
                    }
                }
            }
            catch (System.Exception e)
            {
                // Best effort backup - log warning but don't fail the build
                Debug.LogWarning($"[Solana SDK] Could not create backup of mainTemplate.gradle: {e.Message}");
            }
        }
    }
}
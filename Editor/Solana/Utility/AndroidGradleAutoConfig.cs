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
        
        //Markers to identify our injections
        private const string DependencyMarker = "// [Solana.Unity-SDK] Dependencies";
        private const string ResolutionMarker = "// [Solana.Unity-SDK] Conflict Resolution";
        private const string SessionKey = "SolanaGradleChecked";

        //Run on Editor Load to warn the user immediately if setup is missing
        [InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            //Only run this check once per Editor Session to avoid overhead on every reload.
            if (SessionState.GetBool(SessionKey, false)) return;
            
            EditorApplication.delayCall += () => 
            {
                CheckConfiguration(true);
                SessionState.SetBool(SessionKey, true);
            };
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
                
                //--- ADAPTIVE VERSIONING ---
#if UNITY_6000_0_OR_NEWER
                //Unity 6+ (Modern Stable)
                string browserVersion = "1.9.0";
                string parcelableVersion = "1.2.1";
                string guavaVersion = "33.5.0-android";
                string coreVersion = "1.17.0";
#else
                //Unity 2022/2021 (Legacy Stable to avoid Kotlin conflicts)
                string browserVersion = "1.5.0";
                string parcelableVersion = "1.1.1";
                string guavaVersion = "31.1-android";
                string coreVersion = "1.8.0";
#endif

                //Explicit androidx.core dependency
                string newDepsBlock = $@"
    {DependencyMarker}
    implementation 'androidx.browser:browser:{browserVersion}'
    implementation 'androidx.core:core:{coreVersion}'
    implementation 'androidx.versionedparcelable:versionedparcelable:{parcelableVersion}'
    implementation 'com.google.guava:guava:{guavaVersion}'
    implementation 'com.google.guava:listenablefuture:9999.0-empty-to-avoid-conflict-with-guava'
";

                string newResolutionBlock = $@"

{ResolutionMarker}
configurations.all {{
    resolutionStrategy {{
        exclude group: 'com.google.guava', module: 'listenablefuture'
        force 'androidx.core:core:{coreVersion}'
    }}
}}
";

                bool modified = false;

                //SANITIZE: Remove any existing/old Solana injections to prevent duplicates or version mismatch
                if (content.Contains(DependencyMarker))
                {
                   //We check for the explicit Core version. If it doesn't match, we are in a Dirty/Upgrade state.
                   bool hasCorrectDeps = content.Contains($"implementation 'androidx.core:core:{coreVersion}'") &&
                                         content.Contains($"implementation 'androidx.browser:browser:{browserVersion}'") &&
                                         content.Contains($"implementation 'androidx.versionedparcelable:versionedparcelable:{parcelableVersion}'") &&
                                         content.Contains($"implementation 'com.google.guava:guava:{guavaVersion}'");
                                         
                   if (!hasCorrectDeps)
                   {
                       //Verify sanitization actually works before proceeding
                       CreateBackup();
                       
                       //Regex to strip old Solana Dependency Block (matches marker --> last known lib)
                       var depsRegex = new Regex($@"\s*{Regex.Escape(DependencyMarker)}[\s\S]*?com\.google\.guava:listenablefuture[^\n]*");
                       string sanitized = depsRegex.Replace(content, "");
                       
                       if (sanitized == content)
                       {
                           Debug.LogWarning("[Solana SDK] Could not remove old dependency block (unexpected format). Please manually delete the old Solana dependencies in mainTemplate.gradle.");
                           return; //Stop here to prevent injecting duplicates
                       }
                       content = sanitized;
                       
                       if (content.Contains(ResolutionMarker))
                       {
                           int resIndex = content.LastIndexOf(ResolutionMarker);
                           if (resIndex >= 0) 
                           {
                               content = content.Substring(0, resIndex).TrimEnd();
                           }
                       }
                       
                       modified = true;
                   }
                }

                //INJECT: Now that we are clean, inject the correct blocks
                
                //Inject Dependencies
                if (!content.Contains(DependencyMarker)) 
                {
                    if (!modified) CreateBackup(); 
                    
                    var regex = new Regex(@"dependencies\s*\{");
                    if (regex.IsMatch(content))
                    {
                        content = regex.Replace(content, "dependencies {\n" + newDepsBlock, 1);
                        modified = true;
                    }
                    else
                    {
                        Debug.LogWarning("[Solana SDK] Could not find 'dependencies' block. Auto-setup skipped.");
                        return; //Stop here to avoid partial configuration
                    }
                }

                //Inject Resolution Strategy
                if (!content.Contains(ResolutionMarker))
                {
                    if (!modified) CreateBackup();
                    content = content.TrimEnd() + newResolutionBlock;
                    modified = true;
                }

                if (modified)
                {
                    File.WriteAllText(GradleTemplatePath, content);
                    AssetDatabase.Refresh();
                    Debug.Log($"[Solana SDK] Updated 'mainTemplate.gradle' dependencies (Target: Core v{coreVersion}).");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Solana SDK] Failed to patch mainTemplate.gradle: {e.Message}\n{e.StackTrace}");
            }
        }

        private static void CreateBackup()
        {
            try
            {
                if (File.Exists(GradleTemplatePath))
                {
                    string backupPath = GradleTemplatePath + ".bak";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(GradleTemplatePath, backupPath, false);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Solana SDK] Backup failed: {e.Message}");
            }
        }
    }
}
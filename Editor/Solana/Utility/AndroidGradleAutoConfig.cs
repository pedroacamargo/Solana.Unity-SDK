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
                //schedule check for next frame to avoid blocking editor initialization
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

            //Check if Custom Gradle Template is actually enabled in Project Settings
#if UNITY_2019_3_OR_NEWER
            if (!PlayerSettings.Android.useCustomMainGradleTemplate)
            {
                Debug.LogError("[Solana SDK] Android Build Setup Required!\n" +
                               "1. Go to: Edit -> Project Settings -> Player -> Android -> Publishing Settings\n" +
                               "2. Check the box: 'Custom Main Gradle Template'\n" +
                               "3. Then try Building again or click 'Solana -> Fix Android Dependencies'.");
                return;
            }
#endif

            if (!File.Exists(GradleTemplatePath))
            {
                Debug.LogError($"[Solana SDK] 'mainTemplate.gradle' not found at {GradleTemplatePath}. Please generate it first.");
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
                string browserVersion = "1.8.0";
                string parcelableVersion = "1.2.1";
                string guavaVersion = "33.5.0-android";
                string coreVersion = "1.15.0";

                //force Kotlin 1.8.22 on Unity 6 to resolve duplicate class errors.
                string kotlinResolutionBlock = @"
        force 'org.jetbrains.kotlin:kotlin-stdlib:1.8.22'
        force 'org.jetbrains.kotlin:kotlin-stdlib-jdk7:1.8.22'
        force 'org.jetbrains.kotlin:kotlin-stdlib-jdk8:1.8.22'";
#else
                //Unity 2022/2021 (Legacy Stable)
                string browserVersion = "1.5.0";
                string parcelableVersion = "1.1.1";
                string guavaVersion = "31.1-android";
                string coreVersion = "1.8.0";

                string kotlinResolutionBlock = ""; //Empty on legacy versions
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
    exclude group: 'com.google.guava', module: 'listenablefuture'
    resolutionStrategy {{
        force 'androidx.core:core:{coreVersion}'{kotlinResolutionBlock}
    }}
}}
";

                bool modified = false;

                //Sanatize and Validate
                if (content.Contains(DependencyMarker) || content.Contains(ResolutionMarker))
                {
                   //Validate Dependencies
                   bool hasCorrectDeps = Regex.IsMatch(content, $@"implementation\s+['""]androidx\.core:core:{Regex.Escape(coreVersion)}['""]") &&
                                         Regex.IsMatch(content, $@"implementation\s+['""]androidx\.browser:browser:{Regex.Escape(browserVersion)}['""]");
                   
                   //Validate Resolution Strategy (Check if it forces the correct Core version)
                   bool hasCorrectResolution = content.Contains(ResolutionMarker) && 
                                               Regex.IsMatch(content, $@"force\s+['""]androidx\.core:core:{Regex.Escape(coreVersion)}['""]");
                                         
                   //If either is wrong, we must regenerate
                   if (!hasCorrectDeps || !hasCorrectResolution)
                   {
                       if (!CreateBackup()) return;
                       
                       //Remove Old Dependencies
                       var depsRegex = new Regex($@"\s*{Regex.Escape(DependencyMarker)}(?:\s+implementation\s+['""][^'""]+['""]\s*)*");
                       string sanitized = depsRegex.Replace(content, "");
                       
                       if (sanitized == content && content.Contains(DependencyMarker))
                       {
                           Debug.LogWarning("[Solana SDK] Could not remove old dependency block. Please manually clean mainTemplate.gradle.");
                           return; 
                       }
                       content = sanitized;
                       
                       //Remove Old Resolution Block
                       var resRegex = new Regex($@"\s*{Regex.Escape(ResolutionMarker)}[\s\S]*?configurations\.all\s*\{{[\s\S]*?\}}\s*\}}");
                       content = resRegex.Replace(content, "");
                       
                       modified = true;
                   }
                }
                
                //Inject Dependencies
                if (!content.Contains(DependencyMarker)) 
                {
                    if (!modified) { if(!CreateBackup()) return; } 
                    
                    var regex = new Regex(@"(?m)^\s*dependencies\s*\{");
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
                    if (!modified)
                    {
                        if(!CreateBackup()) return;
                    } 
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

        private static bool CreateBackup()
        {
            try
            {
                if (File.Exists(GradleTemplatePath))
                {
                    //Creating backups in Library/ to avoid polluting Assets/
                    string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                    string backupDir = Path.Combine(projectRoot, "Library", "SolanaSdk", "GradleBackups");
                    Directory.CreateDirectory(backupDir);

                    //using timestamped backups to prevent overwriting previous states
                    string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string backupPath = Path.Combine(backupDir, $"mainTemplate.gradle.{timestamp}.bak");
                    
                    File.Copy(GradleTemplatePath, backupPath, false);
                    Debug.Log($"[Solana SDK] Created backup: {backupPath}");
                }
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Solana SDK] Backup failed: {e.Message}, Aborting");
                return false;
            }
        }
    }
}
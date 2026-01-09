using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
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
            SessionState.SetBool(SessionKey, true);
            
            EditorApplication.delayCall += () => 
            {
                //schedule check for next frame to avoid blocking editor initialization
                CheckConfiguration(true);
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
            if (!CheckConfiguration(false))
            {
                throw new BuildFailedException("[Solana SDK] Android Gradle configuration failed. See console for details.");
            }
        }

        private static bool CheckConfiguration(bool checkActiveTarget)
        {
            if (checkActiveTarget && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return true;

            //Check if Custom Gradle Template is actually enabled in Project Settings
            if (!File.Exists(GradleTemplatePath))
            {
                Debug.LogError($"[Solana SDK] 'mainTemplate.gradle' not found at {GradleTemplatePath}.\n" +
                               "1. Go to: Edit -> Project Settings -> Player -> Android -> Publishing Settings\n" +
                               "2. Check the box: 'Custom Main Gradle Template'\n" +
                               "3. Then try Building again or click 'Solana -> Fix Android Dependencies'.");
                return false;
            }

            //If the file exists, ensure dependencies are correct
            return PatchGradleFile();
        }

        private static bool PatchGradleFile()
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

                //Sanitize and Validate
                if (content.Contains(DependencyMarker) || content.Contains(ResolutionMarker))
                {
                   //Validate Dependencies
                   bool hasCorrectDeps = Regex.IsMatch(content, $@"implementation\s+['""]androidx\.core:core:{Regex.Escape(coreVersion)}['""]");
                   
                   //Validate Resolution Strategy (Check if it forces the correct Core version)
                   bool hasCorrectResolution = content.Contains(ResolutionMarker) && 
                                               Regex.IsMatch(content, $@"force\s+['""]androidx\.core:core:{Regex.Escape(coreVersion)}['""]");
                                         
                   //If either is wrong, we must regenerate
                   if (!hasCorrectDeps || !hasCorrectResolution)
                   {
                       if (!CreateBackup()) return false;
                       
                       //Remove Old Dependencies
                       var depsRegex = new Regex($@"\s*{Regex.Escape(DependencyMarker)}(?:\s+implementation\s+['""][^'""]+['""]\s*)*");
                       string sanitized = depsRegex.Replace(content, "");
                       content = sanitized;
                       
                       //Remove Old Resolution Block
                       content = RemoveResolutionBlock(content);
                       
                       modified = true;
                   }
                }
                
                //Inject Dependencies
                if (!content.Contains(DependencyMarker)) 
                {
                    if (!modified) { if(!CreateBackup()) return false; } 
                    
                    var regex = new Regex(@"(?m)^\s*dependencies\s*\{");
                    if (regex.IsMatch(content))
                    {
                        content = regex.Replace(content, "dependencies {\n" + newDepsBlock, 1);
                        modified = true;
                    }
                    else
                    {
                        Debug.LogWarning("[Solana SDK] Could not find 'dependencies' block in mainTemplate.gradle.");
                        return false; //Stop here to avoid partial configuration
                    }
                }

                //Inject Resolution Strategy
                if (!content.Contains(ResolutionMarker))
                {
                    if (!modified)
                    {
                        if(!CreateBackup()) return false;
                    } 
                    content = content.TrimEnd() + newResolutionBlock;
                    modified = true;
                }

                if (modified)
                {
                    //Validate syntax before writing
                    if (!ValidateBraces(content))
                    {
                        Debug.LogError("[Solana SDK] Configuration aborted: Generated gradle content has unbalanced braces. Check the template.");
                        return false;
                    }

                    //Atomic Write
                    string tempPath = GradleTemplatePath + ".tmp";
                    File.WriteAllText(tempPath, content);
                    
                    if (File.Exists(GradleTemplatePath)) File.Delete(GradleTemplatePath);
                    File.Move(tempPath, GradleTemplatePath);
                    AssetDatabase.Refresh();
                    Debug.Log($"[Solana SDK] Updated 'mainTemplate.gradle' dependencies (Target: Core v{coreVersion}).");
                }
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Solana SDK] Failed to patch mainTemplate.gradle: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        //Removes the resolution block by counting braces
        private static string RemoveResolutionBlock(string content)
        {
            int markerIndex = content.IndexOf(ResolutionMarker);
            if (markerIndex < 0) return content;

            // Find start of configurations.all block
            var configMatch = Regex.Match(content.Substring(markerIndex), @"configurations\.all\s*\{");
            if (!configMatch.Success) return content;

            int openBraceIndex = markerIndex + configMatch.Index + configMatch.Length - 1;
            int depth = 0;
            int closeBraceIndex = -1;

            // Scan forward to find matching closing brace
            for (int i = openBraceIndex; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}') depth--;

                if (depth == 0)
                {
                    closeBraceIndex = i;
                    break;
                }
            }

            if (closeBraceIndex > 0)
            {
                //Remove from marker up to closing brace
                return content.Remove(markerIndex, (closeBraceIndex - markerIndex) + 1);
            }

            return content;
        }

        //Simple check to ensure the file isn't broken
        private static bool ValidateBraces(string content)
        {
            int depth = 0;
            foreach (char c in content)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                if (depth < 0) return false;
            }
            return depth == 0;
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
                    string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    string backupPath = Path.Combine(backupDir, $"mainTemplate.gradle.{timestamp}.bak");
                    
                    File.Copy(GradleTemplatePath, backupPath, true);
                    
                    //Keep only last 10 backups
                    var oldBackups = Directory.GetFiles(backupDir, "mainTemplate.gradle.*.bak")
                                              .OrderByDescending(f => f)
                                              .Skip(10);
                    foreach (var old in oldBackups)
                    {
                        try { File.Delete(old); } catch {}
                    }
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
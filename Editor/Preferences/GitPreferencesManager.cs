// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.ContextMenus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.Preferences
{
	internal class GitPreferencesManager : Utils.EditorPersistentSingleton<GitPreferencesManager>
	{
		internal enum BoolPreference
		{
			SameAsProjectPreference = 0,
			Enabled = 4,
			Disabled = 8,
		}

		private const string LEGACY_PERSONAL_PREFERENCES_KEY = "WiseGit";
		private const string PERSONAL_PREFERENCES_PATH = "UserSettings/WiseGit.prefs";
		private const string PROJECT_PREFERENCES_PATH = "ProjectSettings/WiseGit.prefs";

		// Icons are stored in the database so we don't reload them every time.
		[SerializeField] private GUIContent[] FileStatusIcons = new GUIContent[0];
		[SerializeField] private GUIContent[] LockStatusIcons = new GUIContent[0];
		[SerializeField] private GUIContent RemoteStatusIcons = null;

		[SerializeField] private bool m_RetryTextures = false;

		[Serializable]
		internal class PersonalPreferences
		{
			public bool EnableCoreIntegration = true;		// Sync file operations with git
			public bool PopulateStatusesDatabase = true;    // For overlay icons etc.
			public bool PopulateIgnoresDatabase = true;     // For git-ignored icons etc.
			public bool ShowNormalStatusOverlayIcon = false;
			public bool ShowExcludedStatusOverlayIcon = true;

			public string GitCLIPath = string.Empty;

			// When populating the database, should it check for server changes as well (locks & modified files).
			public BoolPreference FetchRemoteChanges = BoolPreference.SameAsProjectPreference;
			public bool AutoLockOnModified = false;
			public bool WarnForPotentialConflicts = true;
			public bool AskOnMovingFolders = true;

			public int AutoRefreshDatabaseInterval = 120;    // seconds; Less than 0 will disable it.
			public ContextMenusClient ContextMenusClient = ContextMenusClient.TortoiseGit;
			public GitTraceLogs TraceLogs = GitTraceLogs.GitOperations;

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			public List<string> Exclude = new List<string>();

			public const string AutoLockOnModifiedHint = "Will automatically lock assets if possible when they become modified, instead of prompting the user.\nIf assets have newer version or are locked by someone else, prompt will still be displayed.\n\nNotification will be displayed. Check the logs to know what was locked.";

			public PersonalPreferences Clone()
			{
				var clone = (PersonalPreferences) MemberwiseClone();
				clone.Exclude = new List<string>(Exclude);
				return clone;
			}
		}

		[Serializable]
		internal class ProjectPreferences
		{
			public bool FetchRemoteChanges = true;

			// Use PlatformGitCLIPath instead as it is platform independent.
			public string GitCLIPath = string.Empty;
			public string GitCLIPathMacOS = string.Empty;

#if UNITY_EDITOR_WIN
			public string PlatformGitCLIPath => GitCLIPath;
#else
			public string PlatformGitCLIPath => GitCLIPathMacOS;
#endif
			// Enable lock prompts on asset modify.
			public bool EnableLockPrompt = false;

			[Tooltip("Automatically unlock if asset becomes unmodified (i.e. you reverted the asset).")]
			public bool AutoUnlockIfUnmodified = false;

#if UNITY_2020_2_OR_NEWER
			// Because we are rendering this list manually.
			[NonReorderable]
#endif
			// Lock prompt parameters for when asset is modified.
			public List<LockPromptParameters> LockPromptParameters = new List<LockPromptParameters>();

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			// Show these branches on top.
			public List<string> PinnedBranches = new List<string>();

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			public List<string> Exclude = new List<string>();

			public ProjectPreferences Clone()
			{
				var clone = (ProjectPreferences) MemberwiseClone();

				clone.LockPromptParameters = new List<LockPromptParameters>(LockPromptParameters);
				clone.PinnedBranches = new List<string>(PinnedBranches);
				clone.Exclude = new List<string>(Exclude);

				return clone;
			}
		}

		public PersonalPreferences PersonalPrefs;
		public ProjectPreferences ProjectPrefs;

		public bool TemporarySilenceLockPrompts = false;


		[SerializeField] private long m_ProjectPrefsLastModifiedTime = 0;

		public event Action PreferencesChanged;

		public bool NeedsToAuthenticate { get; internal set; }

		public bool FetchRemoteChanges =>
			PersonalPrefs.FetchRemoteChanges == BoolPreference.SameAsProjectPreference
			? ProjectPrefs.FetchRemoteChanges
			: PersonalPrefs.FetchRemoteChanges == BoolPreference.Enabled;


		public override void Initialize(bool freshlyCreated)
		{
			var lastModifiedDate = File.Exists(PROJECT_PREFERENCES_PATH)
				? File.GetLastWriteTime(PROJECT_PREFERENCES_PATH).Ticks
				: 0
				;

			if (freshlyCreated || m_ProjectPrefsLastModifiedTime != lastModifiedDate) {
				try {
					LoadPreferences();

				} catch(Exception ex) {
					Debug.LogException(ex);
					PersonalPrefs = new PersonalPreferences();
					ProjectPrefs = new ProjectPreferences();
				}
			}

			if (freshlyCreated || m_RetryTextures) {

				LoadTextures();

				m_RetryTextures = false;

				// If WiseGit was just added to the project, Unity won't manage to load the textures the first time. Try again next frame.
				if (FileStatusIcons[(int)VCFileStatus.Modified].image == null) {

					// We're using a flag as assembly reload may happen and update callback will be lost.
					m_RetryTextures = true;

					EditorApplication.CallbackFunction reloadTextures = null;
					reloadTextures = () => {
						LoadTextures();
						m_RetryTextures = false;
						EditorApplication.update -= reloadTextures;

						if (FileStatusIcons[(int)VCFileStatus.Modified].image == null) {
							Debug.LogWarning("Git overlay icons are missing.");
						}
					};

					EditorApplication.update += reloadTextures;
				}

				Debug.Log($"Loaded WiseGit Preferences. WiseGit is turned {(PersonalPrefs.EnableCoreIntegration ? "on" : "off")}.");

				if (PersonalPrefs.EnableCoreIntegration) {
					CheckGitSupport();
				}
			}

			GitContextMenusManager.SetupContextType(PersonalPrefs.ContextMenusClient);
		}

		public GUIContent GetFileStatusIconContent(VCFileStatus status)
		{
			// TODO: this is a legacy hack-fix. The enum status got new values and needs to be refreshed on old running clients. Remove someday.
			var index = (int)status;
			if (index >= FileStatusIcons.Length) {
				LoadTextures();
			}

			return FileStatusIcons[(int)status];
		}


		public GUIContent GetLockStatusIconContent(VCLockStatus status)
		{
			return LockStatusIcons[(int)status];
		}

		public GUIContent GetRemoteStatusIconContent(VCRemoteFileStatus status)
		{
			return status == VCRemoteFileStatus.Modified ? RemoteStatusIcons : null;
		}

		private void LoadPreferences()
		{
			if (File.Exists(PERSONAL_PREFERENCES_PATH)) {
				PersonalPrefs = JsonUtility.FromJson<PersonalPreferences>(File.ReadAllText(PERSONAL_PREFERENCES_PATH));
			} else if (EditorPrefs.HasKey(LEGACY_PERSONAL_PREFERENCES_KEY)) {
				PersonalPrefs = JsonUtility.FromJson<PersonalPreferences>(EditorPrefs.GetString(LEGACY_PERSONAL_PREFERENCES_KEY, string.Empty));
			} else {
				PersonalPrefs = new PersonalPreferences();

#if UNITY_EDITOR_WIN
				PersonalPrefs.ContextMenusClient = ContextMenusClient.TortoiseGit;
#elif UNITY_EDITOR_OSX
				PersonalPrefs.ContextMenusClient = ContextMenusClient.SnailGit;
#else
				PersonalPrefs.ContextMenusClient = ContextMenusClient.CLI;
#endif
			}

			if (File.Exists(PROJECT_PREFERENCES_PATH)) {
				ProjectPrefs = JsonUtility.FromJson<ProjectPreferences>(File.ReadAllText(PROJECT_PREFERENCES_PATH));
				m_ProjectPrefsLastModifiedTime = File.GetLastWriteTime(PROJECT_PREFERENCES_PATH).Ticks;
			} else {
				ProjectPrefs = new ProjectPreferences();
				m_ProjectPrefsLastModifiedTime = 0;
			}
		}

		private void LoadTextures()
		{
			FileStatusIcons = new GUIContent[Enum.GetValues(typeof(VCFileStatus)).Length];
			FileStatusIcons[(int)VCFileStatus.Normal] = LoadTexture("Editor/GitOverlayIcons/Git_Normal_Icon");
			FileStatusIcons[(int)VCFileStatus.Added] = LoadTexture("Editor/GitOverlayIcons/Git_Added_Icon");
			FileStatusIcons[(int)VCFileStatus.Modified] = LoadTexture("Editor/GitOverlayIcons/Git_Modified_Icon");
			FileStatusIcons[(int)VCFileStatus.Replaced] = LoadTexture("Editor/GitOverlayIcons/Git_Modified_Icon");
			FileStatusIcons[(int)VCFileStatus.Deleted] = LoadTexture("Editor/GitOverlayIcons/Git_Deleted_Icon");
			FileStatusIcons[(int)VCFileStatus.Conflicted] = LoadTexture("Editor/GitOverlayIcons/Git_Conflict_Icon");
			FileStatusIcons[(int)VCFileStatus.Ignored] = LoadTexture("Editor/GitOverlayIcons/Git_Ignored_Icon", "This item is in a git-ignore list. It is not tracked by git.");
			FileStatusIcons[(int)VCFileStatus.Unversioned] = LoadTexture("Editor/GitOverlayIcons/Git_Unversioned_Icon");
			FileStatusIcons[(int)VCFileStatus.Excluded] = LoadTexture("Editor/GitOverlayIcons/Git_ReadOnly_Icon", "This item is excluded from monitoring by WiseGit, but it may still be tracked by git. Check the WiseGit preferences - Excludes setting.");

			LockStatusIcons = new GUIContent[Enum.GetValues(typeof(VCLockStatus)).Length];
			LockStatusIcons[(int)VCLockStatus.LockedHere] = LoadTexture("Editor/GitOverlayIcons/Locks/Git_LockedHere_Icon", "You have locked this file.\nClick for more details.");
			LockStatusIcons[(int)VCLockStatus.BrokenLock] = LoadTexture("Editor/GitOverlayIcons/Locks/Git_LockedOther_Icon", "You have a lock that is no longer valid (someone else stole it and released it).\nClick for more details.");
			LockStatusIcons[(int)VCLockStatus.LockedOther] = LoadTexture("Editor/GitOverlayIcons/Locks/Git_LockedOther_Icon", "Someone else locked this file.\nClick for more details.");
			LockStatusIcons[(int)VCLockStatus.LockedButStolen] = LoadTexture("Editor/GitOverlayIcons/Locks/Git_LockedOther_Icon", "Your lock was stolen by someone else.\nClick for more details.");

			RemoteStatusIcons = LoadTexture("Editor/GitOverlayIcons/Others/Git_RemoteChanges_Icon", "Asset is out of date. Update to avoid conflicts.");
		}

		public static GUIContent LoadTexture(string path, string tooltip = null)
		{
			return new GUIContent(Resources.Load<Texture2D>(path), tooltip);

			//var texture = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(path))
			//	.Select(AssetDatabase.GUIDToAssetPath)
			//	.Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
			//	.FirstOrDefault()
			//	;
			//
			//return new GUIContent(texture, tooltip);
		}


		public void SavePreferences(PersonalPreferences personalPrefs, ProjectPreferences projectPrefs)
		{
			PersonalPrefs = personalPrefs.Clone();
			ProjectPrefs = projectPrefs.Clone();

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(PERSONAL_PREFERENCES_PATH));
				File.WriteAllText(PERSONAL_PREFERENCES_PATH, JsonUtility.ToJson(PersonalPrefs, true));
			}
			catch (Exception ex) {
				Debug.LogException(ex);
			}

			try {
				File.WriteAllText(PROJECT_PREFERENCES_PATH, JsonUtility.ToJson(ProjectPrefs, true));
			}
			catch (Exception ex) {
				Debug.LogException(ex);
				EditorUtility.DisplayDialog("Error", $"Failed to write file:\n\"{PROJECT_PREFERENCES_PATH}\"\n\nData not saved! Check the logs for more info.", "Ok");
			}

			GitContextMenusManager.SetupContextType(PersonalPrefs.ContextMenusClient);

			PreferencesChanged?.Invoke();
		}

		// NOTE: Copy pasted from SearchAssetsFilter.
		public static bool ShouldExclude(IEnumerable<string> excludes, string path)
		{
			foreach(var exclude in excludes) {

				bool isExcludePath = exclude.Contains("/");    // Check if this is a path or just a filename

				if (isExcludePath) {
					if (path.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
						return true;

				} else {

					var filename = Path.GetFileName(path);
					if (filename.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) != -1)
						return true;
				}
			}

			return false;
		}

		public static string SanitizeUnityPath(string path)
		{
			return path
				.Trim()
				.TrimEnd('\\', '/')
				.Replace('\\', '/')
				;
		}

		public void CheckGitSupport()
		{
			string gitError;
			try {
				gitError = WiseGitIntegration.CheckForGitErrors();

			}
			catch (Exception ex) {
				gitError = ex.ToString();
			}

			if (string.IsNullOrEmpty(gitError)) {
				if (FetchRemoteChanges || ProjectPrefs.EnableLockPrompt) {
					WiseGitIntegration.CheckForGitAuthErrors().Completed += CheckForGitAuthErrorsResponse;
				}
				return;
			}

			PersonalPrefs.EnableCoreIntegration = false;

			// NOTE: check for Git binaries first, as it tries to recover and may get other errors!

			// System.ComponentModel.Win32Exception (0x80004005): ApplicationName='...', CommandLine='...', Native error= The system cannot find the file specified.
			// Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
			// This is allowed only if there isn't ProjectPreference specified CLI path.
			if (gitError.Contains("0x80004005") || gitError.Contains("IOException")) {

#if UNITY_EDITOR_OSX
				// For some reason OSX doesn't have the git binaries set to the PATH environment by default.
				// If that is the case and we find them at the usual place, just set it as a personal preference.
				if (string.IsNullOrWhiteSpace(PersonalPrefs.GitCLIPath)) {

					// Just shooting in the dark where git could be installed.
					string[] osxDefaultBinariesPaths = new string[] {
						"/usr/local/bin/git",
						"/usr/bin/git",
						"/Applications/Xcode.app/Contents/Developer/usr/bin/git",
						"/opt/subversion/bin/git",
						"/opt/local/bin/git",

						//
						// SnailGit comes with bundled up git binaries. Use those if needed, starting with the higher version. Premium and free.
						//

						// Arm64
						"/Applications/SnailGit.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/arm64/1.14.x/git",
						"/Applications/SnailGitLite.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/arm64/1.14.x/git",

						// Intel x64
						"/Applications/SnailGit.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.14.x/git",
						"/Applications/SnailGit.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.11.x/git",
						"/Applications/SnailGit.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.10.x/git",
						"/Applications/SnailGit.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.9.x/git",


						"/Applications/SnailGitLite.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.14.x/git",
						"/Applications/SnailGitLite.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.11.x/git",
						"/Applications/SnailGitLite.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.10.x/git",
						"/Applications/SnailGitLite.app/Contents/PlugIns/SnailGitExtension.appex/Contents/XPCServices/SnailGitCache.xpc/Contents/Resources/subversion/x86_64/1.9.x/git",
					};

					foreach(string osxPath in osxDefaultBinariesPaths) {
						if (!File.Exists(osxPath))
							continue;

						PersonalPrefs.GitCLIPath = osxPath;

						try {
							string secondGitError = WiseGitIntegration.CheckForGitErrors();
							if (!string.IsNullOrEmpty(secondGitError))
								continue;

							PersonalPrefs.EnableCoreIntegration = true;	// Save this enabled!
							SavePreferences(PersonalPrefs, ProjectPrefs);
							Debug.Log($"Git binaries missing in PATH environment variable. Found them at \"{osxPath}\". Setting this as personal preference.\n\n{gitError}");
							return;

						} catch(Exception) {
						}
					}

					// Failed to find binaries.
					PersonalPrefs.GitCLIPath = string.Empty;
				}
#endif

				WiseGitIntegration.LogStatusErrorHint(StatusOperationResult.ExecutableNotFound, $"\nTemporarily disabling WiseGit integration. Please fix the error and restart Unity.\n\n{gitError}");
#if UNITY_EDITOR_OSX
				Debug.LogError($"If you installed Git via Homebrew or similar, you may need to add \"/usr/local/bin\" (or wherever git binaries can be found) to your PATH environment variable and restart. Example:\nsudo launchctl config user path /usr/local/bin\nAlternatively, you may add git CLI path in your WiseGit preferences at:\n{GitPreferencesWindow.PROJECT_PREFERENCES_MENU}");
#endif
				return;
			}

			// fatal: not a git repository (or any of the parent directories): .git
			// This can be returned when project is not a valid git checkout. (Probably)
			if (gitError.Contains("fatal: not a git repository")) {
				Debug.LogError($"This project is NOT under version control (not a proper git clone). Temporarily disabling WiseGit integration.\n\n{gitError}");
				return;
			}

			// Any other error.
			if (!string.IsNullOrEmpty(gitError)) {
				Debug.LogError($"Calling git CLI (Command Line Interface) caused fatal error!\nTemporarily disabling WiseGit integration. Please fix the error and restart Unity.\n{gitError}\n\n");
			} else {
				// Recovered from error, enable back integration.
				PersonalPrefs.EnableCoreIntegration = true;
			}
		}

		private void CheckForGitAuthErrorsResponse(GitAsyncOperation<StatusOperationResult> operation)
		{
			if (operation.Result == StatusOperationResult.AuthenticationFailed) {
				NeedsToAuthenticate = true;
			}

			WiseGitIntegration.LogStatusErrorHint(operation.Result);
		}

		internal void TryToAuthenticate()
		{
			if (EditorUtility.DisplayDialog("Git Authenticate",
				"This process will open a terminal window and start a git remote server request. It will ask you to authenticate.\n\nThis is part of the git CLI process. WiseGit doesn't know or store your username and password.",
				"Proceed",
				"Cancel")) {

				WiseGitIntegration.PromptForAuth(WiseGitIntegration.ProjectRootNative);

				NeedsToAuthenticate = false;

				WiseGitIntegration.CheckForGitAuthErrors().Completed += CheckForGitAuthErrorsResponse;
			}
		}
	}
}

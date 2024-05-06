// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.LockPrompting;
using DevLocker.VersionControl.WiseGit.ContextMenus;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System.IO;

namespace DevLocker.VersionControl.WiseGit.Preferences
{
	internal class GitPreferencesWindow : EditorWindow
	{
		public enum PreferencesTab
		{
			Personal = 0,
			Project = 1,
			About = 2,
		}

		private static int m_RandomVideoIndex = -1;
		private static Dictionary<string, string> m_RandomVideos = new Dictionary<string, string>() {
			{ "Fire-Arrows!", "https://www.youtube.com/watch?v=zTd_0FRAwOQ" },
			{ "Climate Change Is An Absolute Nightmare", "https://www.youtube.com/watch?v=uqwvf6R1_QY" },
			{ "HL Cat", "https://www.youtube.com/watch?v=dGUYJn9kjJ4" },
			{ "YMS: Kimba the White Lion", "https://www.youtube.com/watch?v=G5B1mIfQuo4" },
			{ "Wildebeest", "https://www.youtube.com/watch?v=JMJXvsCLu6s" },
			{ "Carwash", "https://www.youtube.com/watch?v=-sAjrL0fJzw" },
			{ "How to be a Pirate: Captain Edition", "https://www.youtube.com/watch?v=3YFeE1eDlD0" },
			{ "A Brief Look at Texting and the Internet in Film", "https://www.youtube.com/watch?v=uFfq2zblGXw" },
			{ "The Problems with First Past the Post Voting Explained", "https://www.youtube.com/watch?v=s7tWHJfhiyo" },
			{ "C&C Stupid Zero Hour Facts! [09]: Combat Chinook.", "https://www.youtube.com/watch?v=_hlq8ZJ4tqo" },
			{ "Broken Kill Counts in Classic Serious Sam (Part 1)", "https://www.youtube.com/watch?v=BF0UFuZsHvo" },
			{ "The Patient Gamer", "https://www.youtube.com/watch?v=wiMyCzezfTg" },
			{ "Friendly Shadow | Dystopian Animated Short Film ", "https://www.youtube.com/watch?v=D0sCsXFAdjY" },
			{ "Soviet Car Gas Cap Lock Decoded ", "https://www.youtube.com/watch?v=NhVR7gOSXPo" },
			{ "Using Glitches and Tricks to Beat Half-Life 2", "https://www.youtube.com/watch?v=gm9lE97sIJo" },
			{ "OK Go - Upside Down & Inside Out", "https://www.youtube.com/watch?v=LWGJA9i18Co" },
			{ "The Pythagorean Siphon Inside Your Washing Machine", "https://www.youtube.com/watch?v=Cg8KQfaT9xY" },
		};

		public const string PROJECT_PREFERENCES_MENU = "Assets/Git/Git Preferences";
		[MenuItem(PROJECT_PREFERENCES_MENU, false, GitContextMenusManager.MenuItemPriorityStart + 150)]
		public static void ShowProjectPreferences()
		{
			ShowProjectPreferences(PreferencesTab.Personal);
		}

		public static void ShowProjectPreferences(PreferencesTab tab)
		{
			var window = GetWindow<GitPreferencesWindow>(true, "WiseGit Preferences");
			window.m_PersonalPrefs = GitPreferencesManager.Instance.PersonalPrefs.Clone();
			window.m_ProjectPrefs = GitPreferencesManager.Instance.ProjectPrefs.Clone();
			window.ShowUtility();
			window.position = new Rect(500f, 250f, 550f, 400f);
			window.minSize = new Vector2(550f, 400f);
			window.m_SelectedTab = tab;
		}

		// So SerializedObject() can work with it.
		[SerializeField] private GitPreferencesManager.PersonalPreferences m_PersonalPrefs;
		[SerializeField] private GitPreferencesManager.ProjectPreferences m_ProjectPrefs;

		private PreferencesTab m_SelectedTab = PreferencesTab.Personal;
		private static readonly string[] m_PreferencesTabsNames = Enum.GetNames(typeof(PreferencesTab));

		private Vector2 m_ProjectPreferencesScroll;
		private const string m_FetchRepositoryChangesHint = "Work online - will regularly 'git fetch' the remote changes in the background. This will not change your working tree.\nEnabling this will show locks and out of date additional icons.";

		private bool m_FoldLockPromptHint = true;

		private SerializedObject m_SerializedObject;

		private static string m_Version = "";

		private void OnEnable()
		{
			m_SerializedObject = new SerializedObject(this);
		}

		private void OnDisable()
		{
			if (m_SerializedObject != null) {
				m_SerializedObject.Dispose();
			}
		}

		private void OnGUI()
		{
			m_SerializedObject.Update();

			const float labelWidthAdd = 40;
			EditorGUIUtility.labelWidth += labelWidthAdd;

			EditorGUILayout.Space();

			EditorGUILayout.BeginHorizontal();
			{
				EditorGUILayout.LabelField("Save changes:", EditorStyles.boldLabel);

				GUILayout.FlexibleSpace();

				if (GUILayout.Button("Close", GUILayout.MaxWidth(60f))) {
					GUI.FocusControl("");
					Close();
					EditorGUIUtility.ExitGUI();
				}

				var prevColor = GUI.backgroundColor;
				GUI.backgroundColor = Color.green / 1.2f;
				if (GUILayout.Button("Save All", GUILayout.MaxWidth(150f))) {

					SanitizeBeforeSave();
					GitPreferencesManager.Instance.SavePreferences(m_PersonalPrefs, m_ProjectPrefs);

					// When turning on the integration do instant refresh.
					// Works when editor started with disabled integration. Doing it here to avoid circle dependency.
					if (m_PersonalPrefs.EnableCoreIntegration) {
						WiseGitIntegration.ClearLastDisplayedError();
						GitStatusesDatabase.Instance.InvalidateDatabase();
						GitLockPromptDatabaseStarter.TryStartIfNeeded();

						GitPreferencesManager.Instance.CheckGitSupport();
					}
				}
				GUI.backgroundColor = prevColor;
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();

			m_SelectedTab = (PreferencesTab)GUILayout.Toolbar((int)m_SelectedTab, m_PreferencesTabsNames);

			m_ProjectPreferencesScroll = EditorGUILayout.BeginScrollView(m_ProjectPreferencesScroll);

			switch (m_SelectedTab) {
				case PreferencesTab.Personal:
					DrawPersonalPreferences();
					break;
				case PreferencesTab.Project:
					DrawProjectPreferences();
					break;
				case PreferencesTab.About:
					DrawHelpAbout();
					break;
			}

			EditorGUILayout.EndScrollView();

			EditorGUILayout.Space();


			EditorGUIUtility.labelWidth -= labelWidthAdd;

			m_SerializedObject.ApplyModifiedProperties();
		}

		private void SanitizeBeforeSave()
		{
			m_PersonalPrefs.GitCLIPath = GitPreferencesManager.SanitizeUnityPath(m_PersonalPrefs.GitCLIPath);
			m_PersonalPrefs.Exclude = SanitizePathsList(m_PersonalPrefs.Exclude);

			m_ProjectPrefs.GitCLIPath = GitPreferencesManager.SanitizeUnityPath(m_ProjectPrefs.GitCLIPath);
			m_ProjectPrefs.GitCLIPathMacOS = GitPreferencesManager.SanitizeUnityPath(m_ProjectPrefs.GitCLIPathMacOS);
			m_ProjectPrefs.Exclude = SanitizePathsList(m_ProjectPrefs.Exclude);


			string userPath = m_PersonalPrefs.GitCLIPath;

			if (string.IsNullOrWhiteSpace(userPath)) {
				userPath = m_ProjectPrefs.PlatformGitCLIPath;
			}

			if (!string.IsNullOrWhiteSpace(userPath) && !File.Exists(userPath)) {
				EditorUtility.DisplayDialog("Git Binary Missing", $"Cannot find the \"git\" executable specified in the git preferences:\n\"{userPath}\"", "Ok");
			}

			if (m_ProjectPrefs.EnableLockPrompt) {

				if (m_ProjectPrefs.LockPromptParameters.Count == 0) {
					EditorUtility.DisplayDialog("Lock Prompt", "In order to use lock prompts, you must provide at least one lock prompt parameters element.\n\nLock Prompt will be disabled.", "Ok");
					m_ProjectPrefs.EnableLockPrompt = false;
				}

				m_ProjectPrefs.LockPromptParameters = m_ProjectPrefs.LockPromptParameters
					.Select(sp => sp.Sanitized())
					.ToList();

				if (m_ProjectPrefs.LockPromptParameters.Any(sp => !sp.IsValid)) {
					EditorUtility.DisplayDialog("Lock Prompt", "Some of the lock prompt parameters have invalid data. Please fix it.\n\nLock Prompt will be disabled.", "Ok");
					m_ProjectPrefs.EnableLockPrompt = false;
				}
			}
		}

		private static List<string> SanitizeStringsList(IEnumerable<string> list)
		{
			return list
				.Select(str => str.Trim())
				.Where(str => !string.IsNullOrEmpty(str))
				.ToList();
		}

		private static List<string> SanitizePathsList(IEnumerable<string> list)
		{
			return list
				.Select(GitPreferencesManager.SanitizeUnityPath)
				.Where(str => !string.IsNullOrEmpty(str))
				.ToList();
		}

		private void DrawPersonalPreferences()
		{
			EditorGUILayout.HelpBox("These are personal preferences stored in the registry.\nHint: check the the tooltips.", MessageType.Info);

			var sp = m_SerializedObject.FindProperty("m_PersonalPrefs");

			m_PersonalPrefs.EnableCoreIntegration = EditorGUILayout.Toggle("Enable Git integration", m_PersonalPrefs.EnableCoreIntegration);

			EditorGUI.BeginDisabledGroup(!m_PersonalPrefs.EnableCoreIntegration);

			m_PersonalPrefs.PopulateStatusesDatabase = EditorGUILayout.Toggle(new GUIContent("Enable overlay icons", "Enables overlay icons in the project windows.\nPopulates internal cache with statuses of changed entities.\nFile changes may trigger repopulation of the cache."), m_PersonalPrefs.PopulateStatusesDatabase);
			if (GitStatusesDatabase.Instance.DataIsIncomplete) {
				GUILayout.Label(GitOverlayIcons.GetDataIsIncompleteWarning());
			}
			EditorGUI.BeginDisabledGroup(!m_PersonalPrefs.PopulateStatusesDatabase);

			m_PersonalPrefs.PopulateIgnoresDatabase = EditorGUILayout.Toggle(new GUIContent("Scan for git-ignores", "Enables git-ignore overlay icons in the project windows. If disabled, git-ignored items will be considered as Normal status (green icon)."), m_PersonalPrefs.PopulateIgnoresDatabase);
			m_PersonalPrefs.ShowNormalStatusOverlayIcon = EditorGUILayout.Toggle(new GUIContent("Show Normal status green icon", "Normal status is versioned asset that doesn't have any changes."), m_PersonalPrefs.ShowNormalStatusOverlayIcon);
			m_PersonalPrefs.ShowExcludedStatusOverlayIcon = EditorGUILayout.Toggle(new GUIContent("Show Ignore & Excluded gray icon", "Show gray icon over the items that are git-ignored or added in the Exclude list in the Project tab of these preferences. These are non-recursive."), m_PersonalPrefs.ShowExcludedStatusOverlayIcon);
			m_PersonalPrefs.AutoRefreshDatabaseInterval = EditorGUILayout.IntField(new GUIContent("Overlay icons refresh interval", "How much seconds to wait for the next overlay icons refresh.\nNOTE: -1 will deactivate it - only file changes will trigger refresh."), m_PersonalPrefs.AutoRefreshDatabaseInterval);

			m_PersonalPrefs.FetchRemoteChanges =
				(GitPreferencesManager.BoolPreference)EditorGUILayout.EnumPopup(
					new GUIContent("Fetch remote changes", m_FetchRepositoryChangesHint + "\n\nNOTE: this will override the project preference. Coordinate this with your team.")
					, m_PersonalPrefs.FetchRemoteChanges);

			bool fetchChangesEnabled = m_PersonalPrefs.FetchRemoteChanges == GitPreferencesManager.BoolPreference.Enabled ||
										  m_PersonalPrefs.FetchRemoteChanges == GitPreferencesManager.BoolPreference.SameAsProjectPreference && m_ProjectPrefs.FetchRemoteChanges;


			Color prevColor = GUI.color;
			GUI.color = Color.red;
			if (GitPreferencesManager.Instance.NeedsToAuthenticate && GUILayout.Button("Authenticate")) {
				GitPreferencesManager.Instance.TryToAuthenticate();

				WiseGitIntegration.ClearLastDisplayedError();
				GitStatusesDatabase.Instance.InvalidateDatabase();
			}
			GUI.color = prevColor;

			EditorGUI.BeginDisabledGroup(!m_ProjectPrefs.EnableLockPrompt);
			m_PersonalPrefs.AutoLockOnModified = EditorGUILayout.Toggle(new GUIContent("Auto lock when modified", GitPreferencesManager.PersonalPreferences.AutoLockOnModifiedHint + "\n\nWorks only when lock prompts are enabled in the Project preferences tab."), m_PersonalPrefs.AutoLockOnModified);
			EditorGUI.EndDisabledGroup();

			EditorGUI.BeginDisabledGroup(!fetchChangesEnabled);
			m_PersonalPrefs.WarnForPotentialConflicts = EditorGUILayout.Toggle(new GUIContent("SceneView overlay for conflicts", "Display warning in the SceneView when the current scene or edited prefab is out of date or locked."), m_PersonalPrefs.WarnForPotentialConflicts);
			EditorGUI.EndDisabledGroup();

			EditorGUI.EndDisabledGroup();

			m_PersonalPrefs.AskOnMovingFolders = EditorGUILayout.Toggle(new GUIContent("Ask On Moving Folders", "Ask for confirmation when moving folders inside Unity."), m_PersonalPrefs.AskOnMovingFolders);

			m_PersonalPrefs.GitCLIPath = EditorGUILayout.TextField(new GUIContent("Git CLI Path", "Specify git CLI (git.exe) binary path to use or leave empty for the defaults.\n\nNOTE: this will override the project preference. Coordinate this with your team."), m_PersonalPrefs.GitCLIPath);

			m_PersonalPrefs.ContextMenusClient = (ContextMenusClient)EditorGUILayout.EnumPopup(new GUIContent("Context menus client", "Select what client should be used with the context menus."), m_PersonalPrefs.ContextMenusClient);
			if (GUI.changed) {
				var errorMsg = GitContextMenusManager.IsCurrentlySupported(m_PersonalPrefs.ContextMenusClient);
				if (!string.IsNullOrEmpty(errorMsg)) {
					EditorUtility.DisplayDialog("Context Menus Client Issue", errorMsg, "Ok");
				}
			}

			m_PersonalPrefs.TraceLogs = (GitTraceLogs)EditorGUILayout.EnumFlagsField(new GUIContent("Trace logs", "Logs for nerds and debugging."), m_PersonalPrefs.TraceLogs);

			EditorGUILayout.PropertyField(sp.FindPropertyRelative("Exclude"), new GUIContent("Exclude Paths", "Relative path (contains '/') or asset name to be ignored by the git integrations. Use with caution.\n\nExample: \"Assets/Scenes/Baked\" or \"_deprecated\""), true);

			EditorGUI.EndDisabledGroup();
		}

		private void DrawProjectPreferences()
		{
			EditorGUILayout.HelpBox("These settings will be saved in the ProjectSettings folder.\nFeel free to add them to your version control system.\nCoordinate any changes here with your team.", MessageType.Warning);

			var sp = m_SerializedObject.FindProperty("m_ProjectPrefs");

			m_ProjectPrefs.FetchRemoteChanges = EditorGUILayout.Toggle(new GUIContent("Fetch remote changes", m_FetchRepositoryChangesHint), m_ProjectPrefs.FetchRemoteChanges);

			m_ProjectPrefs.GitCLIPath = EditorGUILayout.TextField(new GUIContent("Git CLI Path", "Specify git CLI (git.exe) binary path to use or leave empty for the defaults."), m_ProjectPrefs.GitCLIPath);
			m_ProjectPrefs.GitCLIPathMacOS = EditorGUILayout.TextField(new GUIContent("Git CLI Path MacOS", "Same as above, but for MacOS."), m_ProjectPrefs.GitCLIPathMacOS);

			if (!m_PersonalPrefs.PopulateStatusesDatabase) {
				EditorGUILayout.HelpBox("Lock prompts require enabled overlay icons support from the Personal preferences!", MessageType.Warning);
			}
			EditorGUI.BeginDisabledGroup(!m_PersonalPrefs.PopulateStatusesDatabase);

			bool prevLockPrompt = m_ProjectPrefs.EnableLockPrompt;
			m_ProjectPrefs.EnableLockPrompt = EditorGUILayout.Toggle(new GUIContent("Enable Lock Prompts", "Prompt user to lock assets when it or its meta becomes modified."), m_ProjectPrefs.EnableLockPrompt);
			if (m_ProjectPrefs.EnableLockPrompt) {
				EditorGUI.indentLevel++;

				m_FoldLockPromptHint = EditorGUILayout.Foldout(m_FoldLockPromptHint, "Lock Prompt Hint:");
				var lockPromptHint = "If asset or its meta becomes modified a pop-up window will prompt the user to lock or ignore it.\n" +
								   "It shows if modified assets are locked by others or out of date, which prevents locking them.\n" +
								   "If left unlocked, the window won't prompt again for those assets.\n" +
								   "On editor startup user will be prompted again for all modified unlocked assets.\n\n" +
								   "Describe below what asset folders and asset types should be monitored for locking.\n" +
								   "To monitor the whole project, type in \"Assets\" for TargetFolder\n" +
								   "Coordinate this with your team and commit the preference.\n" +
								   "Must have at least one entry to work properly."

					;

				if (m_FoldLockPromptHint) {
					EditorGUILayout.BeginHorizontal();
					GUILayout.Space((EditorGUI.indentLevel + 1) * 16f);
					GUILayout.Label(lockPromptHint, EditorStyles.helpBox);
					EditorGUILayout.EndHorizontal();
				}

				EditorGUILayout.PropertyField(sp.FindPropertyRelative("AutoUnlockIfUnmodified"));

				if (!prevLockPrompt && m_ProjectPrefs.LockPromptParameters.Count == 0) {
					m_ProjectPrefs.LockPromptParameters.Add(new LockPromptParameters() {
						TargetFolder = "Assets",
						TargetTypes = (AssetType)~0,
						IncludeTargetMetas = true,
						Exclude = new string[0],
					});
				}

				// HACK: PropertyDrawers are not drawn in EditorWindow! Draw everything manually to have custom stuff!
				var alProperty = sp.FindPropertyRelative("LockPromptParameters").Copy();
				var alPropertyEnd = alProperty.GetEndProperty();

				var prevIndentLevel = EditorGUI.indentLevel;

				EditorGUILayout.PropertyField(alProperty, false);   // Draw the LockPromptParameters itself always.

				while (alProperty.NextVisible(alProperty.isExpanded) && !SerializedProperty.EqualContents(alProperty, alPropertyEnd)) {
					EditorGUI.indentLevel = prevIndentLevel + alProperty.depth - 1;

					var label = new GUIContent(alProperty.displayName);
					label.tooltip = GetSerializedPropertyTooltip<LockPromptParameters>(alProperty, false);

					if (alProperty.type == "Enum") {
						// HACK: If it is enum, it is probably AssetType. No real way to know (unless by field name)!
						alProperty.intValue = (int) (AssetType)EditorGUILayout.EnumFlagsField(label, (AssetType) alProperty.intValue);
					} else {
						EditorGUILayout.PropertyField(alProperty, label, false);
					}
				}
				EditorGUI.indentLevel = prevIndentLevel;

				EditorGUI.indentLevel--;
				EditorGUILayout.Space();
			}

			EditorGUI.EndDisabledGroup();

			EditorGUILayout.PropertyField(sp.FindPropertyRelative("Exclude"), new GUIContent("Exclude Paths", "Relative path (contains '/') or asset name to be ignored by the git integrations. Use with caution.\n\nExample: \"Assets/Scenes/Baked\" or \"_deprecated\""), true);
		}

		public static void DrawHelpAbout()
		{
			EditorGUILayout.LabelField("Version: " + GetVersion(), EditorStyles.boldLabel);
			EditorGUILayout.LabelField("Help:", EditorStyles.boldLabel);

			if (GUILayout.Button("Documentation", GUILayout.MaxWidth(EditorGUIUtility.labelWidth))) {
				var assets = AssetDatabase.FindAssets("WiseGit-Documentation");
				if (assets.Length == 0) {
					EditorUtility.DisplayDialog("Documentation missing!", "The documentation you requested is missing.", "Ok");
				} else {
					Application.OpenURL(Environment.CurrentDirectory + "/" + AssetDatabase.GUIDToAssetPath(assets[0]));
				}
			}

			EditorGUILayout.LabelField("About:", EditorStyles.boldLabel);
			{
				var urlStyle = new GUIStyle(EditorStyles.label);
				urlStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1.00f, 0.65f, 0.00f) : Color.blue;
				urlStyle.active.textColor = Color.red;

				const string mail = "NibbleByte3@gmail.com";

				GUILayout.Label("Created by Filip Slavov", GUILayout.ExpandWidth(false));
				if (GUILayout.Button(mail, urlStyle, GUILayout.ExpandWidth(false))) {
					Application.OpenURL("mailto:"+mail);
				}

				EditorGUILayout.LabelField("In collaboration with Snapshot Games");



				EditorGUILayout.BeginHorizontal();

				if (GUILayout.Button("Asset Store", urlStyle, GUILayout.ExpandWidth(false))) {
					var assetStoreURL = "https://assetstore.unity.com/packages/tools/version-control/TODO:SET_URL_WHEN_AVAILABLE";
					Application.OpenURL(assetStoreURL);
				}

				GUILayout.Label("|", GUILayout.ExpandWidth(false));

				if (GUILayout.Button("GitHub", urlStyle, GUILayout.ExpandWidth(false))) {
					var githubURL = "https://github.com/NibbleByte/UnityWiseGit";
					Application.OpenURL(githubURL);
				}

				GUILayout.Label("|", GUILayout.ExpandWidth(false));

				if (GUILayout.Button("Unity Forum", urlStyle, GUILayout.ExpandWidth(false))) {
					var unityForumURL = "https://forum.unity.com/threads/TODO:SET_URL_WHEN_AVAILABLE";
					Application.OpenURL(unityForumURL);
				}

				GUILayout.Label("|", GUILayout.ExpandWidth(false));

				if (GUILayout.Button("OpenUPM", urlStyle, GUILayout.ExpandWidth(false))) {
					var openUPMurl = "https://openupm.com/packages/devlocker.versioncontrol.wisegit/";
					Application.OpenURL(openUPMurl);
				}

				EditorGUILayout.EndHorizontal();

				EditorGUILayout.Space();

				if (GUILayout.Button("Icons taken from TortoiseSVN (created by Lьbbe Onken)", urlStyle, GUILayout.ExpandWidth(true))) {
					var assetStoreURL = "https://tortoisesvn.net/";
					Application.OpenURL(assetStoreURL);
				}

				GUILayout.FlexibleSpace();

				EditorGUILayout.LabelField("Random Video:", EditorStyles.boldLabel);
				GUILayout.Label("This plugin took a lot of time to make.\nHere are some random videos worth spreading that distracted me along the way. :D");

				if (m_RandomVideoIndex == -1) {
					m_RandomVideoIndex = UnityEngine.Random.Range(0, m_RandomVideos.Count);
				}

				if (GUILayout.Button(m_RandomVideos.Keys.ElementAt(m_RandomVideoIndex), urlStyle, GUILayout.ExpandWidth(false))) {
					Application.OpenURL(m_RandomVideos.Values.ElementAt(m_RandomVideoIndex));
				}
				if (GUILayout.Button("Next Video", GUILayout.ExpandWidth(false))) {
					m_RandomVideoIndex = (m_RandomVideoIndex + 1) % m_RandomVideos.Count;
				}
			}
		}

		private static string GetVersion()
		{
			if (!string.IsNullOrEmpty(m_Version))
				return m_Version;

			string pathToCode = AssetDatabase.FindAssets(nameof(WiseGitIntegration)).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
			if (!string.IsNullOrEmpty(pathToCode)) {

				string pathToPackage = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pathToCode)), "package.json");
				if (File.Exists(pathToPackage)) {

					string versionLine = File.ReadAllLines(pathToPackage).Where(l => l.Contains("\"version\": ")).FirstOrDefault();
					if (!string.IsNullOrEmpty(versionLine)) {
						m_Version = "WiseGit " + System.Text.RegularExpressions.Regex.Match(versionLine, @"\d\.\d\.\d").Value;
						return m_Version;
					}
				}
			}

			m_Version = "Unknown";
			return m_Version;
		}

		private static string GetSerializedPropertyTooltip<Type>(SerializedProperty serializedProperty, bool inherit)
		{
			if (null == serializedProperty) {
				return string.Empty;
			}

			System.Reflection.FieldInfo field = typeof(Type).GetField(serializedProperty.name);
			if (null == field) {
				return string.Empty;
			}

			TooltipAttribute[] attributes = (TooltipAttribute[]) field.GetCustomAttributes(typeof(TooltipAttribute), inherit);

			return attributes.Length > 0 ? attributes[0].tooltip : string.Empty;
		}


		#region UIElements Background HACKS!

		/*
		[MenuItem("TEMP/Generate Background Textures ")]
		private static void GenerateBackgroundTextures()
		{
			// This method was used to generate the needed images for button hover effects etc (read below).
			// Creating textures on the fly didn't work as they get destroyed on assembly reload (alternatively they could leak).

			// As 2019 & 2020 incorporates the UIElements framework, background textures are now null / empty.
			// Because this was written in the old IMGUI style using 2018, this quick and dirty hack was created.
			// Manually create background textures imitating the real buttons ones.
			System.IO.File.WriteAllBytes("Assets/Git_Button_Hover_Dark.png", MakeButtonBackgroundTexture(new Color(0.404f, 0.404f, 0.404f, 1.0f)).EncodeToPNG());
			System.IO.File.WriteAllBytes("Assets/Git_Button_Hover_Light.png", MakeButtonBackgroundTexture(new Color(0.925f, 0.925f, 0.925f, 1.0f)).EncodeToPNG());

			System.IO.File.WriteAllBytes("Assets/Git_Button_Active_Dark.png", MakeButtonBackgroundTexture(new Color(0.455f, 0.455f, 0.455f, 1.0f)).EncodeToPNG());
			System.IO.File.WriteAllBytes("Assets/Git_Button_Active_Light.png", MakeButtonBackgroundTexture(new Color(0.694f, 0.694f, 0.694f, 1.0f)).EncodeToPNG());

			System.IO.File.WriteAllBytes("Assets/Git_Border_Normal_Dark.png", MakeBoxBackgroundTexture(new Color(0.290f, 0.290f, 0.290f, 1.0f)).EncodeToPNG());
			System.IO.File.WriteAllBytes("Assets/Git_Border_Normal_Light.png", MakeBoxBackgroundTexture(new Color(0.740f, 0.740f, 0.740f, 1.0f)).EncodeToPNG());
		}
		*/

		internal static void MigrateButtonStyleToUIElementsIfNeeded(GUIStyle style)
		{
			// As 2019 & 2020 incorporates the UIElements framework, background textures are now null / empty.
			// Because this was written in the old IMGUI style using 2018, this quick and dirty hack was created.
			// Manually create background textures imitating the real buttons ones.

			style.name = "";	// UIElements matches button styles by name and overrides everything.

			if (style.hover.background == null) {
				var path = EditorGUIUtility.isProSkin ? "GitElementsUI/Git_Button_Hover_Dark" : "GitElementsUI/Git_Button_Hover_Light";
				style.hover.background = Resources.Load<Texture2D>(path);

			}

			if (style.active.background == null) {
				var path = EditorGUIUtility.isProSkin ? "GitElementsUI/Git_Button_Active_Dark" : "GitElementsUI/Git_Button_Active_Light";
				style.active.background = Resources.Load<Texture2D>(path);

			}
		}

		internal static void MigrateBorderStyleToUIElementsIfNeeded(GUIStyle style)
		{
			// As 2019 & 2020 incorporates the UIElements framework, background textures are now null / empty.
			// Because this was written in the old IMGUI style using 2018, this quick and dirty hack was created.
			// Manually create background textures imitating the real buttons ones.

			style.name = "";	// UIElements matches button styles by name and overrides everything.
			if (style.normal.background == null) {
				var path = EditorGUIUtility.isProSkin ? "GitElementsUI/Git_Border_Normal_Dark" : "GitElementsUI/Git_Border_Normal_Light";
				style.normal.background = Resources.Load<Texture2D>(path);
			}
		}

		private static Texture2D MakeButtonBackgroundTexture(Color color)
		{
			const int width = 16;
			const int height = 16;

			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

			var pixels = new Color[width * height];
			for (int y = 0; y < height; ++y) {
				for (int x = 0; x < width; ++x) {
					var index = x + y * width;
					pixels[index] = color;

					if (y == 0) {
						pixels[index] *= 0.7f;
						pixels[index].a = 1f;
					}

					if (y == height - 1) {
						pixels[index] *= 1.02f;
						pixels[index].a = 1f;
					}

					if (x == 0 || x == width - 1) {
						pixels[index] *= 0.95f;
						pixels[index].a = 1f;
					}
				}
			}

			texture.SetPixels(pixels);

			texture.SetPixel(0, 0, new Color());
			texture.SetPixel(1, 0, new Color());
			texture.SetPixel(0, 1, new Color());


			texture.SetPixel(width - 1, 0, new Color());
			texture.SetPixel(width - 2, 0, new Color());
			texture.SetPixel(width - 1, 1, new Color());

			texture.SetPixel(0, height - 1, new Color());
			texture.SetPixel(0, height - 2, new Color());
			texture.SetPixel(1, height - 1, new Color());

			texture.SetPixel(width - 1, height - 1, new Color());
			texture.SetPixel(width - 2, height - 1, new Color());
			texture.SetPixel(width - 1, height - 2, new Color());

			texture.Apply();

			return texture;
		}

		private static Texture2D MakeBoxBackgroundTexture(Color color)
		{
			const int width = 16;
			const int height = 16;

			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);

			var pixels = new Color[width * height];
			for (int y = 0; y < height; ++y) {
				for (int x = 0; x < width; ++x) {
					var index = x + y * width;
					pixels[index] = color;

					if (y == 0 || y == height - 1 || x == 0 || x == width - 1) {
						pixels[index] *= 0.5f;
						pixels[index].a = 1f;
					}
				}
			}

			texture.SetPixels(pixels);
			texture.Apply();

			return texture;
		}
		#endregion
	}
}

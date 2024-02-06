// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.ContextMenus.Implementation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace DevLocker.VersionControl.WiseGit.ContextMenus
{
	public enum ContextMenusClient
	{
		None,
		TortoiseGit,	// Good for Windows
		SnailGit,		// Good for MacOS
		RabbitVCS,		// Good for Linux
		CLI = 100,		// Good for anything
	}

	/// <summary>
	/// This class is responsible for the "Assets/Git/..." context menus that pop up git client windows.
	/// You can do this from your code as well. For the most methods you have to provide list of asset paths,
	/// should the method add meta files as well and should it wait for the git client window to close.
	/// *** It is recommended to wait for update operations to finish! Check the Update method for more info. ***
	/// </summary>
	public static class GitContextMenusManager
	{
		public const int MenuItemPriorityStart = -2000;

		private static GitContextMenusBase m_Integration;

		internal static void SetupContextType(ContextMenusClient client)
		{
			string errorMsg;
			m_Integration = TryCreateContextMenusIntegration(client, out errorMsg);

			if (!string.IsNullOrEmpty(errorMsg)) {
				UnityEngine.Debug.LogError($"WiseGit: Unsupported context menus client: {client}. Reason: {errorMsg}");
			}

			WiseGitIntegration.ShowChangesUI -= CheckChangesAll;
			WiseGitIntegration.ShowChangesUI += CheckChangesAll;
		}

		private static GitContextMenusBase TryCreateContextMenusIntegration(ContextMenusClient client, out string errorMsg)
		{
			if (client == ContextMenusClient.None) {
				errorMsg = string.Empty;
				return null;
			}

#if UNITY_EDITOR_WIN
			switch (client) {

				case ContextMenusClient.TortoiseGit:
					errorMsg = string.Empty;
					return new TortoiseGitContextMenus();

				case ContextMenusClient.SnailGit:
					errorMsg = "SnailGit is not supported on Windows.";
					return null;

				case ContextMenusClient.RabbitVCS:
					errorMsg = "RabbitVCS is not supported on Windows.";
					return null;

				case ContextMenusClient.CLI:
					errorMsg = string.Empty;
					return new CLIContextMenus();

				default:
					throw new NotImplementedException(client + " not implemented yet for this platform.");
			}

#elif UNITY_EDITOR_OSX

			switch (client)
			{

				case ContextMenusClient.TortoiseGit:
					errorMsg = "TortoiseGit is not supported on OSX";
					return null;

				case ContextMenusClient.SnailGit:
					errorMsg = string.Empty;
					return new SnailGitContextMenus();

				case ContextMenusClient.RabbitVCS:
					errorMsg = "RabbitVCS is not supported on OSX.";
					return null;

				case ContextMenusClient.CLI:
					errorMsg = string.Empty;
					return new CLIContextMenus();

				default:
					throw new NotImplementedException(client + " not implemented yet for this platform.");
			}

#else

            switch (client) {

				case ContextMenusClient.TortoiseGit:
					errorMsg = "TortoiseGit is not supported on Linux";
					return null;

				case ContextMenusClient.SnailGit:
					errorMsg = "SnailGit is not supported on Linux.";
					return null;

				case ContextMenusClient.RabbitVCS:
					errorMsg = string.Empty;
					return new RabbitGitContextMenu();

				case ContextMenusClient.CLI:
					errorMsg = string.Empty;
					return new CLIContextMenus();

				default:
					throw new NotImplementedException(client + " not implemented yet for this platform.");
			}

#endif
		}

		public static string IsCurrentlySupported(ContextMenusClient client)
		{
			string errorMsg;
			TryCreateContextMenusIntegration(client, out errorMsg);

			return errorMsg;
		}

		private static IEnumerable<string> GetRootAssetPath()
		{
			yield return ".";	// The root folder of the project (not the Assets folder).
		}

		private static IEnumerable<string> GetSelectedAssetPaths()
		{
			string[] guids = Selection.assetGUIDs;
			for (int i = 0; i < guids.Length; ++i) {
				string path = AssetDatabase.GUIDToAssetPath(guids[i]);

				if (string.IsNullOrEmpty(path))
					continue;

				// All direct folders in packages (the package folder) are returned with ToLower() by Unity.
				// If you have a custom package in development and your folder has upper case letters, they need to be restored.
				if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) {
					path = Path.GetFullPath(path)
						.Replace("\\", "/")
						.Replace(WiseGitIntegration.ProjectRootUnity + "/", "")
						;

					// If this is a normal package (not a custom one in development), returned path points to the "Library" folder.
					if (!path.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
						continue;
				}

				yield return path;
			}
		}

		[MenuItem("Assets/Git/\U0001F50D  Diff \u2044 Resolve", true, MenuItemPriorityStart)]
		public static bool DiffResolveValidate()
		{
			// Might be cool to return false if git status is normal or unversioned, but that might slow down the context menu.
			return Selection.assetGUIDs.Length == 1;
		}

		[MenuItem("Assets/Git/\U0001F50D  Diff \u2044 Resolve", false, MenuItemPriorityStart)]
		public static void DiffResolve()
		{
			CheckChangesSelected();
		}

		[MenuItem("Assets/Git/\U0001F50D  Check Changes All", false, MenuItemPriorityStart + 5)]
		public static void CheckChangesAll()
		{
			// TortoiseGit handles nested repositories gracefully. SnailGit - not so much. :(
			m_Integration?.CheckChanges(GetRootAssetPath(), false);
		}

		[MenuItem("Assets/Git/\U0001F50D  Check Changes", false, MenuItemPriorityStart + 6)]
		public static void CheckChangesSelected()
		{
			if (Selection.assetGUIDs.Length > 1) {
				m_Integration?.CheckChanges(GetSelectedAssetPaths(), true);

			} else if (Selection.assetGUIDs.Length == 1) {
				var assetPath = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0]);

				var isFolder = System.IO.Directory.Exists(assetPath);

				if (isFolder || (!DiffAsset(assetPath) && !DiffAsset(assetPath + ".meta"))) {
					m_Integration?.CheckChanges(GetSelectedAssetPaths(), true);
				}
			}
		}

		public static bool DiffAsset(string assetPath)
		{
			var statusData = WiseGitIntegration.GetStatus(assetPath);

			var isModified = statusData.Status != VCFileStatus.Normal
				&& statusData.Status != VCFileStatus.Unversioned
				&& statusData.Status != VCFileStatus.Conflicted
				;

			if (isModified) {
				m_Integration?.DiffChanges(assetPath, false);
				return true;
			}

			if (statusData.Status == VCFileStatus.Conflicted) {
				m_Integration?.Resolve(assetPath, false);
				return true;
			}

			return false;
		}

		public static void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.CheckChanges(assetPaths, includeMeta, wait);
		}



		// It is recommended to freeze Unity while updating.
		// DANGER: git updating while editor is crunching assets IS DANGEROUS! It WILL corrupt your asset guids. Use with caution!!!
		//		   This is the reason why this method freezes your editor and waits for the update to finish.
		[MenuItem("Assets/Git/\u2935  Pull All", false, MenuItemPriorityStart + 20)]
		public static void PullAll()
		{
			// It is recommended to freeze Unity while updating.
			// If Git downloads files while Unity is crunching assets, GUID database may get corrupted.
			// TortoiseGit handles nested repositories gracefully and updates them one after another. SnailGit - not so much. :(
			m_Integration?.Pull(wait: true);
		}

		// It is recommended to freeze Unity while updating.
		// DANGER: Git updating while editor is crunching assets IS DANGEROUS! It WILL corrupt your asset guids. Use with caution!!!
		public static void PullAllAndDontWaitDANGER()
		{
			m_Integration?.Pull(wait: false);
		}

		[MenuItem("Assets/Git/\u2935  Merge All", false, MenuItemPriorityStart + 22)]
		public static void MergeAll()
		{
			m_Integration?.Merge(wait: true);
		}

		[MenuItem("Assets/Git/\u2935  Fetch All", false, MenuItemPriorityStart + 24)]
		public static void FetchAll()
		{
			m_Integration?.Fetch(wait: true);
		}

		[MenuItem("Assets/Git/\u2197  Push All", false, MenuItemPriorityStart + 28)]
		public static void PushAll()
		{
			m_Integration?.Push(wait: true);
		}



		[MenuItem("Assets/Git/\u2197  Commit All", false, MenuItemPriorityStart + 42)]
		public static void CommitAll()
		{
			m_Integration?.Commit(GetRootAssetPath(), false);
		}

		[MenuItem("Assets/Git/\u2197  Commit", false, MenuItemPriorityStart + 43)]
		public static void CommitSelected()
		{
			var paths = GetSelectedAssetPaths().ToList();
			if (paths.Count == 1) {

				if (paths[0] == "Assets") {
					// Special case for the "Assets" folder as it doesn't have a meta file and that kind of breaks the TortoiseGit.
					CommitAll();
					return;
				}

				// TortoiseGit shows "(multiple targets selected)" for commit path when more than one was specified.
				// Don't specify the .meta unless really needed to.
				var statusData = WiseGitIntegration.GetStatus(paths[0] + ".meta");
				if (statusData.Status == VCFileStatus.Normal && !statusData.IsConflicted) {
					m_Integration?.Commit(paths, false);
					return;
				}
			}

			m_Integration?.Commit(paths, true);
		}

		public static void Commit(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.Commit(assetPaths, includeMeta, wait);
		}



		[MenuItem("Assets/Git/\u271A  Add", false, MenuItemPriorityStart + 46)]
		public static void AddSelected()
		{
			m_Integration?.Add(GetSelectedAssetPaths(), true);
		}

		public static void Add(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.Add(assetPaths, includeMeta, wait);
		}



		[MenuItem("Assets/Git/\u21A9  Revert All", false, MenuItemPriorityStart + 60)]
		public static void RevertAll()
		{
			// TortoiseGit handles nested repositories gracefully. SnailGit - not so much. :(
			m_Integration?.Revert(GetRootAssetPath(), false, true);
		}

		[MenuItem("Assets/Git/\u21A9  Revert", false, MenuItemPriorityStart + 61)]
		public static void RevertSelected()
		{
			var paths = GetSelectedAssetPaths().ToList();
			if (paths.Count == 1) {

				if (paths[0] == "Assets") {
					// Special case for the "Assets" folder as it doesn't have a meta file and that kind of breaks the TortoiseGit.
					RevertAll();
					return;
				}

				// TortoiseGit shows the meta file for revert even if it has no changes.
				// Don't specify the .meta unless really needed to.
				var statusData = WiseGitIntegration.GetStatus(paths[0] + ".meta");
				if (statusData.Status == VCFileStatus.Normal && !statusData.IsConflicted) {
					if (Directory.Exists(paths[0])) {
						m_Integration?.Revert(paths, false);
					} else {
						if (EditorUtility.DisplayDialog("Revert File?", $"Are you sure you want to revert this file and it's meta?\n\"{paths[0]}\"", "Yes", "No", DialogOptOutDecisionType.ForThisSession, "WiseGit.RevertConfirm")) {
							WiseGitIntegration.Revert(paths, false, true);
							AssetDatabase.Refresh();
						}
					}
					return;
				}
			}

			m_Integration?.Revert(GetSelectedAssetPaths(), true, true);
		}

		public static void Revert(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.Revert(assetPaths, includeMeta, wait);
		}



		[MenuItem("Assets/Git/\u26A0  Resolve All", false, MenuItemPriorityStart + 65)]
		private static void ResolveAllMenu()
		{
			m_Integration?.ResolveAll(false);
		}

		public static void ResolveAll(bool wait = false)
		{
			m_Integration?.ResolveAll(wait);
		}




		private static bool TryShowLockDialog(List<string> selectedPaths, Action<IEnumerable<string>, bool, bool> operationHandler, bool onlyLocked)
		{
			if (selectedPaths.Count == 0)
				return true;

			if (selectedPaths.All(p => Directory.Exists(p))) {
				operationHandler(selectedPaths, false, false);
				return true;
			}

			bool hasModifiedPaths = false;
			var modifiedPaths = new List<string>();
			foreach (var path in selectedPaths) {
				var guid = AssetDatabase.AssetPathToGUID(path);

				var countPrev = modifiedPaths.Count;
				modifiedPaths.AddRange(GitStatusesDatabase.Instance
					.GetAllKnownStatusData(guid, false, true, true)
					.Where(sd => sd.Status != VCFileStatus.Unversioned)
					.Where(sd => sd.Status != VCFileStatus.Normal || sd.LockStatus != VCLockStatus.NoLock)
					.Where(sd => !onlyLocked || (sd.LockStatus != VCLockStatus.NoLock && sd.LockStatus != VCLockStatus.LockedOther))
					.Select(sd => sd.Path)
					);


				// No change in asset or meta -> just add the asset as it was selected by the user anyway.
				if (modifiedPaths.Count == countPrev) {
					if (!onlyLocked || Directory.Exists(path)) {
						modifiedPaths.Add(path);
					}
				} else {
					hasModifiedPaths = true;
				}
			}

			if (hasModifiedPaths) {
				operationHandler(modifiedPaths, false, false);
				return true;
			}

			return false;
		}

		[MenuItem("Assets/Git/\U0001F512  Get Locks", false, MenuItemPriorityStart + 80)]
		public static void GetLocksSelected()
		{
			if (m_Integration != null) {
				if (!TryShowLockDialog(GetSelectedAssetPaths().ToList(), m_Integration.GetLocks, false)) {

					// This will include the meta which is rarely what you want.
					m_Integration.GetLocks(GetSelectedAssetPaths(), true, false);
				}
			}
		}

		public static void GetLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.GetLocks(assetPaths, includeMeta, wait);
		}


		[MenuItem("Assets/Git/\U0001F513  Release Locks", false, MenuItemPriorityStart + 85)]
		public static void ReleaseLocksSelected()
		{
			if (m_Integration != null) {
				if (!TryShowLockDialog(GetSelectedAssetPaths().ToList(), m_Integration.ReleaseLocks, true)) {
					// No locked assets, show nothing.
				}
			}
		}

		public static void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			m_Integration?.ReleaseLocks(assetPaths, includeMeta, wait);
		}



		[MenuItem("Assets/Git/\u2631  Show Log All", false, MenuItemPriorityStart + 100)]
		public static void ShowLogAll()
		{
			m_Integration?.ShowLog(GetRootAssetPath().First());
		}

		[MenuItem("Assets/Git/\u2631  Show Log", false, MenuItemPriorityStart + 101)]
		public static void ShowLogSelected()
		{
			m_Integration?.ShowLog(GetSelectedAssetPaths().FirstOrDefault());
		}

		public static void ShowLog(string assetPath, bool wait = false)
		{
			m_Integration?.ShowLog(assetPath, wait);
		}

		[MenuItem("Assets/Git/\U0001F4C1  Repo Browser", false, MenuItemPriorityStart + 104)]
		public static void RepoBrowserSelected()
		{
			m_Integration?.RepoBrowser(GetSelectedAssetPaths().FirstOrDefault(), WiseGitIntegration.GetTrackedRemoteBranch());
		}

		/// <summary>
		/// Open Repo-Browser at the remote location.
		/// </summary>
		public static void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			m_Integration?.RepoBrowser(path, remoteBranch, wait);
		}


		[MenuItem("Assets/Git/\U0001F440  Blame", false, MenuItemPriorityStart + 106)]
		public static void BlameSelected()
		{
			m_Integration?.Blame(GetSelectedAssetPaths().FirstOrDefault());
		}

		public static void Blame(string assetPath, bool wait = false)
		{
			m_Integration?.Blame(assetPath, wait);
		}

		[MenuItem("Assets/Git/\U0001F9F9  Cleanup", false, MenuItemPriorityStart + 110)]
		public static void Cleanup()
		{
			m_Integration?.Cleanup(true);
		}

		// It is recommended to freeze Unity while Cleanup is working.
		public static void CleanupAndDontWait()
		{
			m_Integration?.Cleanup(false);
		}

		/// <summary>
		/// Open Switch dialog. localPath specifies the target directory and url the URL to switch to.
		/// Most likely you want the root of the working copy (checkout), not just the Unity project. To get it use WiseGitIntegration.WorkingCopyRootPath();
		/// </summary>
		public static void Switch(bool wait = false)
		{
			m_Integration?.Switch(wait);
		}
	}
}

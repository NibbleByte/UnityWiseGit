// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.Preferences;
using DevLocker.VersionControl.WiseGit.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit
{
	// HACK: This should be internal, but due to inheritance issues it can't be.
	[Serializable]
	public class GuidStatusDatasBind
	{
		[UnityEngine.Serialization.FormerlySerializedAs("Guid")]
		public string Key;	// Guid or Path (if deleted).

		[UnityEngine.Serialization.FormerlySerializedAs("Data")]
		public GitStatusData MergedStatusData;	// Merged data

		public GitStatusData AssetStatusData;
		public GitStatusData MetaStatusData;

		public string AssetPath => MergedStatusData.Path;

		public IEnumerable<GitStatusData> GetSourceStatusDatas()
		{
			yield return AssetStatusData;
			yield return MetaStatusData;
		}
	}

	/// <summary>
	/// Caches known statuses for files and folders.
	/// Refreshes periodically or if file was modified or moved.
	/// Status extraction happens in another thread so overhead should be minimal.
	///
	/// NOTE: Keep in mind that this cache can be out of date.
	///		 If you want up to date information, use the WiseGitIntegration API for direct git queries.
	/// </summary>
	public class GitStatusesDatabase : Utils.DatabasePersistentSingleton<GitStatusesDatabase, GuidStatusDatasBind>
	{
		public const string INVALID_GUID = "00000000000000000000000000000000";
		public const string ASSETS_FOLDER_GUID = "00000000000000001000000000000000";


		// Note: not all of these are rendered. Check the Database icons.
		private readonly static Dictionary<VCFileStatus, int> m_StatusPriority = new Dictionary<VCFileStatus, int> {
			{ VCFileStatus.Conflicted, 10 },
			{ VCFileStatus.Obstructed, 10 },
			{ VCFileStatus.Modified, 8},
			{ VCFileStatus.Added, 6},
			{ VCFileStatus.Deleted, 6},
			{ VCFileStatus.Missing, 6},
			{ VCFileStatus.Replaced, 5},
			{ VCFileStatus.Ignored, 3},
			{ VCFileStatus.Unversioned, 1},
			{ VCFileStatus.External, 0},
			{ VCFileStatus.Normal, 0},
		};

		private GitPreferencesManager.PersonalPreferences m_PersonalPrefs => GitPreferencesManager.Instance.PersonalPrefs;
		private GitPreferencesManager.ProjectPreferences m_ProjectPrefs => GitPreferencesManager.Instance.ProjectPrefs;

		private volatile GitPreferencesManager.PersonalPreferences m_PersonalCachedPrefs;
		private volatile GitPreferencesManager.ProjectPreferences m_ProjectCachedPrefs;
		private volatile bool m_FetchRemoteChangesCached = false;


		/// <summary>
		/// The database update can be enabled, but the git integration to be disabled as a whole.
		/// </summary>
		public override bool IsActive => m_PersonalPrefs.PopulateStatusesDatabase && m_PersonalPrefs.EnableCoreIntegration;
#if UNITY_2018_1_OR_NEWER
		public override bool TemporaryDisabled => WiseGitIntegration.TemporaryDisabled || Application.isBatchMode || BuildPipeline.isBuildingPlayer;
#else
		public override bool TemporaryDisabled => WiseGitIntegration.TemporaryDisabled || UnityEditorInternal.InternalEditorUtility.inBatchMode || BuildPipeline.isBuildingPlayer;
#endif
		public override bool DoTraceLogs => (m_PersonalCachedPrefs.TraceLogs & GitTraceLogs.DatabaseUpdates) != 0;

		public override double RefreshInterval => m_PersonalPrefs.AutoRefreshDatabaseInterval;

		// Any assets contained in these folders are considered unversioned.
		private volatile string[] m_UnversionedFolders = new string[0];

		// NOT SUPPORTED due to git workflow.
		// Nested git repositories (that have ".git" in them). NOTE: These are not external, just check-out inside check-out.
		//public IReadOnlyCollection<string> NestedRepositories => Array.AsReadOnly(m_NestedRepositories);
		//private string[] m_NestedRepositories = new string[0];

		// Git-Ignored files and folders.
		private volatile string[] m_IgnoredEntries = new string[0];

		/// <summary>
		/// The collected statuses are not complete due to some reason (for example, they were too many).
		/// </summary>
		public bool DataIsIncomplete { get; private set; }

		/// <summary>
		/// Last error encountered. Will be set in a worker thread.
		/// </summary>
		public StatusOperationResult LastError { get; private set; }

		//
		//=============================================================================
		//
		#region Initialize

		public override void Initialize(bool freshlyCreated)
		{
			// HACK: Force WiseGit initialize first, so it doesn't happen in the thread.
			WiseGitIntegration.ProjectRootUnity.StartsWith(string.Empty);

			GitPreferencesManager.Instance.PreferencesChanged += RefreshActive;
			RefreshActive();

			// Copy on init, RefreshActive() doesn't do it anymore.
			m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			m_ProjectCachedPrefs = m_ProjectPrefs.Clone();

			base.Initialize(freshlyCreated);
		}

		protected override void RefreshActive()
		{
			base.RefreshActive();

			if (!IsActive) {
				DataIsIncomplete = false;
			}

			// Copy them so they can be safely accessed from the worker thread.
			//m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			//m_ProjectCachedPrefs = m_ProjectPrefs.Clone();
			// Bad idea - can still be changed while thread is working causing bugs.
		}

		#endregion


		//
		//=============================================================================
		//
		#region Populate Data

		private const int SanityStatusesLimit = 600;
		private const int SanityUnversionedFoldersLimit = 250;
		private const int SanityIgnoresLimit = 250;

		protected override void StartDatabaseUpdate()
		{
			// Copy them so they can be safely accessed from the worker thread.
			m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			m_ProjectCachedPrefs = m_ProjectPrefs.Clone();

			m_FetchRemoteChangesCached = GitPreferencesManager.Instance.FetchRemoteChanges && !GitPreferencesManager.Instance.NeedsToAuthenticate;

			LastError = StatusOperationResult.Success;

			base.StartDatabaseUpdate();
		}

		// Executed in a worker thread.
		protected override GuidStatusDatasBind[] GatherDataInThread()
		{
			List<GitStatusData> statuses = new List<GitStatusData>();
			List<string> unversionedFolders = new List<string>();
			List<string> nestedRepositories = new List<string>();
			List<string> ignoredEntries = new List<string>();
			GuidStatusDatasBind[] pendingData;

			var timings = new StringBuilder("GitStatusesDatabase Gathering Data Timings:\n");
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();

			using (var reporter = new WiseGitIntegration.ResultConsoleReporter(true, WiseGitIntegration.Silent, "GitStatusesDatabase Operations:")) {

				bool offline = !m_FetchRemoteChangesCached && !m_ProjectCachedPrefs.EnableLockPrompt;
				if (!offline) {
					FetchRemoteChanges(reporter);
				}

				GatherStatusDataInThreadRecursive("Assets", statuses, unversionedFolders, nestedRepositories, reporter);
#if UNITY_2018_4_OR_NEWER
				GatherStatusDataInThreadRecursive("Packages", statuses, unversionedFolders, nestedRepositories, reporter);
#endif
				var slashes = new char[] { '/', '\\' };

				// Add excluded items explicitly so their icon shows even when "Normal status green icon" is disabled.
				foreach (string excludedPath in m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude)) {
					if (excludedPath.IndexOfAny(slashes) != -1) {   // Only paths
						statuses.Add(new GitStatusData() { Path = excludedPath, Status = VCFileStatus.Excluded, LockDetails = LockDetails.Empty });
					}
				}

				timings.AppendLine($"Gather {statuses.Count} Status Data - {stopwatch.ElapsedMilliseconds / 1000f}s");
				stopwatch.Restart();


				if (m_PersonalCachedPrefs.PopulateIgnoresDatabase) {
					ignoredEntries.AddRange(WiseGitIntegration.GetIgnoredPaths("Assets", true));
#if UNITY_2018_4_OR_NEWER
					ignoredEntries.AddRange(WiseGitIntegration.GetIgnoredPaths("Packages", true));
#endif
					timings.AppendLine($"Gather {ignoredEntries.Count} ignores - {stopwatch.ElapsedMilliseconds / 1000f}s");
					stopwatch.Restart();
				}

				DataIsIncomplete = unversionedFolders.Count >= SanityUnversionedFoldersLimit || statuses.Count >= SanityStatusesLimit || ignoredEntries.Count > SanityIgnoresLimit;

				// Just in case...
				if (unversionedFolders.Count >= SanityUnversionedFoldersLimit) {
					unversionedFolders.Clear();
				}

				if (statuses.Count >= SanityStatusesLimit) {
					// If server has many remote changes, don't spam me with overlay icons.
					// Keep showing locked assets or scenes out of date.
					statuses = statuses
						.Where(s => s.Status != VCFileStatus.Normal || s.LockStatus != VCLockStatus.NoLock || s.Path.EndsWith(".unity"))
						.ToList();
				}

				if (ignoredEntries.Count >= SanityIgnoresLimit) {
					ignoredEntries.RemoveRange(SanityIgnoresLimit, ignoredEntries.Count - SanityIgnoresLimit);
				}


				// HACK: the base class works with the DataType for pending data. Guid won't be used.
				pendingData = statuses
					.Where(s => statuses.Count < SanityStatusesLimit    // Include everything when below the limit
					|| s.Status == VCFileStatus.Added
					|| s.Status == VCFileStatus.Modified
					|| s.Status == VCFileStatus.Conflicted
					|| s.LockStatus != VCLockStatus.NoLock
					|| s.Path.EndsWith(".unity")
					)
					.Select(s => new GuidStatusDatasBind() { MergedStatusData = s })
					.ToArray();

				string projectRootPath = WiseGitIntegration.ProjectRootNative + '\\';
				m_IgnoredEntries = ignoredEntries
					.Select(path => path.Replace(projectRootPath, ""))
					.Select(path => path.Replace('\\', '/'))
					.Distinct()
					.ToArray();

				m_UnversionedFolders = unversionedFolders.ToArray();
				//m_NestedRepositories = nestedRepositories.ToArray();


				if (!DoTraceLogs && LastError != StatusOperationResult.UnknownError) {
					reporter.ClearLogsAndErrorFlag();
				}
			} // Dispose reporter.

			timings.AppendLine("Gather Processing Data - " + (stopwatch.ElapsedMilliseconds / 1000f));
			stopwatch.Restart();

			if (DoTraceLogs) {
				Debug.Log(timings.ToString());
			}

			return pendingData;
		}

		private void FetchRemoteChanges(WiseGitIntegration.ResultConsoleReporter reporter)
		{
			// In git you can't just check remote changes - you need to "git fetch" the remote repository which can result in a lenghty download.

			// Alternative option would be to clone the remote repo in a temp dir without downloading the blobs, then check it's history like this:
			//   git merge-base --fork-point master origin/master	// Get the commit (SHA) where current working branch diverged from the remote one.
			//   git clone --bare --filter=blob:none --single-branch --branch master https://yourrepohere.git	// Clone without downloading blobs.
			//   git log f2f232908049336777deef13da9f7afe61691771..master --oneline --name-only  // Displays files with changes, no download.
			//   (this works with git 2.37, previous did download blobs).
			// If background fetching causes issues, implement this stragtegy.

			string remote = WiseGitIntegration.GetTrackedRemote();
			if (string.IsNullOrEmpty(remote))
				return;

			const string fetchProcessIdFile = "Temp/git_fetch_process.txt";

			// No fetch in progress - start one.
			if (!File.Exists(fetchProcessIdFile)) {
				var result = ShellUtils.ExecuteCommand(new ShellUtils.ShellArgs {
					Command = WiseGitIntegration.Git_Command,
					// "--porcelain" is too modern, may not be supported by recent git versions. We don't care about it anyway.
                    // Use -q instead, or it will spit out spam in the error stream.
					Args = $"fetch --atomic -q {remote} {WiseGitIntegration.GetWorkingBranch()}",
					WaitForOutput = true,
					WaitTimeout = WiseGitIntegration.ONLINE_COMMAND_TIMEOUT,
					SkipTimeoutError = true,
					Monitor = reporter
				});
				// Timeout may or may not have kicked in by now.
				// If it did, process will continue downloading, while we track it regularly by the id in the file (see below).

				if (result.HasErrors) {
					// error: cannot lock ref '...': is at ... but expected ...
					// This means another fetch was in progress when this one started. The fetch was done, but ours got error. Continue normally.
					if (result.Error.Contains("error: cannot lock ref")) {
						reporter.ResetErrorFlag();

					} else {
						LastError = WiseGitIntegration.ParseCommonStatusError(result.Error);
					}
				}

				if (result.Error.Contains(ShellUtils.TIME_OUT_ERROR_TOKEN) && ShellUtils.IsProcessAlive(result.ProcessId)) {
					reporter?.AppendTraceLine("Fetching remote took too long. Skipping until it finishes downloading in the background.");

					// Write process id in file to track when it finished downloading server changes.
					File.WriteAllText(fetchProcessIdFile, result.ProcessId.ToString());
				}
			}

			// Fetch in progress - check if finished and delete process id file.
			if (File.Exists(fetchProcessIdFile)) {
				if (!ShellUtils.IsProcessAlive(int.Parse(File.ReadAllText(fetchProcessIdFile)))) {
					reporter?.AppendTraceLine("Fetching remote finished. Obtaining remote changes...");
					File.Delete(fetchProcessIdFile);
				}
			}
		}

		private void GatherStatusDataInThreadRecursive(string repositoryPath, List<GitStatusData> foundStatuses, List<string> foundUnversionedFolders, List<string> nestedRepositories, IShellMonitor shellMonitor)
		{
			bool offline = !m_FetchRemoteChangesCached && !m_ProjectCachedPrefs.EnableLockPrompt;
			var excludes = m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude);

			// Will get statuses of all added / modified / deleted / conflicted / unversioned files. Only normal files won't be listed.
			var statuses = new List<GitStatusData>();
			StatusOperationResult result = WiseGitIntegration.GetStatuses(repositoryPath, offline, statuses, WiseGitIntegration.COMMAND_TIMEOUT * 2, shellMonitor);

			statuses.RemoveAll(
				s => GitPreferencesManager.ShouldExclude(excludes, s.Path) || // TODO: This will skip overlay icons for excludes by filename.
				s.Status == VCFileStatus.Missing);

			if (result != StatusOperationResult.Success) {
				LastError = result;
				return;
			}

			for (int i = 0; i < statuses.Count; ++i) {
				var statusData = statuses[i];

				// Statuses for entries under unversioned directories are not returned so we need to keep track of them.
				if (statusData.Status == VCFileStatus.Unversioned && Directory.Exists(statusData.Path)) {

					// Nested repositories return unknown status, but are hidden in the TortoiseGit commit window.
					// Add their statuses to support them. Also removing this folder data should display it as normal status.
					if (Directory.Exists($"{statusData.Path}/.git") && false) {

						nestedRepositories.Add(statusData.Path);

						// NOT SUPPORTED!!!
						//   Git requires all commands be run from inside the repository folder (working directory),
						//   which conflicts with all the shell commands written up till now (all our paths are relative to the Unity root, not the nested repos).
						//GatherStatusDataInThreadRecursive(statusData.Path, foundStatuses, foundUnversionedFolders, nestedRepositories, shellMonitor);

						// Folder meta file could also be unversioned. This will force unversioned overlay icon to show, even though the folder status is removed.
						// Remove the meta file status as well.
						var metaIndex = statuses.FindIndex(sd => sd.Status == VCFileStatus.Unversioned && sd.Path == statusData.Path + ".meta");
						if (metaIndex != -1) {

							foundStatuses.RemoveAll(sd => sd.Path == statusData.Path + ".meta");

							statuses.RemoveAt(metaIndex);

							if (metaIndex < i) {
								--i;
							}
						}

						statuses.RemoveAt(i);
						--i;
						continue;
					}

					foundUnversionedFolders.Add(statusData.Path);
				}

				foundStatuses.Add(statusData);
			}
		}

		protected override void WaitAndFinishDatabaseUpdate(GuidStatusDatasBind[] pendingData)
		{
			// Handle error here, to avoid multi-threaded issues.
			if (LastError != StatusOperationResult.Success) {

				// Always log the error - if repeated it will be skipped inside.
				WiseGitIntegration.LogStatusErrorHint(LastError);

				if (LastError == StatusOperationResult.AuthenticationFailed) {
					GitPreferencesManager.Instance.NeedsToAuthenticate = true;
				}
			}

			// Sanity check!
			if (pendingData.Length > SanityStatusesLimit) {
				// No more logging, displaying an icon.
				if (DoTraceLogs) {
					Debug.LogWarning($"GitStatusDatabase gathered {pendingData.Length} changes which is waay to much. Ignoring gathered changes to avoid slowing down the editor!");
				}

				return;
			}

			// Process the gathered statuses in the main thread, since Unity API is not thread-safe.
			foreach (var pair in pendingData) {

				// HACK: Guid is not used here.
				var statusData = pair.MergedStatusData;

				var assetPath = statusData.Path;
				bool isMeta = false;

				// Meta statuses are also considered. They are shown as the asset status.
				if (statusData.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) {
					assetPath = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
					isMeta = true;
				}

				// Conflicted is with priority.
				if (statusData.IsConflicted) {
					statusData.Status = VCFileStatus.Conflicted;
				}

				var guid = AssetDatabase.AssetPathToGUID(assetPath);
				if (string.IsNullOrEmpty(guid)) {
					// Files were added in the background without Unity noticing.
					// When the user focuses on Unity, it will refresh them as well.
					if (statusData.Status != VCFileStatus.Deleted)
						continue;

					// HACK: Deleted assets don't have guids, but we still want to keep track of them (Lock prompt for example).
					//		 As long as this is unique it will work.
					guid = assetPath;
				}

				// File was added to the repository but is missing in the working copy.
				if (statusData.RemoteStatus == VCRemoteFileStatus.Modified
					&& statusData.Status == VCFileStatus.Normal
					&& string.IsNullOrEmpty(guid)
					)
					continue;

				SetStatusData(guid, statusData, false, true, isMeta);

				AddModifiedFolders(statusData);
			}
		}

		private void AddModifiedFolders(GitStatusData statusData)
		{
			var status = statusData.Status;
			if (status == VCFileStatus.Unversioned || status == VCFileStatus.Ignored || status == VCFileStatus.Normal || status == VCFileStatus.Excluded || status == VCFileStatus.External || status == VCFileStatus.ReadOnly)
				return;

			if (statusData.IsConflicted) {
				statusData.Status = VCFileStatus.Conflicted;
			} else if (status != VCFileStatus.Modified) {
				statusData.Status = VCFileStatus.Modified;
			}

			// Folders don't have locks.
			statusData.LockStatus = VCLockStatus.NoLock;

			statusData.Path = Path.GetDirectoryName(statusData.Path);

			while (!string.IsNullOrEmpty(statusData.Path)) {
				// "Packages" folder doesn't have valid guid. "Assets" do have a special guid.
				if (statusData.Path == "Packages")
					break;

				var guid = AssetDatabase.AssetPathToGUID(statusData.Path);

				// Folder may be deleted.
				if (string.IsNullOrWhiteSpace(guid))
					return;

				// Added folders should not be shown as modified.
				if (GetKnownStatusData(guid).Status == VCFileStatus.Added)
					return;

				bool moveToNext = SetStatusData(guid, statusData, false, true, false);

				// If already exists, upper folders should be added as well.
				if (!moveToNext)
					return;

				statusData.Path = Path.GetDirectoryName(statusData.Path);
			}
		}

		#endregion


		//
		//=============================================================================
		//
		#region Invalidate Database

		internal void PostProcessAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets)
		{
			if (!IsActive)
				return;

			// Moving & deleting unversioned assets will trigger database refresh, but we can live with that. Should be a rare operation.
			if (deletedAssets.Length > 0 || movedAssets.Length > 0) {
				InvalidateDatabase();
				return;
			}

			// It will probably be faster.
			if (importedAssets.Length > 10) {
				InvalidateDatabase();
				return;
			}

			foreach (var path in importedAssets) {

				// ProjectSettings, Packages are imported too but we're not interested.
				if (!path.StartsWith("Assets", StringComparison.Ordinal))
					continue;

				var statusData = WiseGitIntegration.GetStatus(path, DoTraceLogs);
				bool isMeta = false;

				// If status is normal but asset was imported, maybe the meta changed. Use that status instead.
				if (statusData.Status == VCFileStatus.Normal && !statusData.IsConflicted) {
					statusData = WiseGitIntegration.GetStatus(path + ".meta", DoTraceLogs);
					isMeta = true;
				}

				var guid = AssetDatabase.AssetPathToGUID(path);

				// Conflicted file got reimported? Fuck this, just refresh.
				if (statusData.IsConflicted) {
					SetStatusData(guid, statusData, true, false, isMeta);
					InvalidateDatabase();
					return;
				}


				if (statusData.Status == VCFileStatus.Normal) {

					var knownStatusBind = m_Data.FirstOrDefault(b => b.Key == guid) ?? new GuidStatusDatasBind();
					var knownMergedData = knownStatusBind.MergedStatusData;

					// Check if just switched to normal from something else.
					// Normal might be present in the database if it is locked.
					if (knownMergedData.Status != VCFileStatus.None && knownMergedData.Status != VCFileStatus.Normal) {
						if (knownMergedData.LockStatus == VCLockStatus.NoLock && knownMergedData.RemoteStatus == VCRemoteFileStatus.None) {
							RemoveStatusData(guid);
						} else {
							bool knownIsMeta = knownStatusBind.AssetStatusData.Status == VCFileStatus.Normal;	// Meta, not asset.
							knownMergedData = knownIsMeta ? knownStatusBind.MetaStatusData : knownStatusBind.AssetStatusData;
							knownMergedData.Status = VCFileStatus.Normal;

							SetStatusData(guid, knownMergedData, true, false, knownIsMeta);
						}

						InvalidateDatabase();
						return;
					}

					continue;
				}

				// Files inside ignored folder are returned as Unversioned. Check if they are ignored and change the status.
				if (statusData.Status == VCFileStatus.Unversioned) {
					statusData.Status = CheckForIgnoredOrExcludedStatus(statusData.Status, path);
				}

				// Every time the user saves a file it will get reimported. If we already know it is modified, don't refresh every time.
				bool changed = SetStatusData(guid, statusData, true, false, isMeta);

				if (changed) {
					InvalidateDatabase();
					return;
				}
			}
		}

		private VCFileStatus CheckForIgnoredOrExcludedStatus(VCFileStatus originalStatus, string path)
		{
			if (GitPreferencesManager.ShouldExclude(m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude), path))
				return VCFileStatus.Excluded;

			foreach (string ignoredPath in m_IgnoredEntries) {
				if (path.StartsWith(ignoredPath, StringComparison.OrdinalIgnoreCase) || (ignoredPath.EndsWith('/') && ignoredPath == path + '/')) {
					return VCFileStatus.Ignored;
				}
			}

			return originalStatus;
		}

		#endregion


		//
		//=============================================================================
		//
		#region Manage status data

		/// <summary>
		/// Get known status for guid.
		/// Unversioned files should return unversioned status.
		/// If status is not known, the file should be versioned unmodified or still undetected.
		/// </summary>
		public GitStatusData GetKnownStatusData(string guid)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"Asking for status with empty guid");
				return new GitStatusData() { Status = VCFileStatus.None };
			}

			foreach (var bind in m_Data) {
				if (bind.Key.Equals(guid, StringComparison.Ordinal))
					return bind.MergedStatusData;
			}

			string path = null;
			if (m_UnversionedFolders.Length > 0) {
				path = path ?? AssetDatabase.GUIDToAssetPath(guid);

				foreach (string unversionedFolder in m_UnversionedFolders) {
					if (path.StartsWith(unversionedFolder, StringComparison.OrdinalIgnoreCase))
						return new GitStatusData() { Path = path, Status = VCFileStatus.Unversioned, LockDetails = LockDetails.Empty };
				}
			}

			if (m_IgnoredEntries.Length > 0) {
				path = path ?? AssetDatabase.GUIDToAssetPath(guid);

				foreach (string ignoredPath in m_IgnoredEntries) {
					if (path.StartsWith(ignoredPath, StringComparison.OrdinalIgnoreCase) || (ignoredPath.EndsWith('/') && ignoredPath == path + '/')) {
						return new GitStatusData() { Path = path, Status = VCFileStatus.Ignored, LockDetails = LockDetails.Empty };
					}
				}
			}

			return new GitStatusData() { Status = VCFileStatus.None };
		}

		public IEnumerable<GitStatusData> GetAllKnownStatusData(string guid, bool mergedData, bool assetData, bool metaData)
		{
			foreach(var pair in m_Data) {
				if (pair.Key.Equals(guid, StringComparison.Ordinal)) {
					if (mergedData && pair.MergedStatusData.IsValid) yield return pair.MergedStatusData;
					if (assetData && pair.AssetStatusData.IsValid) yield return pair.AssetStatusData;
					if (metaData && pair.MetaStatusData.IsValid) yield return pair.MetaStatusData;

					break;
				}
			}
		}

		public IEnumerable<GitStatusData> GetAllKnownStatusData(bool mergedData, bool assetData, bool metaData)
		{
			foreach(var pair in m_Data) {
				if (mergedData && pair.MergedStatusData.IsValid) yield return pair.MergedStatusData;
				if (assetData && pair.AssetStatusData.IsValid) yield return pair.AssetStatusData;
				if (metaData && pair.MetaStatusData.IsValid) yield return pair.MetaStatusData;
			}
		}

		private bool SetStatusData(string guid, GitStatusData statusData, bool skipPriorityCheck, bool compareOnlineStatuses, bool isMeta)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"Git: Trying to add empty guid for \"{statusData.Path}\" with status {statusData.Status}");
				return false;
			}

			foreach (var bind in m_Data) {
				if (bind.Key.Equals(guid, StringComparison.Ordinal)) {

					if (!isMeta && bind.AssetStatusData.EqualStatuses(statusData, !compareOnlineStatuses))
						return false;

					if (isMeta && bind.MetaStatusData.EqualStatuses(statusData, !compareOnlineStatuses))
						return false;

					if (!isMeta) {
						bind.AssetStatusData = statusData;
					} else {
						bind.MetaStatusData = statusData;
					}

					// This is needed because the status of the meta might differ. In that case take the stronger status.
					if (!skipPriorityCheck) {
						if (m_StatusPriority[bind.MergedStatusData.Status] > m_StatusPriority[statusData.Status]) {
							// Merge any other data.
							if (bind.MergedStatusData.LockStatus == VCLockStatus.NoLock) {
								bind.MergedStatusData.LockStatus = statusData.LockStatus;
								bind.MergedStatusData.LockDetails = statusData.LockDetails;
							}
							if (bind.MergedStatusData.RemoteStatus == VCRemoteFileStatus.None) {
								bind.MergedStatusData.RemoteStatus= statusData.RemoteStatus;
							}

							return false;
						}
					}

					// Merged should always display lock and remote status.
					if (statusData.LockStatus == VCLockStatus.NoLock) {
						statusData.LockStatus = bind.MergedStatusData.LockStatus;
						statusData.LockDetails = bind.MergedStatusData.LockDetails;
					}
					if (statusData.RemoteStatus == VCRemoteFileStatus.None) {
						statusData.RemoteStatus= bind.MergedStatusData.RemoteStatus;
					}

					bind.MergedStatusData = statusData;
					if (isMeta) {
						bind.MergedStatusData.Path = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
					}
					return true;
				}
			}

			m_Data.Add(new GuidStatusDatasBind() {
				Key = guid,
				MergedStatusData = statusData,

				AssetStatusData = isMeta ? new GitStatusData() : statusData,
				MetaStatusData = isMeta ? statusData : new GitStatusData(),
			});

			if (isMeta) {
				m_Data.Last().MergedStatusData.Path = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
			}

			return true;
		}


		private bool RemoveStatusData(string guid)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"Trying to remove empty guid");
			}

			for(int i = 0; i < m_Data.Count; ++i) {
				if (m_Data[i].Key.Equals(guid, StringComparison.Ordinal)) {
					m_Data.RemoveAt(i);
					return true;
				}
			}

			return false;
		}
		#endregion
	}


	internal class GitStatusesDatabaseAssetPostprocessor : AssetPostprocessor
	{
		private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			if (!WiseGitIntegration.TemporaryDisabled) {
				GitStatusesDatabase.Instance.PostProcessAssets(importedAssets, deletedAssets, movedAssets);
			}
		}
	}
}

// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit
using DevLocker.VersionControl.WiseGit.Preferences;
using DevLocker.VersionControl.WiseGit.Shell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit
{
	/// <summary>
	/// The core implementation of the git integration.
	/// Hooks up to file operations (create, move, delete) and executes corresponding git operations.
	/// Takes care of the meta files as well.
	/// Also provides API to integrate git with your tools - wraps git commands for your convenience.
	/// Git console commands: https://git-scm.com/book/en/v2/
	/// </summary>
	[InitializeOnLoad]
	public class WiseGitIntegration : AssetModificationProcessor
	{
		#region Git CLI Definitions

		private static readonly Dictionary<char, VCFileStatus> m_FileStatusMap = new Dictionary<char, VCFileStatus>
		{
			{' ', VCFileStatus.Normal},
			{'A', VCFileStatus.Added},
			{'R', VCFileStatus.Added},
			{'C', VCFileStatus.Added},	  // TODO: Copied status? Do we care?
			{'U', VCFileStatus.Conflicted}, // Updated but not merged
			//{'C', VCFileStatus.Conflicted},
			{'D', VCFileStatus.Deleted},
			//{'I', VCFileStatus.Ignored},
			{'M', VCFileStatus.Modified},
			{'T', VCFileStatus.Modified}, // TODO: Type change status? Do we care?
			//{'R', VCFileStatus.Replaced},
			{'?', VCFileStatus.Unversioned},
			//{'!', VCFileStatus.Missing},
			//{'X', VCFileStatus.External},
			//{'~', VCFileStatus.Obstructed},
		};

		#endregion

		public static readonly string ProjectRootNative;
		public static readonly string ProjectRootUnity;

		public static event Action ShowChangesUI;

		/// <summary>
		/// Is the integration enabled.
		/// If you want to temporarily disable it by code use RequestTemporaryDisable().
		/// </summary>
		public static bool Enabled => m_PersonalPrefs.EnableCoreIntegration;

		/// <summary>
		/// Temporarily disable the integration (by code).
		/// File operations like create, move and delete won't be monitored. You'll have to do the git operations yourself.
		/// </summary>
		public static bool TemporaryDisabled => m_TemporaryDisabledCount > 0;

		/// <summary>
		/// Do not show dialogs. To use call RequestSilence();
		/// </summary>
		public static bool Silent => m_SilenceCount > 0;

		/// <summary>
		/// Should the git Integration log operations.
		/// </summary>
		public static GitTraceLogs TraceLogs => m_PersonalPrefs.TraceLogs;

		private static int m_SilenceCount = 0;
		private static int m_TemporaryDisabledCount = 0;

		private static GitPreferencesManager.PersonalPreferences m_PersonalPrefs => GitPreferencesManager.Instance.PersonalPrefs;
		private static GitPreferencesManager.ProjectPreferences m_ProjectPrefs => GitPreferencesManager.Instance.ProjectPrefs;

		// DTOs used to deserialize json output from "git lfs locks --json" command.
		[Serializable]
		private struct LocksJSONEntry
		{
			public List<LockJSONEntry> ours;
			public List<LockJSONEntry> theirs;

			[Serializable]
			public struct LockJSONEntry
			{
				public int id;
				public string path;
				public OwnerJSONEntry owner;
				public string locked_at;

				[Serializable]
				public struct OwnerJSONEntry
				{
					public string name;
				}

				public LockDetails ToLockDetails() => new LockDetails {
					Owner = owner.name,
					Date = locked_at,
				};
			}

			public void ExcludeOutsidePaths(string path)
			{
				ours?.RemoveAll(entry => !entry.path.StartsWith(path, StringComparison.OrdinalIgnoreCase));
				theirs?.RemoveAll(entry => !entry.path.StartsWith(path, StringComparison.OrdinalIgnoreCase));
			}

			public void ApplyAndForgetStatus(string path, out VCLockStatus lockStatus, out LockDetails lockDetails)
			{
				lockStatus = VCLockStatus.NoLock;
				lockDetails = LockDetails.Empty;

				if (ours != null) {
					for (int i = 0; i < ours.Count; i++) {
						LockJSONEntry entry = ours[i];

						// git lfs locks doesn't respect the capital letters :(
						if (path.Equals(entry.path, StringComparison.OrdinalIgnoreCase)) {
							lockDetails = entry.ToLockDetails();
							lockStatus = VCLockStatus.LockedHere;
							ours.RemoveAt(i);
							return;
						}
					}
				}

				if (theirs != null) {
					for (int i = 0; i < theirs.Count; i++) {
						LockJSONEntry entry = theirs[i];

						// git lfs locks doesn't respect the capital letters :(
						if (path.Equals(entry.path, StringComparison.OrdinalIgnoreCase)) {
							lockDetails = entry.ToLockDetails();
							lockStatus = VCLockStatus.LockedOther;
							theirs.RemoveAt(i);
							return;
						}
					}
				}
			}
		}

		internal static string Git_Command {
			get {
				string userPath = m_PersonalPrefs.GitCLIPath;

				if (string.IsNullOrWhiteSpace(userPath)) {
					userPath = m_ProjectPrefs.PlatformGitCLIPath;
				}

				if (string.IsNullOrWhiteSpace(userPath))
					return "git";

				return userPath.StartsWith("/") || userPath.Contains(":")
					? userPath // Assume absolute path
					: Path.Combine(ProjectRootNative, userPath)
					;
			}
		}

		internal const int COMMAND_TIMEOUT = 20000;	// Milliseconds
		internal const int ONLINE_COMMAND_TIMEOUT = 45000;  // Milliseconds

		// Used to avoid spam (specially when importing the whole project and errors start popping up, interrupting the process).
		[NonSerialized]
		private static string m_LastDisplayedError = string.Empty;

		private static HashSet<string> m_PendingErrorMessages = new HashSet<string>();

		private static System.Threading.Thread m_MainThread;

		#region Logging

		// Used to track the shell commands output for errors and log them on Dispose().
		public class ResultConsoleReporter : IShellMonitor, IDisposable
		{
			public bool HasErrors => m_HasErrors;

			private readonly ConcurrentQueue<string> m_CombinedOutput = new ConcurrentQueue<string>();

			private bool m_HasErrors = false;
			private bool m_HasCommand = false;
			private bool m_LogOutput;
			private bool m_Silent;


			public ResultConsoleReporter(bool logOutput, bool silent, string initialText = "")
			{
				m_LogOutput = logOutput;
				m_Silent = silent;

				if (!string.IsNullOrEmpty(initialText)) {
					m_CombinedOutput.Enqueue(initialText);
				}
			}

			public bool AbortRequested { get; private set; }
			public event ShellRequestAbortEventHandler RequestAbort;

			public void AppendCommand(string command, string args)
			{
				m_CombinedOutput.Enqueue(command + " " + args);
				m_HasCommand = true;
			}

			public void AppendOutputLine(string line)
			{
				// Not used for now...
			}

			// Because using AppendOutputLine() will output all the git operation spam that we parse.
			public void AppendTraceLine(string line)
			{
				m_CombinedOutput.Enqueue(line);
			}

			public void AppendErrorLine(string line)
			{
				m_CombinedOutput.Enqueue(line);
				m_HasErrors = true;
			}

			public void Abort(bool kill)
			{
				AbortRequested = true;
				RequestAbort?.Invoke(kill);
			}

			public void ResetErrorFlag()
			{
				m_HasErrors = false;
			}

			public void ClearLogsAndErrorFlag()
			{
				string line;
				while (m_CombinedOutput.TryDequeue(out line)) {
				}

				//m_CombinedOutput.Clear();	// Not supported in 2018.

				ResetErrorFlag();
			}

			public void Dispose()
			{
				if (!m_CombinedOutput.IsEmpty) {
					StringBuilder output = new StringBuilder();
					string line;
					while(m_CombinedOutput.TryDequeue(out line)) {
						output.AppendLine(line);
					}

					if (m_HasErrors) {
						Debug.LogError(output);
						if (!m_Silent) {
							if (m_MainThread == System.Threading.Thread.CurrentThread) {
								DisplayError("Git error happened while processing the assets. Check the logs.");
							}
						}
					} else if (m_LogOutput && m_HasCommand) {
						Debug.Log(output);
					}

					m_HasErrors = false;
					m_HasCommand = false;
				}
			}
		}

		public static ResultConsoleReporter CreateReporter()
		{
			var logger = new ResultConsoleReporter((TraceLogs & GitTraceLogs.GitOperations) != 0, Silent, "Git Operations:");

			return logger;
		}

		internal static void ClearLastDisplayedError()
		{
			m_LastDisplayedError = string.Empty;
			GitPreferencesManager.Instance.NeedsToAuthenticate = false;
		}

		#endregion

		static WiseGitIntegration()
		{
			ProjectRootNative = Path.GetDirectoryName(Application.dataPath);
			ProjectRootUnity = ProjectRootNative.Replace('\\', '/');

			m_MainThread = System.Threading.Thread.CurrentThread;
		}

		/// <summary>
		/// Temporarily don't show any dialogs.
		/// This increments an integer, so don't forget to decrement it by calling ClearSilence().
		/// </summary>
		public static void RequestSilence()
		{
			m_SilenceCount++;
		}

		/// <summary>
		/// Allow dialogs to be shown again.
		/// </summary>
		public static void ClearSilence()
		{
			if (m_SilenceCount == 0) {
				Debug.LogError("WiseGit: trying to clear silence more times than it was requested.");
				return;
			}

			m_SilenceCount--;
		}

		/// <summary>
		/// Temporarily disable the integration (by code).
		/// File operations like create, move and delete won't be monitored. You'll have to do the git operations yourself.
		/// This increments an integer, so don't forget to decrement it by calling ClearTemporaryDisable().
		/// </summary>
		public static void RequestTemporaryDisable()
		{
			m_TemporaryDisabledCount++;
		}

		/// <summary>
		/// Allow git integration again.
		/// </summary>
		public static void ClearTemporaryDisable()
		{
			if (m_TemporaryDisabledCount == 0) {
				Debug.LogError("WiseGit: trying to clear temporary disable more times than it was requested.");
				return;
			}

			m_TemporaryDisabledCount--;
		}


		/// <summary>
		/// Get statuses of files based on the options you provide.
		/// NOTE: data is returned ONLY for files that has something to show (has changes, locks or remote changes).
		///		  Target folders will always return child files recursively.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		///
		/// https://git-scm.com/docs/git-status
		/// </summary>
		///
		/// <param name="offline">If false it will check for changes in the FETCHED remote repository and will ask the LFS for any locks. If remote was not fetched, no change would be detected (locks are still fetched every time).</param>
		/// <param name="resultEntries">List of result statuses</param>
		public static StatusOperationResult GetStatuses(string path, bool offline, List<GitStatusData> resultEntries, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			path = path.Replace('\\', '/');	// Used for locks filtering.

			ShellUtils.ShellResult result;
			LocksJSONEntry locksJSONEntry = new LocksJSONEntry() { ours = new(), theirs = new() };

			var remoteChanges = new HashSet<string>();
			if (!offline) {
				// Check what changed after our branch diverged from the remote one, only files.
				// This works only if remote branch was fetched (downloaded) locally.
				result = ShellUtils.ExecuteCommand(Git_Command, $"diff {GetWorkingBranchDivergingCommit()}..{GetTrackedRemoteBranch()} --name-only \"{path}\"", timeout, shellMonitor);

				// Skip errors checks - nothing to do about them.
				if (!result.HasErrors) {
					foreach (string changePath in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
						if (!string.IsNullOrEmpty(changePath)) {
							remoteChanges.Add(changePath);
						}
					}
				}

				// NOTES ON LOCKS:
				// - Locks doesn't distinguish between multiple clones of the same user. --verify doesn't help.
				// - Parameter --path="filepath" can be used, but it doesn't return if it is ours or not - provides username but we have to look it up. Works only for files, not folders.
				result = ShellUtils.ExecuteCommand(Git_Command, $"lfs locks --verify --json", timeout, shellMonitor);

				if (result.HasErrors) {
					return ParseCommonStatusError(result.Error);
				}

				// TODO: This crashesh the editor on exit when called form a background thread. Callstack:
				// TcpMessagingSession - receive error: operation aborted. errorcode: 995, details: The I/ O operation has been aborted because of either a thread exit or an application request.
				// TcpMessagingSession - receive error: operation aborted. errorcode: 995, details: The I/ O operation has been aborted because of either a thread exit or an application request.
				// Cleanup mono
				// (Unity) LaunchBugReporter
				// (Unity) EditorMonoConsole::LogToConsoleImplementation
				// (Unity) EditorMonoConsole::LogToConsoleImplementation
				// (Unity) DebugStringToFilePostprocessedStacktrace
				// (Unity) DebugStringToFile
				// (Unity) GetManagerFromContext
				// (Unity) scripting_class_from_fullname
				// (Unity) OptionalType
				// (Unity) InitializeCoreScriptingClasses
				// (Unity) GetCoreScriptingClasses
				// (Unity) JSONUtility::DeserializeObject
				// (Unity) FromJsonInternal
				// (Unity) JsonUtility_CUSTOM_FromJsonInternal
				// (Mono JIT Code)(wrapper managed - to - native) UnityEngine.JsonUtility:FromJsonInternal(string, object, System.Type)
				// (Mono JIT Code) UnityEngine.JsonUtility:FromJson(string, System.Type)
				// (Mono JIT Code) UnityEngine.JsonUtility:FromJson < DevLocker.VersionControl.WiseGit.WiseGitIntegration / LocksJSONEntry > (string)
				// (Mono JIT Code)[...\Editor\WiseGitIntegration.cs:387] DevLocker.VersionControl.WiseGit.WiseGitIntegration:GetStatuses(...)
				locksJSONEntry = JsonUtility.FromJson<LocksJSONEntry>(result.Output);
				locksJSONEntry.ExcludeOutsidePaths(path);
			}

			result = ShellUtils.ExecuteCommand(Git_Command, $"status --porcelain -z \"{GitFormatPath(path)}\"", timeout, shellMonitor);

			if (result.HasErrors) {
				return ParseCommonStatusError(result.Error);
			}

			bool emptyOutput = string.IsNullOrWhiteSpace(result.Output);

			// Empty result could also mean: file doesn't exist.
			// Note: git-deleted files still have git status, so always check for status before files on disk.
			if (emptyOutput) {
				if (!File.Exists(path) && !Directory.Exists(path))
					return StatusOperationResult.TargetPathNotFound;
			}

			// If no info is returned for path, the status is normal. Reflect this when searching for Empty depth.
			if (emptyOutput) {
				var ignoredPaths = GetIgnoredPaths(path, true);

				// ... it may be empty because it is ignored.
				var status = ignoredPaths.Length == 0 ? VCFileStatus.Normal : VCFileStatus.Ignored;
				var statusData = new GitStatusData() { Status = status, Path = path };
				locksJSONEntry.ApplyAndForgetStatus(path, out statusData.LockStatus, out statusData.LockDetails);

				resultEntries.Add(statusData);
				return StatusOperationResult.Success;
			}

			resultEntries.AddRange(ExtractStatuses(result.Output));

			// NOTE: if file was marked as moved or deleted, and another unstaged file is present in the original location
			//		 it will be returned as unknown status, after the moved/deleted status (i.e. 2 entries for the same file).
			// Example:
			// > git status -s
			//   R  Foo.png -> Bar2.png
			//   D  README.md
			//   ?? Foo.png
			//   ?? README.md


			for(int i = 0; i < resultEntries.Count; ++i) {
				GitStatusData statusData = resultEntries[i];

				if (remoteChanges.Contains(statusData.Path, StringComparer.OrdinalIgnoreCase)) {
					statusData.RemoteStatus = VCRemoteFileStatus.Modified;
					remoteChanges.Remove(statusData.Path);
				}

				locksJSONEntry.ApplyAndForgetStatus(statusData.Path, out statusData.LockStatus, out statusData.LockDetails);

				resultEntries[i] = statusData;
			}

			foreach(string remoteChange in remoteChanges) {
				if (IsHiddenPath(remoteChange))
					continue;

				GitStatusData statusData = new GitStatusData {
					Path = remoteChange,
					Status = VCFileStatus.Normal,
					RemoteStatus = VCRemoteFileStatus.Modified,
					LockDetails = LockDetails.Empty,
				};

				locksJSONEntry.ApplyAndForgetStatus(statusData.Path, out statusData.LockStatus, out statusData.LockDetails);

				resultEntries.Add(statusData);
			}


			foreach(var lockJSONEntry in locksJSONEntry.ours) {
				if (IsHiddenPath(lockJSONEntry.path) || !lockJSONEntry.path.StartsWith(path))
					continue;

				var statusData = new GitStatusData() {
					Path = lockJSONEntry.path,
					Status = VCFileStatus.Normal,
					LockStatus = VCLockStatus.LockedHere,
					LockDetails = lockJSONEntry.ToLockDetails()
				};

				resultEntries.Add(statusData);
			}

			foreach(var lockJSONEntry in locksJSONEntry.theirs) {
				if (IsHiddenPath(lockJSONEntry.path) || !lockJSONEntry.path.StartsWith(path))
					continue;

				var statusData = new GitStatusData() {
					Path = lockJSONEntry.path,
					Status = VCFileStatus.Normal,
					LockStatus = VCLockStatus.LockedOther,
					LockDetails = lockJSONEntry.ToLockDetails()
				};

				resultEntries.Add(statusData);
			}


			return StatusOperationResult.Success;
		}

		/// <summary>
		/// Get statuses of files based on the options you provide.
		/// NOTE: data is returned ONLY for files that has something to show (has changes, locks or remote changes).
		///		  Target folders will always return child files recursively.
		///
		/// https://git-scm.com/docs/git-status
		/// </summary>
		///
		/// <param name="offline">If false it will check for changes in the FETCHED remote repository and will ask the LFS for any locks. If remote was not fetched, no change would be detected (locks are still fetched every time).</param>
		/// <param name="resultEntries">List of result statuses</param>
		public static GitAsyncOperation<StatusOperationResult> GetStatusesAsync(string path, bool offline, List<GitStatusData> resultEntries, int timeout = -1)
		{
			var threadResults = new List<GitStatusData>();
			var operation = GitAsyncOperation<StatusOperationResult>.Start(op => GetStatuses(path, offline, resultEntries, timeout, op));
			operation.Completed += (op) => {
				resultEntries.AddRange(threadResults);
			};

			return operation;
		}


		/// <summary>
		/// Get offline status for a single file (non recursive). This won't check the fetched remote for changes.
		/// Will return valid status even if the file has nothing to show (has no changes).
		/// If error happened, invalid status data will be returned (check statusData.IsValid).
		/// Since git doesn't know about folders, use the folder meta status instead for them.
		/// </summary>
		public static GitStatusData GetStatus(string path, bool logErrorHint = true, IShellMonitor shellMonitor = null)
		{
			string originalPath = path;

			if (Directory.Exists(path)) {
				// git doesn't know about folders - use the folder meta status instead.
				path += ".meta";
			}

			List<GitStatusData> resultEntries = new List<GitStatusData>();
			StatusOperationResult result = GetStatuses(path, true, resultEntries, COMMAND_TIMEOUT, shellMonitor);

			if (logErrorHint) {
				LogStatusErrorHint(result);
			}

			GitStatusData statusData = resultEntries.FirstOrDefault();

			// NOTE: if file was marked as moved or deleted, and another unstaged file is present in the original location
			//		 it will be returned as unknown status, after the moved/deleted status (i.e. 2 entries for the same file).
			// Example:
			// > git status -s
			//   R  Foo.png -> Bar2.png
			//   D  README.md
			//   ?? Foo.png
			//   ?? README.md
			//
			// NOTE2: the git status command for a specific file returns D even when file was actually renamed (R).

			if (statusData.Status == VCFileStatus.Deleted
				&& resultEntries.Count == 2
				&& resultEntries[1].Status == VCFileStatus.Unversioned
				&& resultEntries[0].Path == resultEntries[1].Path
				) {
				statusData = resultEntries[1];
			}

			statusData.Path = originalPath;	// Restore original path in case of folder.

			// If no path was found, error happened.
			if (!statusData.IsValid || result != StatusOperationResult.Success) {
				// Fallback to unversioned as we don't touch them.
				statusData.Status = VCFileStatus.Unversioned;
			}

			return statusData;
		}

		/// <summary>
		/// Get status for a single file (non recursive).
		/// Will return valid status even if the file has nothing to show (has no changes).
		/// If error happened, invalid status data will be returned (check statusData.IsValid).
		/// Folders will always return normal status, as git doesn't care about them.
		/// </summary>
		public static GitAsyncOperation<GitStatusData> GetStatusAsync(string path, bool offline, bool logErrorHint = true, int timeout = -1)
		{
			return GitAsyncOperation<GitStatusData>.Start(op => {

				string originalPath = path;

				if (Directory.Exists(path)) {
					// git doesn't know about folders - use the folder meta status instead.
					path += ".meta";
				}

				List<GitStatusData> resultEntries = new List<GitStatusData>();
				StatusOperationResult result = GetStatuses(path, offline, resultEntries, timeout, op);

				if (logErrorHint) {
					LogStatusErrorHint(result);
				}

				var statusData = resultEntries.FirstOrDefault();
				statusData.Path = originalPath; // Restore original path in case of folder.

				// If no path was found, error happened.
				if (!statusData.IsValid || result != StatusOperationResult.Success) {
					// Fallback to unversioned as we don't touch them.
					statusData.Status = VCFileStatus.Unversioned;
				}

				return statusData;
			});
		}

		internal static StatusOperationResult ParseCommonStatusError(string error)
		{
			// fatal: not a git repository (or any of the parent directories): .git
			// This can be returned when project is not a valid git checkout. (Probably)
			if (error.Contains("fatal: not a git repository"))
				return StatusOperationResult.NotWorkingCopy;

			// warning: could not open directory '...': No such file or directory
			// Folder for target file was not found.
			// Happens when duplicating unversioned folder with unversioned files inside - requests for status by OnWillCreateAsset(), but files are not actually created yet.
			if (error.Contains("No such file or directory"))
				return StatusOperationResult.TargetPathNotFound;

			// System.ComponentModel.Win32Exception (0x80004005): ApplicationName='...', CommandLine='...', Native error= The system cannot find the file specified.
			// Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
			if (error.Contains("0x80004005"))
				return StatusOperationResult.ExecutableNotFound;

			// User needs to log in using normal git client and save their authentication. This or they have multiple accounts.
			// fatal: User cancelled dialog.
			// fatal: could not read Username for '...': No such file or directory
			// Authentication failed
			if (error.Contains("fatal: User cancelled dialog.") || error.Contains("fatal: could not read Username for"))
				return StatusOperationResult.AuthenticationFailed;

			// Unable to connect to repository indicating some network or server problems.
			// fatal: unable to access '...': Could not resolve host: ...
			if (error.Contains("fatal: unable to access") || error.Contains("No such device or address"))
				return StatusOperationResult.UnableToConnectError;

			// Git version is too old and doesn't support some features. Try updating git.
			// error: unknown option `porcelain'
			if (error.Contains("error: unknown option"))
				return StatusOperationResult.OldUnsupportedGitVersion;

			// Git lfs is not installed properly or using an old version?
			// Error while retrieving locks: missing protocol: ""
			// OR
			// git: 'lfs' is not a git command. See 'git --help'.
			if (error.Contains("missing protocol") || error.Contains("'lfs' is not a git command."))
				return StatusOperationResult.BadLFSSupport;

			// Operation took too long, shell utils time out kicked in.
			if (error.Contains(ShellUtils.TIME_OUT_ERROR_TOKEN))
				return StatusOperationResult.Timeout;

			return StatusOperationResult.UnknownError;
		}

		/// <summary>
		/// Get all ignored paths in the repository.
		/// </summary>
		/// <param name="skipFilesInIgnoredDirectories">If true, when whole directory is ignored, return just the directory, without the files in it.</param>
		public static string[] GetIgnoredPaths(string path, bool skipFilesInIgnoredDirectories)
		{
			string directoryArg = skipFilesInIgnoredDirectories ? "--directory" : "";

			var result = ShellUtils.ExecuteCommand(Git_Command, $"ls-files -i -o --exclude-standard {directoryArg} -z \"{GitFormatPath(path)}\"", COMMAND_TIMEOUT);

			// Happens for nested directory paths of ignored one, only when using --directory.
			// fatal: git ls-files: internal error - directory entry not superset of prefix
			if (skipFilesInIgnoredDirectories && result.Error.Contains("internal error - directory entry not superset of prefix")) {
				// Run without the flag and filter the results. May be slower depending on the results count.
				var ignoredPaths = GetIgnoredPaths(path, false);
				if (ignoredPaths.Length > 0) {
					// Yes, this path is ignored, no need to show the recursion.
					ignoredPaths = new string[] { path };
				}

				return ignoredPaths;
			}

			return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		}

		/// <summary>
		/// Lock a file on the remote LFS server.
		/// Use force to steal a lock from another user or working copy.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static LockOperationResult LockFile(string path, bool force, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			return LockFiles(Enumerate(path), force, timeout, shellMonitor);
		}

		/// <summary>
		/// Lock a file on the remote LFS server.
		/// Use force to steal a lock from another user or working copy.
		/// NOTE: If assembly reload happens, task will be lost, complete handler won't be called.
		/// </summary>
		public static GitAsyncOperation<LockOperationResult> LockFileAsync(string path, bool force, int timeout = -1)
		{
			return GitAsyncOperation<LockOperationResult>.Start(op => LockFile(path, force, timeout, op));
		}

		/// <summary>
		/// Lock files on the remote LFS server.
		/// Use force to steal a lock from another user or working copy.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static LockOperationResult LockFiles(IEnumerable<string> paths, bool force, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			// Locking for non-existing or unversion files works, which is confusing. Don't allow that.
			if (paths.Any(p => GetStatus(p, logErrorHint: false).Status == VCFileStatus.Unversioned)) {
				shellMonitor?.AppendErrorLine("Cannot lock unversioned files.");
				return LockOperationResult.TargetPathNotFound;
			}

			// Lock doesn't have force argument :(
			if (force) {
				LockOperationResult opResult = UnlockFiles(paths, true, timeout, shellMonitor);
				if (opResult != LockOperationResult.Success)
					return opResult;
			}

			// Not sure if paths are truely pathspec with wildcards etc. But it does support list of paths.
			var result = ShellUtils.ExecuteCommand(Git_Command, $"lfs lock {GitPathspecs(paths)}", timeout, shellMonitor);

			if (result.HasErrors) {

				// Locking ... failed: Lock exists
				// File is already locked by THIS or another working copy (can be the same user).
				// This happens even if this working copy got the lock.
				// NOTE: to check if lock is ours or not, we need to parse the user that owns it, which is too much hassle for now.
				if (result.Error.Contains("failed: Lock exists"))
					return LockOperationResult.LockAlreadyExists;

				// cannot lock directory: ...
				// Locking directories is not supported.
				if (result.Error.Contains("cannot lock directory"))
					return LockOperationResult.DirectoryLockNotSupported;

				// Sadly, lfs doesn't care if remote has changes for this file.
				//if (result.Error.Contains("???"))
				//	return LockOperationResult.RemoteHasChanges;

				return (LockOperationResult) ParseCommonStatusError(result.Error);
			}

			return LockOperationResult.Success;
		}

		/// <summary>
		/// Lock files on the remote LFS server.
		/// Use force to steal a lock from another user or working copy.
		/// NOTE: If assembly reload happens, task will be lost, complete handler won't be called.
		/// </summary>
		public static GitAsyncOperation<LockOperationResult> LockFilesAsync(IEnumerable<string> paths, bool force, int timeout = -1)
		{
			return GitAsyncOperation<LockOperationResult>.Start(op => LockFiles(paths, force, timeout, op));
		}

		/// <summary>
		/// Unlock a file on the remote LFS server.
		/// Use force to break a lock held by another user or working copy.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static LockOperationResult UnlockFile(string path, bool force, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			return UnlockFiles(Enumerate(path), force, timeout, shellMonitor);
		}

		/// <summary>
		/// Unlock a file on the remote LFS server.
		/// Use force to break a lock held by another user or working copy.
		/// NOTE: If assembly reload happens, task will be lost, complete handler won't be called.
		/// </summary>
		public static GitAsyncOperation<LockOperationResult> UnlockFileAsync(string path, bool force, int timeout = -1)
		{
			return GitAsyncOperation<LockOperationResult>.Start(op => UnlockFile(path, force, timeout, op));
		}

		/// <summary>
		/// Unlock files on the remote LFS server.
		/// Use force to break a lock held by another user or working copy.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static LockOperationResult UnlockFiles(IEnumerable<string> paths, bool force, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			// Locking for non-existing or unversion files works, which is confusing. Don't allow that.
			if (paths.Any(p => GetStatus(p, logErrorHint: false).Status == VCFileStatus.Unversioned)) {
				shellMonitor?.AppendErrorLine("Cannot unlock unversioned files.");
				return LockOperationResult.TargetPathNotFound;
			}

			var forceArg = force ? "--force" : string.Empty;

			// Not sure if paths are truely pathspec with wildcards etc. But it does support list of paths.
			var result = ShellUtils.ExecuteCommand(Git_Command, $"lfs unlock {forceArg} {GitPathspecs(paths)}", timeout, shellMonitor);

			result.Error = FilterOutLines(result.Error,
				// unable to get lock ID: no matching locks found
				// No one has locked this file. Consider this as success.
				"unable to get lock ID: no matching locks found",

				// warning: unlocking with uncommitted changes because --force
				// It will make file read-only, although it has changes. Fine.
				"unlocking with uncommitted changes because --force"
				);

			if (result.HasErrors) {

				// ... is locked by ...
				// File is locked by another user.
				if (result.Error.Contains("is locked by"))
					return LockOperationResult.LockAlreadyExists;

				// Cannot unlock file with uncommitted changes
				// Unlocking will make the file read-only which may be confusing for the users. Use force to bypass this.
				if (result.Error.Contains("Cannot unlock file with uncommitted changes"))
					return LockOperationResult.BlockedByUncommittedChanges;

				// "You must have admin access to force delete a lock" on github LFS (may be server specific?).
				// Example: github collabolators can't steal locks, only repo owners can.
				if (result.Error.Contains("You must have admin access"))
					return LockOperationResult.InsufficientPrivileges;

				// CreateFile ...: The system cannot find the file specified.
				// Unversioned file was locked, then moved. Unlocking the old location produces this error.
				if (result.Error.Contains("The system cannot find the file specified"))
					return LockOperationResult.TargetPathNotFound;

				// Unable to determine path: cannot lock directory: ...
				// Locking directories is not supported.
				if (result.Error.Contains("cannot lock directory"))
					return LockOperationResult.DirectoryLockNotSupported;

				return (LockOperationResult) ParseCommonStatusError(result.Error);
			}

			return LockOperationResult.Success;
		}

		/// <summary>
		/// Unlock files on the remote LFS server.
		/// Use force to break a lock held by another user or working copy.
		/// NOTE: If assembly reload happens, task will be lost, complete handler won't be called.
		/// </summary>
		public static GitAsyncOperation<LockOperationResult> UnlockFilesAsync(IEnumerable<string> paths, bool force, int timeout = -1)
		{
			return GitAsyncOperation<LockOperationResult>.Start(op => UnlockFiles(paths, force, timeout, op));
		}

		/// <summary>
		/// Fetch specified remote repository changes to the local one (without GUI). If remote is left empty, all remotes will be fetched.
		/// This will NOT make any changes in your working folder - call merge to do this.
		/// Pass "--all" for remote to fetch all of them.
		/// https://git-scm.com/docs/git-fetch
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static PullOperationResult FetchRemote(string remote = "", string branch = "", bool atomic = false, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			var atomicArg = atomic ? $"--atomic" : "";

			var result = ShellUtils.ExecuteCommand(Git_Command, $"fetch {atomicArg} --porcelain {remote} {branch}", timeout, shellMonitor);

			if (result.HasErrors) {

				// error: cannot lock ref '...': is at ... but expected ...
				// This means another fetch was in progress when this one started. The fetch was done, but ours got error. Continue normally.
				if (result.Error.Contains("error: cannot lock ref"))
					return PullOperationResult.Success;

				// fatal: '...' does not appear to be a git repository
				// fatal: Could not read from remote repository.
				// Remote repository not found..
				if (result.Error.Contains("does not appear to be a git repository"))
					return PullOperationResult.RemoteNotFound;

				// fatal: couldn't find remote ref ...
				// Remote branch not found.
				if (result.Error.Contains("couldn't find remote ref"))
					return PullOperationResult.BranchNotFound;

				return (PullOperationResult) ParseCommonStatusError(result.Error);
			}

			return PullOperationResult.Success;
		}

		/// <summary>
		/// Fetch specified remote repository changes to the local one (without GUI). If remote is left empty, all remotes will be fetched.
		/// This will NOT make any changes in your working folder - call merge to do this.
		/// Pass "--all" for remote to fetch all of them.
		/// https://git-scm.com/docs/git-fetch
		/// </summary>
		public static GitAsyncOperation<PullOperationResult> FetchRemoteAsync(string remote = "", string branch = "", bool atomic = false, int timeout = -1)
		{
			return GitAsyncOperation<PullOperationResult>.Start(op => FetchRemote(remote, branch, atomic, timeout, op));
		}

		/// <summary>
		/// Perform "git merge" - incorporates changes from fetched remotes to the current branch (without GUI).
		/// Use <see cref="HasAnyConflicts(string, int, IShellMonitor)"/> to see if the merge produced some conflicts.
		/// https://git-scm.com/docs/git-merge
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static PullOperationResult Merge(string branch = "", string mergeStrategy = "", int timeout = -1, IShellMonitor shellMonitor = null)
		{
			ShellUtils.ShellResult result;
			try {

				AssetDatabase.DisallowAutoRefresh();
				result = ShellUtils.ExecuteCommand(Git_Command, $"merge {branch} {mergeStrategy}", timeout, shellMonitor);
			} finally {
				AssetDatabase.AllowAutoRefresh();
			}

			if (result.HasErrors) {
				// error: Your local changes to the following files would be overwritten by merge:
				// Please commit your changes or stash them before you merge.
				if (result.Error.Contains("Your local changes to the following files would be overwritten by merge"))
					return PullOperationResult.LocalChangesFound;

				// merge: ... - not something we can merge
				// Branch not found.
				if (result.Error.Contains("not something we can merge"))
					return PullOperationResult.BranchNotFound;

				return (PullOperationResult)ParseCommonStatusError(result.Error);
			}

			// CONFLICT (content): Merge conflict in Assets/Readme.txt
			// Automatic merge failed; fix conflicts and then commit the result.
			// Conflicts happened.
			if (result.Output.Contains("Automatic merge failed;"))
				return PullOperationResult.SuccessWithConflicts;

			return PullOperationResult.Success;
		}

		/// <summary>
		/// Perform "git merge" - incorporates changes from fetched remotes to the current branch (without GUI).
		/// Use <see cref="HasAnyConflicts(string, int, IShellMonitor)"/> to see if the merge produced some conflicts.
		/// https://git-scm.com/docs/git-merge
		/// DANGER: git updating while editor is crunching assets IS DANGEROUS! This Merge method will disable unity auto-refresh feature until it has finished.
		/// </summary>
		public static GitAsyncOperation<PullOperationResult> MergeAsync(string branch = "", string mergeStrategy = "", int timeout = -1)
		{
			return GitAsyncOperation<PullOperationResult>.Start(op => Merge(branch, mergeStrategy, timeout, op));
		}

		/// <summary>
		/// Perform "git pull" (i.e. fetch and merge) (without GUI).
		/// Use <see cref="HasAnyConflicts(string, int, IShellMonitor)"/> to see if the merge produced some conflicts.
		/// Pass "--all" for remote to fetch all of them.
		/// https://git-scm.com/docs/git-pull
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static PullOperationResult Pull(string remote = "", string branch = "", string mergeStrategy = "", bool atomic = false, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			var atomicArg = atomic ? $"--atomic" : "";

			ShellUtils.ShellResult result;
			try {

				AssetDatabase.DisallowAutoRefresh();
				result = ShellUtils.ExecuteCommand(Git_Command, $"pull {atomicArg} --porcelain {remote} {branch} {mergeStrategy}", timeout, shellMonitor);
			}
			finally {
				AssetDatabase.AllowAutoRefresh();
			}

			if (result.HasErrors) {
				//
				// Fetch errors
				//

				// fatal: '...' does not appear to be a git repository
				// fatal: Could not read from remote repository.
				// Remote repository not found..
				if (result.Error.Contains("does not appear to be a git repository"))
					return PullOperationResult.RemoteNotFound;

				// fatal: couldn't find remote ref ...
				// Remote branch not found.
				if (result.Error.Contains("couldn't find remote ref"))
					return PullOperationResult.BranchNotFound;

				//
				// Merge errors
				//

				// error: Your local changes to the following files would be overwritten by merge:
				// Please commit your changes or stash them before you merge.
				if (result.Error.Contains("Your local changes to the following files would be overwritten by merge"))
					return PullOperationResult.LocalChangesFound;

				// merge: ... - not something we can merge
				// Branch not found.
				if (result.Error.Contains("not something we can merge"))
					return PullOperationResult.BranchNotFound;


				return (PullOperationResult) ParseCommonStatusError(result.Error);
			}

			// CONFLICT (content): Merge conflict in Assets/Readme.txt
			// Automatic merge failed; fix conflicts and then commit the result.
			// Conflicts happened.
			if (result.Output.Contains("Automatic merge failed;"))
				return PullOperationResult.SuccessWithConflicts;

			return PullOperationResult.Success;
		}

		/// <summary>
		/// Perform "git pull" (i.e. fetch and merge) (without GUI).
		/// Use <see cref="HasAnyConflicts(string, int, IShellMonitor)"/> to see if the merge produced some conflicts.
		/// Pass "--all" for remote to fetch all of them.
		/// https://git-scm.com/docs/git-pull
		/// DANGER: git updating while editor is crunching assets IS DANGEROUS! This Pull method will disable unity auto-refresh feature until it has finished.
		/// </summary>
		public static GitAsyncOperation<PullOperationResult> PullAsync(string remote = "", string branch = "", string mergeStrategy = "", bool atomic = false, int timeout = -1)
		{
			return GitAsyncOperation<PullOperationResult>.Start(op => Pull(remote, branch, mergeStrategy, atomic, timeout, op));
		}

		/// <summary>
		/// Perform "git merge --abort" - to cancel the merge in progress (without GUI).
		/// Returns true if successful.
		/// https://git-scm.com/docs/git-merge
		/// </summary>
		public static bool MergeAbort(int timeout = -1, IShellMonitor shellMonitor = null)
		{
			ShellUtils.ShellResult result;
			try {
				AssetDatabase.DisallowAutoRefresh();
				result = ShellUtils.ExecuteCommand(Git_Command, $"merge --abort", timeout, shellMonitor);
			} finally {
				AssetDatabase.AllowAutoRefresh();
			}

			return !result.HasErrors;
		}

		/// <summary>
		/// Commit files to git directly (without GUI).
		/// Files MUST be versioned!
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		private static PushOperationResult Commit([AllowNull] IEnumerable<string> assetPaths, bool includeMeta, bool autoStageModified, string message, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			if (string.IsNullOrWhiteSpace(message))
				return PushOperationResult.MessageIsEmpty;

			if (includeMeta && assetPaths != null) {
				assetPaths = assetPaths.Select(path => path + ".meta").Concat(assetPaths);
			}

			// NOTE: paths and --all are mutually exclusive!
			var autoStageArg = autoStageModified ? "--all" : "";
			string pathspecs = assetPaths != null ? GitPathspecs(assetPaths) : string.Empty;

			var result = ShellUtils.ExecuteCommand(Git_Command, $"commit --message=\"{message}\" {autoStageArg} {pathspecs}", timeout, shellMonitor);
			if (result.HasErrors) {

				// no changes added to commit (use "git add" and/or "git commit -a")
				if (result.ErrorCode == -1 && result.Output.Contains("no changes added to commit"))
					return PushOperationResult.NoChangesToCommit;

				// Some files have conflicts. Clear them before trying to commit.
				// error: Committing is not possible because you have unmerged files.
				// fatal: Exiting because of an unresolved conflict.
				if (result.Error.Contains("you have unmerged files") || result.Error.Contains("unresolved conflict"))
					return PushOperationResult.ConflictsError;

				// Cannot do partial commits during merge. Do the special merge commit with staged changes first.
				// fatal: cannot do a partial commit during a merge.
				if (result.Error.Contains("cannot do a partial commit during a merge"))
					return PushOperationResult.NoPartialCommitsInMerge;

				// Can't commit unversioned files directly. Add them before trying to commit. Recursive skips unversioned files.
				// error: pathspec '...' did not match any file(s) known to git
				if (result.Error.Contains("did not match any file(s) known to git"))
					return PushOperationResult.UnversionedError;

				return (PushOperationResult) ParseCommonStatusError(result.Error);
			}

			return PushOperationResult.Success;
		}

		/// <summary>
		/// Commit files to git directly (without GUI).
		/// Files MUST be versioned!
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static PushOperationResult Commit(IEnumerable<string> assetPaths, bool includeMeta, string message, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			return Commit(assetPaths, includeMeta, autoStageModified: false, message, timeout, shellMonitor);
		}

		/// <summary>
		/// Commit files to git directly (without GUI).
		/// Files MUST be versioned!
		/// </summary>
		public static GitAsyncOperation<PushOperationResult> CommitAsync(IEnumerable<string> assetPaths, bool includeMeta, string message, int timeout = -1)
		{
			return GitAsyncOperation<PushOperationResult>.Start(op => Commit(assetPaths, includeMeta, autoStageModified: false, message, timeout, op));
		}

		/// <summary>
		/// Commit staged files to git directly (without GUI).
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		/// <param name="autoStageModified">Tell the command to automatically stage files that have been modified and deleted, but new files you have not told Git about are not affected. NOTE: this will commit files that still have conflicts!</param>
		public static PushOperationResult Commit(string message, bool autoStageModified, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			return Commit(null, includeMeta: false, autoStageModified, message, timeout, shellMonitor);
		}

		/// <summary>
		/// Commit staged files to git directly (without GUI).
		/// </summary>
		/// <param name="autoStageModified">Tell the command to automatically stage files that have been modified and deleted, but new files you have not told Git about are not affected. NOTE: this will commit files that still have conflicts!</param>
		public static GitAsyncOperation<PushOperationResult> CommitAsync(string message, bool autoStageModified, int timeout = -1)
		{
			return GitAsyncOperation<PushOperationResult>.Start(op => Commit(null, includeMeta: false, autoStageModified, message, timeout, op));
		}

		/// <summary>
		/// Perform "git push" to upload your working repository to the remote (without GUI).
		/// https://git-scm.com/docs/git-push
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static PushOperationResult Push(string remote = "", string branch = "", bool atomic = false, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			var atomicArg = atomic ? $"--atomic" : "";

			var result = ShellUtils.ExecuteCommand(Git_Command, $"pull {atomicArg} --porcelain {remote} {branch}", timeout, shellMonitor);
			if (result.HasErrors) {

				// error: failed to push some refs to '...'
				// Remote has changes. You need to pull first.
				if (result.Error.Contains("failed to push some refs to"))
					return PushOperationResult.RejectedByRemote;

				// fatal: '...' does not appear to be a git repository
				// fatal: Could not read from remote repository.
				// Remote repository not found..
				if (result.Error.Contains("does not appear to be a git repository"))
					return PushOperationResult.RemoteNotFound;

				// error: src refspec ... does not match any
				// error: failed to push some refs to '...'
				// Remote branch not found.
				if (result.Error.Contains("does not match any"))
					return PushOperationResult.BranchNotFound;

				return (PushOperationResult) ParseCommonStatusError(result.Error);
			}

			return PushOperationResult.Success;
		}

		/// <summary>
		/// Perform "git push" to upload your working repository to the remote (without GUI).
		/// https://git-scm.com/docs/git-push
		/// </summary>
		public static GitAsyncOperation<PushOperationResult> PushAsync(string remote = "", string branch = "", bool atomic = false, int timeout = -1)
		{
			return GitAsyncOperation<PushOperationResult>.Start(op => Push(remote, branch, atomic, timeout, op));
		}

		/// <summary>
		/// Revert files to git directly (without GUI).
		/// Pathspecs support wildcards and more. Read here: https://css-tricks.com/git-pathspecs-and-how-to-use-them/
		/// This is an offline operation, but it still can take time, since it will copy from the original files.
		/// checkoutAfterReset will restore files in the working tree after it resets them in the index.
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static RevertOperationResult Revert(IEnumerable<string> assetPathspecs, bool includeMeta, bool checkoutAfterReset, int timeout = -1, IShellMonitor shellMonitor = null)
		{
			if (includeMeta) {
				assetPathspecs = assetPathspecs.Select(path => path + ".meta").Concat(assetPathspecs);
			}

			string pathspec = GitPathspecs(assetPathspecs);

			var result = ShellUtils.ExecuteCommand(Git_Command, $"reset -q -- {pathspec}", timeout, shellMonitor);
			if (result.HasErrors) {

				// Operation took too long, shell utils time out kicked in.
				if (result.Error.Contains(ShellUtils.TIME_OUT_ERROR_TOKEN))
					return RevertOperationResult.Timeout;

				return RevertOperationResult.UnknownError;
			}

			if (checkoutAfterReset) {
				result = ShellUtils.ExecuteCommand(Git_Command, $"checkout -q -- {pathspec}", timeout, shellMonitor);
				if (result.HasErrors) {

					//error: pathspec '...' did not match any file(s) known to git
					// File is actually unversioned/untracked, which is valid case after reset of an added file. Nothing to do.
					if (result.Error.Contains("did not match any file(s) known to git"))
						return RevertOperationResult.Success;

					// Operation took too long, shell utils time out kicked in.
					if (result.Error.Contains(ShellUtils.TIME_OUT_ERROR_TOKEN))
						return RevertOperationResult.Timeout;

					return RevertOperationResult.UnknownError;
				}
			}

			return RevertOperationResult.Success;
		}

		/// <summary>
		/// Revert files to git directly (without GUI).
		/// Pathspecs support wildcards and more. Read here: https://css-tricks.com/git-pathspecs-and-how-to-use-them/
		/// This is an offline operation, but it still can take time, since it will copy from the original files.
		/// checkoutAfterReset will restore files in the working tree after it resets them in the index.
		/// </summary>
		public static GitAsyncOperation<RevertOperationResult> RevertAsync(IEnumerable<string> assetPaths, bool includeMeta, bool checkoutAfterReset, int timeout = -1)
		{
			return GitAsyncOperation<RevertOperationResult>.Start(op => Revert(assetPaths, includeMeta, checkoutAfterReset, timeout, op));
		}


		/// <summary>
		/// Add files to git directly (without GUI).
		/// </summary>
		public static bool Add(string path, bool includeMeta, IShellMonitor shellMonitor = null)
		{
			if (string.IsNullOrEmpty(path))
				return true;

			// Will add parent folders and their metas.
			var success = CheckAndAddParentFolderIfNeeded(path, false, shellMonitor);
			if (success == false)
				return false;

			// "git add" overrides the conflicted status which is not nice.
			var statusData = GetStatus(path);
			if (statusData.IsConflicted)
				return false;

			var result = ShellUtils.ExecuteCommand(Git_Command, $"add --force \"{GitFormatPath(path)}\"", COMMAND_TIMEOUT, shellMonitor);
			if (result.HasErrors)
				return false;

			if (includeMeta) {

				// "git add" overrides the conflicted status which is not nice.
				statusData = GetStatus(path + ".meta");
				if (statusData.IsConflicted)
					return false;

				result = ShellUtils.ExecuteCommand(Git_Command, $"add --force \"{GitFormatPath(path + ".meta")}\"", COMMAND_TIMEOUT, shellMonitor);
				if (result.HasErrors)
					return false;
			}

			return true;
		}

		/// <summary>
		/// Adds all parent unversioned folder META FILES (as git doesn't know about folders themselves)!
		/// If this is needed it will ask the user for permission if promptUser is true.
		/// </summary>
		public static bool CheckAndAddParentFolderIfNeeded(string path, bool promptUser)
		{
			using (var reporter = CreateReporter()) {
				return CheckAndAddParentFolderIfNeeded(path, promptUser, reporter);
			}
		}

		/// <summary>
		/// Adds all parent unversioned folder META FILES (as git doesn't know about folders themselves)!
		/// If this is needed it will ask the user for permission if promptUser is true.
		/// </summary>
		public static bool CheckAndAddParentFolderIfNeeded(string path, bool promptUser, IShellMonitor shellMonitor = null)
		{
			var directory = Path.GetDirectoryName(path);

			// Special case - Root folders like Assets, ProjectSettings, etc...
			// They don't have metas and git doesn't care about directories.
			if (string.IsNullOrEmpty(directory) || directory.IndexOfAny(new[] { '/', '\\' }) == -1) {
				return true;
			}

			// Git doesn't know about directories - check the meta instead.
			var newDirectoryStatusData = GetStatus(directory + ".meta");

			// Moving to unversioned folder -> add it to git.
			if (newDirectoryStatusData.Status == VCFileStatus.Unversioned) {

				if (!Silent && promptUser && !EditorUtility.DisplayDialog(
					"Unversioned directory",
					$"The target directory (the meta):\n\"{directory}\"\nis not under git control. Should it be added?",
					"Add it!",
					"Cancel"
#if UNITY_2019_4_OR_NEWER
					, DialogOptOutDecisionType.ForThisSession, "WiseGit.AddUnversionedFolder"
				))
#else
				))
#endif
					return false;

				if (!AddParentFolders(directory, shellMonitor))
					return false;

			}

			return true;
		}

		/// <summary>
		/// Adds all parent unversioned folders META FILES!
		/// </summary>
		public static bool AddParentFolders(string newDirectory, IShellMonitor shellMonitor = null)
		{
			// If working outside Assets folder, don't consider metas.
			if (!newDirectory.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
				return true;

			// Now add all folder metas upwards
			var directoryMeta = newDirectory + ".meta";
			var directoryMetaStatus = GetStatus(directoryMeta).Status; // Will be unversioned.
			while (directoryMetaStatus == VCFileStatus.Unversioned) {

				// Don't use the Add() method, as it calls this one.
				var result = ShellUtils.ExecuteCommand(Git_Command, $"add --force \"{GitFormatPath(directoryMeta)}\"", COMMAND_TIMEOUT, shellMonitor);
				if (result.HasErrors)
					return false;

				directoryMeta = Path.GetDirectoryName(directoryMeta) + ".meta";

				// The assets folder doesn't have meta - we reached the top.
				if (directoryMeta.Equals("Assets.meta", StringComparison.OrdinalIgnoreCase))
					return true;

				directoryMetaStatus = GetStatus(directoryMeta).Status;
			}

			return true;
		}

		/// <summary>
		/// Delete file in git directly (without GUI).
		/// </summary>
		/// <param name="keepLocal">Will mark files for deletion, without removing them from the working tree</param>
		public static bool Delete(string path, bool includeMeta, bool keepLocal, IShellMonitor shellMonitor = null)
		{
			if (string.IsNullOrEmpty(path))
				return true;

			var keepLocalArg = keepLocal ? "--cached" : "";

			var result = ShellUtils.ExecuteCommand(Git_Command, $"rm --force -r {keepLocalArg} \"{GitFormatPath(path)}\"", COMMAND_TIMEOUT, shellMonitor);
			if (result.HasErrors)
				return false;

			if (includeMeta) {
				result = ShellUtils.ExecuteCommand(Git_Command, $"rm --force -r {keepLocalArg} \"{GitFormatPath(path + ".meta")}\"", COMMAND_TIMEOUT, shellMonitor);
				if (result.HasErrors)
					return false;
			}

			return true;
		}

		/// <summary>
		/// Check if file or folder has conflicts.
		/// </summary>
		public static bool HasAnyConflicts(string path, int timeout = COMMAND_TIMEOUT * 4, IShellMonitor shellMonitor = null)
		{
			List<GitStatusData> resultEntries = new List<GitStatusData>();
			StatusOperationResult result = GetStatuses(path, offline: true, resultEntries, timeout, shellMonitor);

			if (result != StatusOperationResult.Success)
				throw new IOException($"Trying to get conflict status for file {path} caused error:\n{result}!");

			return resultEntries.Any(status => status.IsConflicted);
		}

		/// <summary>
		/// List files and folders in specified remote branch directory.
		/// Results will be appended to resultPaths. Paths will be relative to the specified directory.
		/// Example: "origin/master", "Assets/Art"
		/// NOTE: This is synchronous operation. Better use the Async method version to avoid editor slow down.
		/// </summary>
		public static ListOperationResult ListRemote(string remoteBranch, string remoteDirectory, bool recursive, List<string> resultPaths, int timeout = ONLINE_COMMAND_TIMEOUT, IShellMonitor shellMonitor = null)
		{
			string recursiveArg = recursive ? "-r" : "";

			var result = ShellUtils.ExecuteCommand(Git_Command, $"ls-tree --full-name --name-only {recursiveArg} {remoteBranch}:{remoteDirectory}", timeout, shellMonitor);

			if (!string.IsNullOrEmpty(result.Error)) {

				// Remote directory not found.
				// fatal: Not a valid object name origin/master:sadasda
				if (result.Error.Contains("Not a valid object name"))
					return ListOperationResult.NotFound;

				return (ListOperationResult) ParseCommonStatusError(result.Error);
			}

			var output = result.Output.Replace("\r", "");
			resultPaths.AddRange(output.Split('\n'));

			return ListOperationResult.Success;
		}

		/// <summary>
		/// List files and folders in specified url directory.
		/// Results will be appended to resultPaths. Paths will be relative to the url.
		/// If entry is a folder, it will end with a '/' character.
		/// NOTE: If assembly reload happens, task will be lost, complete handler won't be called.
		/// </summary>
		public static GitAsyncOperation<ListOperationResult> ListRemoteAsync(string remoteBranch, string remoteDirectory, bool recursive, List<string> resultPaths, int timeout = -1)
		{
			var threadResults = new List<string>();
			var operation = GitAsyncOperation<ListOperationResult>.Start(op => ListRemote(remoteBranch, remoteDirectory, recursive, threadResults, timeout, op));
			operation.Completed += (op) => {
				resultPaths.AddRange(threadResults);
			};

			return operation;
		}


		/// <summary>
		/// Retrieve working branch name that is checked out.
		/// E.g. master
		/// </summary>
		public static string GetWorkingBranch(string workingDirectory = "")
		{
			return ShellUtils.ExecuteCommand(Git_Command, "rev-parse --abbrev-ref HEAD", workingDirectory, COMMAND_TIMEOUT).Output;
		}

		/// <summary>
		/// Retrieve tracked remote branch of the working directory one.
		/// E.g. origin/master
		/// </summary>
		public static string GetTrackedRemoteBranch(string workingDirectory = "")
		{
			return ShellUtils.ExecuteCommand(Git_Command, "rev-parse --abbrev-ref --symbolic-full-name @{u}", workingDirectory, COMMAND_TIMEOUT).Output;
		}

		/// <summary>
		/// Retrieve tracked remote.
		/// E.g. origin
		/// </summary>
		public static string GetTrackedRemote(string workingDirectory = "")
		{
			return ShellUtils.ExecuteCommand(Git_Command, "rev-parse --abbrev-ref --symbolic-full-name @{u}", workingDirectory, COMMAND_TIMEOUT).Output.Split('/').FirstOrDefault();
		}

		/// <summary>
		/// Retrieve the commit (SHA) where current working branch diverged from the remote one.
		/// </summary>
		public static string GetWorkingBranchDivergingCommit(string workingDirectory = "")
		{
			return ShellUtils.ExecuteCommand(Git_Command, $"merge-base --fork-point {GetTrackedRemoteBranch()}", workingDirectory, COMMAND_TIMEOUT).Output;
		}

		/// <summary>
		/// Checks if git CLI is setup and working properly.
		/// Returns string containing the git errors if any.
		/// </summary>
		public static string CheckForGitErrors()
		{
			var result = ShellUtils.ExecuteCommand(Git_Command, $"status --porcelain -z \"{GitFormatPath(ProjectRootNative)}\"", COMMAND_TIMEOUT);

			return result.Error;
		}

		/// <summary>
		/// Checks if git can authenticate properly.
		/// This is asynchronous operation as it may take some time. Wait for the result.
		/// </summary>
		public static GitAsyncOperation<StatusOperationResult> CheckForGitAuthErrors()
		{
			var operation = GitAsyncOperation<StatusOperationResult>.Start(op => {
				// This requres authentication.
				var result = ShellUtils.ExecuteCommand(Git_Command, $"remote show {GetTrackedRemote()}", COMMAND_TIMEOUT);

				if (result.HasErrors) {
					return ParseCommonStatusError(result.Error);
				}

				return StatusOperationResult.Success;
			});

			return operation;
		}

		internal static void PromptForAuth(string path)
		{
			string hint = "\nNOTE: You may need to enter your Personal Access Token as your password.\n      Check with your provider.\n\n      If git keeps asking for password, try running this once:\n      'git config --global credential.helper store'\n\n";

			ShellUtils.ExecutePrompt(Git_Command, $"lfs locks", path, hint);

#if !UNITY_EDITOR_WIN
			// Interact with the user since we don't know when the terminal will close.
			EditorUtility.DisplayDialog("Git Authenticate", "A terminal window was open. When you authenticated in the terminal window, press \"Ready\".", "Ready");
#endif
		}

		/// <summary>
		/// Search for hidden files and folders starting with .
		/// Basically search for any "/." or "\."
		/// </summary>
		public static bool IsHiddenPath(string path)
		{
			for (int i = 0, len = path.Length; i < len - 1; ++i) {
				if (path[i + 1] == '.' && (path[i] == '/' || path[i] == '\\'))
					return true;
			}

			return false;
		}


		// NOTE: This is called separately for the file and its meta.
		private static void OnWillCreateAsset(string path)
		{
			if (!Enabled || TemporaryDisabled)
				return;

			var pathStatusData = GetStatus(path);
			if (pathStatusData.Status == VCFileStatus.Deleted) {

				var isMeta = path.EndsWith(".meta");

				/*
				// This is just annoying and not useful. We can't do anything about it.
				// It often happens with tools that pre-generate files, for example: baking light maps.
				if (!isMeta && !Silent) {
					bool choice = EditorUtility.DisplayDialog(
						"Deleted file",
						$"The desired location\n\"{path}\"\nis marked as deleted in git. The file will be replaced in git with the new one.\n\nIf this is an automated change, consider adding this file to the exclusion list in the project preferences:\n\"{GitPreferencesWindow.PROJECT_PREFERENCES_MENU}\"\n...or change your tool to silence the integration.",
						"Replace"
#if UNITY_2019_4_OR_NEWER
						, DialogOptOutDecisionType.ForThisSession, "WiseGit.ReplaceFile"
					);
#else
					);
#endif
					if (!choice)
						return;
				}
				*/

				using (var reporter = CreateReporter()) {
					reporter.AppendTraceLine($"Created file \"{path}\" has deleted git status. Reverting git status, while keeping the original file...");

					// Reset will restore the file in the index, but not in the working tree. For this do "git checkout -- <file>".
					var result = ShellUtils.ExecuteCommand(Git_Command, $"reset -q -- \"{GitFormatPath(path)}\"", COMMAND_TIMEOUT, reporter);
					Debug.Assert(!result.HasErrors, "Revert of deleted file failed.");

					if (isMeta) {
						var mainAssetPath = path.Substring(0, path.Length - ".meta".Length);

						var mainStatusData = GetStatus(mainAssetPath, true, reporter);

						// If asset came OUTSIDE of Unity, OnWillCreateAsset() will get called only for it's meta,
						// leaving the main asset with Deleted git status and existing file.
						if (File.Exists(mainAssetPath) && mainStatusData.Status == VCFileStatus.Deleted) {
							reporter.AppendTraceLine($"Asset \"{mainAssetPath}\" was created from outside Unity and has deleted git status. Reverting git status, while keeping the original file...");

							// Reset will restore the file in the index, but not in the working tree. For this do "git checkout -- <file>".
							result = ShellUtils.ExecuteCommand(Git_Command, $"reset -q -- \"{GitFormatPath(mainAssetPath)}\"", COMMAND_TIMEOUT, reporter);
							Debug.Assert(!result.HasErrors, "Revert of deleted file failed.");
						}
					}
				}

			}
		}

		private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions option)
		{
			if (!Enabled || TemporaryDisabled || GitPreferencesManager.ShouldExclude(m_PersonalPrefs.Exclude.Concat(m_ProjectPrefs.Exclude), path))
				return AssetDeleteResult.DidNotDelete;

			var oldStatus = GetStatus(path).Status;

			if (oldStatus == VCFileStatus.Unversioned)
				return AssetDeleteResult.DidNotDelete;

			using (var reporter = CreateReporter()) {

				var result = ShellUtils.ExecuteCommand(Git_Command, $"rm --force -r \"{GitFormatPath(path)}\"", COMMAND_TIMEOUT, reporter);
				if (result.HasErrors) {

					// fatal: pathspec '...' did not match any files
					// Unversioned file got deleted or is already marked for deletion (staged). Let someone else show the error if any.
					// Or deleted folder was empty.
					if (result.Error.Contains("did not match any files")) {
						reporter.ClearLogsAndErrorFlag();

						// If it was an empty folder, make sure we delete the meta, or it may confuse the git clients.
						// (example - rename empty folder, then delete it. From Added must turn to Deleted stats)
						if (Directory.Exists(path)) {
							ShellUtils.ExecuteCommand(Git_Command, $"rm --force \"{GitFormatPath(path + ".meta")}\"", COMMAND_TIMEOUT, reporter);
							reporter.ResetErrorFlag();	// Whatever happens, happens...
						}

						return AssetDeleteResult.DidNotDelete;
					}

					return AssetDeleteResult.FailedDelete;
				}

				result = ShellUtils.ExecuteCommand(Git_Command, $"rm --force \"{GitFormatPath(path + ".meta")}\"", COMMAND_TIMEOUT, reporter);
				if (result.HasErrors) {

					// fatal: pathspec '...' did not match any files
					// Unversioned file got deleted or is already marked for deletion (staged). Let someone else show the error if any.
					if (result.Error.Contains("did not match any files")) {
						reporter.ClearLogsAndErrorFlag();
						return AssetDeleteResult.DidNotDelete;
					}

					return AssetDeleteResult.FailedDelete;
				}

				return AssetDeleteResult.DidDelete;
			}
		}

		private static AssetMoveResult OnWillMoveAsset(string oldPath, string newPath)
		{
			if (!Enabled || TemporaryDisabled || GitPreferencesManager.ShouldExclude(m_PersonalPrefs.Exclude.Concat(m_ProjectPrefs.Exclude), oldPath))
				return AssetMoveResult.DidNotMove;

			// Let Unity log error for "already existing" folder, before we try to git-mv it, as it will put it inside the folder.
			if (Directory.Exists(newPath))
				return AssetMoveResult.DidNotMove;

			var oldStatusData = GetStatus(oldPath);
			bool isFolder = Directory.Exists(oldPath);

			/* Not needed as git doesn't really do renames - it compares files and decides on-the-fly what came from where. Also, doesn't care about folders.
			 * Moving file onto another deleted file works fine.
			 * Moving folder onto deleted folder works fine, even if contains files with same name but different content.
			 * Moving file or folder onto existing folder will put them inside the target folder, but Unity wouldn't allow it.

			var newStatusData = GetStatus(newPath);
			if (newStatusData.Status == VCFileStatus.Deleted) {
				if (Silent || EditorUtility.DisplayDialog(
					"Deleted file/folder",
					$"The desired location\n\"{newPath}\"\nis marked as deleted in git. Are you trying to replace it with a new one?",
					"Replace",
					"Cancel"
#if UNITY_2019_4_OR_NEWER
					, DialogOptOutDecisionType.ForThisSession, "WiseGit.ReplaceFile"
				)) {
#else
				)) {
#endif
					using (var reporter = CreateReporter()) {
						// Remember - if deleted, target should be missing. If exists, move will fail later on anyway.

						if (isFolder) {
							// Revet the destination without checkout. They will remain "missing".
							var revertPaths = oldStatusData.Status == VCFileStatus.Unversioned
								? new[] { newPath + ".meta" }
								: new[] { newPath, newPath + ".meta" }
								;
							var revertResult = Revert(revertPaths, includeMeta: false, checkoutAfterReset: false, shellMonitor: reporter);
							if (revertResult != RevertOperationResult.Success) {
								return AssetMoveResult.FailedMove;
							}

						} else {
							var revertResult = Revert(new[] { newPath }, includeMeta: true, checkoutAfterReset: false, shellMonitor: reporter);
							if (revertResult != RevertOperationResult.Success) {
								return AssetMoveResult.FailedMove;
							}
						}
					}

				} else {
					return AssetMoveResult.FailedMove;
				}

			}
			*/

			if (oldStatusData.Status == VCFileStatus.Unversioned || oldStatusData.Status == VCFileStatus.Ignored || oldStatusData.Status == VCFileStatus.Excluded) {
				return AssetMoveResult.DidNotMove;
			}

			if (oldStatusData.IsConflicted || (isFolder && HasAnyConflicts(oldPath))) {
				if (Silent || EditorUtility.DisplayDialog(
					"Conflicted files",
					$"Failed to move the files\n\"{oldPath}\"\nbecause it has conflicts. Resolve them first!",
					"Check changes",
					"Cancel")) {
					ShowChangesUI?.Invoke();
				}

				return AssetMoveResult.FailedMove;
			}

			if (m_PersonalPrefs.AskOnMovingFolders && isFolder
				//&& newStatusData.Status != VCFileStatus.Deleted	// Was already asked, don't do it again.
				&& !Application.isBatchMode) {

				if (!Silent && !EditorUtility.DisplayDialog(
					"Move Versioned Folder?",
					$"Do you really want to move this folder in git?\n\"{oldPath}\"",
					"Yes",
					"No"
#if UNITY_2019_4_OR_NEWER
					, DialogOptOutDecisionType.ForThisSession, "WiseGit.AskOnMovingFolders"
				)) {
#else
				)) {
#endif
					Debug.Log($"User aborted move of folder \"{oldPath}\".");
					return AssetMoveResult.FailedMove;
				}
			}

			using (var reporter = CreateReporter()) {

				if (!CheckAndAddParentFolderIfNeeded(newPath, true, reporter))
					return AssetMoveResult.FailedMove;

				var result = ShellUtils.ExecuteCommand(Git_Command, $"mv \"{GitFormatPath(oldPath)}\" \"{newPath}\"", COMMAND_TIMEOUT, reporter);
				if (result.HasErrors) {

					// fatal: source directory is empty, source=..., destination=...
					// Empty "versioned" folders (i.e. it's meta file) returns this error, which is fine - move the folder normally and move the meta with git.
					if (result.Error.Contains("fatal: source directory is empty")) {
						try {
							Directory.Move(oldPath, newPath);
							reporter.ResetErrorFlag();	// Recover and continue doing the meta file.

						} catch (Exception e) {
							reporter.AppendErrorLine($"Failed to move directory with exception: {e}");
						}
					}

					// Moving files from one repository to another is not allowed (nested checkouts or externals).
					//svn: E155023: Cannot copy to '...', as it is not from repository '...'; it is from '...'
					// TODO: Nested repositories support is missing?
					/*
					if (result.Error.Contains("E155023")) {

						if (Silent || EditorUtility.DisplayDialog(
							"Error moving asset",
							$"Failed to move file as destination is in another external repository:\n{oldPath}\n\nWould you like to force move the file anyway?\nWARNING: You'll loose the SVN history of the file.\n\nTarget path:\n{newPath}",
							"Yes, ignore SVN",
							"Cancel"
							)) {

							return MoveAssetByAddDeleteOperations(oldPath, newPath, reporter)
								? AssetMoveResult.DidMove
								: AssetMoveResult.FailedMove
								;

						} else {
							reporter.ResetErrorFlag();
							return AssetMoveResult.FailedMove;
						}

					}
					*/

					// Check if we recovered from the error.
					if (reporter.HasErrors) {
						return AssetMoveResult.FailedMove;
					}
				}

				result = ShellUtils.ExecuteCommand(Git_Command, $"mv \"{GitFormatPath(oldPath + ".meta")}\" \"{newPath}.meta\"", COMMAND_TIMEOUT, reporter);
				if (result.HasErrors)
					return AssetMoveResult.FailedMove;

				return AssetMoveResult.DidMove;
			}
		}

		internal static void LogStatusErrorHint(StatusOperationResult result, string suffix = null)
		{
			if (result == StatusOperationResult.Success)
				return;

			string displayMessage;

			switch(result) {
				case StatusOperationResult.NotWorkingCopy:
					displayMessage = string.Empty;
					break;

				case StatusOperationResult.TargetPathNotFound:
					// We can be checking moved-to path, that shouldn't exist, so this is normal.
					//displayMessage = "Target file/folder not found.";
					displayMessage = string.Empty;
					break;

				case StatusOperationResult.AuthenticationFailed:
					displayMessage = $"Git Error: Trying to reach remote server failed because authentication is needed!\nGo to the WiseGit preferences to do this:\"{GitPreferencesWindow.PROJECT_PREFERENCES_MENU}\"\nTo have working online features authenticate your git once via CLI.";
					break;

				case StatusOperationResult.UnableToConnectError:
					displayMessage = "Git Error: Unable to connect to git remote server. Check your network connection. Overlay icons may not work correctly.";
					break;

				case StatusOperationResult.OldUnsupportedGitVersion:
					displayMessage = "Git Error: Your git version is too old. Please update to the latest version.";
					break;

				case StatusOperationResult.BadLFSSupport:
					displayMessage = "Git Error: LFS (Large File Support) extension is missing or outdated. Please install the latest LFS extension.";

#if UNITY_EDITOR_OSX
					// LFS installed but Unity doesn't find it: https://github.com/sublimehq/sublime_merge/issues/1438#issuecomment-1621436375
					// Also this: https://medium.com/@harendraprasadtest/jenkins-does-not-recognise-git-lfs-on-mac-error-git-lfs-is-not-a-git-command-9bfbda030c3e
					// DEPRECATED: needed locations are now added to the PATH environment variable at CheckGitSupport().
					//displayMessage += "\nIf LFS is installed, but you still get this error it means 'git-lfs' executable is not in the same directory as the 'git' executable\n" +
					//    "Run 'git --exec-path' and 'where git-lfs' in the terminal to see their locations.\n" +
					//    "Run this to make a git-lfs link at the git location:\n" +
					//	"> sudo ln -s \"$(which git-lfs)\" \"$(git --exec-path)/git-lfs\"\n" +
					//	"If this doesn't work try copying it instead of making a link, but remember to update it with the original.";
#endif
					break;

				case StatusOperationResult.ExecutableNotFound:
					string userPath = m_PersonalPrefs.GitCLIPath;

					if (string.IsNullOrWhiteSpace(userPath)) {
						userPath = m_ProjectPrefs.PlatformGitCLIPath;
					}

					if (string.IsNullOrEmpty(userPath)) {
						displayMessage = $"Git CLI (Command Line Interface) not found by WiseGit. " +
							$"Please install it or specify path to a valid \"git\" executable in the WiseGit preferences at \"{GitPreferencesWindow.PROJECT_PREFERENCES_MENU}\"\n" +
							$"You can also disable permanently the git integration.";
					} else {
						displayMessage = $"Cannot find the \"git\" executable specified in the git preferences:\n\"{userPath}\"\n\n" +
							$"You can reconfigure it in the menu:\n\"{GitPreferencesWindow.PROJECT_PREFERENCES_MENU}\"\n\n" +
							$"You can also disable the git integration.";
					}
					break;

				default:
					displayMessage = $"Git \"{result}\" error occurred while processing the assets. Check the logs for more info.";
					break;
			}

			if (!string.IsNullOrEmpty(displayMessage) && !Silent && m_LastDisplayedError != displayMessage) {
				Debug.LogError($"{displayMessage} {suffix}\n");
				m_LastDisplayedError = displayMessage;
				//DisplayError(displayMessage);	// Not thread-safe.
			}
		}

		private static IEnumerable<GitStatusData> ExtractStatuses(string output)
		{
			var lines = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

			for(int lineIndex = 0; lineIndex < lines.Length; ++lineIndex) {
				string line = lines[lineIndex];

				// TODO: Test with submodules
				// All externals append separate sections with their statuses:
				// Performing status on external item at '...':
				//if (line.StartsWith("Performing status", StringComparison.Ordinal))
				//	continue;

				// Rules are described in "git status -h".
				var statusData = new GitStatusData();
				statusData.Path = line.Substring(3);

				// 1st is staging/index, 2nd char is working tree/files. Prefer the working status always.
				// Note that you can have RM, AM, AD, etc...
				// Any 'U' means conflict.
				char statusChar = line[1] == ' ' ? line[0] : line[1];
				bool isRenamed = line[0] == 'R' || line[1] == 'R';
				bool isConflict = line[0] == 'U' || line[1] == 'U' || line.StartsWith("DD") || line.StartsWith("AA");
				if (isConflict) {
					statusData.Status = VCFileStatus.Conflicted;
				} else {
					if (!m_FileStatusMap.TryGetValue(statusChar, out statusData.Status)) {
						// Print lines instead of output, as output has '\0' chars that breaks the logging.
						throw new KeyNotFoundException($"Unknown status {statusChar}, line {lineIndex}:\n{string.Join('\n', lines)}");
					}
				}

				// Locks are handled elsewhere.
				statusData.LockStatus = VCLockStatus.NoLock;
				statusData.LockDetails = LockDetails.Empty;

				// Status is renamed - next line tells us from where.
				if (isRenamed) {
					statusData.MovedFrom = lines[lineIndex + 1];
					lineIndex++;
				}

				if (IsHiddenPath(statusData.Path))
					continue;

				yield return statusData;
			}
		}


		private static string GitFormatPath(string path)
		{
			// TODO: This method made sence for SVN, not sure about git.
			return path;
		}

		private static string GitPathspecs(IEnumerable<string> pathspecs)
		{
			return string.Join(" ", pathspecs.Select(p => '"' + p + '"').Select(GitFormatPath));
		}

		private static string FilterOutLines(string str, params string[] excluded)
		{
			return string.Join('\n', str.Split('\n').Where(line => !excluded.Any(ex => line.Contains(ex, StringComparison.OrdinalIgnoreCase))));
		}

		private static IEnumerable<string> Enumerate(string str)
		{
			yield return str;
		}

		private static void DisplayError(string message)
		{
			EditorApplication.update -= DisplayPendingMessages;
			EditorApplication.update += DisplayPendingMessages;

			m_PendingErrorMessages.Add(message);
		}

		private static void DisplayPendingMessages()
		{
			EditorApplication.update -= DisplayPendingMessages;

			var message = string.Join("\n-----\n", m_PendingErrorMessages);

			if (message.Length > 1500) {
				message = message.Substring(0, 1490) + "...";
			}

#if UNITY_2019_4_OR_NEWER
			EditorUtility.DisplayDialog("Git Error", message, "I will!", DialogOptOutDecisionType.ForThisSession, "WiseGit.ErrorMessages");
#else
			EditorUtility.DisplayDialog("Git Error", message, "I will!");
#endif
		}

		// Use for debug.
		//[MenuItem("Assets/Git/Selected Status", false, 200)]
		private static void StatusSelected()
		{
			if (Selection.assetGUIDs.Length == 0)
				return;

			var path = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs.FirstOrDefault());

			var result = ShellUtils.ExecuteCommand(Git_Command, $"status --porcelain -z \"{GitFormatPath(path)}\"");
			if (!string.IsNullOrEmpty(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
				return;
			}

			Debug.Log($"Status for {path}\n{(string.IsNullOrEmpty(result.Output) ? "No Changes" : result.Output)}", Selection.activeObject);
		}
	}
}

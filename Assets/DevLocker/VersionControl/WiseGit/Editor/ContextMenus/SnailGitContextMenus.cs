// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.Shell;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.ContextMenus.Implementation
{
	// SnailGit: https://langui.net/snailgit/
	// Use the "/Applications/SnailGit.app/Contents/Resources/snailgit.sh" executable as much as possible.
	// usage: /Applications/SnailGitLite.app/Contents/Resources/snailgit.sh <subcommand> [args]
	internal class SnailGitContextMenus : GitContextMenusBase
	{
		private const string ClientLightCommand = "/Applications/SnailGitLite.app/Contents/Resources/snailgit.sh";
		private const string ClientPremiumCommand = "/Applications/SnailGit.app/Contents/Resources/snailgit.sh";

		private const string ClientLightProtocol = "snailgitfree";
		private const string ClientPremiumProtocol = "snailgit";

		protected override string FileArgumentsSeparator => " ";
		protected override bool FileArgumentsSurroundQuotes => true;

		private ShellUtils.ShellResult ExecuteCommand(string command, string workingFolder, bool waitForOutput)
		{
			return ExecuteCommand(command, string.Empty, workingFolder, waitForOutput);
		}

		private ShellUtils.ShellResult ExecuteCommand(string command, string fileArgument, string workingFolder, bool waitForOutput)
		{
			return ShellUtils.ExecuteCommand(new ShellUtils.ShellArgs() {
				Command = File.Exists(ClientPremiumCommand) ? ClientPremiumCommand : ClientLightCommand,
				Args = string.IsNullOrEmpty(fileArgument) ? command : $"{command} {fileArgument}",
				WorkingDirectory = workingFolder,
				WaitForOutput = waitForOutput,
				WaitTimeout = -1,
			});
		}

		private void ExecuteProtocol(string action, string workingFolder)
        {
			if (!workingFolder.StartsWith('/')) {
				workingFolder = Path.Combine(WiseGitIntegration.ProjectRootNative, workingFolder);
            }

			// The snailgit.sh currently doesn't accept somne actions, but with some reverse engineering, managed to make it work like this.
			// open "snailgitfree://git-merge/SomeFolderHere/UnityProject/Assets"
			string url = $"{GetClientProtocol()}://{action}{System.Uri.EscapeUriString(workingFolder)}";
			Application.OpenURL(url);
		}

		private void ExecuteProtocol(string action, string workingFolder, string path)
		{
			if (!workingFolder.StartsWith('/')) {
				workingFolder = Path.Combine(WiseGitIntegration.ProjectRootNative, workingFolder);
			}
			if (!path.StartsWith('/')) {
				path = Path.Combine(WiseGitIntegration.ProjectRootNative, path);
			}

			// The snailgit.sh currently doesn't accept somne actions, but with some reverse engineering, managed to make it work like this.
			// open "snailgitfree://git-merge/SomeFolderHere/UnityProject/Assets"
			string url = $"{GetClientProtocol()}://{action}{System.Uri.EscapeUriString(workingFolder)}?{System.Uri.EscapeUriString(path)}";
			Application.OpenURL(url);
		}

		private string GetClientProtocol() => File.Exists(ClientPremiumCommand) ? ClientPremiumProtocol : ClientLightProtocol;

		public override void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (assetPaths.Count() == 1 && assetPaths.All(File.Exists)) {
				DiffChanges(assetPaths.First(), wait);
				return;
            }

			// Doesn't support cleanup command (doesn't seem to have such a window?)
			UnityEditor.EditorUtility.DisplayDialog("Not supported", "Sorry, check changes functionality is currently not supported by SnailGit.", "Sad");
		}

		public override void DiffChanges(string assetPath, bool wait = false)
		{
			var result = ExecuteCommand("diff", Path.Combine(WiseGitIntegration.ProjectRootNative, assetPath), WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Pull(bool wait = false)
		{
			var result = ExecuteCommand("pull", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Merge(bool wait = false)
		{
			ExecuteProtocol("git-merge", WiseGitIntegration.ProjectRootNative);
		}

		public override void Fetch(bool wait = false)
		{
			var result = ExecuteCommand("fetch", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Push(bool wait = false)
		{
			var result = ExecuteCommand("push", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Commit(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			// If trying to commit directory that was just added, show the parent, so the directory meta is visible. Exclude root "Assets" and "Packages" folder.
			var fixedPaths = assetPaths.Select(path =>
					(path.Contains('/') && Directory.Exists(path) && WiseGitIntegration.GetStatus(path).Status == VCFileStatus.Added)
					? Path.GetDirectoryName(path)
					: path
					);

			var result = ExecuteCommand("commit", GetWorkingPath(assetPaths), wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Add(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			foreach (var path in assetPaths) {
				if (!WiseGitIntegration.CheckAndAddParentFolderIfNeeded(path, true))
					return;
			}

			var metas = assetPaths
				.Select(path => path + ".meta")
				;

			var pathsArg = AssetPathsToContextPaths(includeMeta ? assetPaths.Concat(metas) : assetPaths, false);

			var result = ExecuteCommand("add", pathsArg, WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			} else {
				// No window is shown, add happens instantly.
				GitStatusesDatabase.Instance.InvalidateDatabase();
			}
		}

		public override void Revert(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			var metas = assetPaths
				.Select(path => path + ".meta")
				;

			var pathsArg = AssetPathsToContextPaths(includeMeta ? assetPaths.Concat(metas) : assetPaths, false);

			var result = ExecuteCommand("revert", pathsArg, WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ResolveAll(bool wait = false)
		{
			ExecuteProtocol("git-resolve", WiseGitIntegration.ProjectRootNative);
		}

		public override void Resolve(string assetPath, bool wait = false)
		{
			// Doesn't support resolve command (doesn't seem to have such a window?)
			UnityEditor.EditorUtility.DisplayDialog("Not supported", "Sorry, resolve functionality is currently not supported by SnailGit.", "Sad");
		}


		public override void GetLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			if (assetPaths.Any(Directory.Exists)) {
				UnityEditor.EditorUtility.DisplayDialog("Cannot Lock Directories", "Directory locking is not supported. Please select specific files instead.", "Ok");
				return;
			}

			// Doesn't support locks (doesn't seem to have such a window?)
			using (var reporter = new WiseGitIntegration.ResultConsoleReporter(true, WiseGitIntegration.Silent, "SnailGitContextMenus Operations:")) {
				var result = WiseGitIntegration.LockFiles(assetPaths, false, WiseGitIntegration.ONLINE_COMMAND_TIMEOUT, reporter);
				if (result != LockOperationResult.Success) {
					Debug.LogError($"Git Error: {result}");
				} else {
					GitStatusesDatabase.Instance.InvalidateDatabase();
				}
			}
		}

		public override void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			if (assetPaths.Any(Directory.Exists)) {
				UnityEditor.EditorUtility.DisplayDialog("Cannot Lock Directories", "Directory locking is not supported. Please select specific files instead.", "Ok");
				return;
			}

			// Doesn't support locks (doesn't seem to have such a window?)
			using (var reporter = new WiseGitIntegration.ResultConsoleReporter(true, WiseGitIntegration.Silent, "SnailGitContextMenus Operations:")) {
				var result = WiseGitIntegration.UnlockFiles(assetPaths, false, WiseGitIntegration.ONLINE_COMMAND_TIMEOUT, reporter);
				if (result != LockOperationResult.Success) {
					Debug.LogError($"Git Error: {result}");
				} else {
					GitStatusesDatabase.Instance.InvalidateDatabase();
				}
			}
		}

		public override void ShowLog(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			var workingFolder = Directory.Exists(assetPath) ? assetPath : Path.GetDirectoryName(assetPath);

			ExecuteProtocol("git-log", workingFolder, assetPath);
		}

		public override void Blame(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			// Support only one file.

			var result = ExecuteCommand("blame", assetPath, WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Cleanup(bool wait = false)
		{
			// Doesn't support cleanup command (doesn't seem to have such a window?)
			UnityEditor.EditorUtility.DisplayDialog("Not supported", "Sorry, clean up functionality is currently not supported by SnailGit.", "Sad");
		}

		public override void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			var workingFolder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

			ExecuteProtocol("repo-browser", workingFolder);
		}

		public override void Switch(bool wait = false)
		{
			var result = ExecuteCommand("checkout", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}
	}
}

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
	// Available subcommands:
	// add, checkout (co), cleanup, commit (ci), delete (del, remove, rm), diff (di), export,
	// help (?, h), ignore, import, info, lock, log, merge, relocate, repo-browser (rb),
	// revert, switch, unlock, update (up)
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

		private string GetClientProtocol() => File.Exists(ClientPremiumCommand) ? ClientPremiumProtocol : ClientLightProtocol;

		public override void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			var path = GetWorkingPath(assetPaths);
			if (string.IsNullOrEmpty(path))
				return;

			// The snailgit.sh currently doesn't accept "check-for-modifications" argument, but with some reverse engineering, managed to make it work like this.
			// open "snailgitfree://check-for-modifications/SomeFolderHere/UnityProject/Assets"
			string url = $"{GetClientProtocol()}://check-for-modifications{System.Uri.EscapeUriString(Path.Combine(WiseGitIntegration.ProjectRootNative, path))}";
			Application.OpenURL(url);
		}

		public override void DiffChanges(string assetPath, bool wait = false)
		{
			CheckChanges(new string[] { assetPath }, false, wait);
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
			var result = ExecuteCommand("merge", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
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
			}
		}

		public override void Revert(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			// If trying to revert directory that was just added, show the parent, so the directory meta is visible. Exclude root "Assets" and "Packages" folder.
			var fixedPaths = assetPaths.Select(path =>
					(path.Contains('/') && Directory.Exists(path) && WiseGitIntegration.GetStatus(path).Status == VCFileStatus.Added)
					? Path.GetDirectoryName(path)
					: path
					);

			var result = ExecuteCommand("revert", GetWorkingPath(assetPaths), wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ResolveAll(bool wait = false)
		{
			// Doesn't support resolve command (doesn't seem to have such a window?)
			UnityEditor.EditorUtility.DisplayDialog("Not supported", "Sorry, resolve all functionality is currently not supported by SnailGit.", "Sad");
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

			var result = ExecuteCommand("lock", AssetPathsToContextPaths(assetPaths, includeMeta), WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			var result = ExecuteCommand("unlock", AssetPathsToContextPaths(assetPaths, includeMeta), WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ShowLog(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			var pathArg = Directory.Exists(assetPath) ? assetPath : Path.GetDirectoryName(assetPath);

			var result = ExecuteCommand("log", pathArg, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
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
			// NOTE: SnailGit doesn't pop up dialog for clean up. It just does some shady stuff in the background and a notification is shown some time later.
			var result = ExecuteCommand("cleanup", WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			// SnailGit Repo-Browser doesn't accept URLs, only working copy paths which is no good for us.
			Debug.LogError($"SnailGit doesn't support Repo-Browser very well. Opening Repo-Browser for the current working copy.");

			var result = ExecuteCommand("repo-browser", string.Empty, WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Switch(bool wait = false)
		{
			var result = ExecuteCommand("switch", string.Empty, WiseGitIntegration.ProjectRootNative, wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}
	}
}

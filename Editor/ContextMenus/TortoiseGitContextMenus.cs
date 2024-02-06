// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.Shell;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.ContextMenus.Implementation
{
#if UNITY_EDITOR_WIN
	// TortoiseGit Commands: https://tortoisegit.org/docs/tortoisegit/tgit-automation.html
	internal class TortoiseGitContextMenus : GitContextMenusBase
	{
		private const string ClientCommand = "TortoiseGitProc.exe";

		protected override string FileArgumentsSeparator => "*";
		protected override bool FileArgumentsSurroundQuotes => false;

		public override void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:repostatus /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void DiffChanges(string assetPath, bool wait = false)
		{
			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:diff /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Pull(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:pull /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			// Cancel produces error code -1. Ignore.
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Merge(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:merge /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			// Cancel produces error code -1. Ignore.
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Fetch(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:fetch /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			// Cancel produces error code -1. Ignore.
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Push(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:push /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			// Cancel produces error code -1. Ignore.
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Commit(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:commit /path:\"{pathsArg}\"", wait);
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

			var metas = assetPaths.Select(path => path + ".meta");

			string pathsArg = AssetPathsToContextPaths(includeMeta ? assetPaths.Concat(metas) : assetPaths, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:add /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Revert(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:revert /path:\"{pathsArg}\"", wait);
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}



		public override void ResolveAll(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:resolve /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Resolve(string assetPath, bool wait = false)
		{
			if (System.IO.Directory.Exists(assetPath)) {
				var resolveResult = ShellUtils.ExecuteCommand(ClientCommand, $"/command:resolve /path:\"{AssetPathToContextPaths(assetPath, false)}\"", wait);
				if (!string.IsNullOrEmpty(resolveResult.Error)) {
					Debug.LogError($"Git Error: {resolveResult.Error}");
				}

				return;
			}

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:conflicteditor /path:\"{AssetPathToContextPaths(assetPath, false)}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}



		public override void GetLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			// https://tortoisegit.org/docs/tortoisegit/tgit-dug-lfslocking.html

			if (!assetPaths.Any())
				return;

			if (includeMeta) {
				assetPaths = assetPaths.Concat(assetPaths.Select(p => p + ".meta"));
			}

			// TortoiseGit doesn't offer interactive dialog on what to lock - it locks directly. No API to call. So call them with our implementation.
			if (wait) {
				using (var reporter = new WiseGitIntegration.ResultConsoleReporter(true, WiseGitIntegration.Silent, "Git Operations:")) {
					WiseGitIntegration.LockFiles(assetPaths, false, WiseGitIntegration.ONLINE_COMMAND_TIMEOUT, reporter);
				}

			} else {
				WiseGitIntegration.LockFilesAsync(assetPaths.ToList(), false, WiseGitIntegration.ONLINE_COMMAND_TIMEOUT)
					.Completed += (op) => {
						if (op.Result == LockOperationResult.Success) {
							Debug.Log("Git Operations:\n" + op.FinishedCombined);
						} else {
							Debug.LogError("Git Operations:\n" + op.FinishedCombined);
						}
					};
			}
		}

		public override void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			// https://tortoisegit.org/docs/tortoisegit/tgit-dug-lfslocking.html

			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			// TortoiseGit doesn't offer proper unlock dialog - lfslocks shows locks for the whole repository.
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:lfslocks /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ShowLog(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:log /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}



		public override void Blame(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:blame /path:\"{pathsArg}\"", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}



		public override void Cleanup(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:cleanup /path:\"{WiseGitIntegration.ProjectRootNative}\"", wait);
			// Cancel produces error code -1. Ignore.
			if (result.HasErrors && result.ErrorCode != -1) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}


		public override void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			if (string.IsNullOrEmpty(path))
				return;

			// Path is not used, sad :(
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:repobrowser /rev:refs/remotes/{remoteBranch}", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Switch(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"/command:switch", wait);
			if (result.HasErrors) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}
	}
#endif
}

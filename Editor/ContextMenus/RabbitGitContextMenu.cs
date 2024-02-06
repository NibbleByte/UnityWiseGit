// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.Shell;
using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.ContextMenus.Implementation
{
	// RabbitVCS subcommand (or module by their wording) list can be accessed by executing `rabbitvcs`
	// command in terminal, and the subcommand usage reference by `rabbitvcs <module> -h`. Source code
	// is available at https://github.com/rabbitvcs/rabbitvcs .
	internal class RabbitGitContextMenu : GitContextMenusBase
	{
		private const string ClientCommand = "rabbitvcs";

		protected override string FileArgumentsSeparator => " ";
		protected override bool FileArgumentsSurroundQuotes => true;

		public override void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"changes {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void DiffChanges(string assetPath, bool wait = false)
		{
			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"diff -s {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Pull(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"pull {WiseGitIntegration.ProjectRootNative}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Merge(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"merge {WiseGitIntegration.ProjectRootNative}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Fetch(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"fetch {WiseGitIntegration.ProjectRootNative}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Push(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"push {WiseGitIntegration.ProjectRootNative}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
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

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"commit {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
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
				.Where(path => WiseGitIntegration.GetStatus(path).Status == VCFileStatus.Unversioned)
				;

			string pathsArg = AssetPathsToContextPaths(includeMeta ? assetPaths.Concat(metas) : assetPaths, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"add {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
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

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"revert {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ResolveAll(bool wait = false)
		{
			UnityEditor.EditorUtility.DisplayDialog("Not Supported", "RabbitVCS does not support Resolve All yet.", "OK");
		}

		public override void Resolve(string assetPath, bool wait = false)
		{
			UnityEditor.EditorUtility.DisplayDialog("Not Supported", "RabbitVCS does not support Resolve yet.", "OK");
		}

		public override void GetLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"lock {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"unlock {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
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

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"log {pathsArg}", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Blame(string assetPath, bool wait = false)
		{
			UnityEditor.EditorUtility.DisplayDialog("Not Supported", "RabbitVCS does not support Blame function yet.", "OK");
		}

		public override void Cleanup(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"cleanup \"{WiseGitIntegration.ProjectRootNative}\"", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			if (string.IsNullOrEmpty(path))
				return;

			var result = ShellUtils.ExecuteCommand(ClientCommand, $"browser \"{remoteBranch}\" \"{path}\"", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
		}

		public override void Switch(bool wait = false)
		{
			var result = ShellUtils.ExecuteCommand(ClientCommand, $"switch", wait);
			if (MayHaveRabbitVCSError(result.Error)) {
				Debug.LogError($"Git Error: {result.Error}");
			}
			return;
		}

		private string[] possibleRvcsErrorString = new[]{
			"Exception: ",
			"Error: "
		};

		/// <summary>
		///	Gtk most of the time dumps warning and possibly non-error messages to stderr, thus making
		/// current implementation of error reporting reports those warnings even thought the app
		/// is completing it's task just fine.
		///
		/// But luckily RabbitVCS have python backend, any exception should show up in stderr as
		/// Python-style error that can be filtered.
		/// </summary>
		private bool MayHaveRabbitVCSError(string src){
			if(string.IsNullOrWhiteSpace(src)) return false;
			foreach(string str in possibleRvcsErrorString){
				if(src.IndexOf(str, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}
	}
}

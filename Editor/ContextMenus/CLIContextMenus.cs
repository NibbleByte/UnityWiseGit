// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.ContextMenus.Implementation
{
	/// <summary>
	/// Fall-back context menus that pop an editor window with command to be executed. User can modify the command and see the output.
	/// </summary>
	internal class CLIContextMenus : GitContextMenusBase
	{
		protected override string FileArgumentsSeparator => "\n";
		protected override bool FileArgumentsSurroundQuotes => true;

		public override void CheckChanges(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"diff \n{pathsArg}", true);
		}

		public override void DiffChanges(string assetPath, bool wait = false)
		{
			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"diff \n{pathsArg}", true);
		}

		public override void Pull(bool wait = false)
		{
			CLIContextWindow.Show($"pull", false);
		}

		public override void Merge(bool wait = false)
		{
			CLIContextWindow.Show($"merge", false);
		}

		public override void Fetch(bool wait = false)
		{
			CLIContextWindow.Show($"fetch", true);
		}

		public override void Push(bool wait = false)
		{
			CLIContextWindow.Show($"push", true);
		}

		public override void Commit(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			if (assetPaths.All(p => Directory.Exists(p))) {
				CLIContextWindow.Show($"commit --message \"\"", false);
				return;
			}

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"commit --message \"\"\n{pathsArg}", false);
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

			string pathsArg = AssetPathsToContextPaths(includeMeta ? assetPaths.Concat(metas) : assetPaths, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"add\n{pathsArg}", true);
		}

		public override void Revert(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"checkout --\n{pathsArg}", false);
		}



		public override void ResolveAll(bool wait = false)
		{
			CLIContextWindow.Show($"diff\n", false);
		}

		public override void Resolve(string assetPath, bool wait = false)
		{
			DiffChanges(assetPath, wait);
		}



		public override void GetLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"lfs lock\n{pathsArg}", true);
		}

		public override void ReleaseLocks(IEnumerable<string> assetPaths, bool includeMeta, bool wait = false)
		{
			if (!assetPaths.Any())
				return;

			string pathsArg = AssetPathsToContextPaths(assetPaths, includeMeta);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"lfs unlock\n{pathsArg}", true);
		}

		public override void ShowLog(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"log \n{pathsArg}", true);
		}



		public override void Blame(string assetPath, bool wait = false)
		{
			if (string.IsNullOrEmpty(assetPath))
				return;

			string pathsArg = AssetPathToContextPaths(assetPath, false);
			if (string.IsNullOrEmpty(pathsArg))
				return;

			CLIContextWindow.Show($"blame \n{pathsArg}", true);
		}



		public override void Cleanup(bool wait = false)
		{
			CLIContextWindow.Show($"cleanup", true);
		}


		public override void RepoBrowser(string path, string remoteBranch, bool wait = false)
		{
			if (string.IsNullOrEmpty(path))
				return;

			CLIContextWindow.Show($"ls-tree --full-name --name-only -r {remoteBranch}", true);
		}

		public override void Switch(bool wait = false)
		{
			CLIContextWindow.Show($"switch", false);
		}
	}
}

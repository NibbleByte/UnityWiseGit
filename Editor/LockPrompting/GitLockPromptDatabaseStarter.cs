// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.Preferences;
using UnityEditor;

namespace DevLocker.VersionControl.WiseGit.LockPrompting
{
	/// <summary>
	/// Starts the database if enabled.
	/// </summary>
	[InitializeOnLoad]
	internal static class GitLockPromptDatabaseStarter
	{
		// HACK: If this was the GitAutoLockingDatabase itself it causes exceptions on assembly reload.
		//		 The static constructor gets called during reload because the instance exists.
		static GitLockPromptDatabaseStarter()
		{
			TryStartIfNeeded();
		}

		internal static void TryStartIfNeeded()
		{
			var playerPrefs = GitPreferencesManager.Instance.PersonalPrefs;
			var projectPrefs = GitPreferencesManager.Instance.ProjectPrefs;

			// HACK: Just touch the GitAutoLockingDatabase instance to initialize it.
			if (playerPrefs.EnableCoreIntegration && projectPrefs.EnableLockPrompt && GitLockPromptDatabase.Instance.IsActive)
				return;
		}
	}
}

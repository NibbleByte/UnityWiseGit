// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using System;
using System.Linq;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit
{
	public enum VCFileStatus
	{
		Normal,
		Added,
		Conflicted,
		Deleted,
		Ignored,
		Modified,
		Replaced,
		Unversioned,
		Missing,
		External,
		Incomplete,	// Not used
		Merged, 	// Not used
		Obstructed,
		ReadOnly,
		Excluded,	// Used for excluded by WiseGit preference folders / assets
		None,	// File not found or something worse....
	}

	public enum VCLockStatus
	{
		NoLock,
		LockedHere,
		LockedOther,
		LockedButStolen,
		BrokenLock
	}

	public enum VCRemoteFileStatus
	{
		None,
		Modified,
	}

	[Flags]
	public enum GitTraceLogs
	{
		None = 0,
		GitOperations = 1 << 0,
		DatabaseUpdates = 1 << 4,
		All = ~0,
	}

	public enum LockOperationResult
	{
		Success = 0,				// Operation succeeded.

		RemoteHasChanges,			// NOT SUPPORTED ON LFS FOR NOW!!! Newer version of the asset exists in the server repository. Update first.
		LockAlreadyExists,			// File is already locked by THIS or another working copy (can be the same user). Use Force to enforce the operation.
		BlockedByUncommittedChanges, // Cannot unlock file with uncommitted changes (as it wants to make the file read-only). Use force.
		InsufficientPrivileges,		// User has insufficient privileges (probably needs to be admin/owner).
		DirectoryLockNotSupported,	// Locking directories is not supported.

		// Copy-pasted from StatusOperationResult
		AuthenticationFailed = 50,  // User needs to log in using normal git client and save their authentication.
		UnableToConnectError = 55,  // Unable to connect to repository indicating some network or server problems.
		NotWorkingCopy = 60,        // This can be returned when project is not a valid git checkout. (Probably)
		ExecutableNotFound = 65,    // Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
		TargetPathNotFound = 70,    // File or directory not found on disk.
		BadLFSSupport = 75,         // LFS is not installed, not configured properly or an old version is used.
		Timeout = 90,               // Operation timed out.
		UnknownError = 100,         // Failed for any other reason.
	}

	public enum ListOperationResult
	{
		Success = 0,			// Operation succeeded.
		NotFound,               // URL target was not found.

		// Copy-pasted from StatusOperationResult
		AuthenticationFailed = 50,  // User needs to log in using normal git client and save their authentication.
		UnableToConnectError = 55,  // Unable to connect to repository indicating some network or server problems.
		NotWorkingCopy = 60,        // This can be returned when project is not a valid git checkout. (Probably)
		ExecutableNotFound = 65,    // Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
		TargetPathNotFound = 70,    // File or directory not found on disk.
		BadLFSSupport = 75,         // LFS is not installed, not configured properly or an old version is used.
		Timeout = 90,               // Operation timed out.
		UnknownError = 100,         // Failed for any other reason.
	}

	public enum PushOperationResult
	{
		Success = 0,				// Operation succeeded.

		// Commit
		ConflictsError,				// Some folders/files have conflicts. Clear them before trying to commit.
		NoPartialCommitsInMerge,	// Cannot do partial commits during merge. Do the special merge commit with staged changes first.
		UnversionedError,			// Can't commit unversioned files directly. Add them before trying to commit.
		MessageIsEmpty,				// Commit message is needed.
		NoChangesToCommit,			// No changes to commit.

		// Push
		RejectedByRemote,			// Remote has changes. You need to pull first.
		RemoteNotFound,				// Specified remote was not found.
		BranchNotFound,				// Specified branch was not found.

		// Copy-pasted from StatusOperationResult
		AuthenticationFailed = 50,	// User needs to log in using normal git client and save their authentication.
		UnableToConnectError = 55,	// Unable to connect to repository indicating some network or server problems.
		NotWorkingCopy = 60,		// This can be returned when project is not a valid git checkout. (Probably)
		ExecutableNotFound = 65,	// Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
		TargetPathNotFound = 70,	// File or directory not found on disk.
		BadLFSSupport = 75,			// LFS is not installed, not configured properly or an old version is used.
		Timeout = 90,				// Operation timed out.
		UnknownError = 100,			// Failed for any other reason.
	}

	public enum RevertOperationResult
	{
		Success = 0,			// Operation succeeded.
		Timeout = 90,			// Operation timed out.
		UnknownError = 100,		// Failed for any other reason.
	}

	public enum PullOperationResult
	{
		Success = 0,				// Operation succeeded.
		SuccessWithConflicts,		// Update was successful, but some folders/files have conflicts.

		RemoteNotFound,				// Specified remote was not found.
		BranchNotFound,				// Specified branch was not found.
		LocalChangesFound,			// Local changes prevent us from merging / pulling.

		// Copy-pasted from StatusOperationResult
		AuthenticationFailed = 50,  // User needs to log in using normal git client and save their authentication.
		UnableToConnectError = 55,  // Unable to connect to repository indicating some network or server problems.
		NotWorkingCopy = 60,        // This can be returned when project is not a valid git checkout. (Probably)
		ExecutableNotFound = 65,    // Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
		TargetPathNotFound = 70,    // File or directory not found on disk.
		BadLFSSupport = 75,         // LFS is not installed, not configured properly or an old version is used.
		Timeout = 90,               // Operation timed out.
		UnknownError = 100,         // Failed for any other reason.
	}

	// NOTE: This enum is copy-pasted inside other enums so they "inherit" from it. Keep them synched.
	public enum StatusOperationResult
	{
		Success = 0,				// Operation succeeded.
		AuthenticationFailed = 50,  // User needs to log in using normal git client and save their authentication.
		UnableToConnectError = 55,  // Unable to connect to repository indicating some network or server problems.
		NotWorkingCopy = 60,        // This can be returned when project is not a valid git checkout. (Probably)
		ExecutableNotFound = 65,    // Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "git.exe" in the PATH environment.
		TargetPathNotFound = 70,	// File or directory not found on disk.
		OldUnsupportedGitVersion = 75, // Git version is too old and doesn't support some features. Try updating git.
		BadLFSSupport = 78,			// LFS is not installed, not configured properly or an old version is used.
		Timeout = 90,				// Operation timed out.
		UnknownError = 100,			// Failed for any other reason.
	}


	/// <summary>
	/// Data containing all git status knowledge about file.
	/// </summary>
	[Serializable]
	public struct GitStatusData
	{
		public VCFileStatus Status;
		public VCLockStatus LockStatus;
		public VCRemoteFileStatus RemoteStatus;

		public string Path;

		public string MovedTo;		// Displays where asset was moved to. Should have Deleted status.
		public string MovedFrom;    // Displays where asset was moved from. Should have Added status.
		public bool IsMovedFile => !string.IsNullOrEmpty(MovedTo) || !string.IsNullOrEmpty(MovedFrom);

		public LockDetails LockDetails;

		public bool IsValid => !string.IsNullOrEmpty(Path);

		public bool IsConflicted =>	Status == VCFileStatus.Conflicted;

		public bool EqualStatuses(GitStatusData other, bool skipOnline)
		{
			return Status == other.Status
				&& (skipOnline || LockStatus == other.LockStatus)
				&& (skipOnline || RemoteStatus == other.RemoteStatus)
				&& (skipOnline || LockDetails.Equals(other.LockDetails))
				;
		}

		public override string ToString()
		{
			return $"{Status.ToString()[0]} {Path}";
		}
	}

	/// <summary>
	/// Data containing git lock details.
	/// </summary>
	[Serializable]
	public struct LockDetails
	{
		public string Owner;
		//public string Message;
		public string Date;

		public bool IsValid => !string.IsNullOrEmpty(Date);

		public static LockDetails Empty => new LockDetails() { Owner = string.Empty, /*Message = string.Empty, */Date = string.Empty};

		public bool Equals(LockDetails other)
		{
			return Owner == other.Owner
				   //&& Message == other.Message
				   && Date == other.Date
				;
		}
	}

	[Flags]
	public enum AssetType
	{
		Scene = 1 << 3,				// t:Scene
		TerrainData = 1 << 4,		// t:TerrainData

		Prefab = 1 << 5,			// t:Prefab
		Model = 1 << 6,				// t:Model
		Mesh = 1 << 7,				// t:Mesh
		Material = 1 << 8,			// t:Material
		Texture = 1 << 9,			// t:Texture

		Animation = 1 << 12,		// t:AnimationClip
		Animator = 1 << 13,         // t:AnimatorController, t:AnimatorOverrideController

		Script = 1 << 16,			// t:Script
		UIElementsAssets = 1 << 17,
		Shader = 1 << 18,			// t:Shader
		ScriptableObject = 1 << 19,	// t:ScriptableObject


		Audio = 1 << 22,			// t:AudioClip
		Video = 1 << 24,			// t:VideoClip

		TimeLineAssets = 1 << 28,			// t:TimelineAsset

		// Any type that is not mentioned above.
		OtherTypes = 1 << 31,


		PresetCharacterTypes = Prefab | Model | Mesh | Material | Texture | Animation | Animator,
		PresetLevelDesignerTypes = Scene | TerrainData | Prefab | Model | Mesh | Material | Texture,
		PresetUITypes = Scene | Texture | Prefab | Animator | Animation | Script | UIElementsAssets | ScriptableObject,
		PresetScriptingTypes = Prefab | Script | UIElementsAssets | Shader | ScriptableObject,
	}

	/// <summary>
	/// Rules for lock prompt on asset modification.
	/// </summary>
	[Serializable]
	public struct LockPromptParameters
	{
		[Tooltip("Target folder to monitor for lock prompt, relative to the project.\n\nExample: \"Assets/Scenes\"")]
		public string TargetFolder;

		[Tooltip("Target asset types to monitor for lock prompt")]
		public AssetType TargetTypes;

		[Tooltip("Target metas of selected asset types as well.")]
		public bool IncludeTargetMetas;

#if UNITY_2020_2_OR_NEWER
		// Because it looks ugly, bad indents for some reason.
		[NonReorderable]
#endif
		[Tooltip("Relative path (contains '/') or asset name to be ignored in the Target Folder.\n\nExample: \"Assets/Scenes/Baked\" or \"_deprecated\"")]
		public string[] Exclude;

		public bool IsValid => !string.IsNullOrEmpty(TargetFolder) && TargetTypes != 0;

		public LockPromptParameters Sanitized()
		{
			var clone = (LockPromptParameters)MemberwiseClone();

			clone.TargetFolder = Preferences.GitPreferencesManager.SanitizeUnityPath(TargetFolder);

			clone.Exclude = Exclude
					.Select(Preferences.GitPreferencesManager.SanitizeUnityPath)
					.Where(s => !string.IsNullOrEmpty(s))
					.ToArray()
				;

			return clone;
		}
	}
}

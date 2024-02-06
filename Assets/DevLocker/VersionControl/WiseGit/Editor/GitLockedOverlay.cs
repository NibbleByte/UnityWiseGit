// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using System;
using System.Collections.Generic;
using System.IO;
using DevLocker.VersionControl.WiseGit.Preferences;
using DevLocker.VersionControl.WiseGit.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevLocker.VersionControl.WiseGit
{
	/// <summary>
	/// Renders scene or prefab overlay indicating that the asset is locked or out of date.
	/// </summary>
	class GitLockedOverlay : EditorPersistentSingleton<GitLockedOverlay>
	{
		[InitializeOnLoad]
		class GitLockedOverlayStarter
		{
			// HACK: If this was the GitLockPromptDatabase itself it causes exceptions on assembly reload.
			//		 The static constructor gets called during reload because the instance exists.
			static GitLockedOverlayStarter()
			{
				Instance.PreferencesChanged();
			}
		}

		[SerializeField]
		private string m_SceneMessage;
		[SerializeField]
		private float m_SceneMessageWidth;
		[SerializeField]
		private GUIContent m_SceneMessageIcon;

		[SerializeField]
		private string m_PrefabMessage;
		[SerializeField]
		private float m_PrefabMessageWidth;
		[SerializeField]
		private GUIContent m_PrefabMessageIcon;



		[SerializeField]
		private List<Scene> m_CurrentScenes = new List<Scene>();

		[SerializeField]
		private string m_CurrentPrefabPath = string.Empty;

		[NonSerialized]
		private GUIStyle m_MessageStyle;

		private GitPreferencesManager.PersonalPreferences m_PersonalPrefs => GitPreferencesManager.Instance.PersonalPrefs;

		private bool IsActive => m_PersonalPrefs.EnableCoreIntegration
		                         && m_PersonalPrefs.PopulateStatusesDatabase
		                         && GitPreferencesManager.Instance.FetchRemoteChanges
		                         && !GitPreferencesManager.Instance.NeedsToAuthenticate
								 && m_PersonalPrefs.WarnForPotentialConflicts;

		public override void Initialize(bool freshlyCreated)
		{
			GitPreferencesManager.Instance.PreferencesChanged += PreferencesChanged;
			GitStatusesDatabase.Instance.DatabaseChanged += OnDatabaseChanged;
		}

		private GUIStyle GetMessageStyle()
		{
			if (m_MessageStyle == null) {
				m_MessageStyle = new GUIStyle(GUI.skin.box);
				m_MessageStyle.alignment = TextAnchor.MiddleCenter;
				m_MessageStyle.normal.textColor = Color.white;
				m_MessageStyle.active.textColor = Color.white;
				m_MessageStyle.focused.textColor = Color.white;
				m_MessageStyle.hover.textColor = Color.white;
				m_MessageStyle.contentOffset = new Vector2(0f, -2f);
			}

			return m_MessageStyle;
		}

		private void PreferencesChanged()
		{
			if (IsActive) {
#if UNITY_2019_1_OR_NEWER
				SceneView.duringSceneGui -= SceneViewOnGUI;
				SceneView.duringSceneGui += SceneViewOnGUI;
#else
				SceneView.onSceneGUIDelegate -= SceneViewOnGUI;
				SceneView.onSceneGUIDelegate += SceneViewOnGUI;
#endif
			} else {
#if UNITY_2019_1_OR_NEWER
				SceneView.duringSceneGui -= SceneViewOnGUI;
#else
				SceneView.onSceneGUIDelegate -= SceneViewOnGUI;
#endif
			}

			OnDatabaseChanged();
		}

		private void OnDatabaseChanged()
		{
			EditorApplication.RepaintProjectWindow();
		}

		private void CheckScenes()
		{
			if (m_CurrentScenes.Count != SceneManager.sceneCount) {
				RefreshScenesMessage();
				return;
			}

			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				if (m_CurrentScenes[i].handle != SceneManager.GetSceneAt(i).handle) {
					RefreshScenesMessage();
					return;
				}
			}
		}

		private void RefreshScenesMessage()
		{
			m_CurrentScenes.Clear();
			m_SceneMessage = string.Empty;

			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				Scene scene = SceneManager.GetSceneAt(i);

				m_CurrentScenes.Add(scene);

				if (string.IsNullOrEmpty(scene.path))
					continue;

				var guid = AssetDatabase.AssetPathToGUID(scene.path);
				var statusData = GitStatusesDatabase.Instance.GetKnownStatusData(guid);

				if (statusData.RemoteStatus != VCRemoteFileStatus.None) {
					m_SceneMessage += $"Scene \"{scene.name}\" is out of date in git!\n";
					m_SceneMessageIcon = GitPreferencesManager.Instance.GetRemoteStatusIconContent(VCRemoteFileStatus.Modified);

				} else if (statusData.LockStatus == VCLockStatus.LockedOther || statusData.LockStatus == VCLockStatus.LockedButStolen) {
					m_SceneMessage += $"Scene \"{scene.name}\" is locked by {statusData.LockDetails.Owner} in git!\n";
					m_SceneMessageIcon = GitPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.LockedOther);

				} else if (statusData.LockStatus == VCLockStatus.BrokenLock) {
					m_SceneMessage += $"Scene \"{scene.name}\" lock is broken in git!\n";
					m_SceneMessageIcon = GitPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.BrokenLock);
				}

			}

			m_SceneMessage = m_SceneMessage.TrimEnd('\n');

			m_SceneMessageWidth = GetMessageStyle().CalcSize(new GUIContent(m_SceneMessage)).x;
		}

		private void CheckPrefab()
		{
#if UNITY_2021_3_OR_NEWER
			var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
#else
			var stage = UnityEditor.Experimental.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
#endif

#if UNITY_2020_1_OR_NEWER
			string prefabPath = stage?.assetPath ?? string.Empty;
#else
			string prefabPath = stage?.prefabAssetPath ?? string.Empty;
#endif

			bool prefabIsOpen = !string.IsNullOrEmpty(prefabPath);
			bool prefabWasOpen = !string.IsNullOrEmpty(m_CurrentPrefabPath);

			if (prefabWasOpen != prefabIsOpen) {
				RefreshPrefabMessage(prefabPath);
				return;
			}

			if (prefabIsOpen && m_CurrentPrefabPath != prefabPath) {
				RefreshPrefabMessage(prefabPath);
				return;
			}
		}

		private void RefreshPrefabMessage(string prefabPath)
		{
			m_CurrentPrefabPath = prefabPath;
			m_PrefabMessage = String.Empty;

			if (!string.IsNullOrEmpty(m_CurrentPrefabPath)) {
				var guid = AssetDatabase.AssetPathToGUID(m_CurrentPrefabPath);
				var statusData = GitStatusesDatabase.Instance.GetKnownStatusData(guid);

				if (statusData.RemoteStatus != VCRemoteFileStatus.None) {
					m_PrefabMessage = $"Prefab \"{Path.GetFileNameWithoutExtension(prefabPath)}\" is out of date in git!";
					m_PrefabMessageIcon = GitPreferencesManager.Instance.GetRemoteStatusIconContent(VCRemoteFileStatus.Modified);

				} else if (statusData.LockStatus == VCLockStatus.LockedOther || statusData.LockStatus == VCLockStatus.LockedButStolen) {
					m_PrefabMessage = $"Prefab \"{Path.GetFileNameWithoutExtension(prefabPath)}\" is locked by {statusData.LockDetails.Owner} in git!";
					m_PrefabMessageIcon = GitPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.LockedOther);

				} else if (statusData.LockStatus == VCLockStatus.BrokenLock) {
					m_PrefabMessage = $"Prefab \"{Path.GetFileNameWithoutExtension(prefabPath)}\" lock is broken in git!";
					m_PrefabMessageIcon = GitPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.BrokenLock);
				}

			}

			m_PrefabMessageWidth = GetMessageStyle().CalcSize(new GUIContent(m_PrefabMessage)).x;
		}

		private void SceneViewOnGUI(SceneView sceneView)
		{
			if (Application.isPlaying || !GitStatusesDatabase.Instance.IsReady)
				return;

			Handles.BeginGUI();

			CheckScenes();

			CheckPrefab();


			if (!string.IsNullOrEmpty(m_SceneMessage) && string.IsNullOrEmpty(m_CurrentPrefabPath) || !string.IsNullOrEmpty(m_PrefabMessage)) {
				string targetMessage = string.IsNullOrEmpty(m_PrefabMessage) ? m_SceneMessage : m_PrefabMessage;
				float targetWidth = string.IsNullOrEmpty(m_PrefabMessage) ? m_SceneMessageWidth : m_PrefabMessageWidth;
				GUIContent icon = string.IsNullOrEmpty(m_PrefabMessage) ? m_SceneMessageIcon: m_PrefabMessageIcon;

				float width = Mathf.Max(300, targetWidth + 40f);
				const float height = 70f;

				Rect messageRect = new Rect();
				messageRect.x = sceneView.position.width / 2f - width / 2f;
				messageRect.y = 32;
				messageRect.width = width;
				messageRect.height = height;

				const float closeSize = 18f;
				const float closeOffset = 6f;

				Rect closeRect = new Rect();
				closeRect.x = messageRect.x + messageRect.width - closeSize + closeOffset;
				closeRect.y = messageRect.y - closeOffset;
				closeRect.width = closeRect.height = closeSize;

				Rect iconRect = new Rect();
				iconRect.width = iconRect.height = 40f;
				iconRect.x = messageRect.x + messageRect.width / 2f - iconRect.width / 2f;
				iconRect.y = messageRect.y + messageRect.height - iconRect.height / 2f - 4f;

				var prevBackgroundColor = GUI.backgroundColor;
				GUI.backgroundColor = Color.red;


				GUI.Box(messageRect, targetMessage, GetMessageStyle());
				GUI.Label(iconRect, icon);

				// HACK: the text color of the box is done in the style, because it breaks
				//		 when unity starts and displays it immediately.
				var prevColor = GUI.color;
				GUI.color = Color.white;

				if (GUI.Button(closeRect, "X")) {
					if (string.IsNullOrEmpty(m_PrefabMessage)) {
						m_SceneMessage = string.Empty;
					} else {
						m_PrefabMessage = string.Empty;
					}
				}

				GUI.color = prevColor;
				GUI.backgroundColor = prevBackgroundColor;
			}

			Handles.EndGUI();
		}
	}
}

// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseGit

using DevLocker.VersionControl.WiseGit.ContextMenus;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseGit.Documentation
{
	/// <summary>
	/// This an example window showing how you can integrate your tools with the WiseGit plugin.
	/// When your tool needs to run some git operation it is best to run Async method
	/// and subscribe for the task events to avoid editor freezing.
	/// Those events are guaranteed to run on the Unity thread.
	/// </summary>
	public class ExampleStatusWindow : EditorWindow
	{
		private string m_CombinedOutput = "";
		private string m_StateLabel = "Idle";
		private Vector2 m_OutputScroll;

		private GitAsyncOperation<StatusOperationResult> m_GitOperation;

		//[MenuItem("Assets/Git/Example Status Window")]
		private static void Init()
		{
			var window = (ExampleStatusWindow)GetWindow(typeof(ExampleStatusWindow), false, "Example Git Window");

			window.position = new Rect(window.position.xMin + 100f, window.position.yMin + 100f, 450f, 600f);
			window.minSize = new Vector2(450f, 200f);
		}

		private void OnGUI()
		{
			var outputStyle = new GUIStyle(EditorStyles.textArea);
			outputStyle.wordWrap = false;

			var textSize = outputStyle.CalcSize(new GUIContent(m_CombinedOutput));

			m_OutputScroll = EditorGUILayout.BeginScrollView(m_OutputScroll);
			EditorGUILayout.LabelField(m_CombinedOutput, outputStyle, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true), GUILayout.MinWidth(textSize.x), GUILayout.MinHeight(textSize.y));
			EditorGUILayout.EndScrollView();

			EditorGUILayout.BeginHorizontal();
			{
				bool isWorking = m_GitOperation == null || m_GitOperation.HasFinished;

				EditorGUI.BeginDisabledGroup(isWorking);

				if (GUILayout.Button("Abort")) {
					m_GitOperation.Abort(false);
					m_CombinedOutput += "Aborting...\n";
					m_StateLabel = "Aborting...";
				}

				if (GUILayout.Button("Kill")) {
					m_GitOperation.Abort(true);
					m_CombinedOutput += "Killing...\n";
					m_StateLabel = "Killing...";
				}

				EditorGUI.EndDisabledGroup();

				GUILayout.FlexibleSpace();

				EditorGUILayout.LabelField(m_StateLabel);

				GUILayout.FlexibleSpace();

				if (GUILayout.Button("Clear")) {
					m_CombinedOutput = "";
				}

				EditorGUI.BeginDisabledGroup(!isWorking);

				if (GUILayout.Button("Get Status")) {
					var resultEntries = new List<GitStatusData>();

					m_GitOperation = WiseGitIntegration.GetStatusesAsync(".", false, resultEntries);

					m_GitOperation.AnyOutput += (line) => { m_CombinedOutput += line + "\n"; };

					m_GitOperation.Completed += (op) => {
						m_StateLabel = op.AbortRequested ? "Aborted!" : "Completed!";
						m_CombinedOutput += m_StateLabel + "\n\n";
						m_GitOperation = null;
					};

					m_StateLabel = "Working...";
				}

				EditorGUI.EndDisabledGroup();
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();

			EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
			{
				GUILayout.FlexibleSpace();

				GUILayout.Label("External Git Client:");

				if (GUILayout.Button("Commit", GUILayout.ExpandWidth(false))) {
					GitContextMenusManager.CommitAll();
				}

				if (GUILayout.Button("Pull", GUILayout.ExpandWidth(false))) {
					GitContextMenusManager.PullAll();
				}
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space();
		}
	}
}

<img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Logo-Round-500x500.png" width="200" align="right">

# WiseGit For Unity

Simple but powerful git integration for Unity 3D utilizing [TortoiseGit](https://tortoisegit.org/) (for Windows), [SnailGit](https://langui.net/snailgit) (for MacOS) or [RabbitVCS](http://rabbitvcs.org/) (for Linux) user interface. A must have plugin if you use git as your version control system in your project.

!!!TODO!!! Assets Store | Unity Forum

[![openupm](https://img.shields.io/npm/v/devlocker.versioncontrol.wisegit?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/devlocker.versioncontrol.wisegit/)

## Table of Contents
[Features](#features)<br />
[Usage](#usage)<br />
[Installation](#installation)<br />
[Overlay Icons](#overlay-icons)<br />
[Screenshots](#screenshots)<br />

## Features
* **Hooks up to Unity move and delete file operations and executes respective git commands to stay in sync.**
  * **Handles meta files as well.**
  * Moving assets to unversioned folder will ask the user to add that folder meta to git as well.
  * Moving folders / files that have conflicts will be rejected.
  * Will work with other custom tools as long as they move / rename assets using Unity API.
* Provides assets context menu for manual git operations like commit, push, pull, revert etc.
* **Show overlay git status icons**
  * Show server changes that you need to merge (works by regularly fetching remote changes).
  * Show locked files by you and your colleges (works via LFS locks).
  * Show ignored icons (by ".gitignore").
* Displays warning in the SceneView when the current scene or edited prefab is out of date or locked.
* Lock prompt on modifying assets by path and type (perforce checkout like)
  * If asset or its meta becomes modified a pop-up window will prompt the user to lock or ignore it.
  * The window shows if modified assets are locked by others or out of date, which prevents locking them.
  * If left unlocked, the window won't prompt again for those assets. Will prompt on editor restart.
* Minimal performance impact
* Survives assembly reloads
* You don't have to leave Unity to do git chores.
* Works on Windows, MacOS and Linux.
* Simple API to integrate with your tools.
  * Use `WiseGitIntegration.RequestSilence()` and `WiseGitIntegration.ClearSilence()` to temporarily suppress any WiseGit pop-ups.
  * Use `WiseGitIntegration.RequestTemporaryDisable()` and `WiseGitIntegration.ClearTemporaryDisable()` to temporarily disable any WiseGit handling of file operations and updates.
  * Use `GitContextMenusManager` methods to invoke TortoiseGit / SnailGit / RabbitVCS commands.
  * Use `WiseGitIntegration.*Async()` methods to run direct git commands without any GUI (check `ExampleStatusWindow`).

*Check the screenshots below*

NOTE: This was started as a quick fork of [WiseSVN](https://github.com/NibbleByte/UnityWiseSVN).

## Usage
Do your file operations in Unity and the plugin will handle the rest.

User git operations are available in the menu (or right-click on any asset): `Assets/Git/...`

**WARNING: Never focus Unity while the project is updating in the background. Newly added asset guids may get corrupted in which case the Library folder needs to be deleted. <br />
Preferred workflow is to always work inside Unity - use the \"Assets/Git/...\" menus. \"Assets/Git/Pull All\" will block Unity while updating, to avoid Unity processing assets at the same time. <br />
This is an issue with how Unity works, not the plugin iteself. Unity says its by "design".**

## Installation
!!!TODO!!!
* Asset Store plugin: ...
* [OpenUPM](https://openupm.com/packages/devlocker.versioncontrol.wisegit) support:
```
npm install -g openupm-cli
openupm add devlocker.versioncontrol.wisegit
```
... or merge this to your `Packages/manifest.json` (replace the package version **XXXXX** with current):
```
{
    "scopedRegistries": [
        {
            "name": "package.openupm.com",
            "url": "https://package.openupm.com",
            "scopes": [
                "devlocker.versioncontrol.wisegit"
            ]
        }
    ],
    "dependencies": {
        "devlocker.versioncontrol.wisegit": "1.0.XXXXX"
    }
}
```
* Github upm package - merge this to your `Packages/manifest.json`
```
{
  "dependencies": {
    "devlocker.versioncontrol.wisegit": "https://github.com/NibbleByte/UnityWiseGit.git#upm"
}
```

#### Prerequisites
* You need to have git 2.43.0 or higher installed with LFS support (used for locking).
* You need to have [TortoiseGit](https://tortoisegit.org/) (for Windows), [SnailGit](https://langui.net/snailgit) (for MacOS) or [RabbitVCS](http://rabbitvcs.org) (for Linux) installed.
* Test if git works by typing "git version" in the command line / terminal




## Overlay Icons
* Unversioned <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Git_Unversioned_Icon.png" width="16">
* Modified <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Git_Modified_Icon.png" width="16">
* Added <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Git_Added_Icon.png" width="16">
* Deleted <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Git_Deleted_Icon.png" width="16">
* Conflict <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Git_Conflict_Icon.png" width="16">
* Locked by me <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Locks/Git_LockedHere_Icon.png" width="16">
* Locked by others <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Locks/Git_LockedOther_Icon.png" width="16">
* Server has changes, update <img src="https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Assets/DevLocker/VersionControl/WiseGit/Editor/Resources/GitOverlayIcons/Others/Git_RemoteChanges_Icon.png" width="16">

## Screenshots
![OverlayIcons1](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-OverlayIcons-Shot.png)
![OverlayIcons2](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-OverlayIcons2-Shot.png)

![ContextMenu](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-ContextMenu-Shot.png)
![File Operations](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-Rename-Shot.png)
![Preferences](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-Preferences-Shot.png)

![Lock Prompt](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-Lock-Prompt.png)
![Locked Scene Warning](https://raw.githubusercontent.com/NibbleByte/UnityWiseGit/master/Docs/Screenshots/WiseGit-Locked-Scene-Warning.png)
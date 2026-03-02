# VR2Gather_sample

This repository contains a sample Unity project that uses VR2Gather, `nl.cwi.dis.vr2gather`, a Unity package for creating social virtual reality applications.

More information on VR2Gather itself can be found at its repository, <https://github.com/cwi-dis/VR2Gather>.

The Unity project here should run on desktop computers (Windows, Mac, Linux) either with keyboard/mouse/screen or with an attached tethered HMD (Meta Quest, Vive).

The project should also run natively on the Meta Quest.

## Getting started, desktop

Full instructions are in the <https://github.com/cwi-dis/VR2Gather> repository, please refer to those. But here are the quick steps:

- Ensure you have `git` installed, and you have done `git lfs install`.
- Clone this repository.
- Install  the _cwipc_ point cloud package. Instructions are can be found at <https://github.com/cwi-dis/cwipc>
- Open the project in the Unity Editor.
- Select the `VRTLogin` scene.
- Play.

## Getting started, Meta Quest

- Ensure you have `git` installed, and you have done `git lfs install`.
- Clone this repository.
- Ensure you have Android build support included in your Unity install. Unity Hub can do this for you.
- No need to install _cwipc_, the native support for Android is included.
- Open the project in the Unity Editor.
- In Player Settings, select the Android platform, and select your HMD in the target popup.
- Build.
- Run. 
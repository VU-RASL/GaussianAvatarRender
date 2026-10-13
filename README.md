# Depth-consistent rendering of animatable Gaussian avatars in Unity/OpenXR for Meta Quest 3 passthrough AR.



This repository contains official implementation of the accepted paper:

> Depth-consistent rendering of animatable Gaussian avatars in Unity/OpenXR for Meta Quest 3 passthrough AR
> 
> Rendong Zhang ;  Alexandra Watkins ; Nilanjan Sarkar 
>
> ICVR 2026 

![Method overview](docs/images/method_overview.png)


The project demonstrates a practical rendering pipeline for placing an animatable 3D Gaussian avatar into Quest 3 AR while preserving correct ordering with ordinary Unity objects, such as an opaque sphere. The goal is to let a Gaussian avatar behave like a spatial AR character rather than a flat composited overlay.

## Highlights

- Unity/OpenXR project targeting Meta Quest 3 passthrough AR.
- Animatable full-body Gaussian avatar driven by an SMPL-X-based pose controller.
- Correct depth ordering between Gaussian splats and ordinary opaque Unity objects.
- Preserves soft Gaussian avatar boundaries without relying on a visible mesh proxy.
- Includes AR placement, grabbable test sphere, pose controls, optional headset FPS display, and performance-recording utilities.
- Designed as a reproducible research prototype accompanying an accepted paper.

## Repository Status

The release scene is:

```text
Assets/Scenes/GSAvatarRenderer.unity
```

The default scene configuration is AR mode:

```text
buildAR: enabled
buildVR: disabled
showHeadsetFps: disabled
runPerformanceProtocol: disabled
```

## Aim 2 chair memory recording example

Use the existing Quest avatar app and the saved room containing the user-confirmed `Chair_01`. The left Touch controller buttons are **X** and **Y** (the first and second buttons referred to as A/B in the recording plan).

Before recording, press **right B** to show the complete saved room scan in blue. While it is visible, press **right A** to reveal the orange chair mesh, forward arrow, bounding box, and **T0** label. This presentation step represents an assumed segmentation/orientation result; it does not run a segmentation algorithm.

1. With the physical chair at its scanned position **T0**, press **left X**. The avatar moves to the chair's original location and heading.
2. Move the physical chair away. Press **left X** again: the avatar still goes to **T0**, demonstrating the stale chair information.
3. Press **left Y once** to reveal the virtual **T1** chair mesh, bounding box, arrow, and label. The avatar stays where it is, including any manual joystick offset; chair memory still remains at T0. Align the physical chair with the revealed target.
4. Press **left Y again** to update chair memory and place the avatar at T1. Every subsequent Y press places it at the same T1 location and heading.

T1 is a fixed example input: **0.9 m along the chair's original forward direction, with a 90 degree left turn (-90 degrees yaw)**. It represents the assumed output of a future pose-estimation algorithm. Repeated X presses always select T0. After the initial reveal, repeated Y presses always select T1, without accumulating motion. X after Y restores T0 for another take. Physical chair movement is not detected automatically.

**Left index trigger** toggles between a built-in static **Sitting** pose and the existing **No Pose** state. Release the trigger between presses. Sitting uses the existing custom SMPL-X joint interface with a reversible pelvis offset to preserve floor contact; it does not play an imported animation. The user-supplied custom pose array is preserved.

X/Y placement keeps the current body pose. Placement accounts for the Gaussian body's offset from the avatar root and places its feet at the chair's floor footprint. Room geometry and the initial T0 reference remain unchanged. The selected avatar position follows room-anchor relocalization.

**Left thumbstick** moves the avatar horizontally relative to your viewing direction, at up to 0.5 m/s. It keeps the avatar's height and facing direction unchanged. This manual movement does not move either chair target or update chair memory. Press X to return exactly to T0; after T1 has been revealed, press Y to return exactly to T1.

On every fresh app launch, all room/chair meshes start hidden and the segmentation reveal resets. Until **right A** succeeds, **right B** only shows/hides the complete blue room scan, including the original chair triangles. A while meshes are hidden or the room is unavailable does nothing. After A, the T1 mesh, bounds, arrow, and label remain hidden until the first successful **left Y** press. The first Y press reveals only the target and does not move the avatar or update chair memory. T1 remains revealed for retakes, including after X returns the chair memory to T0; X does not reset the two-stage Y sequence. Restart the app to begin again with T1 hidden.

The left trigger is reserved for sitting in this demo. The test ball remains grabbable with either grip or the right trigger.

**Right A** reveals segmentation once per app session; later A presses do not hide it or move the avatar. After that reveal, **right B** shows/hides the room, orange chair, and eligible markers together, retaining the accepted 0.19 behavior. B, X, and room relocalization preserve the revealed phase. It never reveals T1 before the first Y press. Release buttons between presses; B takes priority if A and B are pressed together. The orange **T0** label stays above the original chair bounding box; the cyan **T1** label marks the updated target once revealed.

Build with **Gaussian Splatting > Aim 2 > Build Avatar with Hardcoded Chair Example**. The build validates the chair reference, staged mesh presentation, T0/T1 behavior, avatar placement, sitting pose, and controller input before writing `Builds/avatar-chair-example.apk` (version 0.20). Installation alone does not launch the app. Runtime diagnostics are written to `chair-avatar-example-status.json` in the app's persistent data directory.

### Developer Hub launch permission

On the tested Quest 3 (Android API 34), Developer Hub disabled **Launch app** because the installed package listed `android.permission.POST_NOTIFICATIONS` as ungranted, although the APK did not declare it and the app launched normally from the headset. Granting this permission to the development installation resolved the warning:

```sh
adb shell pm grant com.DefaultCompany.UnityGaussianSplatting android.permission.POST_NOTIFICATIONS
```

Refresh the Developer Hub Apps list after granting it. This is a device permission setting; it does not change the demo or its APK.

## Requirements

- Unity `2022.3.7f1`
- Git LFS
- Meta Quest 3 with developer mode enabled
- Android Build Support for Unity
- OpenXR / Meta XR packages restored through Unity Package Manager
- A Windows development machine is recommended, matching the tested environment

This project uses Git LFS for Unity scenes, assets, textures, FBX files, Gaussian `.bytes` files, and other large files. Do not rely on GitHub's normal "Download ZIP" unless LFS objects are included in the archive.

## Clone

Install Git LFS before cloning:

```bash
git lfs install
git clone https://github.com/VU-RASL/GaussianAvatarRender.git
cd GaussianAvatarRender
git lfs pull
```

After cloning, check that the scene file is not an LFS pointer:

```bash
head -n 1 Assets/Scenes/GSAvatarRenderer.unity
```

On Windows PowerShell:

```powershell
Get-Content Assets\Scenes\GSAvatarRenderer.unity -TotalCount 1
```

The expected first line is:

```text
%YAML 1.1
```

If the file starts with `version https://git-lfs.github.com/spec/v1`, the LFS assets were not downloaded correctly.

## Open In Unity

1. Open the project folder in Unity `2022.3.7f1`.
2. Let Unity restore packages.
3. Open `Assets/Scenes/GSAvatarRenderer.unity`.
4. Select the `general_operator` object in the scene.
5. Confirm that AR mode is enabled.

The `general_operator` component controls the project mode and debug options:

- `VR`: build the project as the VR milestone mode.
- `AR`: build the project as Quest passthrough AR.
- `Show FPS count`: show or hide the in-headset FPS overlay.
- `Run Performance Protocol`: run the evaluation recorder on app start.

## Build For Quest 3

1. Connect the Quest 3 by USB.
2. Confirm that the headset is in developer mode.
3. In Unity, switch platform to Android.
4. Open `File > Build Settings`.
5. Make sure `Assets/Scenes/GSAvatarRenderer.unity` is the enabled scene.
6. Use `Build And Run`.

If Unity reports XR setup warnings, verify:

- Android minimum API level is at least 29.
- OpenXR is enabled for Android.
- Meta Quest / AR / passthrough-related OpenXR features are enabled.
- Git LFS assets have been downloaded.

## Controls And Runtime Behavior

In AR mode, the avatar is placed into the real environment through the AR setup in the scene. Move around briefly after launch so the headset can initialize tracking and environment understanding.

Runtime controls include:

- A pose control panel for switching between clean pose, T-A pose, and pose 1.
- A grabbable sphere for testing whether ordinary Unity objects appear correctly in front of or behind the Gaussian avatar.
- Optional FPS overlay controlled by `general_operator`.

## Rendering Method Summary

The renderer keeps the avatar as Gaussian splats instead of converting it into a conventional mesh. Ordinary Unity objects render through Unity's normal opaque path. The Gaussian avatar is then composited in a depth-aware way so that:

- An ordinary object in front of the avatar hides the Gaussian splats behind it.
- An ordinary object behind the avatar is covered by the visible Gaussian avatar.
- The avatar boundary follows the Gaussian support rather than only the smaller SMPL-X mesh proxy.

This avoids common failure cases from naive integration:

- A sphere behind the avatar appearing in front of the body.
- The avatar being clipped to an SMPL-X-shaped silhouette.
- Black or transparent edge artifacts around the Gaussian avatar.

## Project Structure

```text
Assets/Scenes/
  GSAvatarRenderer.unity              Main release scene

Assets/Scripts/
  GeneralOperator.cs                  AR/VR mode and debug toggles
  QuestARBootstrap.cs                 Quest AR setup and runtime configuration
  ARGroundPlacement.cs                AR placement support
  ARPoseControlPanel.cs               In-headset pose control UI
  PoseController.cs                   Avatar pose control
  GrabbableTestBall.cs                Test sphere interaction
  HeadsetFPSDisplay.cs                Optional headset FPS overlay
  PerformanceProtocolRecorder.cs      Optional runtime performance recorder

Assets/GaussianAssets/
  *.asset, *.bytes                    Gaussian avatar data

Packages/UnityGaussianSplatting/
  Runtime and shader code for Gaussian rendering
```

## Citation

If you use this project in academic work, please cite:

```bibtex
@inproceedings{PLACEHOLDER_GaussianAvatarRender,
  title     = {[PLACEHOLDER: Paper title]},
  author    = {[PLACEHOLDER: Authors]},
  booktitle = {[PLACEHOLDER: Conference / venue]},
  year      = {[PLACEHOLDER: Year]},
  doi       = {[PLACEHOLDER: DOI]},
  url       = {[PLACEHOLDER: URL]}
}
```

## Notes For Reproducibility

- Use Unity `2022.3.7f1` when possible.
- Always clone with Git LFS.
- Keep Unity `.meta` files tracked. They are required for stable GUID references across machines.
- The default checked-in scene is configured for Quest AR with performance recording and FPS overlay disabled.
- The project is a research prototype and may require local Unity/Meta XR package cache refresh on first open.

## Acknowledgements

This project builds on the Unity Gaussian Splatting package by Aras Pranckevicius and integrates Unity, OpenXR, Meta XR, AR Foundation, SMPL-X-based animation control, GSAC, and custom Quest 3 rendering changes for AR Gaussian avatar deployment.


[PLACEHOLDER: Add project license.]

Please also check the licenses of third-party components, avatar assets, SMPL-X-related assets, Meta SDK packages, and any redistributed data before public release or commercial use.

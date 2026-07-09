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
@inproceedings{zhang2026depthconsistent,
  title     = {Depth-Consistent Rendering of Animatable Gaussian Avatars in Standalone Augmented Reality},
  author    = {Zhang, Rendong and Watkins, Alexandra and Sarkar, Nilanjan},
  booktitle = {Proceedings of the International Conference on Virtual Reality (ICVR)},
  year      = {2026},
  note      = {Accepted, to appear}
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

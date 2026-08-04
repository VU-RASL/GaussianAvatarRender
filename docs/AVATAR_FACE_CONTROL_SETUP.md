# AVATAR Face-Control Setup

This branch contains the renderer-side facial-control code only. It does not
include any AVATAR assets, Gaussian data, trajectory files, or licensed SMPL-X
model data.

Use this checklist when cloning the branch on another computer and importing
the same AVATAR inputs locally.

## Required Local Inputs

- The AVATAR Gaussian asset or imported Gaussian PLY.
- The AVATAR Gaussian-to-face binding data used by the renderer.
- The matching SMPL-X mesh/rig used when the AVATAR was created.
- A GSAC face-basis `.bytes` file with 50 expression controls and two eyelid
  controls.
- A GSAC face-trajectory `.json` file, if trajectory playback is desired.

Keep these files local unless you have explicit permission to redistribute
them.

## Scene Setup

1. Open the Unity project from this branch.
2. Import or recreate the AVATAR Gaussian asset using the normal project flow.
3. Add the AVATAR SMPL-X object and Gaussian renderer objects to the scene.
4. On the AVATAR root object, make sure `PoseController` is present and wired
   to the AVATAR `SMPLX` component and Gaussian binding data.
5. Add `GsacFaceParameterController` to the same root object, or to an object
   that can find the AVATAR `PoseController`.
6. In `GsacFaceParameterController`, assign:
   - `Pose Controller`
   - `SMPLX`
   - `Skinned Mesh Renderer`
   - `Face Basis Bytes`, or `Face Basis File Path`
7. Add `GsacFaceTrajectoryPlayer` if animation playback is needed.
8. In `GsacFaceTrajectoryPlayer`, assign:
   - `Face Controller`
   - trajectory JSON text asset or file path
   - the desired emotion/clip settings

## Expected Status

When Play mode starts, `GsacFaceParameterController` validates the SMPL-X mesh
against the supplied face basis before adding any missing blendshapes.

Expected successful cases include:

- Exact prefix topology, where the source `10475` vertices match and Unity's
  extra vertices are mapped as seam duplicates.
- Non-prefix Unity-imported topology, where the controller builds a full
  expression-delta mapping from existing `Exp000..Exp009` controls.

On success, the controller should report exact support for:

- `Exp000..Exp049`
- `GsacEyelidLeft`
- `GsacEyelidRight`

If the mesh cannot be safely mapped, the controller refuses to install the
extra basis controls and leaves the existing renderer path intact.

## Playback Check

With `GsacFaceTrajectoryPlayer` enabled:

1. Select one trajectory/emotion checkbox in the inspector.
2. Enter Play mode.
3. The AVATAR should repeat that facial animation until Play mode stops.
4. Check mouth, cheeks, eyelids, and brow movement in the rendered Gaussian
   avatar, not only on the hidden SMPL-X mesh.

With trajectory playback disabled:

- The AVATAR should render normally.
- Body pose and Gaussian splat updates should continue to use the existing
  renderer path.
- Facial controls remain idle unless driven through
  `GsacFaceParameterController`.

## Smoke Test

Use the context menu on `GsacFaceParameterController`:

- `GSAC Face/Open Mouth Smoke Test`
- `GSAC Face/Close Mouth`
- `GSAC Face/Reset To Neutral`

Expected result: the rendered Gaussian mouth opens and closes repeatably.

## Troubleshooting

- `required blendshape Exp000 is missing`: the SMPL-X mesh has no expression
  blendshapes, so the 50D basis cannot be validated.
- `Face basis topology mismatch`: the target mesh has fewer vertices than the
  basis.
- `Cannot full-map face basis topology`: the imported mesh does not provide a
  mathematically safe mapping from existing expression controls to the source
  basis.
- `PoseController is not assigned`: SMPL-X face motion may apply, but Gaussian
  splats will not update until the bake path is wired.

Do not treat a `50/50` and `2/2` capability report as the only validation. In
Play mode, verify that the rendered Gaussian splats move in the mouth, cheek,
eyelid, and brow regions without visible seams or cracks.

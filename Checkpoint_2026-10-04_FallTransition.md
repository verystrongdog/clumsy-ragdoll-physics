# Checkpoint: Fall transition stability

Date: 2026-10-04
Scene: `Assets/Scenes/StepByStepTest.unity`

## Verified stable behavior

- Side falls no longer show the previous direct hand/foot penetration in the tested case.
- The dynamic handoff was moved earlier: `RaccoonCenterOfMassBalance.HybridPhysicsStart = 0.30`.
- The handoff is spread over 3 fixed physics steps with `RaccoonPhysicsRig.HybridReleaseFixedSteps = 3`.
- Play mode starts successfully and the rig builds 15 proxy rigidbodies.

## Implementation checkpoint

- `RaccoonPhysicsRig.MoveKinematicFallPose` resolves proxy-vs-ground penetration before dynamic handoff.
- Dynamic fall release preserves the current pose and clears initial linear/angular velocity.
- Scene ground remains `DemoPlatform` with an enabled, non-trigger `BoxCollider`, top surface at `Y = 0`.

## Suggested rollback

Use the Git checkpoint commit created with this record. Do not reset unrelated work manually; inspect the checkpoint first and restore only the files needed for comparison.

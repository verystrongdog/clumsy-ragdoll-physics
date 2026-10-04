# Checkpoint: real-time walking balance and fall feedback

Date: 2026-10-04
Scene: `Assets/Scenes/StepByStepTest.unity`

Git base checkpoint: `3f123e6` (`checkpoint: stabilize fall physics handoff`)

## Current verified behavior

- Walking and leg lifting use the active proxy-rig controller: kinematic Rigidbody/CapsuleCollider bodies driven by the alternating step controller.
- The weighted proxy-body COM is evaluated every `FixedUpdate` against the current single-foot or double-foot support region.
- During single support, the body first shifts toward the planted foot before the remaining COM error is evaluated.
- Body advance after a landing is limited by `BodyAdvanceFactor = 0.45`, so an overly long or badly placed step can produce real balance feedback.
- Invalid planted foot placement can trigger a fall when feet overlap, become too close, or cross left/right ordering.
- The foot separation threshold is `FootMinimumSeparation = 0.13` and crossing tolerance is `FootCrossTolerance = 0.015`.
- The controlled fall hands off to hybrid/dynamic physics at `HybridPhysicsStart = 0.30`; dynamic release is spread over 3 fixed steps.
- The tested side-fall transition is stable, and the ground is an enabled non-trigger BoxCollider on `DemoPlatform`.
- Leg segment roll uses a stable initial forward reference to avoid lower-leg axial flipping.

## Current input model

- Left/right mouse buttons alternate the active foot.
- Holding a button lifts/holds the active foot; releasing starts landing.
- Mouse position and WASD influence the target foot direction while the foot is lifted.
- R resets the character to the initial standing pose.

## Intentional architecture boundary

Walking is an active/kinematic ragdoll proxy system. Falling transitions from controlled kinematic motion into dynamic physics. This checkpoint does not yet include get-up animation, recovery state, terrain adaptation, or fully dynamic physics-driven walking.

## Suggested rollback

Use the Git commit containing this document (`checkpoint: real-time walking balance fall feedback`) for this record. Inspect the checkpoint before restoring individual files; preserve unrelated future work.

using UnityEngine;

namespace ClumsyRagdoll
{
    public static class PlaygroundBuilder
    {
        public static void Build(out Vector3 spawnA, out Vector3 spawnB)
        {
            spawnA = new Vector3(-1.35f, 0.02f, -2.1f);
            spawnB = new Vector3(1.35f, 0.02f, -2.1f);

            PhysicsMaterial ground = SurfaceKit.Friction(0.55f, 0f, PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Minimum);
            PhysicsMaterial solid = SurfaceKit.Friction(0.7f, 0.05f, PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Average);
            PhysicsMaterial bouncy = SurfaceKit.Friction(0.28f, 0.78f, PhysicsMaterialCombine.Average, PhysicsMaterialCombine.Maximum);

            Material grass = SurfaceKit.Lit(new Color(0.46f, 0.56f, 0.4f), 0.05f);
            Material wood = SurfaceKit.Lit(new Color(0.72f, 0.5f, 0.3f), 0.12f);
            Material woodDark = SurfaceKit.Lit(new Color(0.55f, 0.36f, 0.2f), 0.1f);
            Material stone = SurfaceKit.Lit(new Color(0.52f, 0.54f, 0.58f), 0.18f);
            Material heavy = SurfaceKit.Lit(new Color(0.32f, 0.3f, 0.28f), 0.08f);
            Material ball = SurfaceKit.Lit(new Color(0.86f, 0.28f, 0.26f), 0.35f);
            Material grip = SurfaceKit.Lit(new Color(0.95f, 0.76f, 0.22f), 0.3f);
            Material padA = SurfaceKit.Lit(new Color(0.93f, 0.45f, 0.28f), 0.15f);
            Material padB = SurfaceKit.Lit(new Color(0.25f, 0.55f, 0.72f), 0.15f);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "地面";
            floor.transform.position = new Vector3(0f, -0.5f, 0f);
            floor.transform.localScale = new Vector3(48f, 1f, 48f);
            floor.GetComponent<Renderer>().sharedMaterial = grass;
            floor.GetComponent<Collider>().sharedMaterial = ground;
            floor.AddComponent<NotGrabbable>();

            Pad(spawnA, padA, solid);
            Pad(spawnB, padB, solid);

            for (int i = 0; i < 7; i++)
            {
                Vector3 pos = new Vector3(-1.6f + i * 0.48f, 0.38f, 0.4f);
                Solid("多米诺", PrimitiveType.Cube, pos, Quaternion.identity, new Vector3(0.28f, 0.72f, 0.08f), 0.55f, false, wood, solid);
            }

            Solid("木箱", PrimitiveType.Cube, new Vector3(-0.4f, 0.38f, 2.8f), Quaternion.Euler(0f, 18f, 0f), new Vector3(0.72f, 0.72f, 0.72f), 2.2f, false, wood, solid);
            Solid("木箱", PrimitiveType.Cube, new Vector3(0.55f, 0.38f, 3.3f), Quaternion.Euler(0f, -12f, 0f), new Vector3(0.72f, 0.72f, 0.72f), 2.2f, false, woodDark, solid);
            Solid("木箱", PrimitiveType.Cube, new Vector3(0.05f, 1.12f, 3.05f), Quaternion.Euler(0f, 8f, 0f), new Vector3(0.72f, 0.72f, 0.72f), 2.2f, false, wood, solid);
            Solid("重箱", PrimitiveType.Cube, new Vector3(2.4f, 0.45f, 2.4f), Quaternion.identity, new Vector3(0.9f, 0.9f, 0.9f), 26f, false, heavy, solid);

            for (int i = 0; i < 4; i++)
                Solid("木箱", PrimitiveType.Cube, new Vector3(-3.4f, 0.36f + i * 0.74f, 3.4f), Quaternion.Euler(0f, i * 8f, 0f), new Vector3(0.7f, 0.7f, 0.7f), 1.6f, false, i % 2 == 0 ? wood : woodDark, solid);

            Solid("球", PrimitiveType.Sphere, new Vector3(-3.1f, 0.85f, 1.2f), Quaternion.identity, Vector3.one * 0.7f, 1.1f, false, ball, bouncy);
            Solid("木板", PrimitiveType.Cube, new Vector3(2.2f, 0.4f, 4.6f), Quaternion.Euler(0f, 20f, 8f), new Vector3(0.38f, 0.08f, 1.7f), 3f, false, woodDark, solid);
            Solid("木板", PrimitiveType.Cube, new Vector3(-1.8f, 0.35f, 5.2f), Quaternion.Euler(4f, -15f, 0f), new Vector3(0.38f, 0.08f, 1.7f), 3f, false, wood, solid);

            Solid("坡道", PrimitiveType.Cube, new Vector3(0.2f, 0.72f, 8.2f), Quaternion.Euler(-16f, 0f, 0f), new Vector3(2.4f, 0.18f, 5.2f), 0f, true, stone, solid);
            Solid("平台", PrimitiveType.Cube, new Vector3(0.2f, 0.55f, 12.2f), Quaternion.identity, new Vector3(4.2f, 1.1f, 3.2f), 0f, true, stone, solid);

            Solid("攀爬墙", PrimitiveType.Cube, new Vector3(-6.2f, 1.15f, 5.4f), Quaternion.identity, new Vector3(0.45f, 2.3f, 3.4f), 0f, true, stone, solid);
            Solid("把手", PrimitiveType.Cube, new Vector3(-5.85f, 0.7f, 4.5f), Quaternion.identity, new Vector3(0.28f, 0.22f, 0.55f), 0f, true, grip, solid);
            Solid("把手", PrimitiveType.Cube, new Vector3(-5.85f, 1.25f, 5.4f), Quaternion.identity, new Vector3(0.28f, 0.22f, 0.55f), 0f, true, grip, solid);
            Solid("把手", PrimitiveType.Cube, new Vector3(-5.85f, 1.8f, 6.3f), Quaternion.identity, new Vector3(0.28f, 0.22f, 0.55f), 0f, true, grip, solid);

            Solid("支架", PrimitiveType.Cube, new Vector3(-1.3f, 0.8f, 6.6f), Quaternion.identity, new Vector3(0.18f, 1.6f, 0.18f), 0f, true, grip, solid);
            Solid("支架", PrimitiveType.Cube, new Vector3(1.5f, 0.8f, 6.6f), Quaternion.identity, new Vector3(0.18f, 1.6f, 0.18f), 0f, true, grip, solid);
            Solid("单杠", PrimitiveType.Cube, new Vector3(0.1f, 1.62f, 6.6f), Quaternion.identity, new Vector3(3.1f, 0.12f, 0.12f), 0f, true, grip, solid);

            GameObject fulcrum = Solid("支点", PrimitiveType.Cube, new Vector3(5.4f, 0.28f, 1.6f), Quaternion.identity, new Vector3(0.35f, 0.56f, 0.35f), 0f, true, heavy, solid);
            GameObject plank = Solid("跷跷板", PrimitiveType.Cube, new Vector3(5.4f, 0.62f, 1.6f), Quaternion.identity, new Vector3(1.15f, 0.1f, 3.8f), 8f, false, wood, solid);
            HingeJoint hinge = plank.AddComponent<HingeJoint>();
            hinge.connectedBody = fulcrum.GetComponent<Rigidbody>();
            hinge.axis = Vector3.right;
            hinge.anchor = Vector3.zero;
            hinge.autoConfigureConnectedAnchor = true;
            hinge.useLimits = true;
            JointLimits limits = hinge.limits;
            limits.min = -16f;
            limits.max = 16f;
            hinge.limits = limits;

            for (int i = 0; i < 10; i++)
            {
                float angle = i / 10f * Mathf.PI * 2f;
                Vector3 pos = new Vector3(Mathf.Sin(angle) * 15f, 0.4f, Mathf.Cos(angle) * 15f);
                GameObject post = Solid("界桩", PrimitiveType.Cube, pos, Quaternion.identity, new Vector3(0.32f, 0.8f, 0.32f), 0f, true, woodDark, solid);
                post.AddComponent<NotGrabbable>();
            }
        }

        static void Pad(Vector3 spawn, Material mat, PhysicsMaterial phys)
        {
            GameObject pad = Solid("落点", PrimitiveType.Cube, new Vector3(spawn.x, 0.015f, spawn.z), Quaternion.identity, new Vector3(1.1f, 0.03f, 1.1f), 0f, true, mat, phys);
            pad.AddComponent<NotGrabbable>();
        }

        static GameObject Solid(string name, PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale, float mass, bool kinematic, Material mat, PhysicsMaterial phys)
        {
            GameObject go = new GameObject(name);
            go.transform.SetPositionAndRotation(position, rotation);
            Collider collider = type == PrimitiveType.Sphere ? go.AddComponent<SphereCollider>() : go.AddComponent<BoxCollider>();
            if (collider is SphereCollider sphere)
                sphere.radius = scale.x * 0.5f;
            else
                ((BoxCollider)collider).size = scale;
            collider.sharedMaterial = phys;

            GameObject visual = GameObject.CreatePrimitive(type);
            visual.name = "模型";
            Collider extra = visual.GetComponent<Collider>();
            if (extra != null)
            {
                extra.enabled = false;
                Object.Destroy(extra);
            }
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = type == PrimitiveType.Sphere ? Vector3.one * scale.x : scale;
            visual.GetComponent<Renderer>().sharedMaterial = mat;

            Rigidbody body = go.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(mass, 0.1f);
            body.isKinematic = kinematic;
            body.interpolation = kinematic ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = kinematic ? CollisionDetectionMode.Discrete : CollisionDetectionMode.ContinuousSpeculative;
            if (!kinematic)
            {
                body.angularDamping = 0.4f;
                body.maxDepenetrationVelocity = 3f;
            }
            return go;
        }
    }
}

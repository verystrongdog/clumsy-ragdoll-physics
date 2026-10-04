# -*- coding: utf-8 -*-
"""
把 `raccoon_anim.py` 建好的动画片段渲成图，用来「用眼睛验」。

用法（在同一个 Blender 会话里跑完 raccoon_anim.py 之后再跑本文件）：

    exec(open(r"...\\raccoon_anim.py").read(), g)
    exec(open(r"...\\raccoon_render.py").read(), g2)

输出：`%TEMP%\\raccoon_shots\\<clip>_<view>_f<frame>.png`。
"""

import bpy
import math
import os
from mathutils import Vector

OUT_DIR = os.path.join(os.environ.get("TEMP", r"C:\Windows\Temp"), "raccoon_shots")
ARM_NAME = "Armature"

# 角色世界包围盒（实测）：x±0.50、y±0.37、z 0.00–0.85；面朝 **世界 −Y**。
CENTER = Vector((0.0, -0.10, 0.42))
SIZE = 1.05


def ensure_scene():
    scn = bpy.context.scene
    scn.render.resolution_x = 420
    scn.render.resolution_y = 560
    scn.render.resolution_percentage = 100
    scn.render.image_settings.file_format = 'PNG'
    scn.render.film_transparent = False
    scn.render.engine = 'BLENDER_WORKBENCH'
    sh = scn.display.shading
    sh.light = 'STUDIO'
    sh.color_type = 'MATERIAL'
    sh.show_shadows = True
    sh.show_cavity = True
    scn.display.render_aa = '8'
    scn.world = scn.world or bpy.data.worlds.new("W")
    scn.world.use_nodes = False
    scn.world.color = (0.22, 0.24, 0.28)
    return scn


def ensure_camera():
    cam = bpy.data.objects.get("ShotCam")
    if cam is None:
        cd = bpy.data.cameras.new("ShotCam")
        cam = bpy.data.objects.new("ShotCam", cd)
        bpy.context.collection.objects.link(cam)
    cam.data.lens = 55.0
    cam.data.sensor_width = 36.0
    bpy.context.scene.camera = cam
    return cam


VIEWS = {
    # 名字: (相机位置, 看向的点) —— 侧视最能看出步态
    "side": (CENTER + Vector((2.35, 0.0, 0.10)), CENTER),
    "front": (CENTER + Vector((0.0, -2.35, 0.10)), CENTER),
    "iso": (CENTER + Vector((1.70, -1.70, 0.85)), CENTER),
}


def aim(cam, pos, target):
    cam.location = pos
    d = (target - pos)
    cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


def render_clip(clip_name, frames, views=("side", "iso"), prefix=None):
    scn = ensure_scene()
    cam = ensure_camera()
    arm = bpy.data.objects[ARM_NAME]
    ad = arm.animation_data or arm.animation_data_create()
    act = bpy.data.actions[clip_name]
    ad.action = act
    try:
        if hasattr(ad, "action_slot") and ad.action_slot is None and hasattr(act, "slots") and len(act.slots):
            ad.action_slot = act.slots[0]
    except Exception:
        pass

    os.makedirs(OUT_DIR, exist_ok=True)
    made = []
    tag = prefix or clip_name
    for view in views:
        pos, target = VIEWS[view]
        aim(cam, pos, target)
        for f in frames:
            scn.frame_set(int(f))
            path = os.path.join(OUT_DIR, "%s_%s_f%03d.png" % (tag, view, int(f)))
            scn.render.filepath = path
            bpy.ops.render.render(write_still=True)
            made.append(path)
    return made


def main(plan=None):
    if plan is None:
        plan = {
            "RaccoonIdle": ([0, 22, 45, 68], ("side", "iso")),
            "RaccoonWalk": ([0, 4, 8, 11, 15, 19, 23, 26], ("side",)),
            "RaccoonRun": ([0, 3, 6, 9, 12, 15], ("side",)),
            "RaccoonAir": ([0, 8, 15, 23], ("iso",)),
            "RaccoonFlail": ([0, 9, 18, 27], ("iso",)),
            "RaccoonLand": ([0, 3, 7, 12, 18], ("side",)),
            "RaccoonPush": ([0, 5, 10, 16, 24], ("iso",)),
            "RaccoonWave": ([0, 8, 16, 24, 32, 40], ("iso",)),
        }
    all_made = []
    for clip, (frames, views) in plan.items():
        if clip not in bpy.data.actions:
            continue
        all_made += render_clip(clip, frames, views)
    return {"dir": OUT_DIR, "count": len(all_made), "files": ", ".join(os.path.basename(p) for p in all_made)}


result = main()

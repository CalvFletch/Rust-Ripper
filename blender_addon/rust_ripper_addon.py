bl_info = {
    "name": "Rust Ripper",
    "author": "Rust Ripper",
    "version": (0, 1, 0),
    "blender": (4, 2, 0),
    "location": "3D Viewport > Sidebar > Rust",
    "description": "Import Rust Ripper GLB exports: correct visibility, paint controls, light tools, daemon search",
    "category": "Import-Export",
}

import json
import os
import tempfile
import urllib.parse
import urllib.request

import bpy
from bpy.props import BoolProperty, FloatProperty, PointerProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

DAEMON = "http://127.0.0.1:17071"


# ---------------------------------------------------------------- settings

class RustRipperSettings(bpy.types.PropertyGroup):
    query: StringProperty(name="Search", description="Asset to fetch from the Rust Ripper daemon", default="")
    root_display_size: FloatProperty(
        name="Root Size", description="Display size of imported root empties",
        default=10.0, min=0.1, max=100.0)
    auto_hide: BoolProperty(
        name="Hide utility objects",
        description="Hide objects the game never shows (disabled renderers, IO origins, runtime flares)",
        default=True)


# ---------------------------------------------------------------- core

def _post_process(objects, settings):
    """Apply everything the GLB carries but core glTF cannot express."""
    hidden = 0
    for obj in objects:
        if settings.auto_hide and obj.get("unity_hidden"):
            obj.hide_set(True)
            obj.hide_render = True
            hidden += 1
        if obj.parent is None and obj.type == "EMPTY":
            obj.empty_display_size = settings.root_display_size
    return hidden


def _build_paint_nodes(glb_path, materials):
    """Paint-nodes exports ship the albedo raw plus mask sidecars: build
    mask -> Mix.Factor, albedo -> A, _RUST_PAINT colour attribute -> B."""
    base = os.path.splitext(glb_path)[0]
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        mask_path = f"{base}.paintmask.{mat.name}.png"
        if not os.path.exists(mask_path):
            continue
        tree = mat.node_tree
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if not bsdf or not bsdf.inputs["Base Color"].is_linked:
            continue
        albedo_out = bsdf.inputs["Base Color"].links[0].from_socket

        mask_img = bpy.data.images.load(mask_path, check_existing=True)
        mask_img.colorspace_settings.name = "Non-Color"
        mask_node = tree.nodes.new("ShaderNodeTexImage")
        mask_node.image = mask_img
        mask_node.label = "Paint Mask"
        mask_node.location = (-600, 500)

        attr = tree.nodes.new("ShaderNodeVertexColor")
        attr.layer_name = "_RUST_PAINT"
        attr.label = "Paint Colour"
        attr.location = (-600, 250)

        mix = tree.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.label = "Paint"
        mix.location = (-300, 400)

        tree.links.new(mask_node.outputs["Color"], mix.inputs["Factor"])
        tree.links.new(albedo_out, mix.inputs[6])
        tree.links.new(attr.outputs["Color"], mix.inputs[7])
        tree.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
        built += 1
    return built


def _import_glb(context, filepath):
    settings = context.scene.rust_ripper
    before_objects = set(bpy.data.objects)
    before_materials = set(bpy.data.materials)
    bpy.ops.import_scene.gltf(filepath=filepath)
    new_objects = [o for o in bpy.data.objects if o not in before_objects]
    new_materials = [m for m in bpy.data.materials if m not in before_materials]
    hidden = _post_process(new_objects, settings)
    painted = _build_paint_nodes(filepath, new_materials)
    return len(new_objects), hidden, painted


def _light_info(obj):
    info = obj.get("unity_light")
    return dict(info) if info is not None and hasattr(info, "keys") else None


def _is_fill_light(obj):
    """Heuristic, not ground truth: vertex-mode lights are Rust's cheap fill
    lights; shadowless cookie-less faint lights are usually bounce fakes."""
    info = _light_info(obj)
    if not info:
        return False
    if info.get("unity_render_mode") == 2:
        return True
    return (info.get("unity_shadows", 0) == 0
            and not info.get("unity_cookie")
            and info.get("unity_intensity", 0) <= 2.0)


# ---------------------------------------------------------------- operators

class RUST_OT_import_glb(bpy.types.Operator, ImportHelper):
    bl_idname = "rust.import_glb"
    bl_label = "Import Rust GLB"
    bl_description = "Import a Rust Ripper GLB with visibility, paint and light handling"
    bl_options = {"REGISTER", "UNDO"}
    filename_ext = ".glb"
    filter_glob: StringProperty(default="*.glb", options={"HIDDEN"})

    def execute(self, context):
        count, hidden, painted = _import_glb(context, self.filepath)
        self.report({"INFO"}, f"{count} objects ({hidden} hidden, {painted} paint materials)")
        return {"FINISHED"}


class RUST_OT_daemon_import(bpy.types.Operator):
    bl_idname = "rust.daemon_import"
    bl_label = "Fetch from Game"
    bl_description = "Export the searched asset via the Rust Ripper daemon and import it (first fetch of an asset may load bundles for a couple of minutes)"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        query = context.scene.rust_ripper.query.strip()
        if not query:
            self.report({"ERROR"}, "Type an asset name first")
            return {"CANCELLED"}
        out_dir = os.path.join(tempfile.gettempdir(), "rust_ripper")
        os.makedirs(out_dir, exist_ok=True)
        url = f"{DAEMON}/export?q={urllib.parse.quote(query)}&out={urllib.parse.quote(out_dir)}"
        try:
            with urllib.request.urlopen(url, timeout=600) as response:
                result = json.loads(response.read())
        except Exception as error:
            self.report({"ERROR"}, f"daemon not reachable ({error}) - run: ripper serve")
            return {"CANCELLED"}
        if not result.get("success"):
            self.report({"ERROR"}, result.get("message", "export failed"))
            return {"CANCELLED"}
        count, hidden, painted = _import_glb(context, result["path"])
        self.report({"INFO"}, f"{query}: {count} objects in {result.get('seconds', 0):.1f}s export")
        return {"FINISHED"}


class RUST_OT_hide_fill_lights(bpy.types.Operator):
    bl_idname = "rust.hide_fill_lights"
    bl_label = "Hide Fill Lights"
    bl_description = "Hide bounce/fill lights (vertex render mode, or shadowless cookie-less faint lights) - heuristic, review the list"

    def execute(self, context):
        count = 0
        for obj in context.scene.objects:
            if obj.type == "LIGHT" and _is_fill_light(obj):
                obj.hide_set(True)
                obj.hide_render = True
                count += 1
        self.report({"INFO"}, f"{count} fill lights hidden")
        return {"FINISHED"}


class RUST_OT_show_all_lights(bpy.types.Operator):
    bl_idname = "rust.show_all_lights"
    bl_label = "Show All Lights"

    def execute(self, context):
        for obj in context.scene.objects:
            if obj.type == "LIGHT":
                obj.hide_set(False)
                obj.hide_render = False
        return {"FINISHED"}


# ---------------------------------------------------------------- panels

class RUST_PT_main(bpy.types.Panel):
    bl_label = "Rust Ripper"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Rust"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.rust_ripper
        row = layout.row(align=True)
        row.prop(settings, "query", text="")
        row.operator("rust.daemon_import", text="", icon="IMPORT")
        layout.operator("rust.import_glb", icon="FILE_3D")
        layout.prop(settings, "root_display_size")
        layout.prop(settings, "auto_hide")


class RUST_PT_lights(bpy.types.Panel):
    bl_label = "Lights"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Rust"
    bl_parent_id = "RUST_PT_main"

    def draw(self, context):
        layout = self.layout
        row = layout.row(align=True)
        row.operator("rust.hide_fill_lights", icon="LIGHT_SUN")
        row.operator("rust.show_all_lights", icon="HIDE_OFF")
        lights = [o for o in context.scene.objects if o.type == "LIGHT" and _light_info(o)]
        if not lights:
            layout.label(text="No Rust lights in scene")
            return
        col = layout.column(align=True)
        for obj in sorted(lights, key=lambda o: o.name):
            info = _light_info(obj) or {}
            row = col.row(align=True)
            row.prop(obj, "hide_viewport", text="", emboss=False,
                     icon="HIDE_ON" if obj.hide_viewport or obj.hide_get() else "HIDE_OFF")
            badges = []
            if info.get("unity_shadows"):
                badges.append("S")
            if info.get("unity_cookie"):
                badges.append("C")
            if info.get("unity_render_mode") == 2:
                badges.append("fill")
            label = obj.name if not badges else f"{obj.name}  [{'/'.join(badges)}]"
            row.label(text=label, icon=f"LIGHT_{obj.data.type}" if obj.data.type in ("POINT", "SPOT", "SUN", "AREA") else "LIGHT")
            row.label(text=f"{info.get('unity_intensity', 0):.0f}")


classes = (
    RustRipperSettings,
    RUST_OT_import_glb,
    RUST_OT_daemon_import,
    RUST_OT_hide_fill_lights,
    RUST_OT_show_all_lights,
    RUST_PT_main,
    RUST_PT_lights,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.rust_ripper = PointerProperty(type=RustRipperSettings)


def unregister():
    del bpy.types.Scene.rust_ripper
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()

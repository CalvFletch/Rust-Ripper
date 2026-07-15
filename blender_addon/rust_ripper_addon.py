bl_info = {
    "name": "Rust Ripper",
    "author": "Rust Ripper",
    "version": (0, 1, 4),
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
    reuse_meshes: BoolProperty(
        name="Reuse existing meshes",
        description="If a mesh with the same Unity identity is already in the file, link it instead of importing a duplicate (e.g. snow and normal pines share one mesh)",
        default=True)
    light_power_scale: FloatProperty(
        name="Light Power",
        description="Scale factor applied to imported light energy",
        default=1.0, min=0.0, max=100.0)
    tidy_nodes: BoolProperty(
        name="Tidy node layouts",
        description="Auto-arrange material nodes left-to-right after import (no stacked nodes)",
        default=True)


# ---------------------------------------------------------------- core

def _mesh_key(mesh):
    pid = mesh.get("unity_path_id")
    return (str(pid), str(mesh.get("unity_collection"))) if pid is not None else None


def _post_process(objects, settings):
    """Apply everything the GLB carries but core glTF cannot express."""
    hidden = 0
    reused = 0
    new_meshes = {o.data for o in objects if o.type == "MESH"}
    registry = {}
    if settings.reuse_meshes:
        for mesh in bpy.data.meshes:
            key = _mesh_key(mesh)
            if key and mesh not in new_meshes and key not in registry:
                registry[key] = mesh
    for obj in objects:
        if settings.auto_hide and obj.get("unity_hidden"):
            obj.hide_set(True)
            obj.hide_render = True
            hidden += 1
        if obj.parent is None and obj.type == "EMPTY":
            obj.empty_display_size = settings.root_display_size
        if obj.type == "MESH" and settings.reuse_meshes:
            key = _mesh_key(obj.data)
            if key and key in registry and registry[key] is not obj.data:
                duplicate = obj.data
                obj.data = registry[key]
                if duplicate.users == 0:
                    bpy.data.meshes.remove(duplicate)
                reused += 1
            elif key:
                registry[key] = obj.data
        if obj.type == "LIGHT":
            info = _light_info(obj)
            if info:
                # glTF has no range: use Blender's custom distance cutoff so
                # small intense lights (gauges) stop flooding the scene
                rng = info.get("unity_range", 0)
                if rng and rng > 0:
                    obj.data.use_custom_distance = True
                    obj.data.cutoff_distance = rng
                if hasattr(obj.data, "shadow_soft_size"):
                    obj.data.shadow_soft_size = max(obj.data.shadow_soft_size, 0.03)
                obj.data.energy *= settings.light_power_scale
    return hidden, reused


def _build_paint_nodes(glb_path, materials):
    """Layer-nodes exports ship the albedo raw plus mask sidecars: build
    mask -> Mix.Factor, albedo -> A, _RUST_DETAILCOLOR attribute -> B."""
    base = os.path.splitext(glb_path)[0]
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        mask_path = f"{base}.detailmask.{mat.name}.png"
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
        attr.layer_name = "_RUST_DETAILCOLOR"
        attr.label = "Detail Colour"
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


# ------------------------------------------------- blend layer (compiled-shader curve)

def _texture_entry(mat, slot):
    """unity_textures extras entry {name, scale, offset} for a slot, or None."""
    textures = mat.get("unity_textures")
    entry = textures.get(slot) if textures is not None and hasattr(textures, "get") else None
    return dict(entry) if entry is not None and hasattr(entry, "keys") else None


def _sidecar_path(glb_path, texture_name):
    path = f"{os.path.splitext(glb_path)[0]}.{texture_name}.png"
    return path if os.path.exists(path) else None


def _material_color_attributes(mat, objects):
    """Colour attribute names present on the imported meshes using this material."""
    names = set()
    for obj in objects:
        if obj.type == "MESH" and any(slot.material is mat for slot in obj.material_slots):
            names |= {a.name for a in obj.data.color_attributes}
    return names


_BLEND_LAYER_GROUP = "Rust/Standard Blend Layer"
_GROUP_VERSION = 2


def _blend_layer_group():
    """One shared node group per shader family - the Blender equivalent of
    the shader asset itself. Materials are instances: their own textures
    outside, their own authored values on the group's sliders. The math
    inside is the exact curve read from the game's compiled fragment
    programs (docs/OUTPUT_CONTRACT.md):

        blend = min(1, (vertexWeight * tintAlpha * mask.G * (_DetailBlendFactor + 1)) ** _DetailBlendFalloff)
        color = lerp(base, detailAlbedo * tint, blend)

    Versioned: an older group in the file gets missing sockets added and
    its internals rebuilt in place, so existing materials keep working.
    """
    group = bpy.data.node_groups.get(_BLEND_LAYER_GROUP)
    if group is not None and group.get("rust_ripper_version", 0) >= _GROUP_VERSION:
        return group
    if group is None:
        group = bpy.data.node_groups.new(_BLEND_LAYER_GROUP, "ShaderNodeTree")
    group["rust_ripper_version"] = _GROUP_VERSION
    group.use_fake_user = True

    present = {(item.name, item.in_out) for item in group.interface.items_tree
               if getattr(item, "in_out", None)}

    def socket(name, in_out, socket_type, default=None):
        if (name, in_out) in present:
            return
        item = group.interface.new_socket(name=name, in_out=in_out, socket_type=socket_type)
        if default is not None:
            item.default_value = default

    socket("Base Color", "INPUT", "NodeSocketColor", (0.8, 0.8, 0.8, 1.0))
    socket("Detail Albedo", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Tint", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Tint Alpha", "INPUT", "NodeSocketFloat", 1.0)
    socket("Mask", "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    socket("Vertex Weight", "INPUT", "NodeSocketFloat", 1.0)
    # shader defaults; each material instance sets its authored values
    socket("_DetailBlendFactor", "INPUT", "NodeSocketFloat", 8.0)
    socket("_DetailBlendFalloff", "INPUT", "NodeSocketFloat", 1.0)
    socket("_DetailBlendMaskMapInvert", "INPUT", "NodeSocketFloat", 0.0)
    socket("Color", "OUTPUT", "NodeSocketColor")
    socket("Blend Factor", "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    nodes.clear()
    group_in = nodes.new("NodeGroupInput")
    group_out = nodes.new("NodeGroupOutput")

    def math(op, label, first=None, second=None):
        node = nodes.new("ShaderNodeMath")
        node.operation = op
        node.label = label
        if first is not None:
            node.inputs[0].default_value = first
        if second is not None:
            node.inputs[1].default_value = second
        return node

    sep = nodes.new("ShaderNodeSeparateColor")
    sep.label = "mask green (shader reads .g)"
    links.new(group_in.outputs["Mask"], sep.inputs["Color"])

    inverted = math("SUBTRACT", "1 - mask", first=1.0)
    links.new(sep.outputs["Green"], inverted.inputs[1])
    pick = nodes.new("ShaderNodeMix")
    pick.data_type = "FLOAT"
    pick.label = "_DetailBlendMaskMapInvert switch"
    links.new(group_in.outputs["_DetailBlendMaskMapInvert"], pick.inputs[0])
    links.new(sep.outputs["Green"], pick.inputs[2])
    links.new(inverted.outputs[0], pick.inputs[3])

    weighted = math("MULTIPLY", "mask x vertex weight")
    links.new(pick.outputs[0], weighted.inputs[0])
    links.new(group_in.outputs["Vertex Weight"], weighted.inputs[1])

    tint_alpha = math("MULTIPLY", "x tint alpha (weight = vcol.a x tint.a)")
    links.new(weighted.outputs[0], tint_alpha.inputs[0])
    links.new(group_in.outputs["Tint Alpha"], tint_alpha.inputs[1])

    gain = math("ADD", "_DetailBlendFactor + 1", second=1.0)
    links.new(group_in.outputs["_DetailBlendFactor"], gain.inputs[0])
    gained = math("MULTIPLY", "x (_DetailBlendFactor + 1)")
    links.new(tint_alpha.outputs[0], gained.inputs[0])
    links.new(gain.outputs[0], gained.inputs[1])

    curved = math("POWER", "^ _DetailBlendFalloff")
    links.new(gained.outputs[0], curved.inputs[0])
    links.new(group_in.outputs["_DetailBlendFalloff"], curved.inputs[1])
    clamped = math("MINIMUM", "min 1 (saturate)", second=1.0)
    links.new(curved.outputs[0], clamped.inputs[0])

    tinted = nodes.new("ShaderNodeMix")
    tinted.data_type = "RGBA"
    tinted.blend_type = "MULTIPLY"
    tinted.inputs[0].default_value = 1.0
    tinted.label = "detail layer (albedo x colour)"
    links.new(group_in.outputs["Detail Albedo"], tinted.inputs[6])
    links.new(group_in.outputs["Tint"], tinted.inputs[7])

    blend = nodes.new("ShaderNodeMix")
    blend.data_type = "RGBA"
    blend.blend_type = "MIX"
    blend.label = "blend by _DetailBlendMaskMap"
    links.new(clamped.outputs[0], blend.inputs[0])
    links.new(group_in.outputs["Base Color"], blend.inputs[6])
    links.new(tinted.outputs[2], blend.inputs[7])

    links.new(blend.outputs[2], group_out.inputs["Color"])
    links.new(clamped.outputs[0], group_out.inputs["Blend Factor"])
    _arrange_nodes(group)
    return group


def _build_blend_layer_nodes(glb_path, materials, objects):
    """Materials with an active blend layer get a group instance wired to
    their own textures and attributes - samplers cannot pass through group
    sockets, so they live in the material and feed sampled colors in."""
    built = 0
    for mat in materials:
        if not mat or not mat.use_nodes:
            continue
        floats = mat.get("unity_floats")
        if floats is None or floats.get("_DetailBlendLayer", 0.0) != 1.0:
            continue
        mask_entry = _texture_entry(mat, "_DetailBlendMaskMap")
        detail_entry = _texture_entry(mat, "_DetailAlbedoMap")
        mask_path = mask_entry and _sidecar_path(glb_path, mask_entry["name"])
        detail_path = detail_entry and _sidecar_path(glb_path, detail_entry["name"])
        if not mask_path or not detail_path:
            continue
        tree = mat.node_tree
        bsdf = next((n for n in tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        attrs = _material_color_attributes(mat, objects)
        nodes, links = tree.nodes, tree.links

        def image_node(path, label, non_color):
            img = bpy.data.images.load(path, check_existing=True)
            if non_color:
                img.colorspace_settings.name = "Non-Color"
            node = nodes.new("ShaderNodeTexImage")
            node.image = img
            node.label = label
            return node

        layer = nodes.new("ShaderNodeGroup")
        layer.node_tree = _blend_layer_group()
        layer.label = _BLEND_LAYER_GROUP
        # authored values, straight from the material data
        layer.inputs["_DetailBlendFactor"].default_value = floats.get("_DetailBlendFactor", 8.0)
        layer.inputs["_DetailBlendFalloff"].default_value = floats.get("_DetailBlendFalloff", 1.0)
        layer.inputs["_DetailBlendMaskMapInvert"].default_value = floats.get("_DetailBlendMaskMapInvert", 0.0)

        mask_node = image_node(mask_path, "_DetailBlendMaskMap", non_color=True)
        if floats.get("_DetailBlendMaskAddLowFreq", 0.0) != 0.0:
            mask_node.label += " (AddLowFreq second sample not built)"
        links.new(mask_node.outputs["Color"], layer.inputs["Mask"])

        # meshes without a colour stream read (1,1,1,1) in Unity: leave weight at 1
        if "_RUST_COLOR" in attrs:
            vcol = nodes.new("ShaderNodeVertexColor")
            vcol.layer_name = "_RUST_COLOR"
            vcol.label = "vertex colour (blend weight)"
            if floats.get("_DetailBlendMaskVertexSource", 0.0) == 0.0:
                links.new(vcol.outputs["Alpha"], layer.inputs["Vertex Weight"])
            else:
                vsep = nodes.new("ShaderNodeSeparateColor")
                vsep.label = "_DetailBlendMaskVertexSource=1 (red)"
                links.new(vcol.outputs["Color"], vsep.inputs["Color"])
                links.new(vsep.outputs["Red"], layer.inputs["Vertex Weight"])

        detail_node = image_node(detail_path, "_DetailAlbedoMap", non_color=False)
        scale = list(detail_entry.get("scale", [1.0, 1.0]))
        offset = list(detail_entry.get("offset", [0.0, 0.0]))
        if scale != [1.0, 1.0] or offset != [0.0, 0.0]:
            mapping = nodes.new("ShaderNodeMapping")
            mapping.label = "_DetailAlbedoMap tiling (data)"
            mapping.inputs["Scale"].default_value = (scale[0], scale[1], 1.0)
            mapping.inputs["Location"].default_value = (offset[0], offset[1], 0.0)
            uv = nodes.new("ShaderNodeUVMap")
            links.new(uv.outputs["UV"], mapping.inputs["Vector"])
            links.new(mapping.outputs["Vector"], detail_node.inputs["Vector"])
        links.new(detail_node.outputs["Color"], layer.inputs["Detail Albedo"])

        # weight = vcol.a x tint.a in the compiled shader: alpha rides along
        if "_RUST_CUSTOMCOLOUR_01" in attrs:
            tint = nodes.new("ShaderNodeVertexColor")
            tint.layer_name = "_RUST_CUSTOMCOLOUR_01"
            tint.label = "customColour 01 (swap layer for other palette entries)"
            links.new(tint.outputs["Color"], layer.inputs["Tint"])
            links.new(tint.outputs["Alpha"], layer.inputs["Tint Alpha"])
        elif "_RUST_DETAILCOLOR" in attrs:
            tint = nodes.new("ShaderNodeVertexColor")
            tint.layer_name = "_RUST_DETAILCOLOR"
            tint.label = "_DetailColor (authored)"
            links.new(tint.outputs["Color"], layer.inputs["Tint"])
            links.new(tint.outputs["Alpha"], layer.inputs["Tint Alpha"])
        else:
            colors = mat.get("unity_colors")
            authored = list(colors.get("_DetailColor", [1.0, 1.0, 1.0, 1.0])) if colors is not None else [1.0, 1.0, 1.0, 1.0]
            layer.inputs["Tint"].default_value = (*authored[:3], 1.0)
            layer.inputs["Tint Alpha"].default_value = authored[3] if len(authored) > 3 else 1.0

        base_input = bsdf.inputs["Base Color"]
        if base_input.is_linked:
            links.new(base_input.links[0].from_socket, layer.inputs["Base Color"])
        else:
            layer.inputs["Base Color"].default_value = base_input.default_value
        links.new(layer.outputs["Color"], base_input)
        built += 1
    return built


# ------------------------------------------------------------- node layout

_NODE_HEIGHT = {
    "TEX_IMAGE": 290, "BSDF_PRINCIPLED": 640, "MIX": 190, "MATH": 160,
    "VALUE": 90, "RGB": 200, "SEPARATE_COLOR": 130, "NORMAL_MAP": 170,
    "MAPPING": 330, "UVMAP": 100, "VERTEX_COLOR": 120, "GROUP": 300,
    "GROUP_INPUT": 300, "GROUP_OUTPUT": 120,
    "OUTPUT_MATERIAL": 110,
}


def _arrange_nodes(tree):
    """Neat left-to-right layout. Columns = longest link distance to the
    output. Rows follow the consumers' input-socket order (base colour
    chain is the top band, roughness below, normal below that - the BSDF's
    own order), and every node is pulled level with the socket it feeds,
    pushed down only when it would overlap its column neighbour."""
    nodes = [n for n in tree.nodes if n.type != "FRAME"]
    depth = {n: 0 for n in nodes}
    for _ in range(len(nodes)):
        changed = False
        for link in tree.links:
            a, b = link.from_node, link.to_node
            if a in depth and b in depth and depth[a] < depth[b] + 1:
                depth[a] = depth[b] + 1
                changed = True
        if not changed:
            break

    # semantic order: depth-first from the output side, inputs top-to-bottom,
    # so each column stacks in the order its chains hang off the BSDF
    order = {}

    def visit(node):
        if node in order:
            return
        order[node] = len(order)
        for socket in node.inputs:
            for link in socket.links:
                visit(link.from_node)

    for node in nodes:
        if not any(s.links for s in node.outputs):
            visit(node)
    for node in nodes:
        visit(node)

    columns = {}
    for node in nodes:
        columns.setdefault(depth[node], []).append(node)
    for d in sorted(columns):
        cursor = None
        for node in sorted(columns[d], key=lambda n: order[n]):
            # level with the topmost socket this node feeds
            desired = []
            for socket in node.outputs:
                for link in socket.links:
                    consumer = link.to_node
                    try:
                        slot = list(consumer.inputs).index(link.to_socket)
                    except ValueError:
                        slot = 0
                    desired.append(consumer.location.y - 40.0 - slot * 22.0)
            y = max(desired) if desired else 0.0
            if cursor is not None:
                y = min(y, cursor)
            node.location = (-d * 340.0, y)
            cursor = y - _NODE_HEIGHT.get(node.type, 160) - 40.0


def _import_glb(context, filepath):
    settings = context.scene.rust_ripper
    before_objects = set(bpy.data.objects)
    before_materials = set(bpy.data.materials)
    bpy.ops.import_scene.gltf(filepath=filepath)
    new_objects = [o for o in bpy.data.objects if o not in before_objects]
    new_materials = [m for m in bpy.data.materials if m not in before_materials]
    hidden, reused = _post_process(new_objects, settings)
    painted = _build_paint_nodes(filepath, new_materials)
    painted += _build_blend_layer_nodes(filepath, new_materials, new_objects)
    if settings.tidy_nodes:
        for mat in new_materials:
            if mat.use_nodes:
                _arrange_nodes(mat.node_tree)
    return len(new_objects), hidden, painted, reused


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
        count, hidden, painted, reused = _import_glb(context, self.filepath)
        self.report({"INFO"}, f"{count} objects ({hidden} hidden, {painted} paint, {reused} meshes reused)")
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
        count, hidden, painted, reused = _import_glb(context, result["path"])
        self.report({"INFO"}, f"{query}: {count} objects in {result.get('seconds', 0):.1f}s export ({reused} meshes reused)")
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
        layout.prop(settings, "reuse_meshes")
        layout.prop(settings, "light_power_scale")
        layout.prop(settings, "tidy_nodes")


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
